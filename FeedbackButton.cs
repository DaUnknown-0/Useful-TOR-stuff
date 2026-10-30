// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackButton - the small bug button that opens the feedback editor (User 2026-10-30).
 *
 * A clone of the game's own map button (top right): the same grey tile and frame, hover behaviour and
 * screen anchoring (AspectPosition); only the map glyph is swapped for a bug drawn here in the grey and
 * dark outline of the game's corner icons. It sits right under the map button. Shown in the lobby and
 * during a round; hidden in meetings and exile screens, while the editor is open and whenever the
 * settings button is hidden (intro, cutscenes).
 */

using System;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff;

internal static class FeedbackButton
{
    private static GameObject button;
    private static HudManager builtFor;
    private static Sprite sprite;

    internal static bool Visible => button != null && button.activeSelf;

    internal static void Tick()
    {
        var hud = HudManager.Instance;
        if (hud == null || FeedbackModule.ShowButton?.Value != true) { Hide(); return; }
        if (builtFor != hud || button == null) Build(hud);
        if (button == null) return;
        bool show = false;
        try {
            show = PlayerControl.LocalPlayer != null && !FeedbackPanel.IsOpen
                   && MeetingHud.Instance == null && ExileController.Instance == null
                   && hud.SettingsButton != null && hud.SettingsButton.gameObject.activeInHierarchy;
        } catch { }
        if (button.activeSelf != show) button.SetActive(show);
    }

    internal static void HideNow() => Hide();

    private static void Hide()
    {
        if (button != null && button.activeSelf) button.SetActive(false);
    }

