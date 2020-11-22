﻿using UnityEngine;
using UniInject;
using static ThreadPool;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class SongEditorMidiSoundPlayAlong : MonoBehaviour, INeedInjection
{
    [Inject]
    private SongAudioPlayer songAudioPlayer;

    [Inject]
    private SongMeta songMeta;

    [Inject]
    private MidiManager midiManager;

    [Inject]
    private Settings settings;

    private SongEditorMidiSoundPlayAlongThread thread;
    private double positionInSongInMillisOld;

    void Update()
    {
        if (!settings.SongEditorSettings.MidiSoundPlayAlongEnabled)
        {
            // Do not play midi sounds.
            // Furthermore, stop currently playing sounds if this setting changed during playback.
            if (thread != null)
            {
                StopThread();
            }
            return;
        }

        if (songAudioPlayer.IsPlaying && thread == null)
        {
            // Start thread for playing Midi note at correct time
            StartThread();
        }
        else if (!songAudioPlayer.IsPlaying && thread != null)
        {
            StopThread();
        }

        if (thread != null)
        {
            if (songAudioPlayer.PositionInSongInMillis < positionInSongInMillisOld)
            {
                // Jumped back, thus recalculate upcomingSortedNotes and stop any currently playing notes.
                thread.CalculateUpcomingSortedNotes();
                midiManager.StopAllMidiNotes();
            }

            thread.SynchronizeWithPlaybackPosition((int)songAudioPlayer.PositionInSongInMillis);
        }
        positionInSongInMillisOld = songAudioPlayer.PositionInSongInMillis;
    }

    private void StopThread()
    {
        thread.Stop();
        thread = null;

        midiManager.StopAllMidiNotes();
    }

    private void StartThread()
    {
        midiManager.InitIfNotDoneYet();
        thread = new SongEditorMidiSoundPlayAlongThread(settings, songMeta, midiManager);
        thread.Start((int)songAudioPlayer.PositionInSongInMillis);
    }
}
