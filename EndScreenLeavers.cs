// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EndScreenLeavers - players who leave mid-round keep their line in the end screen's role summary.
 *
 * THE PROBLEM
 * TOR builds that summary in OnGameEndPatch.Postfix (Patches/EndGamePatch.cs:79) by walking
 * `PlayerControl.AllPlayerControls`. A player who disconnects is gone from that list - Among Us
 * destroys their PlayerControl - so the round ends with no trace of them: no name, no role, no
 * tasks. In a group that plays with role reveal at the end, the one player who crashed is exactly
 * the one everybody asks about, and "he left" is not an answer to "what was he?".
 *
 * WHY A RUNNING SNAPSHOT AND NOT A LOOKUP AT THE END
 * By the time the game ends there is nothing left to look up. The PlayerControl is destroyed, and
 * TOR's role statics (Sheriff.sheriff, Jackal.jackal, ...) either point at a destroyed object or
 * were cleared. The only moment the facts still exist is while the player is still connected, so
 * this file writes them down every couple of seconds and keeps the last version.
 *
 * WHAT IS RECORDED
 * Name, the fully formatted role string (TOR's own GetRolesString, so modifiers and colours match
 * the rest of the summary), task progress, whether they were already dead, and the kill count -
 * everything, because everything this needs is gone by the time the summary is built (see the note
 * on the kill count below).
 *
 * TWO LIMITS WORTH KNOWING
 *   - Somebody who leaves DURING the intro is not covered: the snapshot is cleared when the intro
 *     ends, and by then they are already out of AllPlayerControls.
 *   - Modifiers TOR hides from the living (Bait, Bloody, Vip) are not in a mid-round reading, so a
 *     leaver's line can lack them. Recreating that at game end is impossible - the PlayerControl it
 *     would have to be read from no longer exists.
 *
 * HOW THE LINE GETS BACK IN
 * A postfix on TOR's own OnGameEndPatch.Postfix - so it runs after the list is built and long
 * before the end screen reads it. Missing players are appended as TOR's own PlayerRoleInfo
 * entries (internal type, built by reflection), marked "(left)" and flagged as not alive, which is
 * what greys the line out in TOR's renderer.
 *
 * SAFETY
 * If any of the three reflection handles cannot be resolved (a TOR rename), the file logs once and
 * does nothing at all - the summary is then exactly what TOR would have shown on its own. The
 * snapshot is cleared on lobby join and on TOR's round reset, so a name can never leak from one
 * round into the next.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Patches;
using UnityEngine;

namespace UsefulTORStuff {

    public static class EndScreenLeavers {

        private const float SnapshotInterval = 2f;
        private const string LeftMarker = " (left)";

        private class Snapshot {
            public string Name;
            public string RoleNames;
            public int TasksCompleted;
            public int TasksTotal;
            public bool WasDead;
            public int? Kills;
        }

        private static readonly Dictionary<byte, Snapshot> snapshots = new Dictionary<byte, Snapshot>();
        private static float nextSnapshot = float.NegativeInfinity;

        // ---- recording ---------------------------------------------------------------------------
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class SnapshotTickPatch {
            public static void Postfix() {
                try {
                    if (Time.realtimeSinceStartup < nextSnapshot) return;
                    nextSnapshot = Time.realtimeSinceStartup + SnapshotInterval;
                    Record();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenLeavers] snapshot failed: {e}");
                }
            }
        }

        private static void Record() {
            // Lobby and menus have no roles to record, and recording there would only capture the
            // "no role yet" state of everyone.
            if (PlayerControl.LocalPlayer == null) return;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.IsGameStarted) return;

