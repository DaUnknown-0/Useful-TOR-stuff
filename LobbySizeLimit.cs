// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbySizeLimit - lets the host raise the lobby size above 15 (up to 25) with TOR's "/size" chat
 * command. EXPERIMENTAL and OFF by default: the option "Maximum Lobby Size (/size)" sits at 15.
 *
 * Where the 15 comes from. TOR's DynamicLobbies forces MaxPlayers = 15 in HostGame ("Force 15
 * Player Lobby on Server") and clamps "/size" to 4..15; our TorLobbyFixes (TOR-M23) mirrors that
 * clamp; the vanilla slider ends at 15 as well. On the community servers (Impostor) the limit is
 * NOT a server rule: Impostor's join check is "_players.Count >= Options.MaxPlayers", and
 * Game.Options is overwritten from the host's synced game options (LogicOptions.DeserializeInto).
 * The SyncSettings RPC itself is ignored by Impostor, so TOR's RpcSyncSettings never reaches the
 * server's limit: the sync that counts is GameManager.LogicOptions.SyncOptions().
 * The Miniduikboot operators confirmed (2026-10-08) that 20 to 25 players are fine on their
 * servers, with reports of trouble only above ~40.
 *
 * What this does (host only, no RPC, no handshake: joiners need nothing):
 *  1) SizeCommandPatch captures "/size N" before TOR's own handler clears the chat field, and after
 *     TOR's handler (which has clamped to 15) applies N up to the option's limit: DynamicLobbies.
 *     LobbyLimit (TOR's own join kick), currentNormalGameOptions.MaxPlayers and a LogicOptions sync.
 *  2) TorLobbyFixes' M23 clamp uses CurrentMax instead of the literal 15.
 *  3) WatchPatch re-applies the size when something (the vanilla settings screen) put MaxPlayers
 *     back to a smaller value while TOR's LobbyLimit stayed raised, and logs it.
 *
 * Not touched / not verified without a real session: the vanilla settings screen and the
 * validity check on the options (GameOptionsData.AreInvalid, which TOR also patches), the meeting
 * grid layout with more than 15 players, colour assignment (TOR's CheckColor patch already walks
 * through its extended palette).
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Modules;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class LobbySizeLimit {
        public const int VanillaMax = 15;
        public const int AbsoluteMax = 25;

        public static CustomOption OptionLimit; // 1378

        /// The host's allowed ceiling for /size: 15 (vanilla behaviour) unless the option is raised.
        public static int CurrentMax {
            get {
                try {
                    if (OptionLimit == null) return VanillaMax;
                    return Mathf.Clamp(Mathf.RoundToInt(OptionLimit.getFloat()), VanillaMax, AbsoluteMax);
                } catch {
                    return VanillaMax;
                }
            }
        }

        public static void CreateOptions() {
            try {
                // ID 1378 (free per ID-Registry.md; keep unique across all our plugins).
                OptionLimit = CustomOption.Create(
                    1378, Types.General, "Maximum Lobby Size (/size)", 15f, 15f, 25f, 1f);
                UTSLocalization.BindOptionTitle(OptionLimit, "uts.lobbysize.limit_option");

                // Next to the role-count block of the General tab, like the impostor count options.
                var opts = CustomOption.options;
                opts.Remove(OptionLimit);
                int idx = opts.IndexOf(CustomOptionHolder.crewmateRolesFill);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, OptionLimit);

                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbySizeLimit] Option created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LobbySizeLimit] CreateOptions failed: {e}");
            }
        }

        // The number typed after "/size ", captured before TOR's handler clears the chat field.
        private static int pendingSize;

        private static bool IsLobbyHost() {
            var c = AmongUsClient.Instance;
            return c != null && c.AmHost && c.CanBan()
                && c.GameState != InnerNet.InnerNetClient.GameStates.Started;
        }

        // ---- "/size N" above 15 -------------------------------------------------------------

        [HarmonyPatch]
        private static class SizeCommandPatch {
            private static MethodBase TargetMethod() {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.DynamicLobbies+SendChatPatch");
                return t?.GetMethod("Prefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            private static bool Prepare(MethodBase original) {
                bool found = TargetMethod() != null;
                if (!found)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[LobbySizeLimit] DynamicLobbies.SendChatPatch.Prefix not found - /size above 15 inactive.");
                return found;
            }

            // First: TOR's handler clears freeChatField, so the text has to be read before it runs.
            [HarmonyPriority(Priority.First)]
            // TOR's Prefix is STATIC and its first parameter is merely NAMED __instance, so it is
            // picked by index here (Harmony would hand a real __instance injection a null).
            public static void Prefix([HarmonyArgument(0)] ChatController chat) {
                pendingSize = 0;
                try {
                    if (CurrentMax <= VanillaMax || !IsLobbyHost()) return;
                    string text = chat.freeChatField.Text;
                    if (text == null || !text.ToLower().StartsWith("/size ")) return;
                    if (int.TryParse(text.Substring(6), out int n)) pendingSize = n;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbySizeLimit] reading /size failed: {e.Message}");
                }
            }

            // Last: after TOR's clamp to 15 and after TorLobbyFixes' M23 postfix.
            [HarmonyPriority(Priority.Last)]
            public static void Postfix([HarmonyArgument(0)] ChatController chat) {
                int requested = pendingSize;
                pendingSize = 0;
                if (requested <= VanillaMax) return;
                try {
                    Apply(chat, requested);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LobbySizeLimit] applying size failed: {e}");
                }
            }
        }

        private static void Apply(ChatController chat, int requested) {
            int max = CurrentMax;
            int n = Mathf.Clamp(requested, VanillaMax + 1, max);
            var opts = GameOptionsManager.Instance.currentNormalGameOptions;

            DynamicLobbies.LobbyLimit = n;
            if (opts.MaxPlayers != n) opts.MaxPlayers = n;
            if (DestroyableSingleton<GameStartManager>.InstanceExists)
                DestroyableSingleton<GameStartManager>.Instance.LastPlayerCount = n;
            GameManager.Instance?.LogicOptions?.SyncOptions();

            chat.AddChat(PlayerControl.LocalPlayer, UTSLocalization.Tr("uts.lobbysize.applied", n, max));
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[LobbySizeLimit] Lobby size set to {n} (requested {requested}, limit {max}), " +
                $"MaxPlayers now {opts.MaxPlayers}, clients {AmongUsClient.Instance.allClients.Count}.");
        }

        // ---- keep the raised size alive -------------------------------------------------------

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        private static class WatchPatch {
            private static float nextCheck;
            private static float nextFix;

            public static void Postfix() {
                try {
                    if (Time.unscaledTime < nextCheck) return;
                    nextCheck = Time.unscaledTime + 2f;
                    int limit = DynamicLobbies.LobbyLimit;
                    if (limit <= VanillaMax) return;
                    var c = AmongUsClient.Instance;
                    if (c == null || !c.AmHost) return;
                    var opts = GameOptionsManager.Instance.currentNormalGameOptions;
                    if (opts.MaxPlayers == limit) return;

                    // Something (most likely the vanilla settings screen) wrote a smaller MaxPlayers
                    // while TOR's own limit stayed raised: the server would keep the smaller one.
                    if (Time.unscaledTime < nextFix) return;
                    nextFix = Time.unscaledTime + 10f;
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[LobbySizeLimit] MaxPlayers was {opts.MaxPlayers}, expected {limit}: re-applying.");
                    opts.MaxPlayers = limit;
                    GameManager.Instance?.LogicOptions?.SyncOptions();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbySizeLimit] watch failed: {e.Message}");
                }
            }
        }
    }
}
