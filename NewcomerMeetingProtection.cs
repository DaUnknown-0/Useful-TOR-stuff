// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * NewcomerMeetingProtection - what the newcomer shield does inside the first meeting.
 *
 * Outside a meeting the shield is a kill blocker and lives in NewcomerShield.cs. This file is the
 * other half: for the length of the first meeting, a shielded newcomer cannot be GUESSED and cannot
 * be VOTED for. Those are the only two ways to remove somebody from a meeting, so between them the
 * newcomer survives their first vote and starts really playing in round two.
 *
 * ONLY THE NEWCOMER SHIELD, NOT THE SPAWN PROTECTION
 * AntiStartKill covers the opening seconds of a round and clears itself the moment MeetingHud.Start
 * runs (AntiStartKill.cs, MeetingStartPatch) - by design, it is about the spawn camp and nothing
 * else. So it has no say in a meeting, and this file deliberately does not consult it. An earlier
 * version of this code asked both shields; that was not wrong so much as pointless, because the
 * spawn protection is always already empty by the time a meeting exists.
 *
 * WHY THE VOTE BLOCK IS HOST-SIDE
 * MeetingHud.CastVote(srcPlayerId, suspectIdx) is where every vote lands, and it lands on the HOST:
 * a remote client clicking a face only reaches CmdCastVote, which sends an RPC that the host turns
 * into CastVote. Refusing it there therefore holds for the whole lobby, including players who do
 * not have this mod - the same host-authority argument the Silencer's skip block already relies on
 * (Silencer.cs). The greyed-out button further down is only a courtesy so a modded client can see
 * why the click did nothing; it is not what enforces the rule.
 *
 * WHY THE GUESS BLOCK IS SEND-SIDE
 * A guess is the opposite situation: RPCProcedure.guesserShoot kills the target on every client
 * independently, with no host arbitration anywhere. Refusing it only on modded clients would mean
 * modded players see the newcomer alive while unmodded ones see a corpse, and a lobby that
 * disagrees about who is dead poisons vote counting, task progress and the win check. So the block
 * sits BEFORE the shot instead: TOR's own guesserOnClick, the click that opens the role grid, never
 * opens it for a protected player and no RPC is ever produced. TOR uses exactly this shape for the
 * Medic shield (MeetingPatch.cs:463-471), so a guess bouncing off a protected player is already an
 * established interaction. The receive-side prefix below is the belt to that pair of braces, and it
 * only acts when every client can refuse the same message in the same tick.
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class NewcomerMeetingProtection {

        // The one question this file asks. Note what it does NOT ask: AntiStartKill - see the header.
        private static bool IsProtected(byte playerId) {
            try { return NewcomerShield.IsShielded(playerId); } catch { return false; }
        }

        private static void NotifyLocal(string key) {
            try {
                var hud = HudManager.Instance;
                if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
                hud.Chat.AddChat(PlayerControl.LocalPlayer, UTSLocalization.Tr(key));
            } catch { }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 1. Not votable - host-authoritative
        // ══════════════════════════════════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CastVote))]
        static class CastVotePatch {
            // suspectIdx carries the PlayerId of whoever is being voted for; 253 is the skip vote,
            // which must always be allowed to pass (that is the mechanism by which a meeting ends).
            private const byte SkipVote = 253;

            public static bool Prefix([HarmonyArgument(0)] byte srcPlayerId, [HarmonyArgument(1)] byte suspectIdx) {
                try {
                    if (suspectIdx == SkipVote) return true;
                    if (!IsProtected(suspectIdx)) return true;

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[NewcomerMeetingProtection] refused a vote by {srcPlayerId} against protected "
                        + $"newcomer {suspectIdx}.");

                    // Tell the voter, but only if the voter is us: the host runs this for the whole
                    // lobby, so an ungated message would put a line in the host's chat for every
                    // remote vote as well.
                    var me = PlayerControl.LocalPlayer;
                    if (me != null && me.PlayerId == srcPlayerId) NotifyLocal("uts.newcomershield.vote_blocked");

                    return false;   // the vote never happens: no state written, nothing to undo
                } catch { return true; }
            }
        }

        // Courtesy only: grey out the button so a modded client can see the rule instead of
        // wondering why the click did nothing. Unmodded clients still see a normal button and their
        // vote is refused by the host above, which is where the guarantee actually lives.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class VoteButtonGreyOutPatch {
            public static void Postfix(MeetingHud __instance) {
                try {
                    if (__instance == null || __instance.playerStates == null) return;
                    if (!NewcomerShield.Active) return;           // nothing shielded: nothing to grey
                    foreach (var pva in __instance.playerStates) {
                        if (pva == null || pva.Buttons == null) continue;
                        if (!IsProtected((byte)pva.TargetPlayerId)) continue;
                        // Same handle TOR uses to hide vote buttons; disabling the collider is what
                        // actually stops the click, the tint is what explains it.
                        var buttons = pva.Buttons.transform;
                        for (int i = 0; i < buttons.childCount; i++) {
                            var child = buttons.GetChild(i);
                            if (child == null || child.name == "CancelButton") continue;
                            var passive = child.GetComponent<PassiveButton>();
                            if (passive != null) passive.enabled = false;
                            var rend = child.GetComponent<SpriteRenderer>();
                            if (rend != null) rend.color = Palette.DisabledClear;
                        }
                    }
                } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 2. Not guessable - send side (always) plus a gated receive side
        // ══════════════════════════════════════════════════════════════════════════════════════
        public static void TryPatch(Harmony harmony) {
            try {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch");
                var m = t?.GetMethod("guesserOnClick", BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[NewcomerMeetingProtection] guesserOnClick not found - the send-side guess block "
                        + "is disabled (the gated guesserShoot block still applies).");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(NewcomerMeetingProtection),
                                                          nameof(GuesserClickPrefix)));
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    "[NewcomerMeetingProtection] Patched guesserOnClick (protected newcomers cannot be guessed).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerMeetingProtection] TryPatch failed: {e}");
            }
        }

        // Signature mirrors TOR's private guesserOnClick(int buttonTarget, MeetingHud __instance).
        public static bool GuesserClickPrefix(int buttonTarget, MeetingHud __instance) {
            try {
                if (__instance == null || __instance.playerStates == null) return true;
                if (buttonTarget < 0 || buttonTarget >= __instance.playerStates.Length) return true;
                var pva = __instance.playerStates[buttonTarget];
                if (pva == null) return true;
                byte targetId = (byte)pva.TargetPlayerId;
                if (!IsProtected(targetId)) return true;

                // The same feedback TOR gives for a Medic-shielded guess: the fail cue plus a line
                // to read, rather than a click that silently does nothing.
                try { SoundEffectsManager.play("fail"); } catch { }
                NotifyLocal("uts.newcomershield.kill_blocked");
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[NewcomerMeetingProtection] refused to open the guess UI on protected newcomer {targetId}.");
                return false;
            } catch { return true; }
        }

        // The receive-side half. Gated on EveryoneHasMod because guesserShoot is applied locally by
        // every client: refusing it on only some of them would split the lobby over who is dead.
        // Consequence, stated plainly: a shooter on an unpatched build still gets the kill. The mod
        // sync is the tool for that gap, not more code here.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootPatch {
            public static bool Prefix([HarmonyArgument(1)] byte dyingTargetId) {
                try {
                    if (!IsProtected(dyingTargetId)) return true;
                    if (!UsefulVersionHandshake.EveryoneHasMod()) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            $"[NewcomerMeetingProtection] a protected newcomer ({dyingTargetId}) was guessed, but "
                            + "not every client has this mod - letting the shot through to avoid a life/death desync.");
                        return true;
                    }
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[NewcomerMeetingProtection] blocked a guesser shot on protected newcomer {dyingTargetId}.");
                    return false;
                } catch { return true; }
            }
        }
    }
}