            foreach (var player in PlayerControl.AllPlayerControls) {
                if (player == null || player.Data == null || player.Data.Disconnected) continue;
                try {
                    var (completed, total) = TasksHandler.taskInfo(player.Data);
                    snapshots[player.PlayerId] = new Snapshot {
                        Name = player.Data.PlayerName,
                        // suppressGhostInfo: true, unlike TOR's own call at game end. TOR reads the
                        // string once the game is over, where the live decorations a ghost sees
                        // ("(cursed)", "(bounty)", the cause of death) are no longer added. Reading
                        // it mid-round with them enabled would bake whatever was true in that second
                        // into the summary permanently.
                        RoleNames = RoleInfo.GetRolesString(player, true, true, true),
                        TasksCompleted = completed,
                        TasksTotal = total,
                        WasDead = player.Data.IsDead,
                        Kills = CountKills(player.PlayerId),
                    };
                } catch { /* one unreadable player must not cost the others their snapshot */ }
            }
        }

        // ---- putting the missing lines back -------------------------------------------------------
        // Priority.Last: HarmonyX runs postfixes from highest priority down, so this one runs after
        // TOR's (which has no priority and therefore sits at Normal). Its list is complete by then.
        // The hook is the game's OnGameEnd rather than TOR's own postfix method, so a rename inside
        // TOR cannot silently detach it.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        private static class AppendLeaversPatch {
            public static void Postfix() {
                try { AppendMissing(); } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenLeavers] append failed: {e}");
                }
            }
        }

        private static bool resolved;
        private static FieldInfo playerRolesField;   // AdditionalTempData.playerRoles
        private static Type roleInfoEntryType;       // AdditionalTempData+PlayerRoleInfo

        private static bool Resolve() {
            if (resolved) return playerRolesField != null && roleInfoEntryType != null;
            resolved = true;
            try {
                var asm = typeof(CustomOption).Assembly;
                var tempData = asm.GetType("TheOtherRoles.Patches.AdditionalTempData");
                playerRolesField = tempData?.GetField("playerRoles", BindingFlags.Public | BindingFlags.Static);
                roleInfoEntryType = tempData?.GetNestedType("PlayerRoleInfo", BindingFlags.NonPublic | BindingFlags.Public);
                if (playerRolesField == null || roleInfoEntryType == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[EndScreenLeavers] AdditionalTempData.playerRoles/PlayerRoleInfo not found - "
                        + "leavers stay missing from the end screen, everything else is untouched.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenLeavers] reflection failed: {e}");
            }
            return playerRolesField != null && roleInfoEntryType != null;
        }

        private static void AppendMissing() {
            // Logged unconditionally and with numbers: the first version of this file returned here
            // in silence when the snapshot was empty, which made a wiped snapshot look exactly like
            // a feature that was never installed.
            if (!Resolve()) return;
            var list = playerRolesField.GetValue(null) as IList;
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[EndScreenLeavers] game end: {snapshots.Count} snapshot(s), {(list?.Count ?? -1)} player(s) in TOR's summary.");
            if (snapshots.Count == 0 || list == null) return;

            // TOR's entry carries only the display name, so that is what "already in the list" is
            // decided on. Two players cannot share a name in a lobby, and the summary itself would
            // be ambiguous if they could.
            var present = new HashSet<string>(StringComparer.Ordinal);
            var nameProp = roleInfoEntryType.GetProperty("PlayerName");
            foreach (var entry in list) {
                var name = nameProp?.GetValue(entry) as string;
                if (!string.IsNullOrEmpty(name)) present.Add(name);
            }

            int added = 0;
            foreach (var pair in snapshots) {
                var snap = pair.Value;
                if (snap == null || string.IsNullOrEmpty(snap.Name)) continue;
                if (present.Contains(snap.Name)) continue;

                var entry = Activator.CreateInstance(roleInfoEntryType);
                Set(entry, "PlayerName", snap.Name + LeftMarker);
                Set(entry, "RoleNames", snap.RoleNames ?? "");
                Set(entry, "Roles", new List<RoleInfo>());   // never null: TOR enumerates this elsewhere
                Set(entry, "TasksCompleted", snap.TasksCompleted);
                Set(entry, "TasksTotal", snap.TasksTotal);
                Set(entry, "IsGuesser", false);
                Set(entry, "Kills", snap.Kills);
                Set(entry, "IsAlive", false);                // greys the line out, like a dead player
                list.Add(entry);
                added++;
            }

            if (added > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EndScreenLeavers] restored {added} player(s) who left mid-round to the end screen.");
        }

        private static void Set(object entry, string property, object value) {
            try { roleInfoEntryType.GetProperty(property)?.SetValue(entry, value); } catch { }
        }

        // Counted DURING the round, into the snapshot - not at game end, which is where the first
        // version put it and where it could only ever return null. Two independent reasons, both
        // caused by TOR calling resetVariables() at the end of its own OnGameEnd postfix
        // (EndGamePatch.cs:233), i.e. before this file's append runs:
        //   - resetVariables -> clearGameHistory replaces GameHistory.deadPlayers with a fresh empty
        //     list (RPC.cs:191, GameHistory.cs:42), so there is nothing left to count;
        //   - DeadPlayer.killerIfExisting is a PlayerControl, and a leaver's PlayerControl has been
        //     destroyed by then, which Unity's null operator reports as null - so precisely the
        //     kills this feature wants would be filtered out even from an intact list.
        // GameHistory itself is internal to TOR, so the list is reached by reflection; DeadPlayer is
        // public, so its entries need no further indirection.
        private static bool deadPlayersResolved;
        private static FieldInfo deadPlayersField;

        private static int? CountKills(byte playerId) {
            try {
                if (!deadPlayersResolved) {
                    deadPlayersResolved = true;
                    deadPlayersField = typeof(CustomOption).Assembly
                        .GetType("TheOtherRoles.GameHistory")
                        ?.GetField("deadPlayers", BindingFlags.Public | BindingFlags.Static);
                }
                var deadPlayers = deadPlayersField?.GetValue(null) as IEnumerable;
                if (deadPlayers == null) return null;

                int kills = 0;
                foreach (var item in deadPlayers) {
                    var dead = item as DeadPlayer;
                    if (dead?.killerIfExisting != null && dead.killerIfExisting.PlayerId == playerId) kills++;
                }
                return kills > 0 ? kills : (int?)null;
            } catch {
                return null;
            }
        }

        // ---- resets --------------------------------------------------------------------------------
        public static void Clear() {
            snapshots.Clear();
            nextSnapshot = float.NegativeInfinity;
        }

        // NOT RPCProcedure.resetVariables, which is the obvious choice and the one that broke this
        // feature on its first outing: TOR calls resetVariables at the END of its own OnGameEnd
        // postfix (EndGamePatch.cs:233), so clearing there wiped the snapshot a moment before the
        // end screen wanted it. The round START is the correct moment, and the intro ending is the
        // last point at which every player is guaranteed to be present.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        private static class RoundStartResetPatch {
            public static void Postfix() => Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyResetPatch {
            public static void Postfix() => Clear();
        }
    }
}
