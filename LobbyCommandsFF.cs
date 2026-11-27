// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbyCommandsFF - the mod author's lobby gags that belong to Forgotten Fixes (User 2026-11-27; Unknown's Collection
 * has the role gags, the same command shape "<x>.trigger.<y>(<args>)"):
 *
 *   ff.trigger.music(Title)        a draft-music song for everybody (title or a unique start of it; "()" stops it)
 *   ff.trigger.pet(all|Name)       a custom pet walks with the player for 15 s
 *   ff.trigger.rename(Name, Nick)  the player's name shows a nickname for 10 s
 *
 * Only in the lobby, only from the author's account (EasterEggCoding.IsOwner, checked by every client), the chat line
 * stays visible. Purely local and cosmetic, no RPC; looks go back after the time, at the latest when a game starts.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UsefulTORStuff {
    internal static class LobbyCommandsFF {
        private static readonly Regex Rx = new Regex(@"^\s*ff\.trigger\.([a-z]+)\s*\((.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Dictionary<string, float> lastRun = new Dictionary<string, float>();
        private static readonly List<(float until, PlayerControl p)> restore = new List<(float, PlayerControl)>();

        private static bool InLobby() {
            try {
                var c = AmongUsClient.Instance;
                return c != null && c.GameState == InnerNet.InnerNetClient.GameStates.Joined && LobbyBehaviour.Instance != null;
            } catch { return false; }
        }

        [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
        static class ChatPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl sourcePlayer, [HarmonyArgument(1)] string chatText) {
                try {
                    if (string.IsNullOrEmpty(chatText)) return;
                    var m = Rx.Match(chatText);
                    if (!m.Success || !InLobby() || !EasterEggCoding.IsOwner(sourcePlayer)) return;
                    string cmd = m.Groups[1].Value.ToLowerInvariant();
                    if (lastRun.TryGetValue(cmd, out float last) && Time.time - last < 3f) return;
                    lastRun[cmd] = Time.time;
                    string arg = m.Groups[2].Value.Trim();
                    switch (cmd) {
                        case "music": Music(arg); break;
                        case "pet": foreach (var p in Players(arg)) Pet(p); break;
                        case "rename": Rename(arg); break;
                        default: return;
                    }
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[LobbyCmd] ff.trigger.{cmd}({arg}).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyCmd] failed: {e.Message}");
                }
            }
        }

        private static List<PlayerControl> All() =>
            PlayerControl.AllPlayerControls.ToArray().Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();

        private static PlayerControl Find(string name) {
            var all = All();
            var exact = all.Where(p => string.Equals(p.Data.PlayerName?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var start = all.Where(p => (p.Data.PlayerName ?? "").Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return start.Count == 1 ? start[0] : null;
        }

        private static List<PlayerControl> Players(string arg) {
            if (arg.Length == 0 || arg.Equals("all", StringComparison.OrdinalIgnoreCase)) return All();
            var p = Find(arg);
            return p != null ? new List<PlayerControl> { p } : new List<PlayerControl>();
        }

        // ---- music: the draft-music preview, played for everybody (it loops until "()" or another song) ----
        private static void Music(string title) {
            DraftMusic.StopPreview();
            if (title.Length == 0) return;
            var tracks = MusicLibrary.AllVersions.Where(t => t != null && !string.IsNullOrEmpty(t.Title)).ToList();
            var hit = tracks.Where(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = tracks.Where(t => t.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = tracks.Where(t => t.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 0) { UsefulTORStuffPlugin.Logger?.LogInfo($"[LobbyCmd] no song called '{title}'."); return; }
            DraftMusic.TogglePreview(hit[0].Key);
        }

        // ---- pet: one of the custom pets for 15 s ----
        private static void Pet(PlayerControl p) {
            var ids = UTSPets.LiveIds();
            if (ids.Count == 0) return;
            var o = p.Data.DefaultOutfit;
            try { p.setLook(p.Data.PlayerName, o.ColorId, o.HatId, o.VisorId, o.SkinId, ids[UnityEngine.Random.Range(0, ids.Count)]); } catch { }
            restore.Add((Time.time + 15f, p));
        }

        // ---- rename: a nickname for 10 s ----
        private static void Rename(string arg) {
            int comma = arg.IndexOf(',');
            if (comma <= 0) return;
            var p = Find(arg.Substring(0, comma).Trim());
            string nick = arg.Substring(comma + 1).Trim();
            if (p == null || nick.Length == 0) return;
            if (nick.Length > 24) nick = nick.Substring(0, 24);
            var o = p.Data.DefaultOutfit;
            try { p.setLook(nick, o.ColorId, o.HatId, o.VisorId, o.SkinId, o.PetId); } catch { }
            restore.Add((Time.time + 10f, p));
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (restore.Count == 0) return;
                bool lobby = InLobby();
                for (int i = restore.Count - 1; i >= 0; i--) {
                    if (lobby && Time.time < restore[i].until) continue;
                    var p = restore[i].p;
                    restore.RemoveAt(i);
                    try { if (p != null) p.setDefaultLook(); } catch { }
                }
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
        static class StartPatch {
            public static void Prefix() {
                foreach (var r in restore) { try { if (r.p != null) r.p.setDefaultLook(); } catch { } }
                restore.Clear();
                DraftMusic.StopPreview();
            }
        }
    }
}
