// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * LobbyPanelGuard - the game cannot be started through an open UTS lobby panel.
 *
 * The lobby panels (newcomer shield, early-death shield and stats, session stats, round replay) are
 * screen-space UGUI canvases. Their backdrop swallows UGUI clicks, but Among Us' own buttons are
 * collider-based PassiveButtons and never see that canvas: a click on the panels' Close button, which
 * sits right above the host's Start button, also started the game (User 2026-10-02).
 *
 * While a panel is open, and for a short moment after one closes (the closing click may be handled
 * before or after the Start button's in the same frame), GameStartManager.BeginGame is skipped.
 * The Start button's collider is switched off while blocking, so the click never reaches
 * BeginGame: HarmonyX would still run every other BeginGame prefix, TOR's dynamic-map roll included.
 * The BeginGame prefix stays as a fallback; returning false there only skips the original.
 */

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class LobbyPanelGuard {
        private const float Grace = 0.5f;
        private static readonly List<GameObject> panels = new List<GameObject>();
        private static float closedAt = -10f;

        /// <summary>Called where a panel root is created.</summary>
        internal static void Track(GameObject panel) {
            if (panel != null) panels.Add(panel);
        }

        /// <summary>Called from every Close; starts the grace window.</summary>
        internal static void Closed() => closedAt = Time.unscaledTime;

        internal static bool Blocking() {
            if (Time.unscaledTime - closedAt < Grace) return true;
            panels.RemoveAll(p => p == null);   // Unity null: destroyed panels drop out
            return panels.Count > 0;
        }

        // The click must not even reach BeginGame: HarmonyX runs every BeginGame prefix, and TOR's
        // rolls a new dynamic map (and may switch the preset) even when the start is refused below.
        // So the Start button's collider is switched off while blocking; only what we switched off
        // is switched back on. The prefix stays as the last line of defence (hotkey, other callers).
        private static bool colliderOffByUs;

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        static class StartButtonPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(GameStartManager __instance) {
                try {
                    if (__instance == null || __instance.StartButton == null) return;
                    bool block = Blocking();
                    if (!block && !colliderOffByUs) return;
                    var col = __instance.StartButton.GetComponent<Collider2D>();
                    if (col == null) return;
                    if (col.enabled == block) col.enabled = !block;
                    colliderOffByUs = block;
                } catch { }
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class BeginGamePatch {
            public static bool Prefix() {
                if (!Blocking()) return true;
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPanelGuard] start ignored: a lobby panel is open.");
                return false;
            }
        }
    }
}
