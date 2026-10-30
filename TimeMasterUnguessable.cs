// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TimeMasterUnguessable - new Time Master option "Unguessable In The Meeting After A Saved Kill".
 *
 * TOR's Time Master is normally guessable. With this option ON the Time Master is unguessable in the
 * ONE meeting that follows a kill his Time Shield actually prevented (and rewound time); the flag is
 * cleared when that meeting closes (renamed 2026-10-04, the old name promised it for good).
 *
 * "Shield prevented a kill" is detected via RPCProcedure.timeMasterRewindTime: it is only ever
 * called from Helpers.checkMuderAttempt when a kill on the shielded Time Master is suppressed (and
 * its RPC dispatch on RPC.cs:1464), so it runs on every client. A postfix latches a per-game flag;
 * RPCProcedure.resetVariables clears it each round.
 *
 * The block itself is a prefix on RPCProcedure.guesserShoot: a successful guess of the Time Master
 * (guessedRoleId == TimeMaster AND the dying target IS the Time Master) is swallowed, so he does not
 * die and the shot is not consumed. Wrong guesses (which kill the guesser) are left untouched.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TimeMasterUnguessable {
        public static CustomOption Option;  // Off/On toggle

        // True once the Time Shield has prevented a kill this round (set on all clients via the
        // rewind RPC). Reset each round in RPCProcedure.resetVariables.
        private static bool shieldSavedThisGame;

        // Temp state while the guesser UI is being built (see GuesserUIPrefix/Finalizer).
        private static RoleInfo removedRoleInfo;
        private static int removedIndex = -1;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1260, Types.Crewmate, "Time Master Unguessable In The Meeting After A Saved Kill",
                    false, CustomOptionHolder.timeMasterSpawnRate);
                UTSLocalization.BindOptionTitle(Option, "uts.timemasterunguessable.option_name");

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.timeMasterShieldDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TimeMasterUnguessable] Option created under Time Master.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TimeMasterUnguessable] CreateOptions failed: {e}");
            }
        }

        // EVERYONE NEEDS THE MOD (AUDIT M-16)
        // The guesserShoot prefix below runs inside a TOR RPC handler, i.e. on every client in the
        // lobby - but only clients carrying THIS mod would refuse the shot. That splits the lobby
        // over whether a player is alive: the Time Master keeps standing here and lies dead there,
        // and everything downstream (vote counting, task progress, the win check) reads that split.
        // A rule about who may die has to hold everywhere or nowhere, so it is bound to the
        // handshake, exactly like TricksterBoxCount's value is. The list-hide in the guesser UI is
        // NOT gated: it is purely local presentation on this client and cannot desync anything.
        private static bool Active() => Option != null && UTSGate.Bool(Option) && shieldSavedThisGame;

        // The rule as it is ACTUALLY enforced - see the block above.
        private static bool ActiveForEveryone() => Active() && UsefulVersionHandshake.EveryoneHasMod();

        // Hide the Time Master from the guesser role list — exactly like TOR hides the Spy from the
        // evil guesser (MeetingPatch.cs:417/429). guesserOnClick builds the role buttons by iterating
        // RoleInfo.allRoleInfos; we temporarily remove the Time Master's RoleInfo around that build so
        // no Time Master button is created. Private static in TOR → patched via reflection.
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var t = torAsm.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                var m = t?.GetMethod("guesserOnClick", BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TimeMasterUnguessable] guesserOnClick not found — list-hide disabled (guesserShoot block still applies).");
                    return;
                }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(TimeMasterUnguessable), nameof(GuesserUIPrefix)),
                    finalizer: new HarmonyMethod(typeof(TimeMasterUnguessable), nameof(GuesserUIFinalizer)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[TimeMasterUnguessable] Patched guesserOnClick (hide Time Master from list).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TimeMasterUnguessable] TryPatch failed: {e}");
            }
        }

        public static void GuesserUIPrefix() {
            removedRoleInfo = null;
            removedIndex = -1;
            try {
                if (!Active()) return;
                var list = RoleInfo.allRoleInfos;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++) {
                    if (list[i] != null && list[i].roleId == RoleId.TimeMaster) {
                        removedRoleInfo = list[i];
                        removedIndex = i;
                        list.RemoveAt(i);
                        break;
                    }
                }
            } catch { removedRoleInfo = null; removedIndex = -1; }
        }

        // Finalizer runs even if guesserOnClick throws, guaranteeing the RoleInfo is restored.
        public static void GuesserUIFinalizer() {
            try {
                if (removedRoleInfo != null && removedIndex >= 0) {
                    var list = RoleInfo.allRoleInfos;
                    if (list != null) {
                        if (removedIndex <= list.Count) list.Insert(removedIndex, removedRoleInfo);
                        else list.Add(removedRoleInfo);
                    }
                }
            } catch { }
            removedRoleInfo = null;
            removedIndex = -1;
        }

        // Latch the flag whenever the shield rewinds (i.e. it prevented a kill). Runs on every client.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.timeMasterRewindTime))]
        static class RewindPatch {
            public static void Postfix() {
                shieldSavedThisGame = true;
            }
        }

        // Clear the flag each round (same reset hook TOR/this mod use elsewhere).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                shieldSavedThisGame = false;
            }
        }

        // Clear the flag at the end of each meeting, so the protection only covers the ONE meeting
        // that follows the saved kill — not every subsequent meeting for the rest of the game. The
        // guess (guesserShoot) happens while the MeetingHud is open, so the flag is still true during
        // that meeting and is only cleared once it closes.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        static class MeetingClosePatch {
            public static void Postfix() {
                shieldSavedThisGame = false;
            }
        }

        // ── Tiering-proof layer (User 2026-10-30: "das war an und ich wurde trotzdem geguessed") ──────────
        // Every patch above sits on a MANAGED TOR method (timeMasterRewindTime, guesserShoot, guesserOnClick), and
        // .NET tiered compilation silently drops Harmony detours on those (project note "Tiering killt Harmony-
        // Detours"; guesserShoot was listed there as unprotected). The playtest log shows exactly that: option on,
        // shield saved the Time Master, a correct guess in the next meeting killed him anyway. The layer below
        // only hooks Il2Cpp game methods, which never lose their detours, and reads TOR's state through plain
        // static FIELDS (no detour involved). The managed patches above stay as a first line.

        // TOR's CustomRPC enum and MeetingHudPatch are internal: the RPC id and the guess-list field are looked up
        // once by reflection (a field read and an enum value, no method detour involved).
        private static readonly int GuesserShootId = ResolveGuesserShootId();
        private static readonly FieldInfo GuesserUiField = typeof(CustomOption).Assembly
            .GetType("TheOtherRoles.Patches.MeetingHudPatch")?.GetField("guesserUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        private static int ResolveGuesserShootId() {
            try {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.CustomRPC");
                return t == null ? -1 : Convert.ToInt32(Enum.Parse(t, "GuesserShoot"));
            } catch { return -1; }
        }

        // 1) The latch: TOR sets TimeMaster.isRewinding while a saved kill rewinds time, on every client.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class RewindPollPatch {
            public static void Postfix() {
                try { if (TimeMaster.isRewinding && !shieldSavedThisGame) shieldSavedThisGame = true; } catch { }
            }
        }

        // the round reset also on an Il2Cpp method (resetVariables is managed and can lose its postfix)
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class RoundStartResetPatch {
            public static void Postfix() { shieldSavedThisGame = false; }
        }

        // 2) The remote path: TOR handles CustomRPC.GuesserShoot in its POSTFIX on PlayerControl.HandleRpc, so a
        // prefix there sees the message first. For a protected Time Master the dying target byte is rewritten to
        // 255: guesserShoot then finds no player and returns at once (no death, shot not consumed), on every client
        // that runs this mod - and ActiveForEveryone() makes that every client.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        static class GuesserRpcPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix([HarmonyArgument(0)] byte callId, [HarmonyArgument(1)] Hazel.MessageReader reader) {
                try {
                    if (GuesserShootId < 0 || callId != GuesserShootId || reader == null || !ActiveForEveryone()) return;
                    if (TimeMaster.timeMaster == null) return;
                    int at = reader.Offset + reader.Position;          // killerId, dyingTarget, guessedTarget, guessedRoleId
                    var buf = reader.Buffer;
                    if (buf == null || at + 3 >= buf.Length) return;
                    byte dying = buf[at + 1], role = buf[at + 3];
                    if (role != (byte)RoleId.TimeMaster || dying != TimeMaster.timeMaster.PlayerId) return;
                    buf[at + 1] = 255;
                    UsefulTORStuffPlugin.Logger?.LogInfo("[TimeMasterUnguessable] guess on the saved Time Master refused (RPC).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TimeMasterUnguessable] HandleRpc prefix failed: {e.Message}");
                }
            }
        }

        // 3) The guesser's own client calls guesserShoot directly from the button, without the RPC: hide the Time
        // Master's button in TOR's guess list every frame (the list-hide prefix above is on a managed method too).
        private static string timeMasterName;

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class GuessListPatch {
            public static void Postfix() {
                try {
                    if (!Active()) return;
                    var ui = GuesserUiField?.GetValue(null) as UnityEngine.GameObject;
                    if (ui == null) return;
                    if (timeMasterName == null) {
                        foreach (var ri in RoleInfo.allRoleInfos)
                            if (ri != null && ri.roleId == RoleId.TimeMaster) { timeMasterName = ri.name; break; }
                        if (timeMasterName == null) return;
                    }
                    foreach (var label in ui.GetComponentsInChildren<TMPro.TextMeshPro>()) {
                        if (label == null || label.text == null || !label.text.Contains(timeMasterName)) continue;
                        var button = label.transform.parent;              // TOR: label is a child of the role button
                        if (button != null && button.gameObject.activeSelf && button.GetComponent<PassiveButton>() != null)
                            button.gameObject.SetActive(false);
                    }
                } catch { }
            }
        }

        // Block a successful guess of the Time Master once the shield has saved a kill.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootPatch {
            public static bool Prefix(byte killerId, byte dyingTargetId, byte guessedTargetId, byte guessedRoleId) {
                try {
                    if (!ActiveForEveryone()) return true;
                    if (TimeMaster.timeMaster == null) return true;
                    // Only suppress a CORRECT guess of the Time Master (the dying target is him).
                    if (guessedRoleId == (byte)RoleId.TimeMaster
                        && dyingTargetId == TimeMaster.timeMaster.PlayerId) {
                        return false; // unguessable: no death, shot not consumed
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TimeMasterUnguessable] guesserShoot prefix failed: {e}");
                }
                return true;
            }
        }
    }
}
