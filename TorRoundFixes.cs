// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorRoundFixes - three silent behavioural bugfixes in TOR itself (AUDIT-2026-08-15). Unlike
 * TorNullGuards these do not crash or freeze anything; they just make the round play out
 * differently from what TOR's own documented behaviour promises. All three are option-less and
 * NOT behind UTSGate: they only restore TOR's own stated intent, so there is nothing to gate.
 *
 *  1) Sunglasses must not survive a Sidekick promotion
 *     TOR's own README says: "If you have the Sunglasses Modifier and get sidekicked, you will
 *     lose the Modifier." RPCProcedure.jackalCreatesSidekick calls erasePlayerRoles(playerId,
 *     ignoreModifier: true) for the real promotion path, and the only Sunglasses removal
 *     (Sunglasses.sunglasses.RemoveAll(...)) sits behind `if (!ignoreModifier)`, so it is never
 *     reached from here. The freshly promoted Sidekick keeps the reduced sight radius
 *     (ShipStatusPatch.CalculateLightRadius checks list membership only, no role/team check) for
 *     the rest of the round with no indication why. We remove the Sunglasses entry ourselves,
 *     right after TOR's own promotion runs.
 *
 *  2) TOR's own version handshake dictionary is never cleared between lobbies
 *     GameStartManagerPatch.playerVersions is written per clientId (RPC.cs) but never cleared
 *     anywhere in TOR. ClientIds are connection-scoped and get reused between lobbies of the same
 *     session, so a player can briefly inherit their predecessor's stale entry before their own
 *     handshake RPC lands - the host's mismatch check (GameStartManagerPatch) can read that as
 *     "everyone matches" for a few frames. We clear it on AmongUsClient.OnGameJoined, exactly like
 *     UsefulVersionHandshake and UTSModInventory already do for their own equivalent dictionaries.
 *
 *  3) End screen shows disconnected players as alive
 *     OnGameEndPatch.Postfix sets `IsAlive = !playerControl.Data.IsDead`. A player who disconnects
 *     mid-round (or rage-quits) is, in the ordinary case, Disconnected == true with IsDead still
 *     false - TOR itself treats the two as distinct everywhere else in this file (e.g.
 *     PlayerStatistics.GetPlayerCounts explicitly excludes Disconnected players from "alive"). The
 *     end screen therefore shows a disconnected player's name in white, as if they had survived. We
 *     run after TOR's own Postfix (HarmonyPriority.Last) and flip IsAlive to false for every entry
 *     whose matching GameData player is Disconnected.
 *     AdditionalTempData (and its nested PlayerRoleInfo) are internal TOR types, so both are
 *     resolved once via reflection and cached; a failed resolve degrades this to a no-op rather
 *     than touching anything half-built.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Patches;

namespace UsefulTORStuff {
    public static class TorRoundFixes {
        // ── 1) Sunglasses must not survive a Sidekick promotion ────────────────────────────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.jackalCreatesSidekick))]
        static class JackalCreatesSidekickSunglassesPatch {
            public static void Postfix(byte targetId) {
                try {
                    PlayerControl player = Helpers.playerById(targetId);
                    if (player == null) return;

                    // jackalCreatesSidekick has two outcomes: the real promotion (erasePlayerRoles
                    // + Sidekick.sidekick = player) or, when impostor-sidekicking is disabled, only
                    // Jackal.fakeSidekick is set and nothing about the player actually changes. Only
                    // the first one is a promotion, so only that one should strip the modifier.
                    if (Sidekick.sidekick != player) return;

                    Sunglasses.sunglasses.RemoveAll(x => x != null && x.PlayerId == player.PlayerId);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundFixes] Sunglasses sidekick-promotion fix failed: {e}");
                }
            }
        }

        // ── 2) TOR's own version handshake dictionary must not leak between lobbies ────────────
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class ClearTorPlayerVersionsOnGameJoinedPatch {
            public static void Postfix() {
                // try/catch only: GameStartManagerPatch.playerVersions is a public TOR field
                // reached directly (no reflection needed), but a future TOR rename should degrade
                // this to a no-op instead of throwing.
                try {
                    GameStartManagerPatch.playerVersions.Clear();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundFixes] playerVersions clear failed: {e}");
                }
            }
        }

        // ── 3) End screen must not show disconnected players as alive ──────────────────────────
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class DisconnectedNotAliveOnEndPatch {
            // AdditionalTempData and its nested PlayerRoleInfo are internal TOR types - resolved
            // once by reflection and cached, same pattern as SheriffParityWin's TOR-internal probe.
            private static bool resolved;
            private static FieldInfo playerRolesField;
            private static PropertyInfo playerNameProp;
            private static PropertyInfo isAliveProp;

            private static void Resolve() {
                resolved = true;
                try {
                    var torAsm = typeof(CustomOption).Assembly;
                    Type tempDataType = torAsm.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    Type roleInfoType = torAsm.GetType("TheOtherRoles.Patches.AdditionalTempData+PlayerRoleInfo");
                    if (tempDataType == null || roleInfoType == null) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[TorRoundFixes] AdditionalTempData/PlayerRoleInfo not found - end-screen disconnect fix disabled.");
                        return;
                    }

                    playerRolesField = tempDataType.GetField("playerRoles", BindingFlags.Public | BindingFlags.Static);
                    playerNameProp = roleInfoType.GetProperty("PlayerName", BindingFlags.Public | BindingFlags.Instance);
                    isAliveProp = roleInfoType.GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);

                    if (playerRolesField == null || playerNameProp == null || isAliveProp == null) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[TorRoundFixes] AdditionalTempData member(s) not found - end-screen disconnect fix disabled.");
                        playerRolesField = null; // make the disabled state a single flag to check
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundFixes] AdditionalTempData reflection failed: {e}");
                    playerRolesField = null;
                }
            }

            public static void Postfix() {
                try {
                    if (!resolved) Resolve();
                    if (playerRolesField == null) return; // resolve failed - stay a no-op

                    IList playerRoles = playerRolesField.GetValue(null) as IList;
                    if (playerRoles == null || playerRoles.Count == 0) return;
                    if (GameData.Instance == null) return;

                    // GameData.Instance.AllPlayers still holds this round's NetworkedPlayerInfo
                    // entries at this point (Priority.Last only orders our own Postfix after TOR's,
                    // it runs nowhere near resetVariables), each carrying PlayerName + Disconnected.
                    // Indexed loop, not foreach/LINQ: AllPlayers is an Il2Cpp list, which offers
                    // Count and an indexer but none of the managed enumerator helpers.
                    var disconnectedByName = new Dictionary<string, bool>();
                    var allPlayers = GameData.Instance.AllPlayers;
                    if (allPlayers == null) return;
                    for (int i = 0; i < allPlayers.Count; i++) {
                        var pi = allPlayers[i];
                        if (pi == null || pi.PlayerName == null) continue;
                        disconnectedByName[pi.PlayerName] = pi.Disconnected;
                    }

                    foreach (object entry in playerRoles) {
                        if (entry == null) continue;
                        string name = playerNameProp.GetValue(entry) as string;
                        if (name == null) continue;
                        if (disconnectedByName.TryGetValue(name, out bool disconnected) && disconnected)
                            isAliveProp.SetValue(entry, false);
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundFixes] end-screen disconnect fix failed: {e}");
                }
            }
        }
    }
}
