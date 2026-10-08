// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MeetingGridFit - keeps the meeting's voting grid inside its panel with more than 15 players.
 *
 * Vanilla lays the player cards out in 3 columns and room for 5 rows (15 players). With the lobby size
 * option (LobbySizeLimit) a 20 player meeting produced 7 rows: the last row hung below the panel and the
 * Skip Vote button, which sits at a fixed place for 15 players, covered a card (playtest 2026-10-08,
 * 20 dummies, screenshot).
 *
 * From 16 players on, the cards are repacked into more columns (just enough that 5 rows are enough: 4 columns
 * up to 20 players, 5 up to 25, 6 up to 30), scaled by 3 / columns and kept in the same area. Nothing is
 * assumed about the vanilla spacing: the first time a meeting is laid out, the column pitch and the row pitch
 * are MEASURED from the positions the game itself just set, and every later layout call of the same meeting
 * (SortButtons runs again, for example) starts from those measured values again, never from the already
 * repacked ones. Up to 15 players the vanilla layout is not touched at all.
 *
 * Purely local rendering: every client lays out its own meeting, nothing is sent, no gate is needed.
 */

using System;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {
    public static class MeetingGridFit {
        private const int VanillaCapacity = 15;
        private const int Rows = 5;
        // Card width relative to the column pitch in the vanilla layout (cards nearly touch).
        private const float CardWidthOfPitch = 0.92f;

        // The vanilla layout of the meeting that is on screen, measured once.
        private static int measuredHudId;
        private static Vector3 origin;
        private static float pitchX, pitchY;
        private static Vector3[] baseScale;
        private static bool loggedThisMeeting;

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.PopulateButtons))]
        private static class PopulatePatch {
            public static void Postfix() => Fit("PopulateButtons");
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.SortButtons))]
        private static class SortPatch {
            public static void Postfix() => Fit("SortButtons");
        }

        private static void Fit(string from) {
            try {
                var hud = MeetingHud.Instance;
                if (hud == null || hud.playerStates == null) return;
                var ps = hud.playerStates;
                int n = ps.Length;
                if (n <= VanillaCapacity) return;

                int id = hud.GetInstanceID();
                if (id != measuredHudId || baseScale == null || baseScale.Length != n) {
                    // First layout of this meeting: take the game's own positions as the vanilla reference.
                    Vector3 p0 = ps[0].transform.localPosition;
                    Vector3 p1 = ps[1].transform.localPosition;
                    Vector3 p3 = ps[3].transform.localPosition;
                    float px = p1.x - p0.x;
                    float py = p3.y - p0.y;
                    if (px <= 0.01f || py >= -0.01f) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            $"[MeetingGridFit] unexpected vanilla layout (pitch {px:0.###}/{py:0.###}), left alone.");
                        return;
                    }
                    measuredHudId = id;
                    origin = p0;
                    pitchX = px;
                    pitchY = py;
                    baseScale = new Vector3[n];
                    for (int i = 0; i < n; i++) baseScale[i] = ps[i].transform.localScale;
                    loggedThisMeeting = false;
                }

                int cols = Math.Max(4, (n + Rows - 1) / Rows);
                float s = 3f / cols;
                int rows = (n + cols - 1) / cols;

                float w = CardWidthOfPitch * pitchX;               // vanilla card width
                float span = 2f * pitchX + w;                      // vanilla width of the 3 card block
                float w2 = w * s;
                float dx = (span - w2) / (cols - 1);               // new column pitch, same block width
                float x0 = origin.x - w / 2f + w2 / 2f;            // keep the left edge where it was
                // Up to 5 rows keep the vanilla row pitch, more are pressed into the vanilla height.
                float dy = rows <= Rows ? pitchY : pitchY * (Rows - 1f) / (rows - 1f);

                for (int i = 0; i < n; i++) {
                    var t = ps[i].transform;
                    Vector3 cur = t.localPosition;
                    t.localPosition = new Vector3(x0 + (i % cols) * dx, origin.y + (i / cols) * dy, cur.z);
                    t.localScale = new Vector3(baseScale[i].x * s, baseScale[i].y * s, baseScale[i].z);
                }

                if (!loggedThisMeeting) {
                    loggedThisMeeting = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[MeetingGridFit] {n} players ({from}): {cols} columns x {rows} rows, card scale {s:0.##}, " +
                        $"pitch {pitchX:0.###}/{pitchY:0.###} -> {dx:0.###}/{dy:0.###}.");
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[MeetingGridFit] {from} failed: {e.Message}");
            }
        }
    }
}
