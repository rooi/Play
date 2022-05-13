﻿//using System; UniRx.ObservableExtensions conflict
using System.Collections.Generic;
using System.Linq;
using CircularBuffer;
using UniInject;
using UniRx;
using UnityEngine;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

/**
 * Analyzes each beat of a player in the sing scene.
 * Thereby, it applies some additional rounding an joker rules.
 */
[RequireComponent(typeof(MicSampleRecorder))]
public class PlayerMicPitchTracker : MonoBehaviour, INeedInjection
{
    private const int SendPositionInSongIntervalInMillis = 2000;

    [Inject]
    private SongAudioPlayer songAudioPlayer;

    [Inject]
    private SongMeta songMeta;

    [Inject]
    private PlayerControl playerControl;

    [Inject]
    private PlayerProfile playerProfile;

    [Inject(Optional = true)]
    private MicProfile micProfile;

    [Inject(SearchMethod = SearchMethods.GetComponentInChildren)]
    private MicSampleRecorder micSampleRecorder;

    [Inject]
    private Settings settings;

    [Inject]
    private ServerSideConnectRequestManager serverSideConnectRequestManager;
    
    // The rounding distance of the PlayerProfile
    private int roundingDistance;

    private int recordingSentenceIndex;
    public int BeatToAnalyze { get; private set; }

    public Sentence RecordingSentence { get; private set; }
    private List<Note> currentAndUpcomingNotesInRecordingSentence;

    private IAudioSamplesAnalyzer audioSamplesAnalyzer;

    private bool hasJoker;

    // Only for debugging: see how many jokers have been used in the inspector
    [ReadOnly]
    public int usedJokerCount;

    private readonly Subject<BeatAnalyzedEvent> beatAnalyzedEventStream = new();
    public System.IObservable<BeatAnalyzedEvent> BeatAnalyzedEventStream => beatAnalyzedEventStream;

    private readonly Subject<NoteAnalyzedEvent> noteAnalyzedEventStream = new();
    public System.IObservable<NoteAnalyzedEvent> NoteAnalyzedEventStream => noteAnalyzedEventStream;

    private readonly Subject<SentenceAnalyzedEvent> sentenceAnalyzedEventStream = new();
    public System.IObservable<SentenceAnalyzedEvent> SentenceAnalyzedEventStream => sentenceAnalyzedEventStream;

    private int lastAnalyzedBeatFromConnectedClient;

    private long lastUnixTimeMillisecondsWhenSentPositionInSongToClient = TimeUtils.GetUnixTimeMilliseconds();

    private void Start()
    {
        // Find first sentence to analyze
        SetRecordingSentence(recordingSentenceIndex);

        if (micProfile == null)
        {
            Debug.LogWarning($"No mic for player {playerProfile.Name}. Not recording player notes.");
            gameObject.SetActive(false);
            return;
        }

        roundingDistance = playerProfile.Difficulty.GetRoundingDistance();
        micSampleRecorder.MicProfile = micProfile;
        if (micProfile.IsInputFromConnectedClient)
        {
            InitPitchDetectionFromConnectedClient();
            serverSideConnectRequestManager.ClientConnectedEventStream
                .Subscribe(evt =>
                {
                    if (evt.IsConnected)
                    {
                        InitPitchDetectionFromConnectedClient();
                    }
                })
                .AddTo(gameObject);
        }
        else
        {
            InitPitchDetectionFromLocalMicrophone();
        }

        beatAnalyzedEventStream.Subscribe(evt => OnBeatAnalyzed(evt));
    }

    private void InitPitchDetectionFromLocalMicrophone()
    {
        micSampleRecorder.StartRecording();

        // The AudioSampleAnalyzer uses the MicSampleRecorder's sampleRateHz. Thus, it must be initialized after the MicSampleRecorder.
        audioSamplesAnalyzer = AbstractMicPitchTracker.CreateAudioSamplesAnalyzer(settings.AudioSettings.pitchDetectionAlgorithm, micSampleRecorder.FinalSampleRate.Value);
    }

