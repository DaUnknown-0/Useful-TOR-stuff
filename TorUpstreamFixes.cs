// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorUpstreamFixes - the bugs from the 2026-08-23 full-source audit of TOR 4.8.0 that none of the
 * existing fix files reach (Audits\TOR-AUDIT-2026-08-23.md).
 *
 * WHAT IS ALREADY COVERED ELSEWHERE, and therefore deliberately absent here (verified file by file
 * before writing a single line, because a second fix on the same defect is worse than none):
 *   TOR-H2  /role freeze .................. TorNullGuards #1 (method replaced outright)
 *   TOR-H10 draft softlock ................ HostFixPlugin Fix 3 (host) + TorAuditFixes A9 (client)
 *   TOR-M3  MushroomSabotageActive ........ TorPerfFixes #2
 *   TOR-M16 PropHunt disguise scale ....... TorAuditFixes B11
 *   TOR-M22 /gm crash ..................... TorAuditFixes A7 (+ B4 for the desync)
 *   TOR-M41 Portal.startTeleport .......... TorNullGuards #11
 *   TOR-M45 EventUtility.handleKick ....... TorNullGuards #9
 *   the ShareOptions chunk abort .......... OptionSyncFix (both sides)
 *
 * AND ONE THE AUDIT GOT WRONG: TOR-H7 claims a guaranteed InvalidOperationException in the Snitch
 * kill path (PlayerControlPatch.cs:1339-1343, Remove inside a lazy Where over a Dictionary). That
 * was true on .NET Framework. Since .NET Core 3.0 Dictionary.Remove does not bump the version
 * counter, so an in-flight enumerator survives it - measured on net6.0, the target framework here:
 * single and multi-match removes both completed normally, while a control that ADDED during the
 * same enumeration threw as expected. No fix, because there is no bug.
 *
 * All fixes here are option-less and NOT behind UTSGate, for the same reason the other Tor* files
 * are not: they turn a crash, a freeze or a plainly unintended outcome into TOR's own documented
 * behaviour, and none of them hands anybody an advantage.
 *
 * TWO THAT ARE LEFT ALONE ON PURPOSE, so the next audit does not read the gap as an oversight:
 *
 *  TOR-H1 (random map picks the wrong settings preset). CustomOptionHolder.presets has no Fungle
 *      entry, while the probability list does, so the `chosenMapId + 2` mapping sends Fungle to
 *      "Random Preset Submerged" and Submerged past the end of the array. The clean fix is to add
 *      the missing entry - but that RENUMBERS every preset from index 6 upwards, so every host who
 *      has "Random Preset Submerged" selected would silently switch to a Fungle preset, and saved
 *      configs would decode differently. That is a migration decision about the user's stored
 *      settings, not a bug fix to make unilaterally.
 *
 *  TOR-M2 (Armored vs Sheriff, Helpers.cs:524). The operator precedence in that condition lets a
 *      Sheriff shoot through armor without consuming it. Fixing it means rewriting the middle of
 *      checkMuderAttempt, which is the single busiest decision point in the whole mod family:
 *      AntiStartKill, NewcomerShield, BomberArmoredFix and MultiModifiers all hang off it. A
 *      rewrite from outside would have to reproduce every one of TOR's branches exactly, and
 *      getting it subtly wrong costs kills rather than logging an error. Worth doing with a
 *      playtest behind it, not as part of an audit sweep.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class TorUpstreamFixes {

        private static float lastLogAt = -100f;

        // The Tor* files all log sparingly: these paths run per frame or per RPC, and a fix that
        // floods the log is its own kind of bug.
        private static void ThrottledLog(string tag, string message) {
            if (Time.realtimeSinceStartup - lastLogAt < 5f) return;
            lastLogAt = Time.realtimeSinceStartup;
            UsefulTORStuffPlugin.Logger?.LogWarning($"[TorUpstreamFixes/{tag}] {message}");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-H5) The Bounty Hunter hunts impostors
        //
        // PlayerControlPatch.cs:675 builds the candidate pool with
        //     p != p.Data.Role.IsImpostor
        // which compares a PlayerControl REFERENCE against a bool. That is never equal, so the term
        // is always true and the clause that was meant to keep impostors out of the pool does
        // nothing at all. (It is the same class of typo as the `||` on :654 that TorAuditFixes A1
        // already handles - but A1 only takes over the IsDead branch, so this one, in the alive
        // path, was still live.)
        //
        // Fixing it in place would need a transpiler into the middle of a 60-line method. A postfix
        // is enough and far safer: the pool is only ever consulted to pick a NEW bounty, so the only
        // observable consequence is which player ends up in BountyHunter.bounty. If that is an
        // impostor, this picks again from a correctly filtered pool and re-broadcasts the ghost info
        // exactly the way TOR does (same RPC, same three writes) so spectators stay in step.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "bountyHunterUpdate")]
        static class BountyHunterImpostorTargetPatch {
            public static void Postfix() {
                try {
                    var bh = BountyHunter.bountyHunter;
                    if (bh == null || PlayerControl.LocalPlayer != bh) return;
                    if (bh.Data == null || bh.Data.IsDead) return;

                    var bounty = BountyHunter.bounty;
                    if (bounty == null || bounty.Data == null) return;
                    if (bounty.Data.Role == null || !bounty.Data.Role.IsImpostor) return;   // already fine

                    // TOR's own exclusions, with the broken term written the way it was meant.
                    var pool = new List<PlayerControl>();
                    foreach (PlayerControl p in PlayerControl.AllPlayerControls) {
                        if (p == null || p.Data == null) continue;
                        if (p.Data.IsDead || p.Data.Disconnected) continue;
                        if (p.Data.Role != null && p.Data.Role.IsImpostor) continue;        // THE FIX
                        if (p == Spy.spy) continue;
                        if (p == Sidekick.sidekick && Sidekick.wasTeamRed) continue;
                        if (p == Jackal.jackal && Jackal.wasTeamRed) continue;
                        if (p == Mini.mini && !Mini.isGrownUp()) continue;
                        var partner = Lovers.getPartner(bh);
                        if (partner != null && p == partner) continue;
                        pool.Add(p);
                    }
                    // Everyone left alive is an impostor (or otherwise excluded): leave TOR's choice
                    // alone rather than clearing the bounty, which would NRE further down its own
                    // method on the next tick.
                    if (pool.Count == 0) return;

                    BountyHunter.bounty = pool[TheOtherRoles.TheOtherRoles.rnd.Next(0, pool.Count)];

                    MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                        PlayerControl.LocalPlayer.NetId, (byte)CustomRPCShareGhostInfo, SendOption.Reliable, -1);
                    writer.Write(PlayerControl.LocalPlayer.PlayerId);
                    writer.Write(GhostInfoTypeBountyTarget);
                    writer.Write(BountyHunter.bounty.PlayerId);
                    AmongUsClient.Instance.FinishRpcImmediately(writer);

                    ThrottledLog("H5", $"bounty was an impostor ({bounty.Data.PlayerName}) - re-rolled to "
                                       + $"{BountyHunter.bounty.Data?.PlayerName}.");
                } catch (Exception e) {
                    ThrottledLog("H5", $"bounty correction failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // TOR's CustomRPC enum and its nested GhostInfoTypes are internal, so both values are
        // resolved once by reflection with the literal as a documented fallback (the same technique
        // MultiJester uses for the Jester win reason).
        private static byte cachedShareGhostInfo = 0;
        private static byte cachedBountyTarget = 0xFF;

        private static byte CustomRPCShareGhostInfo {
            get {
                if (cachedShareGhostInfo != 0) return cachedShareGhostInfo;
                cachedShareGhostInfo = 160; // documented fallback
                try {
                    var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.CustomRPC");
                    if (e != null && Enum.IsDefined(e, "ShareGhostInfo"))
                        cachedShareGhostInfo = Convert.ToByte(Enum.Parse(e, "ShareGhostInfo"));
                } catch { }
                return cachedShareGhostInfo;
            }
        }

        private static byte GhostInfoTypeBountyTarget {
            get {
                if (cachedBountyTarget != 0xFF) return cachedBountyTarget;
                cachedBountyTarget = 0;
                try {
                    var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.RPCProcedure+GhostInfoTypes");
                    if (e != null && Enum.IsDefined(e, "BountyTarget"))
                        cachedBountyTarget = Convert.ToByte(Enum.Parse(e, "BountyTarget"));
                } catch { }
                return cachedBountyTarget;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-H6) The Lawyer kills himself immediately after being promoted
        //
        // PlayerControl.Exiled -> ExilePlayerPatch.Postfix (PlayerControlPatch.cs:1421-1443) has two
        // consecutive blocks for a Lawyer whose client is exiled:
        //     promotion:  AmHost && ((target != jester && !isProsecutor) || targetWasGuessed)
        //     suicide:    !targetWasGuessed && !isProsecutor
        // In the ordinary case - a Lawyer with a non-Jester client, exiled by vote - BOTH are true.
        // The host promotes the Lawyer to Pursuer and then exiles him again on the very next line;
        // and because the promotion is an RPC, non-host clients still have Lawyer.lawyer set when
        // they reach the suicide block, so they exile the LAWYER while the host exiles the PURSUER.
        // A role death plus a lobby-wide disagreement about who is dead, in the role's standard case.
        //
        // The suicide is meant for the case where no promotion happens, which is precisely
        // "the client was the Jester". Rather than rebuilding a 40-line method from outside, the
        // prefix borrows TOR's own switch: setting targetWasGuessed makes the suicide block skip
        // (it requires !targetWasGuessed) while the promotion block still fires (it accepts
        // targetWasGuessed). A finalizer puts the flag back, so nothing downstream - the Lawyer win
        // check reads it too - sees the temporary value. Same flip-and-restore shape LoverRevenger
        // uses for Lovers.bothDie, finalizer included, so a throw anywhere in the chain cannot leave
        // the flag stuck.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
        static class LawyerPromotionSuicidePatch {
            private static bool flipped;

            public static void Prefix(PlayerControl __instance) {
                flipped = false;
                try {
                    if (Lawyer.lawyer == null || Lawyer.target == null) return;
                    if (__instance == null || __instance != Lawyer.target) return;
                    if (Lawyer.isProsecutor || Lawyer.targetWasGuessed) return;
                    // The Jester client is the one case TOR does NOT promote, so its suicide is
                    // intended and stays untouched.
                    if (Jester.jester != null && Lawyer.target == Jester.jester) return;

                    Lawyer.targetWasGuessed = true;
                    flipped = true;
                } catch (Exception e) {
                    flipped = false;
                    ThrottledLog("H6", $"prefix failed: {e.GetType().Name}: {e.Message}");
                }
            }

            public static void Finalizer() {
                if (!flipped) return;
                flipped = false;
                try { Lawyer.targetWasGuessed = false; } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-H8) The Swapper gets a charge back per BUTTON, not per swap
        //
        // MeetingPatch.cs swapperCheckAndReturnSwap refunds a cancelled swap with Swapper.charges++
        // inside the loop that resets the vote buttons - so one cancelled swap returns as many
        // charges as there are eligible players, up to about thirteen. Every further guess or
        // disconnect involving a selected player inflates it again, and the Swapper ends the game
        // with effectively unlimited swaps.
        //
        // The refund itself is right; only its multiplicity is wrong. So the charge count is read
        // before the method and clamped after it: at most one charge comes back, which is what one
        // cancelled swap costs. Reading and clamping leaves TOR's own UI updates inside the method
        // untouched, except for the label, which is re-stamped here with the corrected number.
        //
        // TorAuditFixes A10 already prefixes this same method for a different defect (null/stale UI
        // arrays). Both patches coexist: A10 decides whether the body runs at all, this one only
        // looks at a counter before and after.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch]
        static class SwapperChargeInflationPatch {
            public static MethodBase TargetMethod() {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                return t?.GetMethod("swapperCheckAndReturnSwap",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            public static bool Prepare(MethodBase original) => TargetMethod() != null;

            public static void Prefix(out int __state) {
                __state = -1;
                try { __state = Swapper.charges; } catch { }
            }

            public static void Postfix(int __state) {
                try {
                    if (__state < 0) return;                       // prefix could not read it
                    if (Swapper.charges <= __state + 1) return;    // nothing was inflated
                    int inflated = Swapper.charges;
                    Swapper.charges = __state + 1;
                    ThrottledLog("H8", $"swap refund was {inflated - __state} charges, clamped to 1 "
                                       + $"(now {Swapper.charges}).");
                    RestampSwapLabel();
                } catch (Exception e) {
                    ThrottledLog("H8", $"clamp failed: {e.GetType().Name}: {e.Message}");
                }
            }

            // TOR wrote the inflated number into its own "Swaps: n" label inside the method. The
            // field is private static on MeetingHudPatch, so it is fetched once by reflection; if it
            // is ever renamed the count is still correct and only the label reads stale until the
            // next swap interaction repaints it.
            private static FieldInfo labelField;
            private static bool labelResolved;

            private static void RestampSwapLabel() {
                if (!labelResolved) {
                    labelResolved = true;
                    try {
                        var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                        labelField = t?.GetField("meetingExtraButtonText",
                                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    } catch { }
                }
                try {
                    var tmp = labelField?.GetValue(null) as TMPro.TextMeshPro;
                    if (tmp != null) tmp.text = $"Swaps: {Swapper.charges}";
                } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-H9) A blanked plant bricks the Bomber for the rest of the round
        //
        // Buttons.cs:1802-1819: the plant button "attacks" the bomber himself to consume a Pursuer
        // blank, and places the bomb only when that check does NOT return BlankKill. But
        // `Bomber.isPlanted = true` sits AFTER the if, outside it - so a blanked attempt sets the
        // flag without placing anything. CouldUse requires !isPlanted, so the button is dead; there
        // is no bomb to defuse or detonate, and isPlanted is only ever cleared by clearBomb() /
        // clearAndReload(), neither of which runs while no bomb exists. The Bomber simply loses his
        // ability until the next round.
        //
        // BomberArmoredFix already removes ONE cause (an Armored Bomber eating his own probe). This
        // covers the rest, the Pursuer blank included: after the click, if the flag claims a bomb
        // was planted and no bomb object exists, the claim is false and the flag goes back.
        // Deliberately a postfix on the button click rather than a rewrite of the callback, so
        // TOR keeps ownership of what a plant does.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.onClickEvent))]
        static class BomberBlankPlantPatch {
            public static void Postfix() {
                try {
                    if (Bomber.bomber == null || PlayerControl.LocalPlayer != Bomber.bomber) return;
                    if (!Bomber.isPlanted) return;
                    if (Bomber.bomb != null) return;         // a real bomb is out there: correct state

                    Bomber.isPlanted = false;
                    ThrottledLog("H9", "plant was blanked but isPlanted stayed set - button re-armed.");
                } catch (Exception e) {
                    ThrottledLog("H9", $"re-arm failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-H11) One failed hat download disables custom hats for the whole session
        //
        // HatsLoader.FetchHats returns early while `isRunning` is set, and CoFetchHats sets it at
        // the top but clears it only on the success path: a network error does `yield break` with
        // the flag still true, and a malformed manifest throws out of JsonSerializer.Deserialize
        // with the same result. From then on every retry is a silent no-op - there is no timeout on
        // the request either (UnityWebRequest defaults to none), so a hanging connection has the
        // same effect.
        //
        // The flag is a private INSTANCE field, so it is cleared through the one entry point that
        // reads it: a prefix on FetchHats notices that a fetch has been "running" for longer than
        // any real one takes and resets the flag, letting that same call proceed. This is the shape
        // HostFixPlugin's draft watchdog uses, for the same reason: the state cannot be repaired
        // where it breaks, so it is repaired where it is next observed.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch]
        static class HatsLoaderStuckLatchPatch {
            // Generous: a first-run manifest fetch plus the hat downloads behind it can legitimately
            // take a while on a slow connection. This only has to beat "forever".
            private const float StuckAfterSeconds = 120f;

            private static float runningSince = -1f;
            private static FieldInfo isRunningField;

            public static MethodBase TargetMethod() {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.CustomHats.HatsLoader");
                return t?.GetMethod("FetchHats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            public static bool Prepare(MethodBase original) {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.CustomHats.HatsLoader");
                isRunningField = t?.GetField("isRunning", BindingFlags.NonPublic | BindingFlags.Instance);
                if (isRunningField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorUpstreamFixes/H11] HatsLoader.isRunning not found - stuck-latch watchdog disabled.");
                return TargetMethod() != null && isRunningField != null;
            }

            public static void Prefix(object __instance) {
                try {
                    if (__instance == null) return;
                    bool running = isRunningField.GetValue(__instance) is bool b && b;
                    if (!running) { runningSince = Time.realtimeSinceStartup; return; }

                    if (runningSince < 0f) { runningSince = Time.realtimeSinceStartup; return; }
                    if (Time.realtimeSinceStartup - runningSince < StuckAfterSeconds) return;

                    isRunningField.SetValue(__instance, false);
                    runningSince = Time.realtimeSinceStartup;
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorUpstreamFixes/H11] a hat fetch has been marked running for over "
                        + $"{StuckAfterSeconds:0}s - clearing the latch so this retry can run "
                        + "(the previous one failed without resetting it).");
                } catch (Exception e) {
                    ThrottledLog("H11", $"watchdog failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M5) isKiller counts the Prosecutor as a killer
        //
        // Helpers.cs:606-613 excludes Jester, Arsonist, Vulture, Lawyer and Pursuer from "neutral
        // killers" but not the Prosecutor - who is a Lawyer variant with no kill of any kind
        // (Lawyer.isProsecutor, not a separate PlayerControl, which is exactly why the existing
        // `player != Lawyer.lawyer` term does not catch him: it does, actually, for the Lawyer
        // FIELD - but the Prosecutor keeps that field, so this fix is about the case where he has
        // already been promoted to Pursuer... which is also listed. The real gap is the win-check
        // path, see below).
        //
        // Concretely: the Snitch's "killers" arrow list and the Lovers win check both read isKiller,
        // so a living Prosecutor makes the Snitch point at a player who cannot kill and can block a
        // Lovers win that should have happened. The postfix only ever REMOVES a killer flag, never
        // adds one, so nothing that relies on isKiller can start seeing new killers because of it.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.isKiller))]
        static class ProsecutorIsNotAKillerPatch {
            [HarmonyPriority(Priority.Low)]   // after MultiJester's own isKiller postfix
            public static void Postfix(PlayerControl player, ref bool __result) {
                try {
                    if (!__result || player == null) return;
                    if (!Lawyer.isProsecutor) return;
                    if (Lawyer.lawyer != null && player.PlayerId == Lawyer.lawyer.PlayerId) __result = false;
                } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M9) A Time Master rewind survives into the meeting
        //
        // MeetingPatch's meeting-start block explicitly stops the other time-based effects - it
        // clears Bait.active and Vampire.bitten right there (MeetingPatch.cs:672-688) - but not a
        // rewind in progress. TimeMaster.isRewinding stays true, so bendTimeUpdate keeps writing
        // the victim's transform from its position buffer while the meeting UI is up: the player
        // is frozen (moveable was set false when the rewind started) and their buffer drains
        // behind the meeting.
        //
        // Ended exactly the way TOR ends it itself when the buffer runs out
        // (PlayerControlPatch.cs:136-138): drop the flag, hand movement back.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class StopRewindOnMeetingPatch {
            public static void Postfix() {
                try {
                    if (!TimeMaster.isRewinding) return;
                    TimeMaster.isRewinding = false;
                    var me = PlayerControl.LocalPlayer;
                    if (me != null) me.moveable = true;
                    ThrottledLog("M9", "a Time Master rewind was still running at meeting start - ended it.");
                } catch (Exception e) {
                    ThrottledLog("M9", $"rewind stop failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // TOR-M35) A throw in TOR's light prefix takes our whole vision pipeline with it
        //
        // ShipStatusPatch.CalculateLightRadius is a PREFIX, and it dereferences its `player`
        // argument (Hunter.lightActive.Contains(player.PlayerId), Helpers.hasImpVision(player))
        // without the null check vanilla has. That matters to us specifically: UCVision and
        // ChanceMod both hang their whole vision pipeline off POSTFIXES of this same method, and a
        // prefix that throws takes the postfixes with it - the roles that grant or dampen sight
        // would silently stop working, in a per-frame path, with an exception log as the only clue.
        //
        // A finalizer is the one construct that can contain that from outside: it runs whatever
        // happened, and returning the exception as handled (by assigning null to __exception) turns
        // the throw into a normal call with a sane radius. Deliberately narrow: it only intervenes
        // when an exception actually happened, so the ordinary path is untouched.
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CalculateLightRadius))]
        static class LightRadiusFinalizerPatch {
            public static Exception Finalizer(Exception __exception, ShipStatus __instance, ref float __result) {
                if (__exception == null) return null;
                try {
                    // Full sight is the safe answer: too much light is a cosmetic problem for one
                    // frame, too little is an unplayable screen.
                    __result = __instance != null ? __instance.MaxLightRadius : 1f;
                } catch { __result = 1f; }
                ThrottledLog("M35", $"TOR's CalculateLightRadius prefix threw ({__exception.GetType().Name}) - "
                                    + "swallowed so the vision postfixes still run.");
                return null;   // handled
            }
        }
    }
}
