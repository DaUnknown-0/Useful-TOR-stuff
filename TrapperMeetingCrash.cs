// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TrapperMeetingCrash - a Trapper log about a player who has left the lobby freezes the meeting.
 *
 * WHAT HAPPENS
 * TOR's PlayerControl.StartMeeting PREFIX (MeetingPatch.cs:708-720) writes the Trapper's trap logs
 * into the chat:
 *
 *     PlayerControl p = Helpers.playerById(playerId);
 *     if (Trapper.infoType == 0) message += RoleInfo.GetRolesString(p, false, false, true) + "\n";
 *
 * `playerById` returns NULL for a player who has since disconnected (Helpers.cs:132-137), and
 * GetRolesString dereferences it. The exception escapes the PREFIX, so Among Us' own StartMeeting
 * never runs on that client: no meeting, no camera, no input. The player sits frozen while everyone
 * else is voting.
 *
 * WHO IT HITS
 * The block runs when `LocalPlayer == Trapper.trapper || Helpers.shouldShowGhostInfo()` - and the
 * second half is true for every DEAD player. So one stale id in a trap freezes the Trapper and, more
 * commonly, every ghost in the round. Observed 2026-08-14: a dead player stuck on a black screen
 * while the meeting ran for the living, and an earlier round where part of the lobby returned and
 * part did not.
 *
 * THE FIX, IN TWO LAYERS
 *  1. ROOT: strip ids of players who no longer exist from every trap BEFORE TOR's prefix reads them.
 *     Priority.First puts this ahead of TOR's own prefix, and HarmonyX runs every prefix regardless
 *     of what the others do, so ours always gets its turn. Trap is internal to TOR, hence reflection.
 *  2. NET: GetRolesString(null, ...) returns an empty string instead of throwing. That is public API
 *     and covers the other two Trapper info types plus any future caller that hands it a dead id.
 *
 * Client-side by necessity: the prefix runs on each client, so a host-only fix could not reach it.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {

    public static class TrapperMeetingCrash {

        private static FieldInfo trapsField;        // static List<Trap> Trap.traps
        private static FieldInfo trappedPlayerField; // List<byte> Trap.trappedPlayer
        private static FieldInfo trapPlayerIdMapField; // static Dictionary<byte, Trap> Trap.trapPlayerIdMap
        private static bool resolved;

        public static void TryPatch(Harmony harmony) {
            try {
                var tor = UsefulTORStuffPlugin.TORAssembly
                          ?? AppDomain.CurrentDomain.GetAssemblies()
                             .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
                var trapType = tor?.GetType("TheOtherRoles.Objects.Trap");
                if (trapType == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrapperMeetingCrash] TheOtherRoles.Objects.Trap not found - only the "
                        + "GetRolesString guard is active.");
                    return;
                }

                trapsField = trapType.GetField("traps", BindingFlags.Public | BindingFlags.Static);
                trappedPlayerField = trapType.GetField("trappedPlayer", BindingFlags.Public | BindingFlags.Instance);
                trapPlayerIdMapField = trapType.GetField("trapPlayerIdMap", BindingFlags.Public | BindingFlags.Static);
                resolved = trapsField != null && trappedPlayerField != null;

                if (!resolved)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrapperMeetingCrash] Trap fields not resolved - only the GetRolesString guard is active.");
                else
                    UsefulTORStuffPlugin.Logger?.LogInfo("[TrapperMeetingCrash] trap cleanup armed.");

                // THE ARMOUR. The trap scrub and the null guard cover the two holes we have SEEN;
                // this covers the ones we have not. TOR's StartMeetingPatch.Prefix is itself patched
                // with a finalizer: if any future null access throws inside it, the exception is
                // swallowed AND LOGGED - and because the prefix then returns normally, Among Us' own
                // StartMeeting still runs. A frozen client becomes a log line with the real cause.
                // (An exception escaping the prefix skips the original - that IS the freeze.)
                var torPrefix = tor?.GetType("TheOtherRoles.Patches.MeetingHudPatch+StartMeetingPatch")
                    ?.GetMethod("Prefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (torPrefix != null) {
                    var fin = typeof(TrapperMeetingCrash).GetMethod(nameof(SwallowPrefixException),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    harmony.Patch(torPrefix, finalizer: new HarmonyMethod(fin));
                    UsefulTORStuffPlugin.Logger?.LogInfo("[TrapperMeetingCrash] meeting-start armour installed.");
                } else {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrapperMeetingCrash] TOR's StartMeetingPatch.Prefix not found - no armour.");
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[TrapperMeetingCrash] TryPatch failed: {e.Message}");
            }
        }

        private static Exception SwallowPrefixException(Exception __exception) {
            if (__exception != null)
                UsefulTORStuffPlugin.Logger?.LogError(
                    "[TrapperMeetingCrash] TOR's StartMeeting prefix threw - suppressed so the meeting "
                    + $"still starts. Root cause: {__exception}");
            return null;
        }

        // True when this id still belongs to somebody in the game. Exactly the test TOR's own
        // playerById does, so we drop precisely the ids it would return null for.
        private static bool StillPresent(byte id) {
            try {
                foreach (var p in PlayerControl.AllPlayerControls) if (p != null && p.PlayerId == id) return true;
            } catch { }
            return false;
        }

        private static void ScrubTraps() {
            if (!resolved) return;
            try {
                var traps = trapsField.GetValue(null) as IEnumerable;
                if (traps == null) return;
                int removed = 0;

                foreach (var trap in traps) {
                    if (trap == null) continue;
                    var list = trappedPlayerField.GetValue(trap) as List<byte>;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--) {
                        if (StillPresent(list[i])) continue;
                        list.RemoveAt(i);
                        removed++;
                    }
                }

                // The id -> trap map would otherwise keep pointing at the same ghosts.
                if (trapPlayerIdMapField != null
                    && trapPlayerIdMapField.GetValue(null) is IDictionary map) {
                    var stale = new List<object>();
                    foreach (DictionaryEntry e in map)
                        if (e.Key is byte b && !StillPresent(b)) stale.Add(e.Key);
                    foreach (var k in stale) map.Remove(k);
                }

                if (removed > 0)
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TrapperMeetingCrash] dropped {removed} trapped id(s) of players who left - "
                        + "this would have frozen the meeting for every ghost.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[TrapperMeetingCrash] scrub failed: {e.Message}");
            }
        }

        // Layer 1: runs BEFORE TOR's own StartMeeting prefix reads the traps.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
        [HarmonyPriority(Priority.First)]
        static class ScrubBeforeMeetingPatch {
            public static void Prefix() => ScrubTraps();
        }

        // Layer 2: the safety net. A null player yields an empty string rather than an exception,
        // which is what every caller of this method can cope with - an exception is not.
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.GetRolesString))]
        [HarmonyPriority(Priority.First)]
        static class GetRolesStringNullGuard {
            public static bool Prefix(PlayerControl p, ref string __result) {
                try {
                    if (p != null && p.Data != null) return true;   // normal case: let TOR do its work
                } catch { }
                __result = "";
                return false;
            }
        }
    }
}