    private void InitPitchDetectionFromConnectedClient()
    {
        if (GetConnectedClientHandler() == null)
        {
            Debug.LogWarning($"Did not find connected client handler for player {playerProfile.Name}. Not recording player notes.");
            gameObject.SetActive(false);
            return;
        }

        GetConnectedClientHandler().ReceivedMessageStream
            .ObserveOnMainThread()
            .Subscribe(dto =>
            {
                if (dto is BeatPitchEventDto beatPitchEventDto)
                {
                    HandlePitchEventFromConnectedClient(new BeatPitchEvent(beatPitchEventDto.MidiNote, beatPitchEventDto.Beat));
                }
            })
            .AddTo(gameObject);

        SendPositionInSongToClientRapidly();
    }

    private IConnectedClientHandler GetConnectedClientHandler()
    {
        if (micProfile == null
            || !micProfile.IsInputFromConnectedClient)
        {
            return null;
        }

        serverSideConnectRequestManager.TryGetConnectedClientHandler(micProfile.ConnectedClientId, out IConnectedClientHandler connectedClientHandler);
        return connectedClientHandler;
    }

    private void Update()
    {
        if (micProfile == null)
        {
            return;
        }

        if (micProfile.IsInputFromConnectedClient)
        {
            UpdatePitchDetectionFromConnectedClient();
        }
        else
        {
            UpdatePitchDetectionFromLocalMicrophone();
        }
    }

    private void UpdatePitchDetectionFromLocalMicrophone()
    {
        // No sentence to analyze left (all done).
        if (RecordingSentence == null)
        {
            return;
        }

        // Analyze the next beat with fully recorded mic samples
        double nextBeatToAnalyzeEndPositionInMs = BpmUtils.BeatToMillisecondsInSong(songMeta, BeatToAnalyze + 1);
        if (nextBeatToAnalyzeEndPositionInMs >= songAudioPlayer.PositionInSongInMillis - micProfile.DelayInMillis)
        {
            return;
        }

        // The beat has passed and should have recorded samples in the mic buffer. Analyze the samples now.
        PitchEvent pitchEvent = GetPitchEventOfBeat(BeatToAnalyze);
        Note currentOrUpcomingNote = currentAndUpcomingNotesInRecordingSentence.IsNullOrEmpty()
            ? null
            : currentAndUpcomingNotesInRecordingSentence[0];
        Note noteAtBeat = (currentOrUpcomingNote.StartBeat <= BeatToAnalyze && BeatToAnalyze < currentOrUpcomingNote.EndBeat)
            ? currentOrUpcomingNote
            : null;

        FirePitchEvent(pitchEvent, BeatToAnalyze, noteAtBeat, RecordingSentence);
    }

    private void UpdatePitchDetectionFromConnectedClient()
    {
        // Read messages from client since last time the reader thread was active.
        IConnectedClientHandler connectedClientHandler = GetConnectedClientHandler();
        connectedClientHandler?.ReadMessagesFromClient();

        if (lastUnixTimeMillisecondsWhenSentPositionInSongToClient + SendPositionInSongIntervalInMillis < TimeUtils.GetUnixTimeMilliseconds())
        {
            // Synchronize position in song with connected client.
            SendPositionInSongToClient();
        }
    }

    private int ApplyJokerRule(PitchEvent pitchEvent, int roundedMidiNote, Note noteAtBeat)
    {
        if (noteAtBeat == null)
        {
            return roundedMidiNote;
        }

        // Earn a joker when singing correctly (without using a joker).
        // A failed beat can be undone via joker-rule.
        if (pitchEvent != null && roundedMidiNote == noteAtBeat.MidiNote)
        {
            hasJoker = true;
        }
        // The joker is only for continued singing.
        if (pitchEvent == null)
        {
            hasJoker = false;
        }

        // If the player fails a beat in continued singing, but the previous beats were sung correctly,
        // then this failed beat is ignored.
        if (roundedMidiNote != noteAtBeat.MidiNote
            && hasJoker)
        {
            hasJoker = false;
            usedJokerCount++;
            return noteAtBeat.MidiNote;
        }
        return roundedMidiNote;
    }

