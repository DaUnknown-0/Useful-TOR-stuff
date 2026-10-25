// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * RoundReplayMarkers - fixed points of another mod on the replay map, each with a value per player (User 2026-10-25:
 * a map object's state towards every player, visible in the replay for every player and every object).
 *
 * CONTRACT (AppDomain data, no assembly reference either way; both keys are read once per round at its start):
 *   "UTS.ReplayMarkers.Layout"  Func<string>   null or "" = no markers this round. Lines separated by '\n':
 *                                              line 1: "<legend title>|<stage>|<stage>..." (stages 0..1, drawn as ticks)
 *                                              then one line per marker: "<name>|<world x>|<world y>"
 *   "UTS.ReplayMarkers.Values"  Func<byte, float[]>   called at every replay sample for every living player:
 *                                              his value per marker (0..1, same order as the layout), or null = all 0
 * Every client records them locally like the positions; the providing mod computes the values for all players.
 *
 * VIEW: next to every marker a small bar per player (his colour, height = value) with the stage ticks behind it. With a
 * player chosen in the chips only his bar, wider, with the value in percent.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static partial class RoundReplay {

        private const string MarkerLayoutKey = "UTS.ReplayMarkers.Layout";
        private const string MarkerValuesKey = "UTS.ReplayMarkers.Values";

        private sealed class Marker {
            public string Name;
            public Vector2 Pos;
        }

        private static readonly List<Marker> markers = new List<Marker>();
        private static readonly List<float> markerStages = new List<float>();
        private static string markerTitle = "";
        private static Func<byte, float[]> markerValues;
        // per track: one entry per sample, null = all 0 (or no provider), else the values as 0..255
        private static readonly Dictionary<byte, List<byte[]>> markerRec = new Dictionary<byte, List<byte[]>>();

        private const float MarkerBarH = 30f, MarkerBarW = 5f, MarkerBarWide = 9f;

        private sealed class MarkerUi {
            public RectTransform Root;
            public readonly List<(byte Id, RectTransform Bar, Image Img)> Bars = new List<(byte, RectTransform, Image)>();
            public TMPro.TextMeshProUGUI Pct;
        }
        private static readonly List<MarkerUi> markerUi = new List<MarkerUi>();

        private static void MarkersBegin() {
            markers.Clear(); markerStages.Clear(); markerRec.Clear();
            markerTitle = ""; markerValues = null;
            try {
                var layout = (AppDomain.CurrentDomain.GetData(MarkerLayoutKey) as Func<string>)?.Invoke();
                if (string.IsNullOrEmpty(layout)) return;
                var lines = layout.Split('\n');
                var head = lines[0].Split('|');
                markerTitle = head[0];
                for (int k = 1; k < head.Length; k++)
                    if (float.TryParse(head[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float st)) markerStages.Add(Mathf.Clamp01(st));
                for (int k = 1; k < lines.Length; k++) {
                    var f = lines[k].Split('|');
                    if (f.Length < 3) continue;
                    if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
                    if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
                    markers.Add(new Marker { Name = f[0], Pos = new Vector2(x, y) });
                }
                if (markers.Count == 0) return;
                markerValues = AppDomain.CurrentDomain.GetData(MarkerValuesKey) as Func<byte, float[]>;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] {markers.Count} marker(s) from another mod: {markerTitle}");
            } catch (Exception e) {
                markers.Clear();
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] marker layout failed: {e.Message}");
            }
        }

        // after the positions of this sample (Sample); dead and gone players get null like their NaN position
        private static void MarkersSample() {
            if (markers.Count == 0) return;
            foreach (var t in tracks) {
                if (!markerRec.TryGetValue(t.Id, out var list)) {
                    list = new List<byte[]>();
                    markerRec[t.Id] = list;
                }
                while (list.Count < t.P.Count - 1) list.Add(null);   // late joiners: empty past
                byte[] row = null;
                var last = t.P.Count > 0 ? t.P[t.P.Count - 1] : new Vector2(float.NaN, float.NaN);
                if (markerValues != null && !float.IsNaN(last.x)) {
                    float[] v = null;
                    try { v = markerValues(t.Id); } catch { }
                    if (v != null) {
                        for (int k = 0; k < markers.Count && k < v.Length; k++) {
                            if (v[k] <= 0f) continue;
                            row ??= new byte[markers.Count];
                            row[k] = (byte)Mathf.Clamp(Mathf.RoundToInt(v[k] * 255f), 0, 255);
                        }
                    }
                }
                list.Add(row);
            }
        }

        private static float MarkerValue(byte id, int si, int k) {
            if (!markerRec.TryGetValue(id, out var list) || list.Count == 0) return 0f;
            var row = list[Mathf.Clamp(si, 0, list.Count - 1)];
            return row == null || k >= row.Length ? 0f : row[k] / 255f;
        }

        // in OpenView, above the trail and below the dots
        private static void MarkersBuildUi() {
            markerUi.Clear();
            if (markers.Count == 0 || mapArea == null) return;
            foreach (var m in markers) {
                var ui = new MarkerUi();
                var root = SessionStatsUI.Box(mapArea, Vector2.zero, new Vector2(10, 10), new Color(0.82f, 0.84f, 0.9f, 0.95f));
                ui.Root = root.GetComponent<RectTransform>();
                ui.Root.anchorMin = ui.Root.anchorMax = Vector2.zero; ui.Root.pivot = new Vector2(0.5f, 0.5f);
                ui.Root.localRotation = Quaternion.Euler(0, 0, 45f);     // a small diamond on the spot
                ui.Root.anchoredPosition = ToUi(m.Pos);
                root.GetComponent<Image>().raycastTarget = false;
                // the bars live in an unrotated holder right of the diamond
                var holder = SessionStatsUI.Box(mapArea, Vector2.zero, new Vector2(1, MarkerBarH), new Color(0, 0, 0, 0));
                var hrt = holder.GetComponent<RectTransform>();
                hrt.anchorMin = hrt.anchorMax = Vector2.zero; hrt.pivot = new Vector2(0f, 0.5f);
                hrt.anchoredPosition = ToUi(m.Pos) + new Vector2(9f, 6f);
                holder.GetComponent<Image>().raycastTarget = false;
                foreach (float st in markerStages) {
                    var tick = SessionStatsUI.Box(holder, Vector2.zero, new Vector2(1, 1), new Color(1f, 1f, 1f, 0.35f));
                    var trt = tick.GetComponent<RectTransform>();
                    trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 0); trt.pivot = new Vector2(0, 0.5f);
                    trt.anchoredPosition = new Vector2(0, st * MarkerBarH); trt.sizeDelta = new Vector2(0, 1);
                    tick.GetComponent<Image>().raycastTarget = false;
                }
                foreach (var t in tracks) {
                    var bar = SessionStatsUI.Box(holder, Vector2.zero, new Vector2(MarkerBarW, 0), t.Col);
                    var brt = bar.GetComponent<RectTransform>();
                    brt.anchorMin = brt.anchorMax = brt.pivot = Vector2.zero;
                    var img = bar.GetComponent<Image>();
                    img.raycastTarget = false;
                    bar.SetActive(false);
                    ui.Bars.Add((t.Id, brt, img));
                }
                ui.Pct = SessionStatsUI.Label(holder, "", 12, TMPro.FontStyles.Bold, Color.white, Vector2.zero, Vector2.zero,
                    new Vector2(0f, 0.5f), new Vector2(MarkerBarWide + 3f, MarkerBarH / 2f), new Vector2(60, 16), TMPro.TextAlignmentOptions.Left);
                ui.Pct.enableWordWrapping = false;
                ui.Pct.outlineWidth = 0.25f; ui.Pct.outlineColor = new Color32(0, 0, 0, 255);
                markerUi.Add(ui);
            }
            if (!string.IsNullOrEmpty(markerTitle)) {
                var legend = SessionStatsUI.Label(mapArea, "◆ " + markerTitle, 13, TMPro.FontStyles.Bold, new Color(0.9f, 0.92f, 1f),
                    new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -4), new Vector2(360, 18), TMPro.TextAlignmentOptions.TopLeft);
                legend.enableWordWrapping = false;
                legend.outlineWidth = 0.25f; legend.outlineColor = new Color32(0, 0, 0, 255);
            }
        }

        // every frame (Animate): bars of sample si, only the chosen player's when one is chosen
        private static void MarkersAnimate(int si) {
            for (int k = 0; k < markerUi.Count && k < markers.Count; k++) {
                var ui = markerUi[k];
                float x = 0f, chosen = -1f;
                foreach (var (id, bar, img) in ui.Bars) {
                    float v = MarkerValue(id, si, k);
                    bool show = persp == 255 ? v > 0f : id == persp;
                    if (bar.gameObject.activeSelf != show) bar.gameObject.SetActive(show);
                    if (!show) continue;
                    float w = persp == 255 ? MarkerBarW : MarkerBarWide;
                    bar.anchoredPosition = new Vector2(x, 0f);
                    bar.sizeDelta = new Vector2(w, Mathf.Max(1f, v * MarkerBarH));
                    x += w + 1f;
                    if (id == persp) chosen = v;
                }
                string pct = chosen >= 0f ? $"{Mathf.RoundToInt(chosen * 100f)} %" : "";
                if (ui.Pct != null && ui.Pct.text != pct) ui.Pct.text = pct;
            }
        }

        private static void MarkersDestroy() => markerUi.Clear();
    }
}
