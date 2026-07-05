// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TimeMasterUnguessable - new Time Master option "Unguessable After Shield Saved A Kill".
 *
 * TOR's Time Master is normally guessable. With this option ON the Time Master becomes unguessable
 * in meetings — but ONLY once his Time Shield has actually prevented a kill (and rewound time).
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
                    1260, Types.Crewmate, "Time Master Unguessable After Shield Saved A Kill",
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

        private static bool Active() => Option != null && Option.getBool() && shieldSavedThisGame;

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

        // Block a successful guess of the Time Master once the shield has saved a kill.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootPatch {
            public static bool Prefix(byte killerId, byte dyingTargetId, byte guessedTargetId, byte guessedRoleId) {
                try {
                    if (!Active()) return true;
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