    private void HandlePitchEventFromConnectedClient(BeatPitchEvent pitchEvent)
    {
        if (pitchEvent.Beat < 0
            || pitchEvent.Beat < lastAnalyzedBeatFromConnectedClient)
        {
            // Looks like the companion app does not know the current position in the song. Send it this info again.
            Debug.LogWarning($"Received invalid beat from connected client: beat {pitchEvent.Beat}");
            if (lastUnixTimeMillisecondsWhenSentPositionInSongToClient + (SendPositionInSongIntervalInMillis / 10) < TimeUtils.GetUnixTimeMilliseconds())
            {
                SendPositionInSongToClient();
            }
            return;
        }

        int currentBeat = (int)BpmUtils.MillisecondInSongToBeat(songMeta, songAudioPlayer.PositionInSongInMillis);
        if (pitchEvent.Beat > currentBeat)
        {
            Debug.LogWarning($"Received future beat from connected client (received: {pitchEvent.Beat}, current: {currentBeat}).");
            return;
        }

        lastAnalyzedBeatFromConnectedClient = pitchEvent.Beat;
        FirePitchEventFromConnectedClient(pitchEvent);
    }

    public void SendPositionInSongToClientRapidly()
    {
        // The position in the song changed dramatically.
        // But the client implements methods to ignore single messages with big position differences (resilient behavior).
        // Thus, send the new position in song more aggressively.
        List<float> delaysInSeconds = new(){ 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        delaysInSeconds.ForEach(delayInSeconds =>
            StartCoroutine(CoroutineUtils.ExecuteAfterDelayInSeconds(delayInSeconds, () => SendPositionInSongToClient())));
    }

    private void SendPositionInSongToClient()
    {
        lastUnixTimeMillisecondsWhenSentPositionInSongToClient = TimeUtils.GetUnixTimeMilliseconds();

        if (micProfile == null
            || !micProfile.IsInputFromConnectedClient)
        {
            return;
        }

        IConnectedClientHandler connectedClientHandler = GetConnectedClientHandler();
        if (connectedClientHandler == null)
        {
            // Disconnected
            return;
        }

        PositionInSongDto positionInSongDto = new PositionInSongDto
        {
            SongBpm = songMeta.Bpm,
            SongGap = songMeta.Gap,
            PositionInSongInMillis = songAudioPlayer.PositionInSongInMillisExact,
        };
        Debug.Log($"Send position in song to client {micProfile.ConnectedClientId}: {positionInSongDto.ToJson()}");
        connectedClientHandler.SendMessageToClient(positionInSongDto);
    }

    private void FirePitchEventFromConnectedClient(BeatPitchEvent pitchEvent)
    {
        if (pitchEvent.Beat < 0)
        {
            return;
        }

        Sentence sentenceAtBeat = SongMetaUtils.GetSentenceAtBeat(playerControl.Voice, pitchEvent.Beat);
        Note noteAtBeat = SongMetaUtils.GetNoteAtBeat(sentenceAtBeat, pitchEvent.Beat, true, false);
        int midiNote = pitchEvent.MidiNote;
        if (midiNote < 0)
        {
            FirePitchEvent(null, pitchEvent.Beat, noteAtBeat, sentenceAtBeat);
        }
        else
        {
            FirePitchEvent(new PitchEvent(midiNote), pitchEvent.Beat, noteAtBeat, sentenceAtBeat);
        }
    }

    public void FirePitchEvent(PitchEvent pitchEvent, int beat, Note noteAtBeat, Sentence sentenceAtBeat)
    {
        int recordedMidiNote = pitchEvent != null
            ? pitchEvent.MidiNote
            : -1;
        int roundedRecordedMidiNote = pitchEvent != null
            ? GetRoundedMidiNoteForRecordedMidiNote(noteAtBeat, pitchEvent.MidiNote)
            : -1;
        int roundedMidiNoteAfterJoker = ApplyJokerRule(pitchEvent, roundedRecordedMidiNote, noteAtBeat);

        beatAnalyzedEventStream.OnNext(new BeatAnalyzedEvent(pitchEvent, beat, noteAtBeat, sentenceAtBeat, recordedMidiNote, roundedMidiNoteAfterJoker));
    }

    private void OnBeatAnalyzed(BeatAnalyzedEvent beatAnalyzedEvent)
    {
        if (beatAnalyzedEvent == null
            || RecordingSentence == null)
        {
            return;
        }

        if (BeatToAnalyze <= beatAnalyzedEvent.Beat)
        {
            BeatToAnalyze = beatAnalyzedEvent.Beat + 1;
        }
        if (BeatToAnalyze > RecordingSentence.MaxBeat)
        {
            // All beats of the sentence analyzed. Go to next sentence.
            GoToNextRecordingSentence();
            return;
        }

        // If there is no note at that beat, then use the StartBeat of the following note for next analysis.
        // Remove notes that have been completely analyzed.
        Note passedNote = null;
        if (!currentAndUpcomingNotesInRecordingSentence.IsNullOrEmpty()
            && currentAndUpcomingNotesInRecordingSentence[0].EndBeat <= BeatToAnalyze)
        {
            passedNote = currentAndUpcomingNotesInRecordingSentence[0];
            currentAndUpcomingNotesInRecordingSentence.RemoveAt(0);
        }
        if (passedNote != null)
        {
            noteAnalyzedEventStream.OnNext(new NoteAnalyzedEvent(passedNote));
        }

        // Check if there is still a current note that is analyzed.
        if (!currentAndUpcomingNotesInRecordingSentence.IsNullOrEmpty())
        {
            Note currentOrUpcomingNote = currentAndUpcomingNotesInRecordingSentence[0];
            if (currentOrUpcomingNote.StartBeat > BeatToAnalyze
                && !settings.GraphicSettings.analyzeBeatsWithoutTargetNote)
            {
                // Next beat to analyze is at the next note
                BeatToAnalyze = currentOrUpcomingNote.StartBeat;
            }
        }
        else if (settings.GraphicSettings.analyzeBeatsWithoutTargetNote
                 && BeatToAnalyze < RecordingSentence.MaxBeat)
        {
            BeatToAnalyze++;
        }
        else
        {
            // All notes of the sentence analyzed. Go to next sentence.
            GoToNextRecordingSentence();
        }
    }

    private void GoToNextRecordingSentence()
    {
        // Fire event about finished sentence
        Sentence nextRecordingSentence = playerControl.GetSentence(recordingSentenceIndex + 1);
        sentenceAnalyzedEventStream.OnNext(new SentenceAnalyzedEvent(RecordingSentence, nextRecordingSentence == null));
        // Select next sentence
        recordingSentenceIndex++;
        SetRecordingSentence(recordingSentenceIndex);
    }

    private int GetMicSampleBufferIndexForBeat(int beat)
    {
        double beatInMs = BpmUtils.BeatToMillisecondsInSong(songMeta, beat);
        double beatPassedBeforeMs = songAudioPlayer.PositionInSongInMillis - beatInMs;
        int beatPassedBeforeSamplesInMicBuffer = System.Convert.ToInt32(((beatPassedBeforeMs - micProfile.DelayInMillis) / 1000) * micSampleRecorder.FinalSampleRate.Value);
        // The newest sample has the highest index in the MicSampleBuffer
        int sampleBufferIndex = micSampleRecorder.MicSamples.Length - beatPassedBeforeSamplesInMicBuffer;
        sampleBufferIndex = NumberUtils.Limit(sampleBufferIndex, 0, micSampleRecorder.MicSamples.Length - 1);
        return sampleBufferIndex;
    }

    private void SetRecordingSentence(int sentenceIndex)
    {
        RecordingSentence = playerControl.GetSentence(sentenceIndex);
        if (RecordingSentence == null)
        {
            currentAndUpcomingNotesInRecordingSentence = new List<Note>();
            BeatToAnalyze = 0;
            return;
        }
        currentAndUpcomingNotesInRecordingSentence = SongMetaUtils.GetSortedNotes(RecordingSentence);

        BeatToAnalyze = RecordingSentence.MinBeat;
    }

    void OnDisable()
    {
        if (micProfile != null)
        {
            micSampleRecorder.StopRecording();
        }
    }

    private int GetRoundedMidiNoteForRecordedMidiNote(Note targetNote, int recordedMidiNote)
    {
        if (targetNote == null)
        {
            return recordedMidiNote;
        }

        if (targetNote.Type == ENoteType.Rap || targetNote.Type == ENoteType.RapGolden)
        {
            // Rap notes accept any noise as correct note.
            return targetNote.MidiNote;
        }
        else if (recordedMidiNote < MidiUtils.SingableNoteMin || recordedMidiNote > MidiUtils.SingableNoteMax)
        {
            // The pitch detection can fail, which is the case when the detected pitch is outside of the singable note range.
            // In this case, just assume that the player was singing correctly and round to the target note.
            return targetNote.MidiNote;
        }
        else
        {
            // Round recorded note if it is close to the target note.
            return GetRoundedMidiNote(recordedMidiNote, targetNote.MidiNote, roundingDistance);
        }
    }

    private int GetRoundedMidiNote(int recordedMidiNote, int targetMidiNote, int roundingDistance)
    {
        int distance = MidiUtils.GetRelativePitchDistance(recordedMidiNote, targetMidiNote);
        if (distance <= roundingDistance)
        {
            return targetMidiNote;
        }
        else
        {
            return recordedMidiNote;
        }
    }

    public void SkipToBeat(double currentBeat)
    {
        // Find sentence to analyze next.
        RecordingSentence = playerControl.SortedSentences
            .FirstOrDefault(sentence => currentBeat <= sentence.MaxBeat);
        if (RecordingSentence != null)
        {
            recordingSentenceIndex = playerControl.SortedSentences.IndexOf(RecordingSentence);
            // Find note to analyze next
            currentAndUpcomingNotesInRecordingSentence = RecordingSentence.Notes
                .Where(note => currentBeat <= note.EndBeat)
                .OrderBy(note => note.StartBeat)
                .ToList();
            if (currentAndUpcomingNotesInRecordingSentence.Count > 0)
            {
                if (currentAndUpcomingNotesInRecordingSentence[0].StartBeat < currentBeat)
                {
                    // currentBeat is inside note
                    BeatToAnalyze = (int)currentBeat;
                }
                else
                {
                    // The note is upcoming, analyze its first beat next.
                    BeatToAnalyze = currentAndUpcomingNotesInRecordingSentence[0].StartBeat;
                }
            }
            else
            {
                BeatToAnalyze = RecordingSentence.MaxBeat;
            }
        }

        if (micProfile != null
            && micProfile.IsInputFromConnectedClient)
        {
            // Position changed heavily. Send the new position more aggressively to connected clients.
            SendPositionInSongToClientRapidly();
        }
    }

    public PitchEvent GetPitchEventOfSamples(int startSampleBufferIndex, int endSampleBufferIndex)
    {
        if (startSampleBufferIndex > endSampleBufferIndex)
        {
            ObjectUtils.Swap(ref startSampleBufferIndex, ref endSampleBufferIndex);
        }
        startSampleBufferIndex = NumberUtils.Limit(startSampleBufferIndex, 0, micSampleRecorder.MicSamples.Length - 1);
        endSampleBufferIndex = NumberUtils.Limit(endSampleBufferIndex, 0, micSampleRecorder.MicSamples.Length - 1);
        PitchEvent pitchEvent = audioSamplesAnalyzer.ProcessAudioSamples(micSampleRecorder.MicSamples, startSampleBufferIndex, endSampleBufferIndex, micProfile);
        return pitchEvent;
    }

    public PitchEvent GetPitchEventOfBeat(int beat)
    {
        int startSampleBufferIndex = GetMicSampleBufferIndexForBeat(beat);
        int endSampleBufferIndex = GetMicSampleBufferIndexForBeat(beat + 1);
        PitchEvent pitchEvent = GetPitchEventOfSamples(startSampleBufferIndex, endSampleBufferIndex);
        return pitchEvent;
    }

    public void SendMicProfileToConnectedClient()
    {
        GetConnectedClientHandler()?.SendMessageToClient(new MicProfileMessageDto(micProfile));
    }

    public void SendStopRecordingMessageToConnectedClient()
    {
        GetConnectedClientHandler()?.SendMessageToClient(new StopRecordingMessageDto());
    }

    public void SendStartRecordingMessageToConnectedClient()
    {
        GetConnectedClientHandler()?.SendMessageToClient(new StartRecordingMessageDto());
        SendPositionInSongToClientRapidly();

    }
}
