// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    /// <summary>
    /// Whether the round intro (team screen, TOR's Role Draft) is on screen. IntroCutscene.Instance is
    /// NOT reliable in this game build: in an online round on 2026-10-16 it stayed null for the whole
    /// intro, so the draft music never swapped and the round replay started recording mid-intro.
    /// Three sources, any one is enough: Instance, HudManager.IsIntroDisplayed, and the IntroCutscene
    /// object itself, looked up at most twice a second while a round runs and only until this round's
    /// intro has been seen to end.
    /// </summary>
    internal static class IntroScreen {
        private static IntroCutscene found;
        private static float searchAt;
        private static bool seenThisRound, overThisRound;

        internal static bool Exists {
            get {
                try { if (IntroCutscene.Instance != null) return true; } catch { }
                try {
                    if (DestroyableSingleton<HudManager>.InstanceExists && DestroyableSingleton<HudManager>._instance.IsIntroDisplayed)
                        return true;
                } catch { }
                return Found() != null;
            }
        }

        /// The intro object, or null (for callers that need its texts).
        internal static IntroCutscene Instance {
            get {
                try { if (IntroCutscene.Instance != null) return IntroCutscene.Instance; } catch { }
                return Found();
            }
        }

        private static IntroCutscene Found() {
            try {
                if (found != null) return found;
                if (seenThisRound) { seenThisRound = false; overThisRound = true; found = null; }
                var client = AmongUsClient.Instance;
                if (client == null || client.GameState != InnerNet.InnerNetClient.GameStates.Started) {
                    overThisRound = false;   // next round: look again
                    return null;
                }
                if (overThisRound || Time.realtimeSinceStartup < searchAt) return null;
                searchAt = Time.realtimeSinceStartup + 0.5f;
                found = Object.FindObjectOfType<IntroCutscene>();
                if (found != null) seenThisRound = true;
                return found;
            } catch { found = null; return null; }
        }

        // A new round (or a new lobby): look for the intro again. The getter's own reset above only fired when it was
        // asked outside a started game, and every caller already stops there itself, so from round 2 on the fallback
        // never searched again (Opus audit 2026-10-21, M1).
        private static void NewRound() { found = null; seenThisRound = false; overThisRound = false; searchAt = 0f; }

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
        static class ShipStartPatch { public static void Postfix() => NewRound(); }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinedPatch { public static void Postfix() => NewRound(); }
    }
}
