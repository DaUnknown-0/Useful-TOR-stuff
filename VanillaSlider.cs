// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * VanillaSlider - a horizontal slider in the VanillaUI look: a dark capsule track, a coloured fill and a white
 * knob with the game's heavy outline. Value 0..1.
 *
 * Dragging is polled (Tick, from the owning panel's Update) instead of Unity's drag interfaces, which an
 * injected Il2Cpp class cannot implement without extra plumbing. A press on the track (a little padding
 * around it counts) grabs the knob; while the button is held the value follows the mouse, onChange fires on
 * every change, onRelease once at the end (the place to save).
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UsefulTORStuff {

    internal sealed class VanillaSlider {
        public RectTransform Track, Fill, Knob;
        public float Value;
        public Action<float> OnChange, OnRelease;
        private float width;

        private static readonly List<VanillaSlider> live = new List<VanillaSlider>();
        private static VanillaSlider dragging;

        /// A slider whose top-left corner sits at topLeft inside parent.
        public static VanillaSlider Create(GameObject parent, Vector2 topLeft, float width, float height, float value, Color color,
                                           Action<float> onChange, Action<float> onRelease) {
            var s = new VanillaSlider { Value = Mathf.Clamp01(value), OnChange = onChange, OnRelease = onRelease, width = width };
            int r = Mathf.Max(2, Mathf.RoundToInt(height / 2f));

            var track = VanillaUI.Rounded(parent, new Color(0.06f, 0.07f, 0.10f), r);
            track.name = "SliderTrack";
            s.Track = VanillaUI.Rect(track, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            s.Track.anchoredPosition = topLeft;
            s.Track.sizeDelta = new Vector2(width, height);

            var fill = VanillaUI.Rounded(track, color, r);
            s.Fill = VanillaUI.Rect(fill, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f));
            s.Fill.anchoredPosition = Vector2.zero;

            float k = height + 14f;
            var knob = VanillaUI.Rounded(track, VanillaUI.Frame, Mathf.RoundToInt(k / 2f));
            knob.name = "SliderKnob";
            s.Knob = VanillaUI.Rect(knob, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f));
            s.Knob.sizeDelta = new Vector2(k, k);
            var dot = VanillaUI.Rounded(knob, Color.white, Mathf.RoundToInt(k / 2f) - 3);
            var drt = VanillaUI.Stretch(dot);
            drt.offsetMin = new Vector2(4, 4); drt.offsetMax = new Vector2(-4, -4);

            s.Layout();
            live.Add(s);
            return s;
        }

        private void Layout() {
            float x = Value * width;
            Fill.sizeDelta = new Vector2(Mathf.Max(Track.sizeDelta.y, x), 0f);
            Knob.anchoredPosition = new Vector2(x, 0f);
        }

        private bool Hit(Vector2 mouse) {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Track, mouse, null, out var p)) return false;
            // local coordinates relative to the pivot (top-left): x 0..width, y -height..0; 18 px of padding
            return p.x >= -18f && p.x <= width + 18f && p.y >= -Track.sizeDelta.y - 18f && p.y <= 18f;
        }

        private void Follow(Vector2 mouse) {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Track, mouse, null, out var p)) return;
            float v = Mathf.Clamp01(p.x / width);
            if (Mathf.Approximately(v, Value)) return;
            Value = v;
            Layout();
            try { OnChange?.Invoke(v); } catch { }
        }

        /// From the owning panel's Update.
        public static void Tick() {
            live.RemoveAll(s => s.Track == null);
            Vector2 mouse = Input.mousePosition;
            if (dragging == null && Input.GetMouseButtonDown(0)) {
                foreach (var s in live) if (s.Hit(mouse)) { dragging = s; break; }
            }
            if (dragging == null) return;
            if (dragging.Track == null) { dragging = null; return; }
            if (Input.GetMouseButton(0)) { dragging.Follow(mouse); return; }
            var done = dragging;
            dragging = null;
            try { done.OnRelease?.Invoke(done.Value); } catch { }
        }

        /// While a slider is held, the panel must not rebuild under it.
        public static bool Dragging => dragging != null;
    }
}
