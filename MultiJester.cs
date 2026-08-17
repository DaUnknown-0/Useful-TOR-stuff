// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MultiJester - up to three Jesters in one round, each of whom wins ALONE.
 *
 * TOR's Jester is a single-holder role: `Jester.jester` is one PlayerControl, set by
 * RPCProcedure.setRole (RPC.cs:269), and everything else keys off that one reference. A second
 * setRole would simply overwrite the first, so extra Jesters cannot go through TOR's own field.
 * They live in `extraJesters` (PlayerIds) instead, and the handful of places where TOR asks
 * "is this player the Jester?" are extended by postfixes:
 *
 *   RoleInfo.getRoleInfoForPlayer  - the single source of truth for role display AND for
 *                                    Helpers.isNeutral (which reads the first entry of it). Adding
 *                                    RoleInfo.jester there (and dropping the "crewmate" fallback)
 *                                    makes an extra Jester a neutral for every check TOR derives
 *                                    from role info: intro, name tags, end screen, exile text.
 *   Helpers.hasFakeTasks           - so their tasks never count towards the crew's task win.
 *   Helpers.hasImpVision           - so "Jester Has Impostor Vision" applies to them too.
 *   Helpers.isKiller               - a neutral who is not in TOR's hardcoded non-killer list would
 *                                    otherwise count as a killer (Lovers win checks read this).
 *   EmergencyMinigame.Update       - so "Jester Can Call Emergency" applies to them too.
 *   ExileController WrapUp         - the win trigger, see below.
 *   AmongUsClient.OnGameEnd        - the winner correction, see below.
 *
 * WINNING ALONE
 * TOR's flow is: exile a Jester -> `Jester.triggerJesterWin = true` (every client) -> the host's
 * CheckEndCriteria fires RpcEndGame(CustomGameOverReason.JesterWin) -> OnGameEndPatch replaces the
 * winner list with exactly `Jester.jester`. For several Jesters that last step is wrong twice over:
 * the winner may be an extra Jester (not TOR's field), and the OTHER Jesters must not win with him.
 * The winner is therefore remembered when the exile happens (`winnerId`, computed identically on
 * every client, so no RPC is needed) and the winner list is rewritten to that one player in a
 * postfix. Every other end condition removes all extra Jesters from the winners instead, which is
 * TOR's own "notWinners" treatment for the Jester (EndGamePatch.cs:94) applied to ours.
 *
 * ASSIGNMENT
 * Host-side, after TOR has finished the whole assignment (RoleManager.SelectRoles postfix, low
 * priority - TOR's own postfix assigns roles AND modifiers). Extra Jesters are only added when TOR
 * actually spawned a Jester this round, so the role's spawn chance keeps its meaning: the quantity
 * says "how many, IF the Jester spawns at all", exactly like the Tiebreaker/Mini/Armored quantities
 * of this plugin. Candidates are players who ended up with no special role at all (plain
 * crewmates); the result is broadcast on the consolidated channel (module byte 243).
 * In role draft mode nothing is assigned at all - the players pick, see the draft section below.
 *
 * EVERYONE NEEDS THE MOD
 * The extra Jesters exist only inside this code: their role card, their neutral status, their fake
 * tasks and their win condition are all reimplemented per client. A player without the mod would
 * see a crewmate, count him for the crew and never understand the ending, so the quantity is forced
 * back to 1 unless EVERY player has the mod (EffectiveQuantity, the same rule the extra
 * Mini/Armored holders follow). The host gets a lobby warning when his setting is being ignored.
 *
 * NOTE: this raises the neutral count above TOR's "Neutral Roles" limits by design - the host asked
 * for N Jesters.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MultiJester {
        // Module byte on the consolidated channel (UTSRpc.CallId = 240). New feature, so no legacy
        // dual-send: 243 has never been a standalone callId of this plugin.
        public const byte RpcId = 243;

        public static CustomOption Quantity;          // 1376

        // The ADDITIONAL Jesters, by PlayerId. TOR's own Jester.jester is deliberately NOT in here:
        // everything TOR does for it already works, and mixing the two would double-count him.
        public static readonly List<byte> extraJesters = new List<byte>();

        // The Jester who was actually voted out, i.e. the one who wins. Set on every client from the
        // exile itself (same input everywhere, so no RPC), read in the OnGameEnd postfix.
        private static byte? winnerId;

        private static readonly System.Random rng = new System.Random();

        // Reflection handle for TOR's AdditionalTempData.winCondition, needed only for the edge case
        // where TOR skipped its own Jester branch (see WinnerPatch).
        private static FieldInfo winConditionField;
        private static object winConditionJesterValue;
        private static bool winConditionResolved;

        // Reflection handles for TOR's AdditionalTempData.additionalWinConditions (a List<WinCondition>,
        // both internal), needed to re-add the Lawyer bonus win condition after we rebuild the winner
        // list ourselves (see WinnerPatch / EnsureLawyerBonusWinCondition).
        private static FieldInfo additionalWinConditionsField;
        private static object winConditionLawyerBonusValue;
        private static bool lawyerBonusResolved;

        // TOR's CustomGameOverReason.JesterWin. The enum is internal, so it is resolved by name once;
        // 13 is only the documented fallback (EndGamePatch.cs:19) for the case where TOR renames it.
        private static int jesterWinReason = -1;

        public static void CreateOptions() {
            UTSRpc.Register(RpcId, HandleModuleRpc);

            try {
                Quantity = CustomOption.Create(
                    1376, Types.Neutral, "Jester Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.jesterSpawnRate);
                UTSLocalization.BindOptionTitle(Quantity, "uts.multijester.quantity_option");

                var opts = CustomOption.options;
                opts.Remove(Quantity);
                int idx = opts.IndexOf(CustomOptionHolder.jesterSpawnRate);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Quantity);

                UsefulTORStuffPlugin.Logger?.LogInfo("[MultiJester] Option created under Jester.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] CreateOptions failed: {e}");
            }
        }

        // What the host set. Only the lobby warning uses this; everything else must go through
        // EffectiveQuantity below.
        public static int ConfiguredQuantity => Quantity != null ? UTSGate.Qty(Quantity) : 1;

        private static int Qty() => ConfiguredQuantity;

        // The quantity as it is ACTUALLY applied. Like the extra Mini/Armored holders
        // (MultiModifiers), the extra Jesters only exist inside this mod: their role display, their
        // neutral status and their win condition are all reimplemented per client. A player without
        // the mod would see a crewmate, count him towards the crew's win and never understand the
        // ending, so the feature stands down completely unless EVERY player has the mod.
        public static int EffectiveQuantity() =>
            UsefulVersionHandshake.EveryoneHasMod() ? Qty() : 1;

        public static bool IsExtraJester(PlayerControl p) =>
            p != null && extraJesters.Count > 0 && extraJesters.Contains(p.PlayerId);

        public static bool IsExtraJester(byte playerId) =>
            extraJesters.Count > 0 && extraJesters.Contains(playerId);

        // Any Jester, TOR's or ours. Used by the win/exile logic.
        public static bool IsAnyJester(PlayerControl p) =>
            p != null && ((Jester.jester != null && Jester.jester.PlayerId == p.PlayerId) || IsExtraJester(p));

        // ── Assignment (host) ─────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
        private static class SelectRolesPatch {
            // Low priority: TOR's own postfix (default priority) runs the entire assignment,
            // including assignModifiers(), so by the time we get here every role is final.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!ImpostorCountRange.IsNormalTorGame()) return;
                    // Draft mode assigns the roles by hand, one player at a time: the extra Jesters
                    // are picked there (DraftPickPatch), never topped up here.
                    if (DraftEnabled()) return;

                    int want = EffectiveQuantity();
                    if (want <= 1) return;
                    // No Jester this round (spawn chance missed / role off): nothing to multiply.
                    if (Jester.jester == null) return;

                    var candidates = PlayerControl.AllPlayerControls.ToArray()
                        .Where(IsPlainCrewmate).ToList();
                    int extra = Mathf.Min(want - 1, candidates.Count);
                    if (extra <= 0) {
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[MultiJester] quantity {want} requested, but no plain crewmate left to promote.");
                        return;
                    }

                    var chosen = new List<byte>();
                    for (int i = 0; i < extra; i++) {
                        int pick = rng.Next(candidates.Count);
                        chosen.Add(candidates[pick].PlayerId);
                        candidates.RemoveAt(pick);
                    }

                    Broadcast(chosen);
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[MultiJester] {chosen.Count} extra Jester(s) assigned "
                        + $"(quantity {want}, TOR's Jester is {Jester.jester.Data?.PlayerName}).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] assignment failed: {e}");
                }
            }
        }

        // A player who ended up with no special role at all. getRoleInfoForPlayer(p, false) returns
        // exactly [crewmate] for those (RoleInfo.cs: the default-role fallback), which is the same
        // source of truth TOR uses everywhere else - no separate list of "roles to exclude" that
        // could go stale when TOR adds a role.
        private static bool IsPlainCrewmate(PlayerControl p) {
            try {
                if (p == null || p.Data == null || p.Data.Disconnected || p.Data.IsDead) return false;
                if (p.Data.Role != null && p.Data.Role.IsImpostor) return false;
                var infos = RoleInfo.getRoleInfoForPlayer(p, false);
                return infos.Count == 1 && infos[0] == RoleInfo.crewmate;
            } catch {
                return false;
            }
        }

        private static void Broadcast(List<byte> ids) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write((byte)ids.Count);
                foreach (byte id in ids) w.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] broadcast failed: {e}");
            }
            Apply(ids);   // the sender never receives its own RPC
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                int count = reader.ReadByte();
                var ids = new List<byte>(count);
                for (int i = 0; i < count; i++) ids.Add(reader.ReadByte());
                // HOST-ONLY: this promotes plain crewmates to extra Jesters. The sender is always the
                // host (the pick runs in the host-side assignment path above); a forged copy would let
                // any client hand out Jester roles (AUDIT-2026-08-11.md, H-3).
                if (!UTSRpc.RequireHost("MultiJester.Assign")) return;
                Apply(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] rpc read failed: {e}");
            }
        }

        // Absolute state assignment (not a delta), so a repeated message cannot accumulate.
        private static void Apply(List<byte> ids) {
            extraJesters.Clear();
            extraJesters.AddRange(ids);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[MultiJester] extra Jesters = [{string.Join(", ", ids)}].");
            RefreshLocalIntroRole();
        }

        // "You are the Jester" has to reach the player himself, and TOR's intro takes its text from
        // getRoleInfoForPlayer the moment it renders. That is normally after this message has
        // arrived (it is sent right behind TOR's own SetRole RPCs, on the same reliable channel),
        // but if it ever isn't, the intro would say "Crewmate" and the player would only find out
        // by losing. So whenever the list changes and it names US, the intro texts are re-stamped.
        // Nobody else's screen is touched: this only ever writes the LOCAL player's own role card.
        private static void RefreshLocalIntroRole() {
            try {
                if (!IsExtraJester(PlayerControl.LocalPlayer)) return;
                var intro = UnityEngine.Object.FindObjectOfType<IntroCutscene>();
                if (intro == null) return;

                var info = RoleInfo.jester;
                if (intro.RoleText != null) {
                    intro.RoleText.text = info.name;
                    intro.RoleText.color = info.color;
                }
                if (intro.RoleBlurbText != null) {
                    intro.RoleBlurbText.text = info.introDescription;
                    intro.RoleBlurbText.color = info.color;
                }
                // Same neutral styling TOR uses in setupIntroTeam (IntroPatch.cs).
                var neutralColor = new Color32(76, 84, 78, 255);
                if (intro.TeamTitle != null) {
                    intro.TeamTitle.text = "Neutral";
                    intro.TeamTitle.color = neutralColor;
                }
                if (intro.BackgroundBar != null && intro.BackgroundBar.material != null)
                    intro.BackgroundBar.material.color = neutralColor;

                UsefulTORStuffPlugin.Logger?.LogInfo("[MultiJester] local intro role card re-stamped as Jester.");
            } catch { }
        }

        // Second guard for the same job, patched onto TOR's SetRoleTexts in TryPatch (NOT onto
        // IntroCutscene.ShowRole: that returns a coroutine, so a postfix there runs when the
        // coroutine is created, long before any text is written, and would be overwritten again).
        // In the normal case the assignment has already arrived, TOR builds the card from
        // getRoleInfoForPlayer and our postfix on it has made that say "Jester" - re-stamping is
        // then free and idempotent.
        public static void SetRoleTextsPostfix() => RefreshLocalIntroRole();

        // ── Role draft ────────────────────────────────────────────────────────────────────────
        //
        // In draft mode nobody is assigned a role: every player picks one in turn, and TOR removes a
        // picked role from everybody else's choices with
        //
        //     if (alreadyPicked.Contains((byte)roleInfo.roleId) && roleInfo.roleId != RoleId.Crewmate) continue;
        //
        // inside the draft coroutine (RoleDraft.cs:135). That single line is what has to give for the
        // Jester, and it sits in a compiler-generated iterator - transpiling it blind would risk
        // breaking the whole round start. `alreadyPicked` itself is a plain static List<byte>,
        // though, so the same effect is reached by editing the list right after a pick: the Jester
        // entry is swapped for a PLACEHOLDER that is neutral and never offered in the draft
        // (RoleId.Sidekick, skipped explicitly at RoleDraft.cs:128). The Jester stays selectable and
        // every other count that reads the list keeps working - neutralsPicked still sees a neutral,
        // crewPicked is still right, alreadyPicked.Count for the feed is unchanged. Once the
        // quantity is reached the entry is left alone and TOR's own line locks the role away.
        //
        // The spawn chance is untouched by all of this: the draft only ever offers the Jester when
        // TOR's own filters (its spawn rate among them) let it through, which is exactly what
        // "quantity, chance stays" means here.
        //
        // Known cosmetic side effect: a Jester set to 100% is counted as a plain neutral pick while
        // it wears the placeholder, so TOR's "100% roles still to be handed out" bookkeeping is one
        // short. It can only make the draft push the remaining players TOWARDS the Jester, never
        // away from it, and TOR has a fallback for an empty choice list.
        private const byte DraftPlaceholderRoleId = (byte)RoleId.Sidekick;

        private static int draftJesterPicks;

        // Reflection handles: RoleDraft is internal to TOR.
        private static FieldInfo draftAlreadyPickedField;
        private static FieldInfo draftIsRunningField;

        // Read from TOR's own public option rather than from RoleDraft.isEnabled, so this answer
        // stays correct even if the RoleDraft handles below fail to resolve. It has to: it is what
        // stops the host-side top-up from adding Jesters on top of the drafted ones.
        private static bool DraftEnabled() {
            try { return CustomOptionHolder.isDraftMode != null && CustomOptionHolder.isDraftMode.getBool(); }
            catch { return false; }
        }

        private static bool DraftRunning() {
            try {
                return draftIsRunningField != null && (bool)draftIsRunningField.GetValue(null);
            } catch { return false; }
        }

        private static List<byte> DraftAlreadyPicked() {
            try { return draftAlreadyPickedField?.GetValue(null) as List<byte>; }
            catch { return null; }
        }

        // Prefix on TOR's setRole, live ONLY while the draft runs. TOR's receivePick calls setRole
        // for every pick, and setRole assigns the single Jester.jester field - so the second player
        // to pick the Jester would silently un-Jester the first one. Here the second and third pick
        // become extra Jesters instead. Because receivePick runs on every client (it is driven by
        // TOR's own DraftModePick RPC), this fills the list identically everywhere with no message
        // of our own.
        public static bool SetRolePrefix(byte roleId, byte playerId) {
            try {
                if (roleId != (byte)RoleId.Jester) return true;
                if (!DraftRunning()) return true;
                int want = EffectiveQuantity();
                if (want <= 1) return true;

                // First Jester of the round: TOR's own field, assigned normally.
                if (Jester.jester == null) { draftJesterPicks = 1; return true; }
                if (Jester.jester.PlayerId == playerId) return true;
                // Quantity exhausted: behave exactly like TOR would (this should be unreachable,
                // since the role is locked away again at that point).
                if (draftJesterPicks >= want) return true;

                draftJesterPicks++;
                if (!extraJesters.Contains(playerId)) extraJesters.Add(playerId);
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[MultiJester] draft pick {draftJesterPicks}/{want}: player {playerId} is an extra Jester.");
                RefreshLocalIntroRole();
                return false;   // do NOT overwrite Jester.jester
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] draft setRole failed: {e}");
                return true;
            }
        }

        // Postfix on TOR's receivePick: swap the just-added Jester entry for the placeholder while
        // the quantity still has room (see the block comment above).
        public static void ReceivePickPostfix(byte roleId) {
            try {
                if (roleId != (byte)RoleId.Jester || !DraftRunning()) return;
                int want = EffectiveQuantity();
                if (want <= 1 || draftJesterPicks >= want) return;

                var picked = DraftAlreadyPicked();
                if (picked == null) return;
                int idx = picked.LastIndexOf((byte)RoleId.Jester);
                if (idx < 0) return;
                picked[idx] = DraftPlaceholderRoleId;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] draft pick bookkeeping failed: {e}");
            }
        }

        // ── Round state ───────────────────────────────────────────────────────────────────────

        private static void ClearState() {
            extraJesters.Clear();
            winnerId = null;
            draftJesterPicks = 0;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class ResetPatch {
            public static void Postfix() => ClearState();
        }

        // resetVariables only ever arrives from a host running (this version of) TOR. Joining a
        // lobby whose host doesn't send it would otherwise keep the previous round's PlayerIds
        // alive, and PlayerIds are reused per lobby - whoever now owns them would be treated as a
        // Jester. Same lesson as the Mini/Armored/Tiebreaker lists.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class OnGameJoinedPatch {
            public static void Postfix() => ClearState();
        }

        // The Eraser removes a player's role. TOR clears its own Jester there (RPC.cs:775); ours has
        // to follow, otherwise an erased extra Jester would still win by being voted out.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.erasePlayerRoles))]
        private static class ErasePatch {
            public static void Postfix(byte playerId) {
                if (extraJesters.Remove(playerId))
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[MultiJester] extra Jester {playerId} was erased.");
            }
        }

        // ── Role identity ─────────────────────────────────────────────────────────────────────

        // The single most important patch: role display AND Helpers.isNeutral both read this.
        // Runs after TOR filled the list, so the "crewmate" default it appended for a player with no
        // role is dropped again here and replaced by the Jester.
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        private static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (__result == null || !IsExtraJester(p)) return;
                    __result.Remove(RoleInfo.crewmate);
                    if (!__result.Contains(RoleInfo.jester)) __result.Add(RoleInfo.jester);
                } catch { }
            }
        }

        // Fake tasks: a Jester's tasks must never count towards the crew's task win.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.hasFakeTasks))]
        private static class FakeTasksPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                if (!__result && IsExtraJester(player)) __result = true;
            }
        }

        // "Jester Has Impostor Vision" (TOR option) for the extra Jesters as well.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.hasImpVision))]
        private static class ImpVisionPatch {
            public static void Postfix(NetworkedPlayerInfo player, ref bool __result) {
                try {
                    if (__result || player == null || !Jester.hasImpostorVision) return;
                    if (IsExtraJester(player.PlayerId)) __result = true;
                } catch { }
            }
        }

        // Helpers.isKiller is "impostor OR a neutral that isn't one of the harmless ones". Our extra
        // Jesters became neutrals through the role-info patch above but are not in TOR's hardcoded
        // exclusion list, so without this they would count as killers (Lovers.existingWithKiller,
        // among others, reads this).
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.isKiller))]
        private static class IsKillerPatch {
            public static void Postfix(PlayerControl player, ref bool __result) {
                if (__result && IsExtraJester(player)) __result = false;
            }
        }

        // "Jester Can Call Emergency" (TOR option) for the extra Jesters. Mirrors what TOR's own
        // postfix does for its Jester; runs after it (low priority) and only adds a block.
        [HarmonyPatch(typeof(EmergencyMinigame), nameof(EmergencyMinigame.Update))]
        [HarmonyPriority(Priority.Low)]
        private static class EmergencyPatch {
            public static void Postfix(EmergencyMinigame __instance) {
                try {
                    if (Jester.canCallEmergency || __instance == null) return;
                    if (!IsExtraJester(PlayerControl.LocalPlayer)) return;

                    __instance.StatusText.text = UTSLocalization.Tr("uts.multijester.no_emergency");
                    __instance.NumberText.text = string.Empty;
                    __instance.ClosedLid.gameObject.SetActive(true);
                    __instance.OpenLid.gameObject.SetActive(false);
                    __instance.ButtonActive = false;
                } catch { }
            }
        }

        // TOR blanks the "x impostors remain" line when the exiled player was its Jester, so the
        // Jester win isn't given away by the exile screen. Same for ours.
        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.GetString),
                      new Type[] { typeof(StringNames), typeof(Il2CppReferenceArray<Il2CppSystem.Object>) })]
        [HarmonyPriority(Priority.Low)]
        private static class ExileTextPatch {
            public static void Postfix(ref string __result, [HarmonyArgument(0)] StringNames id) {
                try {
                    if (id != StringNames.ImpostorsRemainP && id != StringNames.ImpostorsRemainS) return;
                    if (__result == "") return;
                    var ec = ExileController.Instance;
                    var exiled = ec?.initData?.networkedPlayer?.Object;
                    if (exiled != null && IsExtraJester(exiled)) __result = "";
                } catch { }
            }
        }

        // ── Win trigger ───────────────────────────────────────────────────────────────────────

        // TOR's own WrapUpPostfix sets Jester.triggerJesterWin when ITS Jester was exiled. Patching
        // that method directly (rather than the three vanilla WrapUp entry points it is called from)
        // means we fire exactly once, on exactly the same occasions, on every client.
        public static void WrapUpPostfix(PlayerControl exiled) {
            try {
                if (exiled == null) return;

                // Remember the winner for the end screen. TOR's Jester is included here: with
                // several Jesters around, "the one who was voted out" is the answer in both cases.
                if (IsAnyJester(exiled)) winnerId = exiled.PlayerId;

                if (!IsExtraJester(exiled)) return;

                // Mini's exile-lose and the Prosecutor win take precedence in TOR's own chain; an
                // extra Jester is never the Mini (the Mini is a modifier and we only promote plain
                // crewmates, but the Mini modifier can sit on one), so mirror that guard.
                if (Mini.mini != null && Mini.mini.PlayerId == exiled.PlayerId && !Mini.isGrownUp()
                    && !RoleInfo.getRoleInfoForPlayer(Mini.mini).Any(x => x.isNeutral)) return;

                Jester.triggerJesterWin = true;
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[MultiJester] extra Jester {exiled.Data?.PlayerName} was exiled - Jester win.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] exile handling failed: {e}");
            }
        }

        // ── Winner correction ─────────────────────────────────────────────────────────────────

        // Runs after TOR's OnGameEnd postfix, which has already built the winner list. Two jobs:
        //   1) Jester win  -> the winner list becomes exactly the Jester who was voted out. This is
        //      what makes each Jester win ALONE: TOR wrote its own Jester in there, and the other
        //      Jesters must not be added.
        //   2) any other end -> extra Jesters are removed from the winners, the same way TOR removes
        //      its own Jester (EndGamePatch "notWinners").
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Low)]
        private static class WinnerPatch {
            public static void Postfix() {
                try {
                    if (extraJesters.Count == 0) return;

                    bool jesterWin = (int)OnGameEndPatch.gameOverReason == JesterWinReason();

                    if (jesterWin) {
                        var winner = winnerId.HasValue ? Helpers.playerById(winnerId.Value) : null;
                        // Fallback: if we somehow never saw the exile (joined late, missing RPC),
                        // leave TOR's own result alone rather than emptying the winner list.
                        if (winner == null || winner.Data == null) return;

                        EndGameResult.CachedWinners = new Il2CppSystem.Collections.Generic.List<CachedPlayerData>();
                        EndGameResult.CachedWinners.Add(new CachedPlayerData(winner.Data));
                        EnsureJesterWinCondition();

                        // AUDIT-2026-08-15: TOR's own OnGameEnd postfix already ran the Lawyer bonus
                        // check (EndGamePatch.cs:209) against the winner list it had just built - but
                        // that list is exactly what we throw away above, taking any Lawyer it had
                        // already added with it. Rebuild the same condition here against the ACTUAL
                        // winner instead of TOR's hardcoded Jester.jester, so it also covers an extra
                        // Jester who happens to be the Lawyer's target (Lawyer.target can currently
                        // only ever become TOR's own Jester.jester, per the possibleTargets filter in
                        // RoleAssignmentPatch.cs:395 - extra Jesters do not exist yet at that point in
                        // the assignment order - but keying off the real winner is correct rather than
                        // hardcoded, and covers the far more common case: TOR's own Jester winning
                        // while an extra Jester also exists this round).
                        if (Lawyer.lawyer != null && Lawyer.target != null && !Lawyer.isProsecutor
                            && !Pursuer.notAckedExiled && Lawyer.target.PlayerId == winner.PlayerId
                            && Lawyer.lawyer.PlayerId != winner.PlayerId) {
                            EndGameResult.CachedWinners.Add(new CachedPlayerData(Lawyer.lawyer.Data));
                            EnsureLawyerBonusWinCondition();
                        }

                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[MultiJester] sole winner: {winner.Data.PlayerName}.");
                        return;
                    }

                    // Not a Jester win: strip every extra Jester from the winners. Matching by name
                    // is what TOR itself does for its "notWinners" list - CachedPlayerData carries no
                    // PlayerId. Iterated backwards so removing an entry can't skip the next one.
                    var names = new HashSet<string>();
                    foreach (byte id in extraJesters) {
                        var p = Helpers.playerById(id);
                        if (p?.Data != null) names.Add(p.Data.PlayerName);
                    }
                    if (names.Count == 0) return;
                    var winners = EndGameResult.CachedWinners;
                    for (int i = winners.Count - 1; i >= 0; i--) {
                        var w = winners[i];
                        if (w != null && names.Contains(w.PlayerName)) winners.RemoveAt(i);
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] winner correction failed: {e}");
                }
            }
        }

        private static int JesterWinReason() {
            if (jesterWinReason >= 0) return jesterWinReason;
            jesterWinReason = 13;   // documented fallback
            try {
                var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.CustomGameOverReason");
                if (e != null && Enum.IsDefined(e, "JesterWin"))
                    jesterWinReason = Convert.ToInt32(Enum.Parse(e, "JesterWin"));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[MultiJester] CustomGameOverReason.JesterWin not found - falling back to 13.");
            } catch { }
            return jesterWinReason;
        }

        // TOR only sets winCondition = JesterWin when ITS Jester still exists (EndGamePatch.cs:111).
        // If it was erased mid-game while an extra Jester lived on to win, the end screen would miss
        // its "Jester Wins" line - set it here. Reflection because AdditionalTempData is internal.
        private static void EnsureJesterWinCondition() {
            try {
                if (!winConditionResolved) {
                    winConditionResolved = true;
                    var asm = typeof(CustomOption).Assembly;
                    var tempData = asm.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    winConditionField = tempData?.GetField("winCondition",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var winCondition = asm.GetType("TheOtherRoles.Patches.WinCondition");
                    if (winCondition != null && Enum.IsDefined(winCondition, "JesterWin"))
                        winConditionJesterValue = Enum.Parse(winCondition, "JesterWin");
                    if (winConditionField == null || winConditionJesterValue == null)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[MultiJester] AdditionalTempData.winCondition not found - the end screen "
                            + "may not name the Jester win if TOR's own Jester was erased.");
                }
                if (winConditionField != null && winConditionJesterValue != null)
                    winConditionField.SetValue(null, winConditionJesterValue);
            } catch { }
        }

        // TOR names the Lawyer bonus on the end screen via
        // AdditionalTempData.additionalWinConditions.Add(WinCondition.AdditionalLawyerBonusWin)
        // (EndGamePatch.cs:218). additionalWinConditions is a List<WinCondition>, and both the list's
        // declaring type and its element type are internal, so it can only be reached by name through
        // reflection - but List<T> still implements the public, non-generic System.Collections.IList,
        // whose Add(object) does not care that T itself is inaccessible to us.
        private static void EnsureLawyerBonusWinCondition() {
            try {
                if (!lawyerBonusResolved) {
                    lawyerBonusResolved = true;
                    var asm = typeof(CustomOption).Assembly;
                    var tempData = asm.GetType("TheOtherRoles.Patches.AdditionalTempData");
                    additionalWinConditionsField = tempData?.GetField("additionalWinConditions",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var winCondition = asm.GetType("TheOtherRoles.Patches.WinCondition");
                    if (winCondition != null && Enum.IsDefined(winCondition, "AdditionalLawyerBonusWin"))
                        winConditionLawyerBonusValue = Enum.Parse(winCondition, "AdditionalLawyerBonusWin");
                    if (additionalWinConditionsField == null || winConditionLawyerBonusValue == null)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[MultiJester] AdditionalTempData.additionalWinConditions not found - the "
                            + "end screen may not name the Lawyer bonus for a multi-Jester win.");
                }
                if (additionalWinConditionsField != null && winConditionLawyerBonusValue != null) {
                    var list = additionalWinConditionsField.GetValue(null) as System.Collections.IList;
                    list?.Add(winConditionLawyerBonusValue);
                }
            } catch { }
        }

        // ── Patch registration ────────────────────────────────────────────────────────────────

        // The exile hook targets a method inside TOR's own patch class, so it needs a manual patch;
        // everything else in this file is attribute-based and picked up by PatchAll.
        public static void TryPatch(Harmony harmony) {
            try {
                var wrapUp = typeof(CustomOption).Assembly
                    .GetType("TheOtherRoles.Patches.ExileControllerWrapUpPatch")
                    ?.GetMethod("WrapUpPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                if (wrapUp == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[MultiJester] ExileControllerWrapUpPatch.WrapUpPostfix not found - extra "
                        + "Jesters cannot win by being voted out; the feature stays off.");
                    return;
                }
                harmony.Patch(wrapUp,
                    postfix: new HarmonyMethod(typeof(MultiJester), nameof(WrapUpPostfix)));

                // Draft mode support. RoleDraft is internal, so every handle is resolved by name; if
                // any of them is missing the draft simply keeps TOR's single-Jester behaviour and
                // says so once in the log, while the normal (non-draft) assignment stays fully
                // functional.
                // AUDIT-2026-08-16: the type name was "TheOtherRoles.RoleDraft", but RoleDraft lives
                // in TheOtherRoles.Modules (Modules/RoleDraft.cs:14). GetType returned null, every
                // handle below stayed null, and draft support silently switched itself off - so
                // "Jester Quantity" was capped at one in draft mode while the option still advertised
                // up to three. Exactly the silent-no-op that the degrade-gracefully design makes
                // invisible; the self-test's reflection-handle check is what surfaced it.
                var draft = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.RoleDraft");
                draftIsRunningField = draft?.GetField("isRunning",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                draftAlreadyPickedField = draft?.GetField("alreadyPicked",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var receivePick = draft?.GetMethod("receivePick",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var setRole = typeof(RPCProcedure).GetMethod(nameof(RPCProcedure.setRole),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                bool draftReady = draftIsRunningField != null && draftAlreadyPickedField != null
                                  && receivePick != null && setRole != null;
                if (draftReady) {
                    harmony.Patch(setRole,
                        prefix: new HarmonyMethod(typeof(MultiJester), nameof(SetRolePrefix)));
                    harmony.Patch(receivePick,
                        postfix: new HarmonyMethod(typeof(MultiJester), nameof(ReceivePickPostfix)));
                    UsefulTORStuffPlugin.Logger?.LogInfo("[MultiJester] role draft support patched.");
                } else {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[MultiJester] RoleDraft handles missing (isRunning=" + (draftIsRunningField != null)
                        + ", alreadyPicked=" + (draftAlreadyPickedField != null)
                        + ", receivePick=" + (receivePick != null)
                        + ") - in draft mode only one Jester can be picked, as in plain TOR.");
                }

                // Intro safety net: re-stamp our own role card after TOR wrote it (see
                // SetRoleTextsPostfix). Optional - without it the card is still correct in every
                // case where the assignment arrived before the intro, which is the normal one.
                // Nested in IntroPatch today, hence the '+'. Looked up by name as a fallback so a
                // move (or an un-nesting) in a future TOR release doesn't silently drop the net.
                var torAsm = typeof(CustomOption).Assembly;
                var setRoleTextsType = torAsm.GetType("TheOtherRoles.Patches.IntroPatch+SetUpRoleTextPatch")
                    ?? torAsm.GetType("TheOtherRoles.Patches.SetUpRoleTextPatch")
                    ?? torAsm.GetTypes().FirstOrDefault(t => t.Name == "SetUpRoleTextPatch");
                var setRoleTexts = setRoleTextsType?.GetMethod("SetRoleTexts",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (setRoleTexts != null)
                    harmony.Patch(setRoleTexts,
                        postfix: new HarmonyMethod(typeof(MultiJester), nameof(SetRoleTextsPostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[MultiJester] SetUpRoleTextPatch.SetRoleTexts not found - the intro role card "
                        + "is not re-stamped (only relevant if the assignment arrives late).");

                UsefulTORStuffPlugin.Logger?.LogInfo("[MultiJester] exile win trigger patched.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiJester] TryPatch failed: {e}");
            }
        }
    }
}
