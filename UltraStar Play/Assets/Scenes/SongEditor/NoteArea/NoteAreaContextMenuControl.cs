﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniInject;
using UniRx;
using UnityEngine.InputSystem;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class NoteAreaContextMenuControl : ContextMenuControl
{
    [Inject]
    private SongMeta songMeta;

    [Inject]
    private SongEditorSceneControl songEditorSceneControl;

    [Inject]
    private SongMetaChangeEventStream songMetaChangeEventStream;

    [Inject]
    private NoteAreaControl noteAreaControl;

    [Inject]
    private SongEditorSelectionControl selectionControl;

    [Inject]
    private AddNoteAction addNoteAction;

    [Inject]
    private SetMusicGapAction setMusicGapAction;

    [Inject]
    private SongEditorCopyPasteManager songEditorCopyPasteManager;
    
    [Inject]
    private NoteAreaDragControl noteAreaDragControl;

    private Vector2 dragDistanceInPx;

    public override void OnInjectionFinished()
    {
        base.OnInjectionFinished();
        FillContextMenuAction = FillContextMenu;
    }

    protected override void CheckOpenContextMenuFromInputAction(InputAction.CallbackContext context)
    {
        // This ContextMenu could open although a drag is in progress.
        if (dragDistanceInPx.magnitude > ContextMenuControl.DragDistanceThreshold)
        {
            return;
        }
        
        base.CheckOpenContextMenuFromInputAction(context);
    }
        
    private void FillContextMenu(ContextMenuPopupControl contextMenu)
    {
        int beat = (int)noteAreaControl.GetHorizontalMousePositionInBeats();
        int midiNote = noteAreaControl.GetVerticalMousePositionInMidiNote();

        contextMenu.AddItem("Fit vertical", () => noteAreaControl.FitViewportVerticalToNotes());

        Sentence sentenceAtBeat = SongMetaUtils.GetSentencesAtBeat(songMeta, beat).FirstOrDefault();
        if (sentenceAtBeat != null)
        {
            int minBeat = sentenceAtBeat.MinBeat - 1;
            int maxBeat = sentenceAtBeat.ExtendedMaxBeat + 1;
            contextMenu.AddItem("Fit horizontal to sentence ", () => noteAreaControl.FitViewportHorizontal(minBeat, maxBeat));
        }

        List<Note> selectedNotes = selectionControl.GetSelectedNotes();
        if (selectedNotes.Count > 0)
        {
            int minBeat = selectedNotes.Select(it => it.StartBeat).Min() - 1;
            int maxBeat = selectedNotes.Select(it => it.EndBeat).Max() + 1;
            contextMenu.AddItem("Fit horizontal to selection", () => noteAreaControl.FitViewportHorizontal(minBeat, maxBeat));
        }

        if (selectedNotes.Count > 0
            || songEditorCopyPasteManager.CopiedNotes.Count > 0)
        {
            contextMenu.AddSeparator();
            if (selectedNotes.Count > 0)
            {
                contextMenu.AddItem("Copy notes", () => songEditorCopyPasteManager.CopySelectedNotes());
            }

            if (songEditorCopyPasteManager.CopiedNotes.Count > 0)
            {
                contextMenu.AddItem("Paste notes", () => songEditorCopyPasteManager.PasteCopiedNotes());
            }
        }
        
        contextMenu.AddSeparator();
        contextMenu.AddItem("Add note", () => addNoteAction.ExecuteAndNotify(songMeta, beat, midiNote));

        if (selectedNotes.Count == 0)
        {
            contextMenu.AddSeparator();
            contextMenu.AddItem("Set Gap to playback position", () => setMusicGapAction.ExecuteAndNotify());
        }
    }
}
