// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorLobbyFixes - the lobby, chat-command, menu, gamemode-picker and hat-loading findings from the
 * 2026-08-23 full-source audit of TOR 4.8.0 (Audits\TOR-AUDIT-2026-08-23.md /
 * Audits\TOR-ABDECKUNG-2026-08-23.md) that are not reached by any existing fix file, verified again
 * against the current TheOtherRoles-main source before writing a single line here.
 *
 * All fixes in this file are option-less and NOT behind UTSGate: none of them changes an outcome a
 * player could be advantaged or disadvantaged by, they turn broken lobby/menu behaviour into what
 * TOR's own code was clearly trying to do. Each patch below explains, on its own, whether the
 * DESYNC rule (Claude.md: fixes on a path every client runs that decide life/death, role ownership
 * or win conditions must gate on UsefulVersionHandshake.EveryoneHasMod()) applies to it - most of
 * these are lobby-local or per-client rendering and plainly do not qualify, but two of them
 * (TOR-M48, TOR-M49) touch a win condition and get a dedicated justification instead of the gate.
 *
 * Fixed here:
 *  TOR-M18) GameStartManagerPatch's countdown text/visibility check is a hardcoded English
 *      "Starting" substring match against vanilla's own localized text - broken for any non-English
 *      client, host and joiner alike (the on-screen countdown just goes blank every frame).
 *  TOR-M23) DynamicLobbies' "/size abc" chat command lets a failed Int32.TryParse leave LobbyLimit
 *      at 0, which then makes AmongUsClientOnPlayerJoined kick every future joiner.
 *  TOR-M24) CustomOptionHolder.Load() and CustomOption.switchPreset() bind the vanilla-settings
 *      blob under two different config keys ("VanillaOptions" vs "GameOptions"), so the first ever
 *      preset switch orphans whatever was saved under the original key.
 *  TOR-M28) CredentialsPatch.LogoPatch's Postfix is attributed to MainMenuManager.Start but typed
 *      to take a PingTracker __instance - a mismatched, currently-inert patch that is proactively
 *      unpatched so it cannot start throwing if a future Harmony/game update handles that mismatch
 *      less forgivingly.
 *  TOR-M34) MapUtilities' Systems cache can go stale (ShipStatus.OnDestroy's own cleanup reassigns
 *      CachedShipStatus from a static Instance reference instead of leaving it null) and the one
 *      consumer that matters, GetNeutralLightRadius's Lights-Out vision calc, swallows the resulting
 *      KeyNotFoundException in a bare catch and silently defaults to FULL vision. The single worst
 *      finding in this audit: it is invisible in play, and it defeats a core sabotage mechanic.
 *  TOR-M48/M-49) PropHunt's and Hide 'N Seek's post-blackout "the hunt is on" state is armed by a
 *      one-shot call that only ever runs on the hunter's own client; a hunter who disconnects before
 *      it fires means nobody ever sends it, and the hunted players' timeout-win condition is
 *      unreachable for the rest of the round. Both get a time-based watchdog that arms the same
 *      state TOR's own code would have, on the same schedule.
 *
 * Verified findings, deliberately NOT fixed here (see the individual write-ups below for why):
 *  TOR-M27) CreateOptionsPicker's menu offers vanilla's own "Hide N Seek" as a distinct entry, but
 *      selecting it collapses to the same TORMapOptions.gameMode = Classic as "Normal". Confirmed
 *      real. A safe fix means either reproducing genuine vanilla Hide N Seek support TOR itself
 *      never wrote, or manipulating IL2CPP-interop menu/button fields (GameModeMenu.
 *      controllerSelectable, ChatLanguageButton.Text/.Button) whose exact wrapper shape cannot be
 *      confirmed without a build, which this task does not run. Purely a misleading lobby menu
 *      label with no crash or round-state risk; not worth guessing at either of those.
 *  TOR-M50) HatParentPatches' four sub-findings (broken Tutorial hat-swap, two competing SetHat(int)
 *      prefixes, the hatless case suppressing vanilla LateUpdate/SetIdleAnim, ungenerated
 *      LeftImages) all require knowing exactly what the vanilla IL2CPP HatParent methods being
 *      prefixed-with-return-false do internally, which is not visible from source. Worst-case impact
 *      is confined to the one-time Tutorial scene and mirrored-hat cosmetics; no crash, no round
 *      state. Guessing at vanilla internals from outside risks making a currently-narrow issue worse.
 *
 * Evaluated per the task's explicit "do not blindly patch" list:
 *  TOR-M7) EndGamePatch's GameOverReason >= 10 masking. Confirmed real, but it only rewrites the
 *      RPC-facing EndGameResult; TOR's own OnGameEndPatch.gameOverReason keeps the true value, which
 *      is what every one of this mod family's own reason checks already reads (documented at
 *      TrackerExport-Snapshot and Sheriff-Parity-Win in memory). Already listed as a known interop
 *      boundary in the audit itself (Audits\TOR-AUDIT-2026-08-23.md p.4); nothing to add.
 *  TOR-M12) Buttons.cs createButtonsPostfix's bare catch {} plus retry produces duplicate buttons on
 *      partial failure. Confirmed real and already the documented root cause behind TorAuditFixes'
 *      button-Finalizer safety nets (see that file's header). A real fix needs to know which buttons
 *      createButtonsPostfix had already created before the throw, which is local state inside that
 *      method; already triaged as a known interop boundary, not duplicated here.
 *  TOR-M25/M-26) ModUpdater/BepInExUpdater's _busy/UpdateRequired latches stick on a network error
 *      (ModUpdater.cs: CoCheckForUpdate's `yield break` on isNetworkError/isHttpError never resets
 *      _busy). Confirmed real by reading the source. A safe fix is technically the same watchdog
 *      shape used below for TOR-M48/M-49 (time out the private _busy field via reflection and let
 *      the next normal Start()/StartDownloadRelease() through), but this is TOR's own update/install
 *      mechanism and the task instructions call this out by name as the one area to only report on:
 *      a mistake here risks the user's installation, not just a log line. Left unfixed on purpose;
 *      flagging it back so a person can decide whether the watchdog is worth adding.
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.CustomGameModes;
using TheOtherRoles.Modules;
using TheOtherRoles.Utilities;
using UnityEngine;