    private static void Build(HudManager hud)
    {
        builtFor = hud;
        button = null;
        try {
            var src = hud.MapButton.gameObject;
            bool wasActive = src.activeSelf;
            button = UnityEngine.Object.Instantiate(src, src.transform.parent);
            button.name = "UTSFeedbackButton";
            button.SetActive(false);

            var pb = button.GetComponent<PassiveButton>();
            if (pb != null) {
                pb.OnClick = new Button.ButtonClickedEvent();
                pb.OnClick.AddListener((Action)OnClick);
            }

            // keep the tile ("Background"), swap every other picture (the map glyph and its hover variant)
            var glyph = Glyph();
            float tileH = 0.7f;
            var parts = new System.Text.StringBuilder();
            foreach (var sr in button.GetComponentsInChildren<SpriteRenderer>(true)) {
                var old = sr.sprite;
                parts.Append($" {sr.gameObject.name}:{(old != null ? old.name : "-")}");
                if (sr.gameObject.name.IndexOf("InputDisplay", StringComparison.OrdinalIgnoreCase) >= 0) {
                    sr.gameObject.SetActive(false);   // the controller hint glyph
                    continue;
                }
                if (sr.gameObject.name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0) {
                    if (old != null) tileH = old.bounds.size.y * sr.transform.lossyScale.y;
                    continue;
                }
                float size = old != null ? Mathf.Max(old.bounds.size.x, old.bounds.size.y) : 0.5f;
                sr.sprite = Sized(glyph, size);
            }

            // right under the map button
            var ap = button.GetComponent<AspectPosition>();
            var srcAp = src.GetComponent<AspectPosition>();
            if (ap != null && srcAp != null) {
                ap.DistanceFromEdge = srcAp.DistanceFromEdge + new Vector3(0f, tileH + 0.08f, 0f);
                ap.AdjustPosition();
            }
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] bug button built (map button was {(wasActive ? "on" : "off")}, tile {tileH:0.00},{parts})");
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] bug button failed: {e.Message}");
            if (button != null) UnityEngine.Object.Destroy(button);
            button = null;
        }
    }

    private static void OnClick()
    {
        if (FeedbackPanel.IsOpen || !FeedbackPanel.CanOpen()) return;
        FeedbackPanel.Open();
    }

    // ---- glyph ----

    private static Texture2D tex;
    private const int N = 160;

    private static Sprite Glyph()
    {
        if (sprite != null) return sprite;
        tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[N * N];
        // the grey and dark outline of the game's corner glyphs (chat, gear, map)
        var white = new Color(0.47f, 0.48f, 0.50f);
        var ink = new Color(0.17f, 0.17f, 0.19f);
        const float outline = 4.5f;
        var tileLight = new Color(0.86f, 0.87f, 0.89f);
        var tileDark = new Color(0.76f, 0.77f, 0.80f);
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++) {
            // the light inner tile of the game's corner icons, the bug on it at 70 %
            float tx = x + 0.5f - N * 0.5f, ty = y + 0.5f - N * 0.5f;
            float tile = RoundRect(tx, ty, N * 0.47f, N * 0.47f, N * 0.06f);
            float tileA = Mathf.Clamp01(0.5f - tile);
            float tileShade = Mathf.Clamp01((ty + N * 0.47f) / (N * 0.94f));       // a touch darker at the bottom
            var c = Color.Lerp(tileDark, tileLight, tileShade);
            const float k = 0.70f;
            float bx = N * 0.5f + tx / k, by = N * 0.5f + ty / k;
            float d = Bug(bx, by) * k;
            float fill = Mathf.Clamp01(0.5f - d);                 // inside the bug
            float edge = Mathf.Clamp01(0.5f - (d - outline));     // inside bug + outline
            float seam = Mathf.Clamp01(1.6f - Seam(bx, by) * k);
            var bug = Color.Lerp(ink, white, fill * (1f - seam));
            c = Color.Lerp(c, bug, edge);
            c.a = tileA;
            px[y * N + x] = c;
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        sprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static readonly System.Collections.Generic.Dictionary<int, Sprite> sized = new();

    /// <summary>Same picture, pixels-per-unit chosen so it fills <paramref name="size"/> world units like the gear did.</summary>
    private static Sprite Sized(Sprite s, float size)
    {
        int k = Mathf.RoundToInt(size * 1000f);
        if (sized.TryGetValue(k, out var hit) && hit != null) return hit;
        var sp = Sprite.Create(s.texture, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N / Mathf.Max(0.05f, size));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        sized[k] = sp;
        return sp;
    }

    /// <summary>Signed distance (pixels) to the bug: oval body, round head, six legs, two feelers.</summary>
    private static float Bug(float x, float y)
    {
        float cx = N * 0.5f;
        float d = Ellipse(x - cx, y - N * 0.42f, 34f, 44f);                     // body
        d = Mathf.Min(d, Circle(x - cx, y - N * 0.74f, 19f));                   // head
        foreach (int s in new[] { -1, 1 }) {
            // legs: upper, middle, lower, each a bent stroke out of the body
            d = Mathf.Min(d, Stroke(x, y, cx + s * 30f, N * 0.56f, cx + s * 56f, N * 0.66f, 5f));
            d = Mathf.Min(d, Stroke(x, y, cx + s * 56f, N * 0.66f, cx + s * 62f, N * 0.78f, 5f));
            d = Mathf.Min(d, Stroke(x, y, cx + s * 32f, N * 0.42f, cx + s * 64f, N * 0.42f, 5f));
            d = Mathf.Min(d, Stroke(x, y, cx + s * 30f, N * 0.28f, cx + s * 54f, N * 0.18f, 5f));
            d = Mathf.Min(d, Stroke(x, y, cx + s * 54f, N * 0.18f, cx + s * 58f, N * 0.07f, 5f));
            // feelers
            d = Mathf.Min(d, Stroke(x, y, cx + s * 8f, N * 0.84f, cx + s * 22f, N * 0.95f, 4f));
        }
        return d;
    }

    /// <summary>Distance to the dark line between head and body and down the middle of the shell.</summary>
    private static float Seam(float x, float y)
    {
        float cx = N * 0.5f;
        float mid = (y < N * 0.62f && y > N * 0.12f) ? Mathf.Abs(x - cx) : 99f;
        float neck = (Mathf.Abs(x - cx) < 26f) ? Mathf.Abs(y - N * 0.63f) : 99f;
        return Mathf.Min(mid, neck);
    }

    private static float RoundRect(float x, float y, float hw, float hh, float r)
    {
        float qx = Mathf.Abs(x) - hw + r, qy = Mathf.Abs(y) - hh + r;
        float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    private static float Circle(float x, float y, float r) => Mathf.Sqrt(x * x + y * y) - r;

    private static float Ellipse(float x, float y, float a, float b)
    {
        // good enough for a near-circle: scale to a unit circle, back to pixels with the smaller radius
        float k = Mathf.Sqrt(x * x / (a * a) + y * y / (b * b));
        return (k - 1f) * Mathf.Min(a, b);
    }

    private static float Stroke(float px, float py, float ax, float ay, float bx, float by, float r)
    {
        float vx = bx - ax, vy = by - ay, wx = px - ax, wy = py - ay;
        float t = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
        float dx = wx - vx * t, dy = wy - vy * t;
        return Mathf.Sqrt(dx * dx + dy * dy) - r;
    }
}
