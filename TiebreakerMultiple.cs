// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TiebreakerMultiple - new option "Tiebreaker Quantity" (1-3): allow up to three Tiebreakers.
 *
 * TOR models the Tiebreaker as a single PlayerControl (Tiebreaker.tiebreaker) and assigns it once.
 * This adds a quantity (max 3) and a multi-tiebreaker tie resolution, WITHOUT rewriting TOR's whole
 * vote-counting prefix (which would be very fragile):
 *
 *  - Assignment: a postfix on RoleManagerSelectRolesPatch.getSelectionForRoleId multiplies the
 *    Tiebreaker spawn count by the quantity (exactly how Invert/Sunglasses/... already do it), so TOR
 *    assigns the modifier to up to `quantity` players.
 *  - Tracking: a postfix on RPCProcedure.setModifier collects every Tiebreaker into our own list
 *    (TOR's single field only keeps the last one). Cleared each round.
 *  - Resolution (the chosen rule): ONLY on a tie, the Tiebreakers' votes are applied to the tied
 *    candidates — the tied candidate the MOST Tiebreakers voted for wins; if that is itself tied it
 *    stays a tie. No extra vote is shown. We implement this with a tiny, low-risk trick: a
 *    high-priority prefix on MeetingHud.CheckForEndVoting computes the winner and then points TOR's
 *    existing single-tiebreaker field at a Tiebreaker who voted for that winner (or null to keep the
 *    tie). TOR's own logic then exiles the winner. With 0-1 Tiebreakers we don't interfere at all.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TiebreakerMultiple {
        public static CustomOption Quantity;  // 1-3 (getQuantity)

        // CustomRPC.SetModifier (enum is internal to TOR; value is stable, see RPC.cs:89-94).
        private const byte TorSetModifierRpcId = 105;

        private static readonly List<PlayerControl> myTiebreakers = new List<PlayerControl>();
        private static readonly System.Random rng = new System.Random();

        // Reflection handles resolved in TryPatch.
        private static MethodInfo calculateVotes;   // MeetingHudPatch+MeetingCalculateVotesPatch.CalculateVotes
        private static FieldInfo swapped1Field;      // MeetingHudPatch.swapped1
        private static FieldInfo swapped2Field;      // MeetingHudPatch.swapped2

        public static void CreateOptions() {
            try {
                Quantity = CustomOption.Create(
                    1310, Types.Modifier, "Tiebreaker Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.modifierTieBreaker);

                var opts = CustomOption.options;
                opts.Remove(Quantity);
                int idx = opts.IndexOf(CustomOptionHolder.modifierTieBreaker);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Quantity);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TiebreakerMultiple] Option created under Tiebreaker.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;

                // Multiply Tiebreaker spawn count by the quantity (private static).
                var rmsr = torAsm.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                var gsfr = rmsr?.GetMethod("getSelectionForRoleId", BindingFlags.NonPublic | BindingFlags.Static);
                if (gsfr != null)
                    harmony.Patch(gsfr, postfix: new HarmonyMethod(typeof(TiebreakerMultiple), nameof(GetSelectionForRoleIdPostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TiebreakerMultiple] getSelectionForRoleId not found — multi-assignment disabled.");

                // Resolve the vote-count helper + swapper-swap fields used by the resolution prefix.
                var mhp = torAsm.GetType("TheOtherRoles.Patches.MeetingHudPatch+MeetingCalculateVotesPatch");
                calculateVotes = mhp?.GetMethod("CalculateVotes", BindingFlags.NonPublic | BindingFlags.Static);
                var outer = torAsm.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                swapped1Field = outer?.GetField("swapped1", BindingFlags.NonPublic | BindingFlags.Static);
                swapped2Field = outer?.GetField("swapped2", BindingFlags.NonPublic | BindingFlags.Static);
                if (calculateVotes == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TiebreakerMultiple] CalculateVotes not found — multi-tiebreak resolution disabled.");

                UsefulTORStuffPlugin.Logger?.LogInfo("[TiebreakerMultiple] Patched.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] TryPatch failed: {e}");
            }
        }

        private static int Qty() => Quantity != null ? Quantity.getQuantity() : 1;

        // Spawn-count multiply for the Tiebreaker (mirrors Invert/Sunglasses/... in TOR).
        public static void GetSelectionForRoleIdPostfix(ref int __result, RoleId roleId, bool multiplyQuantity) {
            try {
                if (roleId == RoleId.Tiebreaker && multiplyQuantity) __result *= Qty();
            } catch { }
        }

        // Collect every assigned Tiebreaker (TOR's field keeps only the last).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.setModifier))]
        static class SetModifierPatch {
            public static void Postfix(byte modifierId, byte playerId) {
                try {
                    if (modifierId != (byte)RoleId.Tiebreaker) return;
                    var p = Helpers.playerById(playerId);
                    if (p != null && !myTiebreakers.Contains(p)) myTiebreakers.Add(p);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { myTiebreakers.Clear(); }
        }

        // Host-authoritative top-up: TOR's chance path under-assigns the Tiebreaker (it consumes the
        // ticket pool with the NON-multiplied count, RoleAssignmentPatch.cs:480), so quantity > 1 only
        // reliably works at 100%. After TOR finishes role assignment we ensure up to `quantity`
        // Tiebreakers exist — but only if at least one already spawned, preserving the chance gate.
        // Runs at Priority.Low so it executes AFTER TOR's RoleManagerSelectRolesPatch.Postfix.
        [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
        [HarmonyPriority(Priority.Low)]
        static class TopUpPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (CustomOptionHolder.modifierTieBreaker == null
                        || CustomOptionHolder.modifierTieBreaker.getSelection() <= 0) return; // not in play
                    int want = Qty();
                    if (myTiebreakers.Count == 0 || myTiebreakers.Count >= want) return; // chance gate / already enough

                    // TOR assigns the Tiebreaker modifier from the full player pool (any alignment),
                    // so just exclude players who already hold it.
                    var eligible = PlayerControl.AllPlayerControls.ToArray()
                        .Where(p => p != null && p.Data != null && !p.Data.Disconnected && !p.Data.IsDead)
                        .Where(p => !myTiebreakers.Any(t => t != null && t.PlayerId == p.PlayerId))
                        .ToList();

                    int toAdd = Math.Min(want - myTiebreakers.Count, eligible.Count);
                    for (int i = 0; i < toAdd; i++) {
                        int idx = rng.Next(eligible.Count);
                        byte playerId = eligible[idx].PlayerId;
                        eligible.RemoveAt(idx);
                        AssignTiebreaker(playerId); // SetModifierPatch tracks it into myTiebreakers
                    }
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TiebreakerMultiple] Tiebreakers assigned: {myTiebreakers.Count} (target {want}).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] top-up failed: {e}");
                }
            }
        }

        // Broadcast + locally apply an extra Tiebreaker modifier via TOR's own SetModifier RPC, so
        // every TOR client (modded or not) applies it — the same path TOR's setModifierToRandomPlayer uses.
        private static void AssignTiebreaker(byte playerId) {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, TorSetModifierRpcId, SendOption.Reliable, -1);
            writer.Write((byte)RoleId.Tiebreaker);
            writer.Write(playerId);
            writer.Write((byte)0); // flag
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            RPCProcedure.setModifier((byte)RoleId.Tiebreaker, playerId, 0); // host applies locally
        }

        private static byte ApplySwap(byte vote) {
            try {
                var s1 = swapped1Field?.GetValue(null) as PlayerVoteArea;
                var s2 = swapped2Field?.GetValue(null) as PlayerVoteArea;
                if (s1 != null && s2 != null) {
                    if (vote == s1.TargetPlayerId) return s2.TargetPlayerId;
                    if (vote == s2.TargetPlayerId) return s1.TargetPlayerId;
                }
            } catch { }
            return vote;
        }

        // High priority → runs before TOR's CheckForEndVoting prefix. Returns void (no skip): we only
        // pre-set TOR's single Tiebreaker field so its own logic resolves the tie our way.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
        [HarmonyPriority(Priority.High)]
        static class ResolvePatch {
            public static void Prefix(MeetingHud __instance) {
                try {
                    if (calculateVotes == null) return;
                    if (myTiebreakers.Count < 2) return; // 0-1 tiebreaker: let TOR handle it unchanged
                    if (__instance == null || __instance.playerStates == null) return;
                    // Only act once everyone has voted (mirror TOR's own entry guard).
                    bool allVoted = true;
                    foreach (var ps in __instance.playerStates) if (!(ps.AmDead || ps.DidVote)) { allVoted = false; break; }
                    if (!allVoted) return;

                    var self = calculateVotes.Invoke(null, new object[] { __instance }) as Dictionary<byte, int>;
                    if (self == null || self.Count == 0) return;

                    int maxV = self.Values.Max();
                    var tied = self.Where(kv => kv.Value == maxV).Select(kv => kv.Key).ToList();
                    if (tied.Count <= 1) return; // not a tie → nothing to resolve

                    // Count tiebreaker votes for each tied candidate (skip = 253 / no-vote excluded).
                    var counts = new Dictionary<byte, int>();
                    var voteByTb = new Dictionary<byte, byte>();
                    foreach (var tb in myTiebreakers) {
                        if (tb == null) continue;
                        PlayerVoteArea pva = null;
                        foreach (var x in __instance.playerStates)
                            if (x.TargetPlayerId == tb.PlayerId) { pva = x; break; }
                        if (pva == null || pva.AmDead) continue;
                        byte vote = ApplySwap(pva.VotedFor);
                        if (vote == 253 || vote == byte.MaxValue || vote == byte.MaxValue - 1) continue;
                        if (!tied.Contains(vote)) continue;
                        counts[vote] = counts.TryGetValue(vote, out var c) ? c + 1 : 1;
                        voteByTb[tb.PlayerId] = vote;
                    }

                    if (counts.Count == 0) { Tiebreaker.tiebreaker = null; return; } // no votes on tied → stays tie
                    int top = counts.Values.Max();
                    var winners = counts.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
                    if (winners.Count != 1) { Tiebreaker.tiebreaker = null; return; } // tie among tiebreakers → stays tie

                    byte winner = winners[0];
                    // Point TOR's single field at a Tiebreaker whose (post-swap) vote is the winner.
                    byte deciderId = voteByTb.First(kv => kv.Value == winner).Key;
                    Tiebreaker.tiebreaker = Helpers.playerById(deciderId);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] resolution prefix failed: {e}");
                }
            }
        }
    }
}