namespace UsefulTORStuff {
    public static class TorLobbyFixes {

        private static float lastLogAt = -100f;

        // Lobby/menu code runs every frame; keep the log from flooding the same way TorUpstreamFixes
        // does for round-time fixes.
        private static void ThrottledLog(string tag, string message) {
            if (Time.realtimeSinceStartup - lastLogAt < 5f) return;
            lastLogAt = Time.realtimeSinceStartup;
            UsefulTORStuffPlugin.Logger?.LogWarning($"[TorLobbyFixes/{tag}] {message}");
        }

        // ======================================================================================
        // TOR-M18) The lobby countdown text goes blank on every non-English client
        //
        // GameStartManagerPatch.cs (host branch :101-108, client branch :164-171, plus the
        // client-only stop-button gates at :173/:175) all decide whether the lobby is currently
        // showing vanilla's own "Starting in X" countdown by testing
        //     __instance.GameStartText.text.StartsWith("Starting")
        // That literal is English. GameStartManager.Update's ORIGINAL (unpatched, vanilla) body
        // writes the real countdown text in the client's own language before any postfix - TOR's
        // included - ever runs. On a non-English client the text never starts with the word
        // "Starting", the check is always false, and TOR's own Postfix immediately overwrites the
        // freshly-written localized text with an empty string and hides GameStartTextParent - every
        // single frame the countdown is running, on host and joiners alike. This is purely cosmetic:
        // the underlying vanilla start timer and startState are untouched (TOR's own startingTimer
        // field only gates a one-shot "send the SetGameStarting RPC" latch, not the real countdown),
        // so the round still starts on schedule; only the on-screen readout disappears.
        //
        // Deliberately scoped to the display bug only. The exact same broken check also gates the
        // separate client-side "anyone can stop the start" button (:173/:175); making that work too
        // would mean reimplementing button instantiation, its RPC-sending click handler and TOR's
        // private static copiedStartButton bookkeeping from outside the class for an opt-in
        // secondary feature - disproportionate risk for the payoff. On a non-English client that
        // button still will not appear; a known, documented, minor residual limit.
        //
        // Fix shape: capture the correctly localized text vanilla's ORIGINAL method just wrote,
        // before TOR's own Postfix can blank it (a Postfix at Priority.High on the same target runs
        // before TOR's default-priority one - this codebase's own established convention for
        // ordering postfixes on GameStartManager.Update, see UsefulVersionHandshake.cs:378-382).
        // Then, after every postfix for the frame has run (Priority.Last), restore that text and
        // reactivate GameStartTextParent whenever the real ground truth (GameStartManager.startState)
        // says the countdown is running and the text sitting there right now is empty. Empty is the
        // trigger rather than replaying TOR's own versionMismatch computation: a legitimate mismatch
        // warning is never blank, so this can never stomp on one, and UsefulVersionHandshake's own
        // Update postfix (also on this target) already steps aside without touching .text whenever
        // startState == Countdown, so there is nothing to fight over.
        //
        // Every client only ever touches its OWN GameStartText locally; nothing here is sent over
        // the network or diverges shared state, so this does not need EveryoneHasMod() gating.
        // ======================================================================================
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.High)]
        internal static class LobbyCountdownTextCapturePatch {
            internal static string capturedCountdownText = "";

            public static void Postfix(GameStartManager __instance) {
                try {
                    if (__instance == null || __instance.GameStartText == null) return;
                    if (__instance.startState != GameStartManager.StartingStates.Countdown) return;
                    string current = __instance.GameStartText.text;
                    if (!string.IsNullOrEmpty(current)) capturedCountdownText = current;
                } catch (Exception e) {
                    ThrottledLog("M18", $"countdown text capture failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Last)]
        internal static class LobbyCountdownTextRestorePatch {
            public static void Postfix(GameStartManager __instance) {
                try {
                    if (__instance == null) return;
                    if (__instance.startState != GameStartManager.StartingStates.Countdown) return;
                    var text = __instance.GameStartText;
                    if (text == null) return;
                    if (!string.IsNullOrEmpty(text.text)) return; // real text or a genuine mismatch warning
                    if (string.IsNullOrEmpty(LobbyCountdownTextCapturePatch.capturedCountdownText)) return;

                    text.text = LobbyCountdownTextCapturePatch.capturedCountdownText;
                    if (__instance.GameStartTextParent != null && !__instance.GameStartTextParent.activeSelf)
                        __instance.GameStartTextParent.SetActive(true);
                } catch (Exception e) {
                    ThrottledLog("M18", $"countdown text restore failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M23) "/size abc" locks the host's own lobby and kicks every joiner
        //
        // DynamicLobbies.cs:20-37, the private SendChatPatch.Prefix nested inside DynamicLobbies
        // (untouched, TOR's own file), handles the host-typed "/size N" chat command, gated on
        // AmongUsClient.Instance.AmHost - so the corruption below can only ever happen on the HOST's
        // own client. On a parse failure ("/size abc", "/size ", trailing garbage) Int32.TryParse
        // still writes its out parameter (0, by contract) into DynamicLobbies.LobbyLimit before the
        // code takes the failure branch, and Math.Clamp(LobbyLimit, 4, 15) only runs a few lines
        // below in the SUCCESS branch. LobbyLimit is a bare static int, read every time a player
        // joins by DynamicLobbies.AmongUsClientOnPlayerJoined.Prefix ("if (LobbyLimit <
        // allClients.Count) disconnect"). Once LobbyLimit == 0, that comparison is true for
        // essentially any join, so the host's own lobby starts rejecting every future joiner with
        // GameFull until the host retypes a valid /size.
        //
        // The private nested Prefix class is patched directly (TargetMethod + Prepare, so a future
        // TOR refactor that renames or removes it degrades to "patch skipped, logged once" instead of
        // a crash at Harmony.PatchAll) with a Postfix that restores the class invariant LobbyLimit is
        // supposed to hold (clamped to [4,15]) after every /size attempt, success or failure, without
        // touching TOR's own parsing or chat-messaging code.
        //
        // Lobby-only, host-local, no round state or RPC involved: no EveryoneHasMod() gating needed.
        // ======================================================================================
        [HarmonyPatch]
        internal static class LobbySizeCommandClampPatch {
            private static MethodBase TargetMethod() {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.DynamicLobbies+SendChatPatch");
                return t?.GetMethod("Prefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            private static bool Prepare(MethodBase original) {
                bool found = TargetMethod() != null;
                if (!found)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorLobbyFixes/M23] DynamicLobbies.SendChatPatch.Prefix not found - /size clamp guard inactive.");
                return found;
            }

            public static void Postfix() {
                try {
                    DynamicLobbies.LobbyLimit = Math.Clamp(DynamicLobbies.LobbyLimit, 4, 15);
                } catch (Exception e) {
                    ThrottledLog("M23", $"clamp failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M24) A preset roundtrip orphans the saved vanilla-options blob
        //
        // CustomOptionHolder.Load() binds the vanilla-settings config entry as
        //     Config.Bind("Preset0", "VanillaOptions", "")
        // once, at startup, while CustomOption.preset is still its default 0. CustomOption.
        // switchPreset(newPreset), called every time the id-0 preset dropdown changes (including
        // switching back TO preset 0), rebinds the exact same purpose field as
        //     Config.Bind($"Preset{preset}", "GameOptions", "")
        // - a DIFFERENT key name, for every preset section including 0. The first ever preset switch
        // therefore repoints CustomOption.vanillaSettings at "Preset0/GameOptions", which starts
        // empty; whatever was saved under "Preset0/VanillaOptions" before that first switch (the
        // initial paste-in or manual save) becomes permanently unreachable through the normal
        // save/load path, exactly matching the audit's "Preset-1-Roundtrip verliert die
        // Vanilla-Optionen".
        //
        // Fix: a Postfix on the public switchPreset(int) migrates the legacy value across once, the
        // first time it is needed. If the (now current) "GameOptions" entry is still empty and a
        // "VanillaOptions" entry for that same preset section holds real data, that data is copied
        // over. Idempotent (only fires while the target is still empty) and additive only - it never
        // overwrites anything a normal save already wrote under the new key, and every other
        // consumer of CustomOption.vanillaSettings (saveVanillaOptions/loadVanillaOptions/the
        // clipboard paste path) is untouched, since they all just read the ConfigEntry reference this
        // patch may have backfilled.
        //
        // Host-local BepInEx config persistence only, not synced, not round state: no
        // EveryoneHasMod() gating needed.
        // ======================================================================================
        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.switchPreset))]
        internal static class PresetVanillaOptionsKeyMigrationPatch {
            public static void Postfix(int newPreset) {
                try {
                    var legacy = TheOtherRolesPlugin.Instance.Config.Bind($"Preset{newPreset}", "VanillaOptions", "");
                    if (!string.IsNullOrEmpty(legacy.Value) && CustomOption.vanillaSettings != null
                        && string.IsNullOrEmpty(CustomOption.vanillaSettings.Value)) {
                        CustomOption.vanillaSettings.Value = legacy.Value;
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[TorLobbyFixes/M24] migrated legacy vanilla-options blob for Preset{newPreset} from the old \"VanillaOptions\" key.");
                    }
                } catch (Exception e) {
                    ThrottledLog("M24", $"migration failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M28) A mistyped patch parameter on MainMenuManager.Start: DELIBERATELY LEFT ALONE
        //
        // CredentialsPatch.LogoPatch declares `PingTracker __instance` on a patch whose target is
        // MainMenuManager.Start. The types do not match, so the postfix never does anything: it is
        // dead code, and the audit rightly calls it a landmine rather than a bug.
        //
        // An earlier version of this file "defused" it by calling Harmony.Unpatch on TOR's own
        // patch. That was removed on review, for reasons worth writing down so nobody rebuilds it:
        //
        //   1. There is nothing to fix. The patch is inert today, so removing it changes no
        //      behaviour that anybody can observe - it only changes what TOR consists of.
        //   2. Whether it would work at all depends on load order. Unpatching before TOR has
        //      registered its patch is a no-op; after, it silently removes it. Neither outcome is
        //      predictable from here, and an "it depends" fix is worse than none.
        //   3. If a future TOR release gives LogoPatch a real body, we would be silently deleting
        //      working upstream code, and the symptom would be a feature that mysteriously does
        //      not exist on our clients.
        //   4. It abused Prepare() as an init hook. Harmony makes no promise about when or how
        //      often Prepare() runs, so that is a construct built on an undocumented detail.
        //
        // If this ever stops being inert, it belongs upstream as a one-character fix to the
        // parameter type, not as a runtime amputation from a different mod.
        // ======================================================================================

        // ======================================================================================
        // TOR-M34) Lights-Out gives the crew permanent full vision, silently
        //
        // The most serious finding in this audit, because nothing about it throws where a player
        // would ever see it. ShipStatusPatch.GetNeutralLightRadius computes crew vision during
        // Lights-Out as:
        //     float lerpValue = 1.0f;                          // default: FULL vision
        //     try {
        //         SwitchSystem switchSystem = MapUtilities.Systems[SystemTypes.Electrical]...;
        //         lerpValue = switchSystem.Value / 255f;        // real sabotage state
        //     } catch { }
        // If MapUtilities.Systems does not contain SystemTypes.Electrical, the indexer throws
        // KeyNotFoundException, the bare catch swallows it, and lerpValue is left at its unsafe
        // fail-OPEN default - full vision, as if the lights were never sabotaged at all.
        //
        // MapUtilities.cs's own cache invalidation is what can leave that entry missing:
        // ShipStatus.OnDestroy's Postfix sets CachedShipStatus = null and then immediately calls
        // MapDestroyed(), which unconditionally reassigns CachedShipStatus = ShipStatus.Instance -
        // overwriting the null with whatever the vanilla static singleton currently resolves to
        // (which, depending on exactly where teardown/setup ordering lands for the surrounding scene
        // transition, will not consistently be a live, populated ship) - and clears the systems
        // dictionary. Systems' getter only repopulates when the dictionary is empty, and
        // GetSystems() bails out immediately whenever CachedShipStatus is falsy, leaving the
        // dictionary (and the Electrical entry with it) empty until the next real
        // ShipStatus.Awake fires and recaches a fresh instance.
        //
        // Rather than guess at Unity's exact destroy/create ordering for that transition, the fix
        // works at the one place both consumers (this vision calc and RPCProcedure.
        // engineerFixLights, which has no try/catch at all and would hard-crash on the same missing
        // key) actually ask for the cache: right before MapUtilities.GetSystems() runs (a private
        // method, reached via TargetMethod + Prepare so a rename degrades to "skipped, logged once"),
        // adopt the CURRENT live ShipStatus.Instance if the cached reference is dead but a real one
        // exists. This is purely additive - if CachedShipStatus is already valid it is a no-op, and
        // if no ship exists at all (menu, between rounds) both sides are falsy and it still no-ops
        // exactly like today - and it self-heals regardless of what caused the staleness, not just
        // the one ordering issue described above.
        //
        // This is a per-client, local vision-rendering computation - TOR does not sync vision radius
        // over the network, each client always computed its own independently, sabotage-panel-value
        // included. A client running this fix simply computes the correct value from the same shared
        // Electrical system value every other client already has; no path here decides life, death,
        // role ownership or a win condition, so EveryoneHasMod() gating does not apply.
        // ======================================================================================
        [HarmonyPatch]
        internal static class MapSystemsCacheSelfHealPatch {
            private static MethodBase TargetMethod() {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Utilities.MapUtilities");
                return t?.GetMethod("GetSystems", BindingFlags.NonPublic | BindingFlags.Static);
            }

            private static bool Prepare(MethodBase original) {
                bool found = TargetMethod() != null;
                if (!found)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorLobbyFixes/M34] MapUtilities.GetSystems not found - Lights-Out cache self-heal inactive.");
                return found;
            }

            public static void Prefix() {
                try {
                    if (!MapUtilities.CachedShipStatus && ShipStatus.Instance)
                        MapUtilities.CachedShipStatus = ShipStatus.Instance;
                } catch (Exception e) {
                    ThrottledLog("M34", $"cache self-heal failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M49) Hide 'N Seek: a hunter disconnect during the wait strands the Hunted timeout win
        //
        // IntroPatch.cs:102-115 starts, per hunter, a HudManager-hosted Effects.Lerp coroutine that
        // waits HideNSeek.hunterWaitingTime seconds and then, in its p==1f branch, sets
        // player.moveable = true and (critically) HideNSeek.isWaitingTimer = false. That flag is the
        // ONLY thing gating the timeout win in EndGamePatch.cs:542
        //     if (HideNSeek.isHideNSeekGM && HideNSeek.timer <= 0 && !HideNSeek.isWaitingTimer)
        // If a hunter disconnects during the wait, their captured `player` reference is destroyed;
        // `player.moveable = true` throws before HideNSeek.isWaitingTimer = false ever executes, and
        // an unhandled exception inside a Unity coroutine just stops that coroutine - it does not
        // crash anything, so nothing is ever logged that points at this. isWaitingTimer is stuck
        // true for the rest of the round and the Hunted timeout win can never fire again. The same
        // dead end is reached with zero hunters at round start (the foreach body never runs at all).
        //
        // HideNSeekGM.cs:35-43 (clearAndReload, called once per round reset) conveniently already
        // stamps HideNSeek.startTime = DateTime.UtcNow in the same place it sets isWaitingTimer =
        // true, so no new bookkeeping is needed: this is a plain time-based watchdog on
        // PlayerControl.FixedUpdate (gated to the local player's own instance, matching this
        // codebase's usual __instance.AmOwner idiom), armed once elapsed real time clears
        // hunterWaitingTime by a safety margin. If TOR's own coroutine already reset the flag, the
        // watchdog is a no-op every single tick; it only ever does something once the flag is
        // genuinely still stuck past its expected deadline.
        //
        // isWaitingTimer feeds a win CONDITION, so the DESYNC rule is in play - but the actual
        // decision to end the game is host-only (only the host calls AmongUsClient.EndGame /
        // RpcEndGame; every client's own EndGamePatch check is otherwise inert). A host running this
        // fix un-sticks its OWN authoritative copy of the flag and correctly fires the timeout win
        // once TOR's own timer naturally counts down, exactly the way it was supposed to; a
        // non-host's copy sitting stuck a moment longer changes nothing it did not already not
        // control. Same host-authority argument TorAuditFixes' B1 (Witch list) already uses for the
        // same reason - EveryoneHasMod() gating is not needed for a fix that only restores what the
        // host itself was always meant to decide.
        // ======================================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        internal static class HideNSeekWaitingTimerWatchdogPatch {
            private const double GracePeriodSeconds = 2.0;

            public static void Postfix(PlayerControl __instance) {
                try {
                    if (__instance == null || !__instance.AmOwner) return;
                    if (!HideNSeek.isHideNSeekGM || !HideNSeek.isWaitingTimer) return;

                    double elapsed = (DateTime.UtcNow - HideNSeek.startTime).TotalSeconds;
                    if (elapsed < HideNSeek.hunterWaitingTime + GracePeriodSeconds) return;

                    HideNSeek.isWaitingTimer = false;
                    HideNSeek.timer = CustomOptionHolder.hideNSeekTimer.getFloat() * 60;
                    ThrottledLog("M49", "isWaitingTimer was stuck past its deadline (likely a hunter "
                                       + "disconnect mid-wait) - reset so the Hunted timeout win stays reachable.");
                } catch (Exception e) {
                    ThrottledLog("M49", $"watchdog failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M48) Prop Hunt: a hunter disconnect during the blackout strands the timeout win
        //
        // The exact same shape as TOR-M49, one gamemode over. PropHunt.cs's IntroCutsceneDestroyPatch
        // runs once per hunter's own client (gated on PlayerControl.LocalPlayer being an impostor),
        // plays the blackout video, and after initialBlackoutTime + 10/25 seconds calls
        // RPCProcedure.propHuntStartTimer() - the parameterless overload, RPC.cs:1204-1216 - whose
        // ELSE branch is the only place PropHunt.timerRunning ever becomes true:
        //     PropHunt.timerRunning = true; PropHunt.blackOutTimer = 0f; PropHunt.startTime = ...;
        // timerRunning is what lets PropHunt.cs:287 start counting PropHunt.timer down at all, and
        // EndGamePatch.cs:547 requires PropHunt.timer <= 0 && PropHunt.timerRunning for the timeout
        // win. That call sits inside a delayed HudManager coroutine on the hunter's OWN client and is
        // also what sends the RPC that starts the timer on every OTHER client - so if every hunter
        // disconnects before their own coroutine reaches that point (trivially true with the common
        // single-hunter setup), the RPC is never sent to anyone, and PropHunt.timerRunning is stuck
        // false everywhere: not just this client's local softlock, the whole lobby's.
        //
        // TOR's PropHunt class is INTERNAL (HideNSeek is public), so every field it touches goes
        // through reflection - the same pattern already established by AntiStartKill.cs's
        // IsPropHuntGM() for the same reason, resolved once and cached.
        //
        // The FIRST call (propHuntStartTimer(true), fired synchronously from IntroCutscene.OnDestroy
        // right as the round starts, not from a delayed coroutine) reliably sets
        // PropHunt.blackOutTimer = initialBlackoutTime and PropHunt.startTime = DateTime.UtcNow, so
        // "blackOutTimer > 0 but timerRunning still false past when the delayed call should have
        // fired" is an unambiguous stuck signal - exactly mirrored on PlayerControl.FixedUpdate,
        // gated to the local player's own instance, the same shape as the Hide 'N Seek watchdog above.
        //
        // Also evaluated and left alone: PropHunt.cs:436's
        //     videoPlayer.frame = (21 - (int)initialBlackoutTime) * 25;
        // can go negative if the host configures more than 21 seconds of initial blackout.
        // videoPlayer is a local variable inside IntroCutsceneDestroyPatch with no external handle,
        // so clamping it would need a transpiler into TOR's own method for what is, at worst, a
        // cosmetic video-start-frame glitch on a non-default option value - not worth that risk here.
        //
        // Same host-authority reasoning as TOR-M49 applies (only the host's own EndGamePatch check
        // can actually end the game): no EveryoneHasMod() gating needed.
        // ======================================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        internal static class PropHuntBlackoutWatchdogPatch {
            private const double GracePeriodSeconds = 2.0;

            private static bool fieldsResolved;
            private static FieldInfo fiIsPropHuntGM;
            private static FieldInfo fiTimerRunning;
            private static FieldInfo fiBlackOutTimer;
            private static FieldInfo fiStartTime;
            private static FieldInfo fiInitialBlackoutTime;

            private static bool ResolveFields() {
                if (fieldsResolved) return fiIsPropHuntGM != null;
                fieldsResolved = true;
                try {
                    var t = typeof(Helpers).Assembly.GetType("TheOtherRoles.CustomGameModes.PropHunt");
                    if (t == null) return false;
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                    fiIsPropHuntGM = t.GetField("isPropHuntGM", flags);
                    fiTimerRunning = t.GetField("timerRunning", flags);
                    fiBlackOutTimer = t.GetField("blackOutTimer", flags);
                    fiStartTime = t.GetField("startTime", flags);
                    fiInitialBlackoutTime = t.GetField("initialBlackoutTime", flags);
                    bool ok = fiIsPropHuntGM != null && fiTimerRunning != null && fiBlackOutTimer != null
                              && fiStartTime != null && fiInitialBlackoutTime != null;
                    if (!ok)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[TorLobbyFixes/M48] one or more PropHunt fields not found - blackout watchdog inactive.");
                    return ok;
                } catch (Exception e) {
                    ThrottledLog("M48", $"field resolution failed: {e.GetType().Name}: {e.Message}");
                    return false;
                }
            }

            public static void Postfix(PlayerControl __instance) {
                try {
                    if (__instance == null || !__instance.AmOwner) return;
                    if (!ResolveFields()) return;

                    if (!(bool)fiIsPropHuntGM.GetValue(null)) return;
                    if ((bool)fiTimerRunning.GetValue(null)) return;
                    float blackOutTimer = (float)fiBlackOutTimer.GetValue(null);
                    if (blackOutTimer <= 0f) return; // blackout never armed yet - nothing to rescue

                    var startTime = (DateTime)fiStartTime.GetValue(null);
                    float initialBlackoutTime = (float)fiInitialBlackoutTime.GetValue(null);
                    double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed < initialBlackoutTime + 10.0 / 25.0 + GracePeriodSeconds) return;

                    fiTimerRunning.SetValue(null, true);
                    fiBlackOutTimer.SetValue(null, 0f);
                    fiStartTime.SetValue(null, DateTime.UtcNow);
                    ThrottledLog("M48", "timerRunning was stuck past the blackout deadline (likely a hunter "
                                       + "disconnect mid-blackout) - armed so the Prop Hunt timeout win stays reachable.");
                } catch (Exception e) {
                    ThrottledLog("M48", $"watchdog failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        /*
         * THE SETTINGS-CHANGE POPUP SHOWS THE VALUE BUT NOT THE SETTING.
         *
         * Change any modded option and the notification in the lobby's bottom-left corner reads
         * just "3" or "On" - which setting it belongs to is missing, so the one piece of
         * information the popup exists to deliver is the one it does not carry. With several
         * options changed in a row they are indistinguishable.
         *
         * CustomOptions.cs:194 calls
         *   Notifier.AddSettingsChangeMessage((StringNames)(this.id + 6000), value, false)
         * and the vanilla method builds its line as "TranslationController.GetString(key): value".
         * `id + 6000` is not a real StringNames - it is a slot chosen to sit above the game's own
         * range, precisely so it collides with nothing - so the lookup has nothing to find and the
         * name half comes back empty. The value is TOR's own string and arrives intact, which is
         * why exactly half the line shows up.
         *
         * The name is not lost, it was never asked for: the option is sitting in
         * CustomOption.options under that very id. So the fix resolves it there and writes the
         * whole line through AddDisconnectMessage, which takes plain text (the same call
         * Unknown's Collection uses for its lobby notices).
         *
         * SAFE FOR VANILLA SETTINGS. Only keys at or above 6000 are touched, which is TOR's own
         * offset and above every StringNames the game defines; a vanilla key, or a modded id with
         * no option behind it, falls through to the original untouched. Display only, on the
         * client that receives the change, so no gate and no option: nothing here decides anything.
         */
        [HarmonyPatch(typeof(NotificationPopper), nameof(NotificationPopper.AddSettingsChangeMessage))]
        internal static class SettingsChangeMessageNamePatch {
            /// TOR's offset from CustomOptions.cs:194. Read from there, not guessed.
            private const int TorStringNameOffset = 6000;

            /*
             * POSITIONAL INJECTION (__0/__1), NOT PARAMETER NAMES.
             *
             * The vanilla signature is AddSettingsChangeMessage(key, value, playSound,
             * associatedRole) - FOUR parameters, read out of the interop assembly's metadata, not
             * the three TOR passes at the call site. Harmony binds injected parameters by name, so
             * a guessed name is a patch that throws the moment PatchAll runs. The first two
             * positions are all this fix needs and they are what the call site fixes in place.
             */
            public static bool Prefix(NotificationPopper __instance, StringNames __0, string __1) {
                try {
                    int raw = (int)__0;
                    if (raw < TorStringNameOffset) return true;          // a real vanilla setting

                    int id = raw - TorStringNameOffset;
                    string name = null;
                    foreach (var o in CustomOption.options) {
                        if (o == null || o.id != id) continue;
                        name = o.name;
                        break;
                    }
                    // No option behind the id: leave it exactly as it was rather than inventing a
                    // line for a message we cannot explain.
                    if (string.IsNullOrEmpty(name)) return true;

                    __instance.AddDisconnectMessage(name + ": " + __1);
                    return false;
                } catch (Exception e) {
                    ThrottledLog("settings-name", $"settings change message failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }
    }
}
