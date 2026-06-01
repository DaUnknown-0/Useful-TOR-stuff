// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SheriffParityWin - new Sheriff option "Sheriff Prevents Killer Parity Win".
 *
 * Normally Impostors/Jackal win the moment their count reaches the crew's
 * (TheOtherRoles.Patches.CheckEndCriteriaPatch.CheckAndEndGameForImpostorWin /
 * .CheckAndEndGameForJackalWin). But while a Sheriff is alive he could shoot a killer and
 * break that parity, so the instant win is premature. With this option ON we suppress that
 * parity win as long as a Sheriff is alive.
 *
 * The win check runs host-authoritatively (RpcEndGame), so the feature ALWAYS applies from the
 * host regardless of who has the mod — it is NOT gated on "everyone has the mod". The host just
 * gets a lobby warning (see UsefulVersionHandshake) when someone is missing the mod, because
 * those clients won't see the option and otherwise wouldn't understand the delayed end.
 *
 * The two win-check methods are private/static in TOR's internal CheckEndCriteriaPatch, so they
 * are patched via reflection + Harmony (like the Bloody patches in UsefulTORStuffPlugin).
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SheriffParityWin {
        // Set in CreateOptions(); read by the win-check prefixes and the lobby warning.
        public static CustomOption Option;       // Off/On toggle
        public static CustomOption ScopeOption;  // 0 = At Exact Parity Only, 1 = Always While Sheriff Alive

        // Create the in-game options (under Sheriff). Called from UsefulTORStuffPlugin.Load() after
        // TOR has already run CustomOptionHolder.Load() (guaranteed by the hard dependency).
        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1200, Types.Crewmate, "Sheriff Prevents Killer Parity Win",
                    false, CustomOptionHolder.sheriffSpawnRate);

                ScopeOption = CustomOption.Create(
                    1201, Types.Crewmate, "Parity Win Block Mode",
                    new string[] { "At Exact Parity Only", "Always While Sheriff Alive" },
                    Option);

                // Options render in CustomOption.options list order (filtered by type), so a
                // late-created option lands at the BOTTOM of the Crewmate tab. Move ours directly
                // after the Sheriff core options so they appear under "Sheriff".
                var opts = CustomOption.options;
                opts.Remove(Option);
                opts.Remove(ScopeOption);
                int idx = opts.IndexOf(CustomOptionHolder.sheriffCanKillNeutrals);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);
                opts.Insert(idx + 2, ScopeOption);

                UsefulTORStuffPlugin.Logger?.LogInfo("[SheriffParityWin] Options created under Sheriff.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SheriffParityWin] CreateOptions failed: {e}");
            }
        }

        // Attach the win-check prefixes via reflection (private statics in an internal TOR class).
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var type = torAsm.GetType("TheOtherRoles.Patches.CheckEndCriteriaPatch");
                if (type == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[SheriffParityWin] CheckEndCriteriaPatch not found — feature disabled.");
                    return;
                }

                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
                var impM = type.GetMethod("CheckAndEndGameForImpostorWin", flags);
                var jackalM = type.GetMethod("CheckAndEndGameForJackalWin", flags);
                if (impM == null || jackalM == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[SheriffParityWin] Win-check method(s) not found — feature disabled.");
                    return;
                }

                harmony.Patch(impM, prefix: new HarmonyMethod(typeof(SheriffParityWin), nameof(ImpostorWinPrefix)));
                harmony.Patch(jackalM, prefix: new HarmonyMethod(typeof(SheriffParityWin), nameof(JackalWinPrefix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[SheriffParityWin] Patched Impostor/Jackal parity-win checks.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SheriffParityWin] TryPatch failed: {e}");
            }
        }

        // Prefix for CheckAndEndGameForImpostorWin: suppress the Impostor parity win while gated.
        public static bool ImpostorWinPrefix(ref bool __result) {
            try {
                CountAlive(out int total, out int impAlive, out int _);
                if (ShouldSuppress(impAlive, total)) { __result = false; return false; }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SheriffParityWin] Impostor prefix failed: {e}");
            }
            return true; // let the original run
        }

        // Prefix for CheckAndEndGameForJackalWin: suppress the Jackal parity win while gated.
        public static bool JackalWinPrefix(ref bool __result) {
            try {
                CountAlive(out int total, out int _, out int jackalAlive);
                if (ShouldSuppress(jackalAlive, total)) { __result = false; return false; }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SheriffParityWin] Jackal prefix failed: {e}");
            }
            return true; // let the original run
        }

        private static bool SheriffAlive() {
            var s = Sheriff.sheriff;
            return s != null && s.Data != null && !s.Data.IsDead;
        }

        // Counts mirror TOR's PlayerStatistics: alive players, alive Impostors, alive Jackal+Sidekick.
        private static void CountAlive(out int total, out int impAlive, out int jackalAlive) {
            total = impAlive = jackalAlive = 0;
            var gd = GameData.Instance;
            if (gd == null) return;
            foreach (var pi in gd.AllPlayers.GetFastEnumerator()) {
                if (pi == null || pi.Disconnected || pi.IsDead) continue;
                total++;
                if (pi.Role != null && pi.Role.IsImpostor) impAlive++;
                if (Jackal.jackal != null && Jackal.jackal.PlayerId == pi.PlayerId) jackalAlive++;
                if (Sidekick.sidekick != null && Sidekick.sidekick.PlayerId == pi.PlayerId) jackalAlive++;
            }
        }

        private static bool ShouldSuppress(int killerAlive, int totalAlive) {
            if (Option == null || !Option.getBool()) return false; // option off
            if (!SheriffAlive()) return false;                     // no Sheriff to break parity
            bool outnumber = killerAlive > (totalAlive - killerAlive);
            // Scope 0 ("At Exact Parity Only"): when killers truly outnumber crew, a Sheriff kill
            // can't break parity, so let the win fire. Scope 1 ("Always"): block while Sheriff alive.
            if (ScopeOption != null && ScopeOption.getSelection() == 0 && outnumber) return false;
            return true;
        }
    }
}
