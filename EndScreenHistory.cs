// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EndScreenHistory - two additions to TOR's role summary on the end screen (User 2026-10-16):
 *
 *   - Role history: whoever changed roles during the round shows every role they held, joined by an
 *     arrow, e.g. "Deputy -> Sheriff", "Sidekick -> Jackal", "Thief -> Sheriff", UC promotions and the
 *     Mixer swap alike. TOR itself only lists the role at the end, so nobody could tell who was what.
 *   - Wrong guesses: a guesser who shot themselves gets "wrong guess: <target> as <role>" on their line.
 *     Shown only here, at the end, never during the round (it would tell everyone what the target
 *     is NOT).
 *
 * Recording: the main roles (no modifiers) of every player are read once a second while a round runs
 * (TOR's getRoleInfoForPlayer, which every client can answer: TOR syncs all roles to all clients);
 * a change appends the previous roles to that player's history. Wrong guesses come from a postfix on
 * RPCProcedure.guesserShoot, which runs on every client through TOR's RPC handler. Both are keyed by
 * the player's name, which is what TOR's summary entry carries. Cleared at the end of the intro and
 * on lobby join, like EndScreenLeavers.
 *
 * Display only, local, nothing is sent; the summary entries are edited in the same OnGameEnd
 * postfix slot EndScreenLeavers uses (Priority.Last, after TOR built the list). The arrow is a real
 * arrow when the end screen's font has the glyph, otherwise "->" (checked on the live text).
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TMPro;
using UnityEngine;

namespace UsefulTORStuff {
    public static class EndScreenHistory {
        private const string Arrow = "→";          // replaced by "->" if the font lacks it
        private const string LeftMarker = " (left)";    // EndScreenLeavers' marker on restored lines

        private sealed class Track {
            public string Current;                       // colored main roles right now
            public string CurrentKey;                    // plain names, for change detection
            public readonly List<string> Before = new();  // earlier roles, oldest first
        }

        private static readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<string>> wrongGuesses = new(StringComparer.Ordinal);
        private static float nextTick = float.NegativeInfinity;

        // ---- recording ---------------------------------------------------------------------------
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class TickPatch {
            public static void Postfix() {
                try {
                    if (Time.realtimeSinceStartup < nextTick) return;
                    nextTick = Time.realtimeSinceStartup + 1f;
                    Record();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenHistory] record failed: {e.Message}");
                }
            }
        }

        private static void Record() {
            if (PlayerControl.LocalPlayer == null) return;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.IsGameStarted) return;
            if (IntroScreen.Exists) return;   // roles are only final once the intro (and the draft) is over
            foreach (var p in PlayerControl.AllPlayerControls) {
                if (p == null || p.Data == null || p.Data.Disconnected) continue;
                try {
                    var roles = RoleInfo.getRoleInfoForPlayer(p, false).Where(r => r != null && !r.isModifier).ToList();
                    if (roles.Count == 0) continue;
                    string key = string.Join("|", roles.Select(r => r.name));
                    string colored = string.Join(" ", roles.Select(r => Helpers.cs(r.color, r.name)));
                    string name = p.Data.PlayerName;
                    if (!tracks.TryGetValue(name, out var t)) {
                        tracks[name] = new Track { Current = colored, CurrentKey = key };
                        continue;
                    }
                    if (t.CurrentKey == key) continue;
                    t.Before.Add(t.Current);
                    t.Current = colored;
                    t.CurrentKey = key;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[EndScreenHistory] {name}: role change -> {key}");
                } catch { /* one unreadable player must not cost the others */ }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        private static class GuessPatch {
            public static void Postfix(byte killerId, byte dyingTargetId, byte guessedTargetId, byte guessedRoleId) {
                try {
                    if (killerId != dyingTargetId) return;            // a hit, not a wrong guess
                    var guesser = Helpers.playerById(killerId);
                    var target = Helpers.playerById(guessedTargetId);
                    if (guesser == null || guesser.Data == null || target == null || target.Data == null) return;
                    var role = RoleInfo.allRoleInfos.FirstOrDefault(x => (byte)x.roleId == guessedRoleId);
                    string roleText = role != null ? Helpers.cs(role.color, role.name) : "?";
                    string line = UTSLocalization.Tr("uts.endscreen.wrong_guess", target.Data.PlayerName, roleText);
                    if (!wrongGuesses.TryGetValue(guesser.Data.PlayerName, out var list))
                        wrongGuesses[guesser.Data.PlayerName] = list = new List<string>();
                    list.Add(line);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[EndScreenHistory] wrong guess by {guesser.Data.PlayerName}: {target.Data.PlayerName} as {role?.name}");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[EndScreenHistory] guess record failed: {e.Message}");
                }
            }
        }

        // ---- writing into TOR's summary -----------------------------------------------------------
        private static bool resolved;
        private static FieldInfo playerRolesField;
        private static PropertyInfo nameProp, rolesProp;

        private static bool Resolve() {
            if (resolved) return playerRolesField != null && nameProp != null && rolesProp != null;
            resolved = true;
            try {
                var tempData = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.AdditionalTempData");
                playerRolesField = tempData?.GetField("playerRoles", BindingFlags.Public | BindingFlags.Static);
                var entryType = tempData?.GetNestedType("PlayerRoleInfo", BindingFlags.NonPublic | BindingFlags.Public);
                nameProp = entryType?.GetProperty("PlayerName");
                rolesProp = entryType?.GetProperty("RoleNames");
                if (playerRolesField == null || nameProp == null || rolesProp == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[EndScreenHistory] TOR summary types not found - end screen stays as TOR built it.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenHistory] reflection failed: {e.Message}");
            }
            return playerRolesField != null && nameProp != null && rolesProp != null;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        private static class ApplyPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try { Apply(); } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[EndScreenHistory] apply failed: {e}");
                }
            }
        }

        private static void Apply() {
            if (!Resolve()) return;
            if (playerRolesField.GetValue(null) is not IList list) return;
            int changed = 0;
            foreach (var entry in list) {
                string name = nameProp.GetValue(entry) as string;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.EndsWith(LeftMarker, StringComparison.Ordinal)) name = name.Substring(0, name.Length - LeftMarker.Length);
                string roles = rolesProp.GetValue(entry) as string ?? "";
                string extra = "";
                if (tracks.TryGetValue(name, out var t) && t.Before.Count > 0) {
                    var sep = $" <color=#BFBFBF>{Arrow}</color> ";
                    extra = string.Join(sep, t.Before) + sep;
                }
                string guesses = "";
                if (wrongGuesses.TryGetValue(name, out var g) && g.Count > 0)
                    guesses = " <color=#FF7A7A><size=85%>(" + string.Join(", ", g) + ")</size></color>";
                if (extra.Length == 0 && guesses.Length == 0) continue;
                rolesProp.SetValue(entry, extra + roles + guesses);
                changed++;
            }
            UsefulTORStuffPlugin.Logger?.LogInfo($"[EndScreenHistory] game end: {tracks.Count(x => x.Value.Before.Count > 0)} role change(s), {wrongGuesses.Count} wrong guesser(s), {changed} line(s) extended.");
        }

        // The arrow glyph: if the summary's font cannot draw it, fall back to ASCII on the live text.
        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        private static class GlyphPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(EndGameManager __instance) {
                try {
                    // TOR instantiates the summary without a parent, so it is not under EndGameManager.
                    bool fallback = false;
                    foreach (var text in UnityEngine.Object.FindObjectsOfType<TMP_Text>()) {
                        if (text == null || text.text == null || !text.text.Contains(Arrow)) continue;
                        bool ok = false;
                        try { ok = text.font != null && text.font.HasCharacter(Arrow[0], true, true); } catch { }
                        if (ok) continue;
                        text.text = text.text.Replace(Arrow, "->");
                        fallback = true;
                    }
                    if (fallback) {
                        try {
                            if (!string.IsNullOrEmpty(Helpers.previousEndGameSummary))
                                Helpers.previousEndGameSummary = Helpers.previousEndGameSummary.Replace(Arrow, "->");
                        } catch { }
                        UsefulTORStuffPlugin.Logger?.LogInfo("[EndScreenHistory] font has no arrow glyph, using \"->\".");
                    }
                } catch { }
            }
        }

        // ---- resets --------------------------------------------------------------------------------
        private static void Clear() { tracks.Clear(); wrongGuesses.Clear(); nextTick = float.NegativeInfinity; }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        private static class RoundStartResetPatch { public static void Postfix() => Clear(); }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyResetPatch { public static void Postfix() => Clear(); }
    }
}
