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
                    if (!InRound()) { loggedDestroy = false; loggedChatClamp = false; return; }

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

                    // IDEMPOTENT, and that word is load-bearing. chat.isActiveAndEnabled is true
                    // for the entire round (the ChatController component always runs), so the old
                    // guard called SetVisible(false) EVERY FRAME of EVERY round. SetVisible is not
                    // a cheap setter: each call logs "Chat is hidden" and walks ControllerManager
                    // through QuickChatMenu/BanMenu CloseOverlayMenu - the exact per-frame spam
                    // filling the 2026-08-14 log (thousands of lines in seconds), plus per-frame UI
                    // menu-stack churn in a round that is already degraded. So the clamp now acts
                    // only when there is visibly something to clamp: the chat button is shown, or
                    // the chat window is open.
                    bool buttonShown = false, windowOpen = false;
                    try { buttonShown = chat.chatButton != null && chat.chatButton.gameObject.activeSelf; } catch { }
                    try { windowOpen = chat.IsOpenOrOpening; } catch { }
                    if (!buttonShown && !windowOpen) return;

                    if (!loggedChatClamp) {
                        loggedChatClamp = true;
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[LobbyLeakGuard] mid-round chat was visible for a living player - clamping "
                            + $"(button={buttonShown}, window={windowOpen}).");
                    }
                    chat.SetVisible(false);
                    // SetVisible only governs the button; a window that is already open stays open
                    // without this.
                    if (windowOpen) { try { chat.ForceClosed(); } catch { } }
                } catch { }
            }
        }
    }
}
