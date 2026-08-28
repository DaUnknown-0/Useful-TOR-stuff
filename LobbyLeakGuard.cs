// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbyLeakGuard - the lobby screen must not survive into a running round.
 *
 * THE FAILURE THIS ANSWERS (observed 2026-08-14, repeatedly)
 * GameStartManager.Start threw a native NullReference on this install (12 times in one session),
 * which leaves the lobby screen half-initialised. From then on two things happen at once:
 *
 *   1. TOR's GameStartManagerUpdatePatch.Postfix throws EVERY FRAME (1116x, and 18k+ in an earlier
 *      session) over the broken instance - an exception storm through an Il2Cpp trampoline that
 *      degrades the whole client.
 *   2. The GameStartManager object itself KEEPS RUNNING INTO THE ROUND (the storm's log lines start
 *      thousands of lines after "Starting intro cutscene"). A live lobby screen inside a round is
 *      exactly the state in which the lobby chat stays openable mid-round - the reported bug.
 *
 * THE GUARD, three layers, all defensive and all no-ops in a healthy game:
 *
 *   A. Finalizers on GameStartManager.Start/Update swallow the exceptions (first one logged). This
 *      does not fix TOR's null access - it stops one broken frame from cascading into thousands.
 *   B. Once a round actually runs (ShipStatus exists), a surviving GameStartManager is destroyed.
 *      In a healthy game it is gone by then anyway, so this only ever removes a leak.
 *   C. The chat clamp: during a running round the chat button is hidden for LIVING players unless
 *      TOR itself wants it open (lovers with lover chat, FreePlay). Ghosts may always chat - that
 *      is vanilla. This mirrors the exact condition of TOR's own EnableChat patch
 *      (Modules/ChatCommands.cs:105-110), just in the opposite direction: TOR only ever turns the
 *      chat ON and relies on the game never having it on elsewhere - which a leaked lobby breaks.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {

    /// The one safe way to ask "is there a lobby screen?".
    ///
    /// NEVER read GameStartManager.Instance as a question. The getter is Among Us'
    /// DestroyableSingleton, and a DestroyableSingleton getter CONSTRUCTS an instance when none
    /// exists - a blank GameStartManager with every serialized field null, whose Start() and
    /// Update() then throw natively every frame. The 2026-08-14 log shows exactly that: a
    /// GameStartManager.Start NullReference at BOOT, before the main menu even loaded, in every
    /// session since the first build that polled the getter from a menu-time component
    /// (v1.3.3.15), and none before. That phantom object is the strongest suspect behind the
    /// degraded rounds (permanently openable chat, uncallable meetings, dead decon doors) that
    /// vanish when this mod is removed. InstanceExists reads the backing field and constructs
    /// nothing; the real lobby screen registers itself there in Awake, so the answer is right
    /// whenever it matters.
    public static class LobbyScreen {
        public static bool Exists {
            get {
                try { return DestroyableSingleton<GameStartManager>.InstanceExists; }
                catch { return false; }
            }
        }

        /// The instance, or null - guaranteed not to construct one.
        public static GameStartManager InstanceOrNull() {
            try { return Exists ? DestroyableSingleton<GameStartManager>._instance : null; }
            catch { return null; }
        }
    }

    public static class LobbyLeakGuard {

        // Log each distinct failure once, not per frame - the per-frame log spam was half the damage.
        private static bool loggedStart;
        private static bool loggedUpdate;
        private static bool loggedDestroy;
        private static bool loggedChatClamp;
        private static bool loggedClampStuck;

        // Edge memory for the chat clamp, kept HERE rather than read back out of the game: see the
        // long note in HudUpdatePatch. `buttonWasShown` is what makes the clamp fire once per
        // appearance instead of once per frame; `windowClamps` caps a ForceClosed that never takes.
        private static bool buttonWasShown;
        private static int windowClamps;
        private const int MaxWindowClamps = 8;

        private static bool InRound() =>
            ShipStatus.Instance != null && AmongUsClient.Instance != null
            && AmongUsClient.Instance.IsGameStarted;

        // ---- A: stop the exception storms ----
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
        static class StartFinalizerPatch {
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null && !loggedStart) {
                    loggedStart = true;
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[LobbyLeakGuard] GameStartManager.Start threw ({__exception.Message}) - "
                        + "suppressed. The lobby screen may be degraded; the in-round cleanup below covers the leak.");
                }
                return null; // swallow - a half-broken lobby beats a cascading one
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        static class UpdateFinalizerPatch {
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null && !loggedUpdate) {
                    loggedUpdate = true;
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[LobbyLeakGuard] GameStartManager.Update threw ({__exception.Message}) - "
                        + "suppressed (this would otherwise repeat every frame).");
                }
                return null;
            }
        }

        // The lobby countdown RPC lands on the same broken instance (observed: one native NRE in
        // SetStartCounter out of PlayerControl.HandleRpc). Left unguarded it would abort the whole
        // HandleRpc for that message.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.SetStartCounter))]
        static class SetStartCounterFinalizerPatch {
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[LobbyLeakGuard] SetStartCounter threw ({__exception.Message}) - suppressed.");
                return null;
            }
        }

        // ---- B + C: per-frame corrections, cheap checks first ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (!InRound()) {
                        loggedDestroy = false; loggedChatClamp = false; loggedClampStuck = false;
                        buttonWasShown = false; windowClamps = 0;
                        return;
                    }

                    // B: the lobby screen has no business existing in a round. Asked through the
                    // side-effect-free helper: the previous build read GameStartManager.Instance
                    // here EVERY ROUND FRAME, and since that getter constructs a blank instance
                    // when none exists, destroy-then-recreate could churn a fresh broken
                    // GameStartManager every frame of the round. See LobbyScreen above.
                    var gsm = LobbyScreen.InstanceOrNull();
                    if (gsm != null) {
                        if (!loggedDestroy) {
                            loggedDestroy = true;
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[LobbyLeakGuard] GameStartManager survived into the round - destroying the leak.");
                        }
                        try { UnityEngine.Object.Destroy(gsm.gameObject); } catch { }
                    }

                    // C: chat clamp for the living. Mirrors TOR's EnableChat conditions inverted;
                    // meetings and ghosts keep their chat, exactly as vanilla wants it.
                    if (MeetingHud.Instance != null || ExileController.Instance != null) return;
                    var me = PlayerControl.LocalPlayer;
                    if (me == null || me.Data == null || me.Data.IsDead) return;
                    if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay) return;
                    if ((me == Lovers.lover1 || me == Lovers.lover2) && Lovers.enableChat) return;

                    var chat = __instance.Chat;
                    if (chat == null) return;

                    /*
                     * EDGE-TRIGGERED, and that word is load-bearing - "idempotent" was the word
                     * here before, and it was wrong.
                     *
                     * The first build called SetVisible(false) every frame because
                     * chat.isActiveAndEnabled is true for the whole round. The 2026-08-14 fix tried
                     * to bound that by acting "only when there is visibly something to clamp", i.e.
                     * while chat.chatButton.gameObject.activeSelf reads true. IT READS TRUE AFTER
                     * THE CALL: SetVisible(false) does not clear that flag on this install, so the
                     * condition never became false and the clamp still ran on EVERY FRAME - 4662
                     * calls in 78 seconds, measured off the 2026-08-23 log, i.e. once per frame from
                     * round start onwards. Each call logs "Chat is hidden" and walks
                     * ControllerManager through QuickChatMenu/BanMenu/ChatUi CloseOverlayMenu, 14
                     * log lines a frame. Both hard client crashes in that log (2026-08-23 19:18:38
                     * and 2026-08-24 14:24:54) died mid-write inside exactly this churn, and they
                     * are the only two in ten days of play.
                     *
                     * A flag the call does not change cannot be the stop condition. So the trigger
                     * is now an edge remembered in OUR OWN field: clamp when the chat becomes
                     * visible, then stay quiet until it has actually gone away again. If the clamp
                     * is powerless here (the button never goes away), that costs one call per round
                     * instead of sixty per second - and hammering sixty times a second would not
                     * have made it work either, only killed the client.
                     */
                    bool buttonShown = false, windowOpen = false;
                    try { buttonShown = chat.chatButton != null && chat.chatButton.gameObject.activeSelf; } catch { }
                    try { windowOpen = chat.IsOpenOrOpening; } catch { }

                    // The open window first, and on its own budget: ForceClosed genuinely closes it,
                    // so this is self-limiting and a re-opened window gets clamped again. The cap is
                    // only there in case it does not take, so that case cannot become a new loop.
                    if (windowOpen) {
                        if (windowClamps < MaxWindowClamps) {
                            windowClamps++;
                            try { chat.ForceClosed(); } catch { }
                        } else if (!loggedClampStuck) {
                            loggedClampStuck = true;
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[LobbyLeakGuard] the chat window stayed open through "
                                + $"{MaxWindowClamps} ForceClosed calls - giving up for this round "
                                + "rather than retrying every frame.");
                        }
                    } else {
                        windowClamps = 0;
                    }

                    // The button: one call per appearance, never per frame.
                    if (!buttonShown) { buttonWasShown = false; return; }
                    if (buttonWasShown) return;
                    buttonWasShown = true;

                    if (!loggedChatClamp) {
                        loggedChatClamp = true;
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[LobbyLeakGuard] mid-round chat was visible for a living player - clamping "
                            + $"(button={buttonShown}, window={windowOpen}).");
                    }
                    chat.SetVisible(false);
                } catch { }
            }
        }
    }
}
