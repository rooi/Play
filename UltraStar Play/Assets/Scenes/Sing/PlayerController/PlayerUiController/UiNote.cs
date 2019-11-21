﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UiNote : MonoBehaviour
{
    public StarParticle goldenStarPrefab;
    public StarParticle perfectStarPrefab;

    private RectTransform rectTransform;

    public Note Note { get; set; }
    public bool isGolden;

    private RectTransform uiEffectsContainer;

    private List<StarParticle> stars = new List<StarParticle>();

    public void Init(Note note, RectTransform uiEffectsContainer)
    {
        this.Note = note;
        this.isGolden = note.IsGolden;
        this.uiEffectsContainer = uiEffectsContainer;
    }

    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
    }

    void Update()
    {
        if (isGolden)
        {
            RemoveDestroyedStarsFromList();
            CreateGoldenNoteEffect();
        }
        else
        {
            DestroyStars();
        }
    }

    void OnDestroy()
    {
        DestroyStars();
    }

    private void RemoveDestroyedStarsFromList()
    {
        foreach (StarParticle star in new List<StarParticle>(stars))
        {
            if (!star)
            {
                stars.Remove(star);
            }
        }
    }

    private void DestroyStars()
    {
        foreach (StarParticle star in stars)
        {
            if (star)
            {
                Destroy(star.gameObject);
            }
        }
        stars.Clear();
    }

    private void CreateGoldenNoteEffect()
    {
        // Create several particles. Longer notes require more particles because they have more space to fill.
        int starCount = stars.Count;
        int targetStarCount = Mathf.Max(6, (int)rectTransform.rect.width / 10);
        if (starCount < targetStarCount)
        {
            CreateGoldenStar();
        }
    }

    private void CreateGoldenStar()
    {
        StarParticle star = Instantiate(goldenStarPrefab);
        star.transform.SetParent(rectTransform);
        RectTransform starRectTransform = star.GetComponent<RectTransform>();
        float anchorX = Random.Range(0f, 1f);
        float anchorY = Random.Range(0f, 1f);
        starRectTransform.anchorMin = new Vector2(anchorX, anchorY);
        starRectTransform.anchorMax = new Vector2(anchorX, anchorY);
        starRectTransform.anchoredPosition = Vector2.zero;

        star.RectTransform.localScale = Vector3.one * Random.Range(0, 0.5f);
        LeanTween.scale(star.RectTransform, Vector3.one * Random.Range(0.5f, 1f), Random.Range(1f, 2f))
            .setOnComplete(() => Destroy(star.gameObject));

        // Move to another parent to ensure that it is drawn in front of the notes.
        star.transform.SetParent(uiEffectsContainer);

        stars.Add(star);
    }

    public void CreatePerfectNoteEffect()
    {
        int targetStarCount = Mathf.Max(6, (int)rectTransform.rect.width / 20);
        for (int i = 0; i < targetStarCount; i++)
        {
            CreatePerfectStar();
        }
    }

    private void CreatePerfectStar()
    {
        StarParticle star = Instantiate(perfectStarPrefab);
        star.transform.SetParent(transform);
        RectTransform starRectTransform = star.GetComponent<RectTransform>();
        float anchorX = Random.Range(0f, 1f);
        float anchorY = Random.Range(0f, 1f);
        starRectTransform.anchorMin = new Vector2(anchorX, anchorY);
        starRectTransform.anchorMax = new Vector2(anchorX, anchorY);
        starRectTransform.anchoredPosition = Vector2.zero;

        star.RectTransform.localScale = Vector3.one * Random.Range(0.5f, 0.8f);
        LeanTween.scale(star.RectTransform, Vector3.zero, 1f)
            .setOnComplete(() => Destroy(star.gameObject));

        // Move to another parent to ensure that it is drawn in front of the notes.
        star.transform.SetParent(uiEffectsContainer);
    }
}
