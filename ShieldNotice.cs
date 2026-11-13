// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ShieldNotice - tells the player who carries a newcomer or early death shield why he has it and how long it
 * lasts (User 2026-11-11). Until now only the killer learned anything (the "kill failed" line); the carrier saw a
 * coloured outline and had to guess.
 *
 * Shown once per shield on the carrier's own client, as TOR's on-screen message (the chat is closed for a living
 * player during the round). Only the list of the round start counts: in the lobby the same message is just the
 * host's preview of who would get a shield. The early death shield sends how many meetings it lasts in a second
 * message right after the list, so the notice waits a moment and reads the duration when it is shown.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class ShieldNotice {
        internal enum Kind { Newcomer, EarlyDeath }

        private const float Delay = 1.5f, Seconds = 9f, RepeatGuard = 120f;

        private static readonly Dictionary<Kind, float> shownAt = new Dictionary<Kind, float>();
        private static readonly Dictionary<Kind, float> dueAt = new Dictionary<Kind, float>();

        /// <summary>A shield list arrived. Queues the notice when the local player is on it and a round is running.</summary>
        internal static void Arrived(Kind kind, List<byte> ids) {
            try {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null || ids == null || !ids.Contains(lp.PlayerId)) return;
                if (ShipStatus.Instance == null || LobbyBehaviour.Instance != null) return;
                float now = Time.realtimeSinceStartup;
                // the host repeats the list (late joiners, meeting changes): the same shield is explained once
                if (shownAt.TryGetValue(kind, out float at) && now - at < RepeatGuard) { shownAt[kind] = now; return; }
                if (!dueAt.ContainsKey(kind)) dueAt[kind] = now + Delay;
            } catch { }
        }

        private static string Text(Kind kind) {
            bool survives = kind == Kind.Newcomer ? NewcomerShield.SurvivesMeeting : EarlyDeathShield.SurvivesMeeting;
            int meetings = kind == Kind.Newcomer ? 1 : EarlyDeathShield.MeetingsFor(PlayerControl.LocalPlayer.PlayerId);
            string until = UTSLocalization.Tr(survives ? "uts.shieldnotice.until_over" : "uts.shieldnotice.until_begins", meetings);
            return UTSLocalization.Tr(kind == Kind.Newcomer ? "uts.shieldnotice.newcomer" : "uts.shieldnotice.earlydeath", until);
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class TickPatch {
            public static void Postfix() {
                if (dueAt.Count == 0) return;
                try {
                    float now = Time.realtimeSinceStartup;
                    Kind? ready = null;
                    foreach (var kv in dueAt) if (now >= kv.Value) { ready = kv.Key; break; }
                    if (ready == null) return;
                    var kind = ready.Value;
                    dueAt.Remove(kind);
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null || lp.Data.IsDead) return;
                    if (ShipStatus.Instance == null || LobbyBehaviour.Instance != null) return;
                    bool still = kind == Kind.Newcomer ? NewcomerShield.IsShielded(lp.PlayerId) : EarlyDeathShield.IsShielded(lp.PlayerId);
                    if (!still) return;
                    shownAt[kind] = now;
                    new CustomMessage(Text(kind), Seconds);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[ShieldNotice] {kind} shield explained to the local player.");
                } catch (Exception e) {
                    dueAt.Clear();
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ShieldNotice] notice failed: {e.Message}");
                }
            }
        }
    }
}
