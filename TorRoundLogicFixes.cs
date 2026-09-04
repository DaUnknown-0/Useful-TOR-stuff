// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorRoundLogicFixes - role- and round-logic bugs from the 2026-08-23 full-source audit of TOR
 * 4.8.0 (Audits\TOR-AUDIT-2026-08-23.md) that are not covered by any existing fix file yet
 * (Audits\TOR-ABDECKUNG-2026-08-23.md, section 5, "Offen, ungefixt").
 *
 * SCOPE of this file: TOR-M1, M6, M10, M11, M17, M30, M32, M33 (M8 is written up below as
 * deliberately NOT patched, house rule), and the section-3
 * robustness findings assigned for this pass (Guesser.remainingShots foreign id, Hacker integer
 * division, win-trigger flags after RpcEndGame). Every one of those was re-read against the
 * current workspace source before writing a patch; three related section-3 claims turned out to
 * be either a false positive or not worth the risk of a rebuild, and are written up at the bottom
 * of this file instead of patched:
 *   - the repeated "if (min > max) min = max;" clamp is TOR's own deliberate, uniform pattern
 *     (15 call sites), not a forgotten swap;
 *   - Medium.getInfo reading the Medium.target static instead of its own parameters is real but
 *     currently inert, because the only caller always passes Medium.target's own fields;
 *   - the same method's unreachable switch case 3 is dead, unfinished, commented-out code; making
 *     it reachable would surface a broken message, so leaving it unreachable is the better state.
 *   - the modifier-budget accounting eating a failed Lover assignment is real but has no safe,
 *     small hook (see the writeup near the end).
 *
 * DESYNC POLICY (see CLAUDE.md): every patch below states, at the point of the fix, whether it
 * needs UsefulVersionHandshake.EveryoneHasMod() and why. The short version, repeated per fix
 * because the reasoning differs case by case:
 *   - A fix is safe WITHOUT the gate when the corrected decision is made once, locally, by the one
 *     client that owns it (the role holder clicking their own ability button, or the host running
 *     a host-only assignment/win-check pass), and the RESULT of that decision is what gets shared
 *     with everyone afterwards over TOR's existing RPCs. A client without the fix just keeps the
 *     old (buggy) behaviour for its own decisions; nobody else's simulation disagrees with it,
 *     because nobody else recomputes that same decision independently.
 *   - A fix NEEDS the gate when the corrected code runs inside an RPC HANDLER that every client
 *     executes identically off the same wire message (so a partially patched lobby would compute
 *     different results for the same event), or when a purely local, deterministic recomputation
 *     (no RNG, no RPC) would only run on modded clients and diverge from unmodded ones watching
 *     the exact same player.
 *
 * Kept in line with project conventions: HarmonyX runs every prefix that targets a method
 * regardless of what other prefixes return, so overrides against methods TOR itself already
 * patches are built as postfixes here, never as competing prefixes.
 */

