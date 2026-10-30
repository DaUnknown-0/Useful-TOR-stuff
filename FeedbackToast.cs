// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackToast - a short line at the top of the screen ("saved", "delivered", "received from ...").
 * One own canvas, newest line wins, fades out after a few seconds.
 */

using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackToast
{
    internal enum Kind { Info, Good, Warn }

    private static GameObject root;
    private static GameObject panel;
    private static TMPro.TextMeshProUGUI label;
    private static CanvasGroup group;
    private static float shownAt = -100f, duration;

    internal static void Show(string text, Kind kind = Kind.Info, float seconds = 4.5f)
    {
        try {
            Build();
            VanillaUI.SetText(label, text);
            label.color = kind switch { Kind.Good => VanillaUI.Good, Kind.Warn => VanillaUI.Warn, _ => VanillaUI.TextColor };
            shownAt = Time.realtimeSinceStartup;
            duration = seconds;
            root.SetActive(true);
            group.alpha = 1f;
        } catch (System.Exception e) {
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] toast failed: {e.Message}");
        }
    }

    internal static void Tick()
    {
        if (root == null || !root.activeSelf) return;
        float age = Time.realtimeSinceStartup - shownAt;
        if (age > duration + 0.6f) { root.SetActive(false); return; }
        group.alpha = age > duration ? 1f - (age - duration) / 0.6f : 1f;
    }

    private static void Build()
    {
        if (root != null) return;
        root = VanillaUI.Canvas("UnknownsFeedbackToast", 32000, false);
        group = root.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;
        panel = VanillaUI.Panel(root, new Vector2(1100f, 84f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f));
        label = VanillaUI.Label(panel, "", 24f, VanillaUI.TextColor, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                                Vector2.zero, new Vector2(-40f, -16f), TMPro.TextAlignmentOptions.Center);
        label.enableWordWrapping = true;
        label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        root.SetActive(false);
    }
}
