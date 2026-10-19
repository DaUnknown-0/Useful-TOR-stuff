// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * PlayerIdentity - who a player is across rounds, lobbies, renames and restarts.
 *
 * Every per-player memory of this mod hangs on one key: the newcomer shield's "already played here" set
 * (NewcomerShield), the early-death history with the host's overrides (DeathTimeHistory, EarlyDeathShield) and
 * the evening statistics (SessionStats). PlayerId and ClientId are per connection and useless for that.
 *
 * KEY ORDER (User 2026-10-18: "check the puid, not the friend code, should be more reliable")
 *   1. "puid:<id>"  the account's product user id (NetworkedPlayerInfo.Puid). Tied to the account itself.
 *   2. the friend code, when the server hands one out but no PUID
 *   3. "name:<name>" on servers that deliver neither (a name can be copied or changed, but without it the
 *      features would not exist at all there)
 * The prefixes keep the three kinds from ever colliding.
 *
 * MIGRATION
 * Everything recorded before this change is keyed by friend code or name. Throwing that away would hand the
 * whole group a fresh newcomer shield and empty statistics. So whenever a player is seen whose key is a PUID,
 * his older keys (friend code, "name:<current name>") are renamed to the PUID in every registered store; a
 * player recorded under both gets the two merged. Each pair is handled once per game start. Every client runs
 * it, because every client keeps these files (whoever hosts next already has the evening).
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UsefulTORStuff {

    public static class PlayerIdentity {

        public const string PuidPrefix = "puid:";
        public const string NamePrefix = "name:";

        public enum Kind { None, Puid, FriendCode, Name }

        private static string PuidOf(PlayerControl p) {
            try { return p?.Data?.Puid; } catch { return null; }
        }

        private static string FriendCodeOf(PlayerControl p) {
            try { return p?.Data?.FriendCode; } catch { return null; }
        }

        private static string NameOf(PlayerControl p) {
            try { return p?.Data?.PlayerName; } catch { return null; }
        }

        /// <summary>The key this player is stored under; "" when nothing identifies him.</summary>
        public static string KeyOf(PlayerControl p) {
            string puid = PuidOf(p);
            if (!string.IsNullOrEmpty(puid)) return PuidPrefix + puid;
            string code = FriendCodeOf(p);
            if (!string.IsNullOrEmpty(code)) return code;
            string name = NameOf(p);
            return string.IsNullOrEmpty(name) ? "" : NamePrefix + name;
        }

        public static Kind KindOf(PlayerControl p) {
            if (!string.IsNullOrEmpty(PuidOf(p))) return Kind.Puid;
            if (!string.IsNullOrEmpty(FriendCodeOf(p))) return Kind.FriendCode;
            return string.IsNullOrEmpty(NameOf(p)) ? Kind.None : Kind.Name;
        }

        /// <summary>Older keys of the same player, which data from before the switch is stored under.</summary>
        private static IEnumerable<string> LegacyKeysOf(PlayerControl p, Kind kind) {
            string code = FriendCodeOf(p), name = NameOf(p);
            if (kind == Kind.Puid && !string.IsNullOrEmpty(code)) yield return code;
            if ((kind == Kind.Puid || kind == Kind.FriendCode) && !string.IsNullOrEmpty(name)) yield return NamePrefix + name;
        }

        // ---- stores ----
        // A store renames one key to another and says whether it held the old one. Registered by the stores
        // themselves, so this file knows none of their formats.
        private static readonly List<(string Name, Func<string, string, bool> Rename)> Stores = new List<(string, Func<string, string, bool>)>();
        private static readonly HashSet<string> Done = new HashSet<string>();
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static float nextRun;

        public static void RegisterStore(string name, Func<string, string, bool> rename) => Stores.Add((name, rename));

        /// <summary>Rename the older keys of everyone present to his current key. Cheap when nothing is new
        /// (each pair once per game start); force skips the throttle (round start).</summary>
        public static void MigratePresent(bool force = false) {
            if (!force && Time.realtimeSinceStartup < nextRun) return;
            nextRun = Time.realtimeSinceStartup + 2f;
            try {
                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    var kind = KindOf(p);
                    string key = KeyOf(p);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (Logged.Add(key))
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[PlayerIdentity] {NameOf(p)}: {(kind == Kind.Puid ? "PUID" : kind == Kind.FriendCode ? "friend code (no PUID)" : "name only")}"
                            + $", friend code {(string.IsNullOrEmpty(FriendCodeOf(p)) ? "empty" : "present")}.");
                    foreach (string old in LegacyKeysOf(p, kind)) {
                        if (old == key || !Done.Add(old + "\n" + key)) continue;
                        foreach (var (name, rename) in Stores) {
                            try {
                                if (rename(old, key))
                                    UsefulTORStuffPlugin.Logger?.LogInfo(
                                        $"[PlayerIdentity] {name}: entries of {Short(old)} now stored under {Short(key)} ({NameOf(p)}).");
                            } catch (Exception e) {
                                UsefulTORStuffPlugin.Logger?.LogWarning($"[PlayerIdentity] {name}: rename failed: {e.Message}");
                            }
                        }
                    }
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[PlayerIdentity] migration failed: {e.Message}");
            }
        }

        // Logs show what kind of key moved, not the full account id.
        private static string Short(string key) {
            if (key.StartsWith(PuidPrefix, StringComparison.Ordinal))
                return PuidPrefix + (key.Length > PuidPrefix.Length + 6 ? key.Substring(PuidPrefix.Length, 6) + "..." : key.Substring(PuidPrefix.Length));
            return key.StartsWith(NamePrefix, StringComparison.Ordinal) ? key : "friend code";
        }
    }
}
