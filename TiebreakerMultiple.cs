// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TiebreakerMultiple - new option "Tiebreaker Quantity" (1-3): allow up to three Tiebreakers,
 * all of which are SHOWN as Tiebreaker and which together break a tie by majority.
 *
 * TOR models the Tiebreaker as a single PlayerControl (Tiebreaker.tiebreaker) and assigns it once,
 * so out of the box only ONE player ever shows the modifier (RoleInfo.cs:180 checks
 * `p == Tiebreaker.tiebreaker`) and only that one counts in the vote resolution. This rewrite makes
 * OUR OWN list `tiebreakers` the single source of truth and stops touching TOR's single field:
 *
 *  - Assignment: a postfix on RoleManagerSelectRolesPatch.getSelectionForRoleId multiplies the
 *    Tiebreaker spawn count by the quantity (exactly how Invert/Sunglasses/... already do it), so TOR
 *    assigns the modifier to up to `quantity` players. A host-authoritative top-up on assignModifiers
 *    covers TOR's chance path under-assigning at quantity > 1.
 *  - Tracking: a postfix on RPCProcedure.setModifier collects every Tiebreaker into `tiebreakers`
 *    (TOR's single field only keeps the last one). Cleared each round on resetVariables.
 *  - Display: a postfix on RoleInfo.getRoleInfoForPlayer adds the Tiebreaker RoleInfo for EVERY
 *    player in our list, so all Tiebreakers are shown as such everywhere TOR renders roles (intro,
 *    name suffix, role tab, exile, end game). De-duped against the one TOR already adds.
 *  - Resolution (full reimplementation): a high-priority prefix on MeetingHud.CheckForEndVoting
 *    REPLACES TOR's vote-resolution prefix (returns false → TOR's prefix is skipped). It reuses TOR's
 *    own CalculateVotes (Mayor double vote + Swapper swap) via reflection and only swaps the
 *    single-Tiebreaker block for the MAJORITY rule: among the tied options the one the most living
 *    Tiebreakers voted for wins. When Skip is part of the tie it counts as its own side, so a player
 *    only loses on a strict Tiebreaker majority over Skip; if Skip wins the majority, the Tiebreakers
 *    split evenly, or none voted on a tied option, it stays a tie. With 0-1 Tiebreakers this collapses
 *    to TOR's original behaviour.
 *
 * Defensive: if the reflection handles needed for resolution can't be resolved the prefix is NOT
 * registered, so TOR's original single-Tiebreaker resolution stays in effect.
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

        // Single source of truth for BOTH display and resolution. TOR's Tiebreaker.tiebreaker field
        // is left alone (it only ever holds the last-assigned one).
        private static readonly List<PlayerControl> tiebreakers = new List<PlayerControl>();
        private static readonly System.Random rng = new System.Random();

        // Reflection handles resolved in TryPatch.
        private static MethodInfo calculateVotes;   // MeetingHudPatch+MeetingCalculateVotesPatch.CalculateVotes
        private static FieldInfo swapped1Field;      // MeetingHudPatch.swapped1
        private static FieldInfo swapped2Field;      // MeetingHudPatch.swapped2
        private static FieldInfo targetField;        // MeetingHudPatch.target
        private static FieldInfo blockSkipField;      // TORMapOptions.blockSkippingInEmergencyMeetings (internal class)
        private static FieldInfo noVoteSelfField;     // TORMapOptions.noVoteIsSelfVote (internal class)
        private static byte setTiebreakRpcId = 255;  // CustomRPC.SetTiebreak (resolved via enum reflection)
        private static bool resolutionReady;         // gates the resolution prefix registration

        public static void CreateOptions() {
            try {
                Quantity = CustomOption.Create(
                    1310, Types.Modifier, "Tiebreaker Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.modifierTieBreaker);
                UTSLocalization.BindOptionTitle(Quantity, "uts.tiebreakermultiple.quantity_option");

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

                // Host-authoritative top-up postfix on assignModifiers (runs in both the classic and the
                // RoleDraft path, after every Tiebreaker SetModifier RPC has been tracked).
                var assignModifiers = rmsr?.GetMethod("assignModifiers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (assignModifiers != null)
                    harmony.Patch(assignModifiers, postfix: new HarmonyMethod(typeof(TiebreakerMultiple), nameof(TopUp)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TiebreakerMultiple] assignModifiers not found — multi-tiebreaker top-up disabled.");

                // Resolve the vote-count helper + swapper/target fields used by the resolution prefix.
                var mhp = torAsm.GetType("TheOtherRoles.Patches.MeetingHudPatch+MeetingCalculateVotesPatch");
                calculateVotes = mhp?.GetMethod("CalculateVotes", BindingFlags.NonPublic | BindingFlags.Static);
                var outer = torAsm.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                swapped1Field = outer?.GetField("swapped1", BindingFlags.NonPublic | BindingFlags.Static);
                swapped2Field = outer?.GetField("swapped2", BindingFlags.NonPublic | BindingFlags.Static);
                targetField = outer?.GetField("target", BindingFlags.NonPublic | BindingFlags.Static);

                // TORMapOptions is an internal class — resolve the two skip flags by reflection.
                var mapOpts = torAsm.GetType("TheOtherRoles.TORMapOptions");
                blockSkipField = mapOpts?.GetField("blockSkippingInEmergencyMeetings", BindingFlags.Public | BindingFlags.Static);
                noVoteSelfField = mapOpts?.GetField("noVoteIsSelfVote", BindingFlags.Public | BindingFlags.Static);

                // Resolve CustomRPC.SetTiebreak by name (no magic number).
                var customRpc = torAsm.GetType("TheOtherRoles.CustomRPC");
                if (customRpc != null && Enum.IsDefined(customRpc, "SetTiebreak"))
                    setTiebreakRpcId = Convert.ToByte(Enum.Parse(customRpc, "SetTiebreak"));

                // Register the full resolution reimplementation ONLY if we have what we need; otherwise
                // TOR's original single-Tiebreaker resolution stays in effect.
                resolutionReady = calculateVotes != null && customRpc != null && Enum.IsDefined(customRpc, "SetTiebreak");
                if (resolutionReady) {
                    var checkForEndVoting = typeof(MeetingHud).GetMethod(nameof(MeetingHud.CheckForEndVoting));
                    harmony.Patch(checkForEndVoting,
                        prefix: new HarmonyMethod(typeof(TiebreakerMultiple), nameof(ResolvePrefix)) { priority = Priority.First });
                } else {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TiebreakerMultiple] resolution handles missing — multi-tiebreak resolution disabled (TOR's single-tiebreaker logic stays active).");
                }

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[TiebreakerMultiple][DIAG] Reflection resolved: getSelectionForRoleId={(gsfr != null)}, " +
                    $"assignModifiers={(assignModifiers != null)}, CalculateVotes={(calculateVotes != null)}, " +
                    $"swapped1={(swapped1Field != null)}, swapped2={(swapped2Field != null)}, target={(targetField != null)}, " +
                    $"SetTiebreak={setTiebreakRpcId}, resolutionReady={resolutionReady}.");
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
                    if (p != null && !tiebreakers.Any(t => t != null && t.PlayerId == playerId)) tiebreakers.Add(p);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { tiebreakers.Clear(); }
        }

        // DISPLAY: show the Tiebreaker modifier for EVERY player in our list (TOR's RoleInfo.cs:180
        // only matches its single field). De-dupe against the one TOR already added. We mirror TOR's
        // gating: the Tiebreaker is shown whenever modifiers are shown at all (it sits OUTSIDE the
        // modifiersAreHidden block in TOR), so respecting `showModifier` is enough.
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class DisplayPatch {
            public static void Postfix(List<RoleInfo> __result, PlayerControl p, bool showModifier) {
                try {
                    if (!showModifier || p == null || __result == null) return;
                    if (!tiebreakers.Any(t => t != null && t.PlayerId == p.PlayerId)) return;
                    if (!__result.Contains(RoleInfo.tiebreaker)) __result.Add(RoleInfo.tiebreaker);
                } catch { }
            }
        }

        // Host-authoritative top-up: TOR's chance path under-assigns the Tiebreaker (it consumes the
        // ticket pool with the NON-multiplied count, RoleAssignmentPatch.cs:480), so quantity > 1 only
        // reliably works at 100%. After TOR finishes modifier assignment we ensure up to `quantity`
        // Tiebreakers exist — but only if at least one already spawned, preserving the chance gate.
        //
        // Hooked as a postfix on RoleManagerSelectRolesPatch.assignModifiers (see TryPatch), NOT on
        // RoleManager.SelectRoles: with RoleDraft enabled the classic Postfix returns early and the
        // draft coroutine assigns modifiers asynchronously later. assignModifiers runs in BOTH paths
        // and only after every Tiebreaker SetModifier RPC has been tracked, so the top-up is timing-safe.
        public static void TopUp() {
            try {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                if (CustomOptionHolder.modifierTieBreaker == null
                    || CustomOptionHolder.modifierTieBreaker.getSelection() <= 0) return; // not in play
                int want = Qty();
                if (tiebreakers.Count == 0 || tiebreakers.Count >= want) return; // chance gate / already enough

                // TOR assigns the Tiebreaker modifier from the full player pool (any alignment),
                // so just exclude players who already hold it.
                var eligible = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.Data != null && !p.Data.Disconnected && !p.Data.IsDead)
                    .Where(p => !tiebreakers.Any(t => t != null && t.PlayerId == p.PlayerId))
                    .ToList();

                int toAdd = Math.Min(want - tiebreakers.Count, eligible.Count);
                for (int i = 0; i < toAdd; i++) {
                    int idx = rng.Next(eligible.Count);
                    byte playerId = eligible[idx].PlayerId;
                    eligible.RemoveAt(idx);
                    AssignTiebreaker(playerId); // SetModifierPatch tracks it into `tiebreakers`
                }
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[TiebreakerMultiple] Tiebreakers assigned: {tiebreakers.Count} (target {want}).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] top-up failed: {e}");
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

        private static bool IsRealVote(byte vote) => vote < 252; // 252/253(skip)/254/255 are not player votes
        private const byte SkipVote = 253;                       // the Skip "candidate" key (matches CalculateVotes)

        // FULL REIMPLEMENTATION of TOR's MeetingHud.CheckForEndVoting prefix (MeetingPatch.cs:70-136),
        // with the single-Tiebreaker block replaced by the MAJORITY rule across `tiebreakers`. Runs at
        // Priority.First and returns false → TOR's own prefix is skipped. CalculateVotes (Mayor +
        // Swapper) is reused via reflection so we never duplicate that logic.
        public static bool ResolvePrefix(MeetingHud __instance) {
            try {
                if (!resolutionReady || __instance == null || __instance.playerStates == null) return true;

                // Guard: only resolve once every living player has voted (TOR's own entry guard).
                bool allVoted = true;
                foreach (var ps in __instance.playerStates) if (!(ps.AmDead || ps.DidVote)) { allVoted = false; break; }
                if (!allVoted) return true; // not ready — let nothing run yet (TOR's prefix would also no-op)

                // Block-skipping self-vote mutation (only in emergency meetings, i.e. target == null).
                var target = targetField?.GetValue(null);
                bool blockSkip = blockSkipField?.GetValue(null) is bool b1 && b1;
                bool noVoteSelf = noVoteSelfField?.GetValue(null) is bool b2 && b2;
                if (target == null && blockSkip && noVoteSelf) {
                    foreach (PlayerVoteArea pva in __instance.playerStates)
                        if (pva.VotedFor == byte.MaxValue - 1) pva.VotedFor = pva.TargetPlayerId;
                }

                var self = calculateVotes.Invoke(null, new object[] { __instance }) as Dictionary<byte, int>;
                if (self == null) return true; // reflection mismatch — fall back to TOR

                bool tie;
                KeyValuePair<byte, int> max = self.MaxPair(out tie);
                NetworkedPlayerInfo exiled = GameData.Instance.AllPlayers.ToArray()
                    .FirstOrDefault(v => !tie && v.PlayerId == max.Key && !v.IsDead);

                // Determine the tied candidates (all at the max vote value) and whether skip ties.
                List<NetworkedPlayerInfo> potentialExiled = new List<NetworkedPlayerInfo>();
                bool skipIsTie = false;
                if (self.Count > 0) {
                    Tiebreaker.isTiebreak = false;
                    int maxVoteValue = self.Values.Max();
                    foreach (KeyValuePair<byte, int> pair in self) {
                        if (pair.Value != maxVoteValue) continue;
                        if (pair.Key != 253)
                            potentialExiled.Add(GameData.Instance.AllPlayers.ToArray().FirstOrDefault(x => x.PlayerId == pair.Key));
                        else
                            skipIsTie = true;
                    }
                }

                // MAJORITY rule: only on a real tie (more than one tied candidate, or skip ties a candidate).
                if (potentialExiled.Count > 1 || (skipIsTie && potentialExiled.Count >= 1)) {
                    var counts = new Dictionary<byte, int>();
                    foreach (var tb in tiebreakers) {
                        if (tb == null) continue;
                        PlayerVoteArea pva = null;
                        foreach (var x in __instance.playerStates)
                            if (x.TargetPlayerId == tb.PlayerId) { pva = x; break; }
                        if (pva == null || pva.AmDead) continue;
                        byte vote = ApplySwap(pva.VotedFor);
                        if (vote == SkipVote) {
                            // Skip is its own side: a Tiebreaker voting Skip counts for "Skip", but only
                            // when Skip is one of the tied options (a vote-vs-skip tie). So a Tiebreaker on
                            // X and a Tiebreaker on Skip cancel out → stays a tie ("bei beiden ein Tiebreaker").
                            if (!skipIsTie) continue;
                        } else {
                            if (!IsRealVote(vote)) continue;                                  // 252/254/255: no/invalid vote
                            if (!potentialExiled.Any(x => x != null && x.PlayerId == vote)) continue; // not a tied candidate
                        }
                        counts[vote] = counts.TryGetValue(vote, out var c) ? c + 1 : 1;
                    }

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TiebreakerMultiple][DIAG] tiebreakers={tiebreakers.Count}, tiedCandidates={potentialExiled.Count}, " +
                        $"skipIsTie={skipIsTie}, votes=[{string.Join(", ", counts.Select(kv => kv.Key + ":" + kv.Value))}].");

                    if (counts.Count > 0) {
                        int top = counts.Values.Max();
                        var winners = counts.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
                        if (winners.Count == 1 && winners[0] != SkipVote) {
                            byte winner = winners[0];
                            exiled = potentialExiled.FirstOrDefault(v => v != null && v.PlayerId == winner);
                            tie = false;

                            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                                PlayerControl.LocalPlayer.NetId, setTiebreakRpcId, SendOption.Reliable, -1);
                            AmongUsClient.Instance.FinishRpcImmediately(writer);
                            RPCProcedure.setTiebreak();
                            UsefulTORStuffPlugin.Logger?.LogInfo($"[TiebreakerMultiple][DIAG] majority winner={winner} → exiled.");
                        } else if (winners.Count == 1 && winners[0] == SkipVote) {
                            // Skip has the Tiebreaker majority → no one is exiled, stays a tie.
                            UsefulTORStuffPlugin.Logger?.LogInfo("[TiebreakerMultiple][DIAG] tiebreaker majority for Skip → stays tie.");
                        } else {
                            UsefulTORStuffPlugin.Logger?.LogInfo("[TiebreakerMultiple][DIAG] tiebreakers split evenly → stays tie.");
                        }
                    } else {
                        UsefulTORStuffPlugin.Logger?.LogInfo("[TiebreakerMultiple][DIAG] no tiebreaker voted a tied candidate → stays tie.");
                    }
                }

                // Build the voter-state array and finish the vote (mirrors TOR).
                MeetingHud.VoterState[] array = new MeetingHud.VoterState[__instance.playerStates.Length];
                for (int i = 0; i < __instance.playerStates.Length; i++) {
                    PlayerVoteArea pva = __instance.playerStates[i];
                    array[i] = new MeetingHud.VoterState {
                        VoterId = pva.TargetPlayerId,
                        VotedForId = pva.VotedFor
                    };
                }

                __instance.RpcVotingComplete(array, exiled, tie);
                return false; // suppress TOR's original prefix
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TiebreakerMultiple] resolution prefix failed — falling back to TOR: {e}");
                return true; // never block the meeting on our account
            }
        }
    }
}
