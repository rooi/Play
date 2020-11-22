﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniInject;
using UniRx;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class SongEditorPlaybackSpeedSlider : MonoBehaviour, INeedInjection
{

    [Inject(searchMethod = SearchMethods.GetComponentInChildren)]
    private Slider slider;

    [Inject]
    private SongAudioPlayer songAudioPlayer;

    [Inject]
    private Settings settings;

    void Start()
    {
        slider.value = settings.SongEditorSettings.MusicPlaybackSpeed;
        songAudioPlayer.PlaybackSpeed = settings.SongEditorSettings.MusicPlaybackSpeed;
        slider.OnValueChangedAsObservable().Subscribe(newValue =>
        {
            settings.SongEditorSettings.MusicPlaybackSpeed = newValue;
            songAudioPlayer.PlaybackSpeed = newValue;
        });
    }
}