using System;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.CustomGameModes;
using TheOtherRoles.Utilities;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class TorRoundLogicFixes {

        private static float lastLogAt = -100f;

        // Shared with the sibling Tor* fix files: these paths run per frame, per click or per RPC,
        // so a fix that floods the log is its own kind of bug.
        private static void ThrottledLog(string tag, string message) {
            if (Time.realtimeSinceStartup - lastLogAt < 5f) return;
            lastLogAt = Time.realtimeSinceStartup;
            UsefulTORStuffPlugin.Logger?.LogWarning($"[TorRoundLogicFixes/{tag}] {message}");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M1) roleCanUseVents compares the wrong player against Janitor/Mafioso
        //
        // Helpers.cs:443-450: the final branch of roleCanUseVents(this PlayerControl player) is
        // reached whenever the player's native role already has CanVent (i.e. a vanilla Impostor
        // role, which Janitor/Mafioso/Godfather also carry). It is meant to withhold venting from a
        // Janitor (who cleans instead) and from a Mafioso while the Godfather is still alive. But
        // both comparisons check "PlayerControl.LocalPlayer", not the "player" parameter that was
        // actually passed in:
        //     if (Janitor.janitor != null && Janitor.janitor == PlayerControl.LocalPlayer) ...
        //     else if (Mafioso.mafioso != null && Mafioso.mafioso == PlayerControl.LocalPlayer ...) ...
        // Every call site that matters for gameplay (Buttons.cs, UpdatePatch.cs, UsablesPatch.cs)
        // happens to invoke this on PlayerControl.LocalPlayer itself, so "player" and "LocalPlayer"
        // are the same reference there and the bug is silent. The one call site where they differ
        // is RPC.cs:405, inside the host-only "setRole" RPC handler
        // (AmongUsClient.Instance.AmHost gate), which promotes a freshly assigned custom role to
        // vanilla Engineer when it can vent. If the HOST happens to be the Janitor (or a Mafioso
        // with a living Godfather) that round, this bug reads the HOST's own role instead of the
        // player being evaluated, so the Engineer promotion is wrongly skipped or wrongly granted
        // for other players depending on what the host is holding that round.
        //
        // Full replacement of the small extension method rather than a transpiler, since Harmony
        // patches it cleanly (it is not itself a Harmony target anywhere in TOR).
        //
        // DESYNC: no gate needed. The one place this changes an actual decision is host-only
        // (AmHost-gated), and that decision is broadcast via player.RpcSetRole/CoSetRole, an RPC
        // every TOR client already understands regardless of whether it has this fix. Everywhere
        // else "player" already equals LocalPlayer, so the corrected comparison is a no-op there.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.roleCanUseVents))]
        static class RoleCanUseVentsPatch {
            public static bool Prefix(PlayerControl player, ref bool __result) {
                try {
                    bool roleCouldUse = false;
                    if (Engineer.engineer != null && Engineer.engineer == player) roleCouldUse = true;
                    else if (Jackal.canUseVents && Jackal.jackal != null && Jackal.jackal == player) roleCouldUse = true;
                    else if (Sidekick.canUseVents && Sidekick.sidekick != null && Sidekick.sidekick == player) roleCouldUse = true;
                    else if (Spy.canEnterVents && Spy.spy != null && Spy.spy == player) roleCouldUse = true;
                    else if (Vulture.canUseVents && Vulture.vulture != null && Vulture.vulture == player) roleCouldUse = true;
                    else if (Thief.canUseVents && Thief.thief != null && Thief.thief == player) roleCouldUse = true;
                    else if (player != null && player.Data?.Role != null && player.Data.Role.CanVent) {
                        if (Janitor.janitor != null && Janitor.janitor == player) roleCouldUse = false;
                        else if (Mafioso.mafioso != null && Mafioso.mafioso == player && Godfather.godfather != null && !Godfather.godfather.Data.IsDead) roleCouldUse = false;
                        else roleCouldUse = true;
                    }
                    __result = roleCouldUse;
                    return false;
                } catch (Exception e) {
                    ThrottledLog("M1", $"prefix failed, falling back to TOR original: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M6 + TOR-M17) The Vampire's natural 10s delayed bite kill has no meeting guard and
        // leaks Vampire.bitten on a suppressed attempt
        //
        // Buttons.cs:785-843 (vampireKillButton) starts an Effects.Lerp coroutine over Vampire.delay
        // seconds when the Vampire bites outside of garlic range. At p==1f it calls
        // Helpers.checkMurderAttemptAndKill(Vampire.vampire, Vampire.bitten, showAnimation: false)
        // and only sends the VampireSetBitten reset RPC (clearing Vampire.bitten) when that call
        // returned PerformKill. Every other outcome (target already dead, first-kill shield, Sheriff
        // armor block, Medic shield, and so on) leaves Vampire.bitten pointing at the old target
        // until the next meeting starts (Vampire.bitten = null in PlayerControl.StartMeeting's
        // prefix), so the "bitten" mark in RoleInfo keeps showing on a target the Vampire can no
        // longer finish off (TOR-M6).
        //
        // The same coroutine also has no explicit "is a meeting already open" check (TOR-M17): it
        // only relies on Vampire.bitten having already been nulled, either by that same StartMeeting
        // prefix or by Helpers.handleVampireBiteOnBodyReport (which resolves the bite synchronously
        // on whichever client calls CmdReportDeadBody, BEFORE MeetingHud exists, and always resets
        // Vampire.bitten regardless of outcome - that helper does not have TOR-M6's bug). Both of
        // those depend on either a local StartMeeting call or a network round trip having already
        // completed by the time the Vampire's own 10s timer expires. Neither is guaranteed: if the
        // Vampire's own client's timer reaches p==1f while MeetingHud is already open (a meeting
        // called by a different client whose reset has not arrived yet), the kill still executes
        // while the vote is in progress.
        //
        // Both are fixed at the one real hook point both bugs share: Helpers.checkMurderAttemptAndKill,
        // the exact call the coroutine makes. isMeetingStart distinguishes this call
        // (Buttons.cs's coroutine passes the default, false) from Helpers.handleVampireBiteOnBodyReport
        // (which always passes true and already resets unconditionally on its own), so only the
        // buggy call site is touched.
        //   - Prefix (M17): if this is the Vampire's own bite target and a meeting is already open,
        //     skip the attempt outright (SuppressKill) instead of letting a kill land mid-vote, and
        //     clear Vampire.bitten via the same RPC the coroutine would have sent on success.
        //   - Postfix (M6): for every other suppressed/blanked outcome on the same pairing, send that
        //     same reset. DelayVampireKill is left alone: that result starts its OWN nested delay
        //     (Helpers.cs:560-570) which owns its own reset once it resolves. The postfix cannot
        //     double-fire after the prefix already reset Vampire.bitten to null, because at that point
        //     "target == Vampire.bitten" is false (target is still the original player reference).
        //
        // DESYNC: no gate needed. vampireKillButton's onClick only ever runs on the Vampire's own
        // client (CouldUse gates it to Vampire.vampire == LocalPlayer), so this whole system is
        // already client-authoritative by TOR's own design (see the comment on
        // checkMurderAttemptAndKill itself: "combining modded and unmodded versions is impossible").
        // Whichever outcome this fix picks is broadcast through the exact same VampireSetBitten /
        // UncheckedMurderPlayer RPCs TOR already uses, understood identically by modded and
        // unmodded clients alike. A Vampire without the mod keeps the old bug for their own bite;
        // nobody else's client independently recomputes this decision to disagree with it.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMurderAttemptAndKill))]
        static class VampireDelayedKillPatch {
            public static bool Prefix(PlayerControl killer, PlayerControl target, bool isMeetingStart, ref MurderAttemptResult __result) {
                try {
                    if (isMeetingStart) return true; // handleVampireBiteOnBodyReport(): meeting has not opened yet at this call site.
                    if (!IsVampireBiteCall(killer, target)) return true;
                    if (MeetingHud.Instance == null) return true; // Normal path: no meeting open, let the real check decide.

                    __result = MurderAttemptResult.SuppressKill;
                    ResetBitten();
                    ThrottledLog("M17", "vampire bite delay expired during an open meeting - kill suppressed.");
                    return false;
                } catch (Exception e) {
                    ThrottledLog("M17", $"meeting guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }

            public static void Postfix(PlayerControl killer, PlayerControl target, bool isMeetingStart, MurderAttemptResult __result) {
                try {
                    if (isMeetingStart) return; // handleVampireBiteOnBodyReport() already resets unconditionally.
                    if (!IsVampireBiteCall(killer, target)) return;
                    if (__result != MurderAttemptResult.SuppressKill && __result != MurderAttemptResult.BlankKill) return;

                    ResetBitten();
                    ThrottledLog("M6", "vampire bite delay resolved without a kill - stale bitten mark cleared.");
                } catch (Exception e) {
                    ThrottledLog("M6", $"stale bitten cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }

            private static bool IsVampireBiteCall(PlayerControl killer, PlayerControl target) {
                if (killer == null || Vampire.vampire == null || killer != Vampire.vampire) return false;
                if (target == null || Vampire.bitten == null || target != Vampire.bitten) return false;
                return true;
            }

            // TOR's CustomRPC enum is internal, so the id is resolved once by reflection with the
            // value read from the source as a documented fallback (the technique MultiJester and
            // TorUpstreamFixes already use for TOR's internal enums).
            private static byte cachedRpcId;

            private static byte VampireSetBittenRpcId {
                get {
                    if (cachedRpcId != 0) return cachedRpcId;
                    cachedRpcId = 121; // documented fallback: RPC.cs enum position of VampireSetBitten
                    try {
                        var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.CustomRPC");
                        if (e != null && Enum.IsDefined(e, "VampireSetBitten"))
                            cachedRpcId = Convert.ToByte(Enum.Parse(e, "VampireSetBitten"));
                        else
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[TorRoundLogicFixes/M6] CustomRPC.VampireSetBitten not found - using the fallback id.");
                    } catch { }
                    return cachedRpcId;
                }
            }

            private static void ResetBitten() {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, VampireSetBittenRpcId, Hazel.SendOption.Reliable, -1);
                writer.Write(byte.MaxValue);
                writer.Write(byte.MaxValue);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                RPCProcedure.vampireSetBitten(byte.MaxValue, byte.MaxValue);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M8) Crewmate win fires while a live Arsonist/Vulture could still win on their own
        //
        // DELIBERATELY NOT PATCHED (house rule, playtests 2026-09-04). The audit read this as a
        // bug: CheckAndEndGameForCrewmateWin ends the round on TeamImpostorsAlive == 0 &&
        // TeamJackalAlive == 0 without asking whether an Arsonist or Vulture is still alive and
        // could still trigger their own win later. A guard that held the crew win back for them
        // lived here from 2026-09-03 to 2026-09-04 and produced exactly the round it was meant to
        // "fix": both Impostors dead, meeting after meeting kept running because a Vulture still had
        // bodies to eat. The user's rule for this pack is that neither of them may hold up a crew
        // win - the Vulture because he cannot kill and so counts as crew for every team check, and
        // the Arsonist by explicit decision the same day ("auch ein Arsonist soll einen Crew-Sieg
        // nicht verhindern"). TOR's original order therefore stands: a neutral who has not fired
        // his own win by the time the crew's lands simply loses. Do not reintroduce a guard here.
        //
        // (Pursuer never belonged in such a guard in the first place: no win-trigger of their own,
        // they win as an additional winner alongside the crew.)

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M10) HandleGuesser.tasksToUnlock keeps the Guesser-GM value in Classic mode
        //
        // Utilities\HandleGuesser.cs:38-55, clearAndReload(): the "if (isGuesserGm)" branch sets
        // tasksToUnlock from CustomOptionHolder.guesserGamemodeCrewGuesserNumberOfTasks, but the
        // "else" branch (Classic mode) never touches tasksToUnlock at all - there is no equivalent
        // Classic-mode option for it in the first place, since Classic's Guesser role was never
        // meant to have a task-gate. If an earlier round in the same lobby was the Guesser game
        // mode, that value survives into a later Classic round untouched. The only place it is read
        // outside of the GM (RoleInfo.cs:257 is itself gated behind HandleGuesser.isGuesserGm) is
        // MeetingPatch.cs:622, which decides whether the Classic-mode crew Guesser even gets a
        // Shoot button for a given target on their own client. A stale non-zero value silently
        // reintroduces a task requirement Classic mode never had.
        //
        // Postfix on clearAndReload resetting tasksToUnlock to 0 specifically for the Classic branch,
        // matching what a Classic round always should have had.
        //
        // DESYNC: no gate needed. MeetingPatch.cs:622 only ever evaluates PlayerControl.LocalPlayer,
        // so this purely decides whether the Guesser's OWN client shows them their own Shoot button;
        // no other client independently recomputes this for them. Once clicked, the resulting kill
        // goes out over the same guess RPC every TOR client understands. A Guesser without the mod
        // just keeps the old (overly restrictive) gate for themselves.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(HandleGuesser), nameof(HandleGuesser.clearAndReload))]
        static class GuesserClassicTasksToUnlockPatch {
            public static void Postfix() {
                try {
                    if (!HandleGuesser.isGuesserGm) HandleGuesser.tasksToUnlock = 0;
                } catch (Exception e) {
                    ThrottledLog("M10", $"reset failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M11) Submerged fires WrapUpPostfix twice for the same exile
        //
        // ExileControllerPatch.cs's ExileControllerWrapUpPatch registers WrapUpPostfix from THREE
        // hooks: a postfix on ExileController.WrapUp, a postfix on
        // AirshipExileController.WrapUpAndSpawn, and (Submerged only) a prefix on
        // UnityEngine.Object.Destroy(GameObject) that fires again whenever an object named
        // "ExileCutscene" is destroyed. On Submerged, both the generic WrapUp postfix and this
        // extra Destroy-based hook end up calling WrapUpPostfix for the same exile event, and the
        // method is not written to tolerate that: Invert.meetings-- runs twice per meeting instead
        // of once, ending the Invert effect a full meeting early. The Deputy promotion inside it is
        // already accidentally self-guarding (deputyCheckPromotion nulls Deputy.deputy on success,
        // so the second call's own early-return guard skips it), and the trap-triggerable coroutine
        // just runs twice redundantly with no functional harm - but rather than reason about every
        // statement in that ~100-line method individually, one dedup guard at the entry point
        // covers all of them the same way.
        //
        // WrapUpPostfix is private static on the same internal ExileControllerWrapUpPatch class,
        // resolved by reflection the same way as the other internal-method patches in this file.
        // The guard is a short real-time window (the two hooks fire within the same exile-cutscene
        // sequence, at most a couple of seconds apart; a real second meeting is always tens of
        // seconds away at minimum) keyed on the exiled player's id (byte.MaxValue standing in for
        // "nobody was exiled"), not on frame count, since the Destroy-based hook can legitimately
        // fire a few frames after WrapUp itself depending on the cutscene animation.
        //
        // DESYNC: gated on UsefulVersionHandshake.EveryoneHasMod(). WrapUpPostfix runs identically
        // on every client (it is not host-only), and the double-fire itself is currently
        // deterministic and IDENTICAL across all TOR clients on Submerged, so today it is not a
        // source of desync by itself. But Invert.meetings is a purely local per-client countdown
        // with no further RPC to resync it, so deduplicating it ONLY on modded clients would make
        // the Invert effect last one meeting longer for them than for unmodded clients watching the
        // exact same affected player in the same lobby - a real, player-visible divergence. Gating
        // keeps every client either fully on the old (buggy) behaviour or fully on the fixed one.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch]
        static class SubmergedExileWrapUpDedupPatch {
            private static MethodBase _target;
            private const float DedupWindowSeconds = 4f;
            private static bool _hasLast;
            private static byte _lastExiledId = byte.MaxValue;
            private static float _lastTime = -100f;

            public static MethodBase TargetMethod() {
                if (_target != null) return _target;
                try {
                    var type = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.ExileControllerWrapUpPatch");
                    _target = type?.GetMethod("WrapUpPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundLogicFixes/M11] TargetMethod lookup failed: {e}");
                }
                return _target;
            }

            public static bool Prepare() {
                bool ok = TargetMethod() != null;
                if (!ok) UsefulTORStuffPlugin.Logger?.LogWarning("[TorRoundLogicFixes/M11] ExileControllerWrapUpPatch.WrapUpPostfix not found - dedup disabled.");
                return ok;
            }

            public static bool Prefix(PlayerControl exiled) {
                try {
                    if (!UsefulVersionHandshake.EveryoneHasMod()) return true; // see DESYNC note above

                    byte id = exiled != null ? exiled.PlayerId : byte.MaxValue;
                    float now = Time.realtimeSinceStartup;
                    if (_hasLast && id == _lastExiledId && (now - _lastTime) < DedupWindowSeconds) {
                        ThrottledLog("M11", "duplicate Submerged WrapUpPostfix for the same exile skipped.");
                        return false;
                    }
                    _hasLast = true;
                    _lastExiledId = id;
                    _lastTime = now;
                } catch (Exception e) {
                    ThrottledLog("M11", $"dedup guard failed: {e.GetType().Name}: {e.Message}");
                }
                return true;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M30) The Spy/Mini blocked role pairing is never enforced
        //
        // CustomOptionHolder.cs registers blockedRolePairings[Spy] = [Mini] and the reverse, and
        // RoleAssignmentPatch.cs's assignEnsuredRoles/assignChanceRoles both honour that dictionary
        // when assigning MAIN roles. But Mini is not a main role at all - it is a modifier, handed
        // out later by assignModifiers()/assignModifiersToPlayers(), which never consults
        // blockedRolePairings. So the declared block has nothing left to remove by the time Mini is
        // actually assigned, and a Spy can end up with the Mini modifier despite the option
        // explicitly forbidding that pairing. TOR already special-cases exactly this shape of
        // problem for Shifter (assignModifiersToPlayers excludes Spy.spy from the Shifter pool by
        // hand, RoleAssignmentPatch.cs:568-571) - Mini was simply never given the same treatment.
        //
        // Fixed at the lowest-level call, setModifierToRandomPlayer(modifierId, playerList, flag):
        // when the modifier being rolled is Mini, Spy is removed from the candidate list before the
        // random pick, mirroring the Shifter exclusion exactly. This only touches the Mini call
        // (Lover and every other modifier pass a different modifierId and are unaffected), leaves
        // Spy eligible for all other modifiers, and degrades gracefully to "nobody gets Mini this
        // round" if Spy was the only remaining candidate (setModifierToRandomPlayer already returns
        // early on an empty list).
        //
        // setModifierToRandomPlayer is private static on RoleManagerSelectRolesPatch, resolved by
        // reflection like the other internal-method patches in this file.
        //
        // DESYNC: no gate needed. Role/modifier assignment only actually executes on the host
        // (RoleManager.SelectRoles itself only runs host-side in vanilla Among Us), and the result
        // is broadcast through the same SetModifier RPC TOR already uses for every modifier. This is
        // host-authoritative exactly like the other RoleAssignmentPatch-facing fixes in the sibling
        // Tor* files.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch]
        static class SpyMiniBlockedPairingPatch {
            private static MethodBase _target;

            public static MethodBase TargetMethod() {
                if (_target != null) return _target;
                try {
                    var type = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                    _target = type?.GetMethod("setModifierToRandomPlayer", BindingFlags.NonPublic | BindingFlags.Static);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorRoundLogicFixes/M30] TargetMethod lookup failed: {e}");
                }
                return _target;
            }

            public static bool Prepare() {
                bool ok = TargetMethod() != null;
                if (!ok) UsefulTORStuffPlugin.Logger?.LogWarning("[TorRoundLogicFixes/M30] setModifierToRandomPlayer not found - Spy/Mini guard disabled.");
                return ok;
            }

            public static void Prefix(byte modifierId, System.Collections.Generic.List<PlayerControl> playerList) {
                try {
                    if (modifierId != (byte)RoleId.Mini) return;
                    if (Spy.spy == null || playerList == null) return;
                    if (playerList.RemoveAll(x => x == Spy.spy) > 0)
                        ThrottledLog("M30", "Spy excluded from the Mini modifier pool (blocked pairing).");
                } catch (Exception e) {
                    ThrottledLog("M30", $"exclusion failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ======================================================================================
        // TOR-M32) Lawyer vision during Lights Out: NOT FIXED HERE, ON PURPOSE
        //
        // The finding is real: ShipStatusPatch collapses the Lawyer's vision multiplier to roughly
        // MinLightRadius while the lights are out, so the ability is effectively off in exactly the
        // situation it matters. An earlier version of this file corrected it with another postfix on
        // ShipStatus.CalculateLightRadius. That was removed on review.
        //
        // That method has ONE owner in this mod family by deliberate design. Unknown's Collection
        // consolidated every vision effect into a single postfix after an audit found several of
        // them fighting over __result (AUDIT-2026-08-11, M-5), and UCVision.cs states the rule in
        // its own header: "do NOT add another CalculateLightRadius patch". The pipeline is now
        // exactly UCVision (default priority) and ChanceMod (Priority.Last, multiplicative).
        // A third writer from a third mod is precisely the situation that consolidation ended.
        //
        // The Lawyer correction belongs INSIDE that pipeline, as one more stage in UCVision, not as
        // a competing patch from here. Left for whoever next opens UCVision.cs, with this note so
        // the reason is on record rather than rediscovered.
        //
        // (TorUpstreamFixes' M-35 guard on the same method is a FINALIZER, not a postfix: it only
        // runs when TOR's own prefix threw, and never writes a radius that anyone else computed.
        // That one does not join the pipeline, it protects it.)
        // ======================================================================================

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M33) A sealed vent traps whoever is still inside it
        //
        // UsablesPatch.cs's VentCanUsePatch.Prefix, lines 29-33:
        //     if (__instance.name.StartsWith("SealedVent_")) { canUse = couldUse = false; ...; return false; }
        // This blocks ALL use of a sealed vent unconditionally, with no check for whether the local
        // player is currently @object.inVent. Compare the normal (unsealed) path a few lines below,
        // which explicitly ORs in @object.inVent so an occupant can always still exit even if their
        // role could not normally vent. SecurityGuard sealing a vent that someone happens to be
        // inside (or that is their only way out of a dead-end vent network) leaves that player stuck
        // with no Exit option until the vent is unsealed or the round otherwise resets.
        //
        // Fixed as a postfix on Vent.CanUse (again a postfix, since TOR's own prefix already returns
        // false here): for a sealed vent, if the target player is inside it, replicate the SAME
        // distance/obstruction check TOR's own unsealed branch performs (Vector2.Distance against
        // UsableDistance, PhysicsHelpers.AnythingBetween), which is naturally true for someone who
        // is already standing inside the vent. Entry into a sealed vent (inVent == false) is left
        // blocked exactly as TOR intends.
        //
        // DESYNC: no gate needed. Vent.CanUse only gates whether the LOCAL player's own Exit prompt
        // is clickable; nobody else's client evaluates this for them. Once clicked, Vent.Use sends
        // the normal RpcExitVent, understood identically by every TOR client. A trapped player
        // without the mod stays trapped, same as before; nobody else's simulation disagrees.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Vent), nameof(Vent.CanUse))]
        static class SealedVentTrappedOccupantPatch {
            public static void Postfix(Vent __instance, [HarmonyArgument(0)] NetworkedPlayerInfo pc, ref float __result, [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (__instance == null || __instance.name == null || !__instance.name.StartsWith("SealedVent_")) return;
                    PlayerControl player = pc?.Object;
                    if (player == null || !player.inVent) return; // Only rescue a trapped occupant, never permit a fresh entry.

                    couldUse = !pc.IsDead && (player.CanMove || player.inVent);
                    canUse = couldUse;
                    float distance = float.MaxValue;
                    if (canUse) {
                        Vector3 center = player.Collider.bounds.center;
                        Vector3 position = __instance.transform.position;
                        distance = Vector2.Distance(center, position);
                        canUse &= distance <= __instance.UsableDistance
                            && !PhysicsHelpers.AnythingBetween(player.Collider, center, position, Constants.ShipOnlyMask, false);
                    }
                    __result = distance;
                    if (canUse) ThrottledLog("M33", "sealed vent occupant allowed to exit.");
                } catch (Exception e) {
                    ThrottledLog("M33", $"trapped-occupant check failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // Section 3) Guesser.remainingShots' else-branch decrements the Evil count for a foreign id
        //
        // TheOtherRoles.cs:1289-1296:
        //     int remainingShots = remainingShotsEvilGuesser;
        //     if (niceGuesser != null && niceGuesser.PlayerId == playerId) { ...nice path... }
        //     else if (shoot) { remainingShotsEvilGuesser = Max(0, remainingShotsEvilGuesser - 1); }
        // The else branch fires for ANY playerId that is not the nice guesser, including one that
        // is not evilGuesser either (evilGuesser == null, or a genuinely unrelated id). In today's
        // only mutating call site (RPCProcedure.guesserShoot, RPC.cs:1019, killerId always the
        // player who fired the shot) this is normally inert, since guesserOnClick is only reachable
        // through UI gated behind HandleGuesser.isGuesser(LocalPlayer). It is still a real
        // correctness gap in a method that is meant to read "which of the two guessers is this id,
        // if either" and instead silently assumes "not nice" means "must be evil".
        //
        // Full replacement of the small method with an explicit check for both ids. The nice-match
        // and confirmed-evil-match paths are byte-for-byte identical to TOR's own logic; only the
        // genuinely-foreign-id case (neither guesser) changes, and even that only once
        // EveryoneHasMod() is true (see DESYNC below) - report zero remaining shots instead of
        // silently consuming a shot from the wrong counter.
        //
        // DESYNC: gated on UsefulVersionHandshake.EveryoneHasMod() for the foreign-id branch only.
        // Unlike the click-gate fixes above, remainingShots(shoot: true) runs inside
        // RPCProcedure.guesserShoot, which every client executes identically off the same wire
        // message - a genuine RPC handler, not a local-only decision. Without the gate, a foreign id
        // would leave remainingShotsEvilGuesser different on modded vs. unmodded clients after the
        // same RPC. Until everyone has the mod, the else branch keeps TOR's original (buggy)
        // behaviour so every client stays in lockstep; the nice/evil matched paths never differ from
        // TOR's own result and are not gated.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Guesser), nameof(Guesser.remainingShots))]
        static class GuesserForeignIdShotsPatch {
            public static bool Prefix(byte playerId, bool shoot, ref int __result) {
                try {
                    bool isNice = Guesser.niceGuesser != null && Guesser.niceGuesser.PlayerId == playerId;
                    if (isNice) {
                        __result = Guesser.remainingShotsNiceGuesser;
                        if (shoot) Guesser.remainingShotsNiceGuesser = Mathf.Max(0, Guesser.remainingShotsNiceGuesser - 1);
                        return false;
                    }

                    bool isEvil = Guesser.evilGuesser != null && Guesser.evilGuesser.PlayerId == playerId;
                    if (isEvil || !UsefulVersionHandshake.EveryoneHasMod()) {
                        // Either a confirmed Evil Guesser match (always correct), or the lockstep
                        // fallback: reproduce TOR's original else-branch so an unmodded client in the
                        // same lobby computes the exact same counters from the exact same RPC.
                        __result = Guesser.remainingShotsEvilGuesser;
                        if (shoot) Guesser.remainingShotsEvilGuesser = Mathf.Max(0, Guesser.remainingShotsEvilGuesser - 1);
                        return false;
                    }

                    // Foreign id and everyone has the mod: nothing to decrement.
                    __result = 0;
                    return false;
                } catch (Exception e) {
                    ThrottledLog("S3-Guesser", $"prefix failed, falling back to TOR original: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // Section 3) Hacker's integer division loses one charge on an odd tool count
        //
        // TheOtherRoles.cs:734-735, Hacker.clearAndReload():
        //     chargesVitals = Mathf.RoundToInt(CustomOptionHolder.hackerToolsNumber.getFloat()) / 2;
        //     chargesAdminTable = Mathf.RoundToInt(CustomOptionHolder.hackerToolsNumber.getFloat()) / 2;
        // Both sides use integer division. hackerToolsNumber defaults to 5 (range 1-30, step 1), and
        // 5 / 2 truncates to 2 twice, so the Hacker gets 2 + 2 = 4 total charges out of a configured
        // 5 - one charge is simply lost on every odd tool count.
        //
        // Postfix on Hacker.clearAndReload recomputing both fields so they always sum exactly to the
        // configured total, giving the remainder to chargesAdminTable (chargesVitals stays the
        // floor half, matching what the buggy code already produced for that one).
        //
        // DESYNC: no gate needed. This purely decides how many times the Hacker's OWN two buttons
        // can be clicked before running out (Buttons.cs decrements chargesVitals/chargesAdminTable
        // locally on click, no RPC involved in the counter itself); nobody else's client tracks or
        // compares the Hacker's remaining charges. A Hacker without the mod just keeps one fewer
        // charge than configured, same as before.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Hacker), nameof(Hacker.clearAndReload))]
        static class HackerToolChargeSplitPatch {
            public static void Postfix() {
                try {
                    int total = Mathf.RoundToInt(CustomOptionHolder.hackerToolsNumber.getFloat());
                    Hacker.chargesVitals = total / 2;
                    Hacker.chargesAdminTable = total - Hacker.chargesVitals;
                } catch (Exception e) {
                    ThrottledLog("S3-Hacker", $"charge split recompute failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // Section 3) Win-trigger flags are not cleared until the NEXT round's role assignment
        //
        // Mini.triggerMiniLose, Jester.triggerJesterWin, Arsonist.triggerArsonistWin,
        // Vulture.triggerVultureWin and Lawyer.triggerProsecutorWin are all one-shot "end the game
        // now" flags, consumed by CheckEndCriteriaPatch.Prefix and only ever reset inside each
        // role's own clearAndReload(), which runs at the START of the next round's role assignment
        // (RoleManagerSelectRolesPatch.Postfix -> resetVariables/assignRoles), not right after
        // RpcEndGame fires. In the normal Among Us lifecycle the ShipStatus/LogicGameFlowNormal
        // instance that would read them is torn down with the round, so this window is not
        // observably reachable today - but that lifecycle guarantee lives entirely outside this
        // codebase, and resetting a one-shot flag that has already been consumed is always safe.
        //
        // Postfix on AmongUsClient.OnGameEnd (already a TOR-patched Harmony target, so this is a
        // postfix rather than a competing prefix per the HarmonyX rule) clearing all five flags
        // immediately once the round result is known, closing the gap defensively regardless of
        // whether it is exploitable under the current game lifecycle.
        //
        // DESYNC: no gate needed. These flags are pure per-client local state, never read by
        // anything except each client's own next CheckEndCriteria pass, and clearAndReload will set
        // them to the exact same false value moments later regardless. Resetting them slightly
        // earlier changes nothing observable on any client, modded or not.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class WinTriggerFlagsResetPatch {
            public static void Postfix() {
                try {
                    Mini.triggerMiniLose = false;
                    Jester.triggerJesterWin = false;
                    Arsonist.triggerArsonistWin = false;
                    Vulture.triggerVultureWin = false;
                    Lawyer.triggerProsecutorWin = false;
                } catch (Exception e) {
                    ThrottledLog("S3-WinTrigger", $"flag reset failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        /*
         * ══════════════════════════════════════════════════════════════════════════════════════
         * VERIFIED, NOT PATCHED
         * ══════════════════════════════════════════════════════════════════════════════════════
         *
         * Section 3) "Min>Max-Clamp verwirft die obere Grenze statt zu tauschen" - FEHLBEFUND.
         * The exact pattern "if (min > max) min = max;" appears with byte-for-byte identical shape
         * at 15 separate call sites across the codebase: Modules\CustomOptions.cs (8x, both the
         * live-option and the saved-preset decode paths), Modules\RoleDraft.cs (2x), Patches\
         * RoleAssignmentPatch.cs (4x, crewmate/neutral/impostor/modifier count pairs), and
         * TheOtherRoles.cs:1865 (report delay). That level of uniform repetition across unrelated
         * files is a deliberate, consistent defensive fallback, not a forgotten swap: whenever a
         * host misconfigures a Min/Max pair, TOR resolves it to the lower, always-valid bound
         * rather than throwing from rnd.Next(min, max+1). Patching this "properly" would mean either
         * changing one arbitrarily chosen call site (inconsistent with the other 14) or rewriting
         * all 15 to swap instead of clamp, for a purely cosmetic difference in an edge case that
         * only occurs when a host deliberately sets min above max. Not worth the blast radius.
         *
         * Section 3) "Medium.getInfo liest Static statt Parameter" - verified, currently inert.
         * TheOtherRoles.cs:1439, Medium.getInfo(PlayerControl target, PlayerControl killer,
         * DeadPlayer.CustomDeathReason deathReason): only the opening suicide/teamkill checks
         * (lines 1445-1455) use the target/killer parameters. Every line after that (the random
         * flavour-text branch and the "additional info" appendix) reads the Medium.target STATIC
         * field instead - Medium.target.wasCleaned, Medium.target.killerIfExisting,
         * Medium.target.player, and so on. This is a real footgun for any future second caller, but
         * today's only call site (Buttons.cs:1513) is
         *     Medium.getInfo(Medium.target.player, Medium.target.killerIfExisting, Medium.target.deathReason)
         * which derives every argument FROM Medium.target itself, so target == Medium.target.player
         * and killer == Medium.target.killerIfExisting hold by construction. Reading the static
         * therefore currently returns byte-for-byte the same value the parameter would have. Fixing
         * it would mean rebuilding roughly ninety lines of branching flavour-text logic (a real risk
         * of introducing a typo into player-visible ghost chat) for zero observable behaviour change
         * today, since there is no second caller to diverge for yet. Left open for awareness rather
         * than patched.
         *
         * Section 3) Medium.getInfo's "unreachable case 3" - FEHLBEFUND in the direction implied.
         * TheOtherRoles.cs:1508, `switch (rnd.Next(3))` only ever yields 0/1/2, so the `case 3:`
         * block at line 1521 can never run. That block is dead, commented-out, unfinished developer
         * code (`//count = alivePlayersList.Where(pc =>` with no closing logic and no message
         * assigned), which would print a broken "0 [condition] were still alive" line with an
         * empty condition string if it were ever reached. Changing the roll to rnd.Next(4) to make
         * it reachable, which is the fix the audit's phrasing implies, would be a regression: it
         * would surface that half-finished branch in live ghost chat instead of leaving it dormant.
         * The current mismatch is accidentally the correct behaviour, so nothing was changed here.
         *
         * Section 3) "Modifier-Budget frisst den Lover-Sentinel" - verified, deliberately left open.
         * RoleAssignmentPatch.cs:448-462, inside assignModifiers(): the Lover roll always executes
         *     modifierCount--;
         * once it wins the RNG check, regardless of whether setModifierToRandomPlayer actually
         * placed a Lover on anyone. setModifierToRandomPlayer returns the byte.MaxValue sentinel on
         * an empty candidate list rather than throwing, and nothing here checks for it - so a round
         * that rolls "assign Lover" but has no eligible crew/impostor players left (all already
         * holding an exclusive modifier, or simply too few players) still consumes one unit of the
         * shared modifier budget for a Lover pairing that was never actually created, silently
         * costing some OTHER modifier its slot. This is a real accounting bug, but it lives entirely
         * inside the arithmetic on a local variable (modifierCount) in the middle of a single ~70
         * line method with no separately callable seam near it; the only two hooks available from
         * outside are the whole method (assignModifiers, a public entry point but far too broad a
         * rebuild for one counter) and setModifierToRandomPlayer (already patched above for TOR-M30,
         * but it has no visibility into the caller's local modifierCount to correct it from there).
         * Reproducing the fix safely would mean re-implementing the entire modifier assignment pass
         * from outside TOR, risking a change to the actual probability distribution of every other
         * modifier for a bug whose worst case is "one fewer modifier than configured, in the
         * relatively rare case the Lover roll wins with too few eligible players left". Left open
         * per this file's rule 5: no small, safe hook exists, so no patch was written.
         */
    }
}
