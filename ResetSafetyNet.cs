// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ResetSafetyNet - the round reset of every mod still happens when its detour died.
 *
 * About seventy features of Unknown's Collection, this mod, Chance and HostFix clear their state in
 * a postfix on TOR's RPCProcedure.resetVariables. That is a MANAGED TOR method, and with .NET's
 * tiered compilation on (the default for everyone, see DetourWatchdog) its Harmony detour can drop
 * silently mid-session: TOR's body still runs, none of the postfixes do. Then state of the last
 * game leaks into the next one in the same lobby. Seen 2026-10-02: a Revenger who was alive for the
 * host and dead for others (stale Lover-Revenger state on one client), the Hunter and a second
 * "Follower" offered in the Role Draft (guess entries never removed), an old Auditor still treated
 * as Auditor.
 *
 * Detection, not repair (no code writes, see DetourWatchdog for why): a canary postfix on
 * resetVariables flags each run. Around TOR's three call sites (the host's RoleManager.SelectRoles
 * postfix, the ResetVaribles RPC on clients, the OnGameEnd postfix) the flag is cleared before and
 * read after. Those three hooks are on GAME methods (Il2Cpp), which tiering never touches.
 * When the canary stayed silent, every resetVariables postfix of every mod is run from here, taken
 * from Harmony's own patch list (so manual patches count too).
 *  - At a round start: right away (client: after TOR's handling of the RPC, the same order as
 *    normal; host: before TOR's postfix when the drop is already known, after it otherwise).
 *  - At the game end: only noted. Running them there would come after the end-screen postfixes
 *    and could wipe what those just recorded; the next round start does it.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {

    internal static class ResetSafetyNet {
        private static bool canaryRan;
        private static bool dropKnown;          // the last observed call ran without our postfixes
        private static bool ranThisRoundStart;
        private static byte resetRpcId = byte.MaxValue;
        private static bool resetRpcResolved;

        private static MethodInfo ResetMethod => AccessTools.Method(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables));

        private static byte ResetRpcId {
            get {
                if (resetRpcResolved) return resetRpcId;
                resetRpcResolved = true;
                try {
                    var e = typeof(RPCProcedure).Assembly.GetType("TheOtherRoles.CustomRPC");
                    if (e != null) resetRpcId = (byte)(int)Enum.Parse(e, "ResetVaribles");
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ResetSafetyNet] ResetVaribles id not found: {ex.Message}");
                }
                return resetRpcId;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class Canary {
            public static void Postfix() => canaryRan = true;
        }

        /// <summary>Runs every resetVariables postfix of every mod, except this canary.</summary>
        private static void RunAll(string where) {
            int ok = 0, failed = 0, skipped = 0;
            try {
                var info = Harmony.GetPatchInfo(ResetMethod);
                if (info == null) return;
                foreach (var p in info.Postfixes.OrderByDescending(x => x.priority)) {
                    var m = p.PatchMethod;
                    if (m == null || m.DeclaringType == typeof(Canary)) continue;
                    if (!m.IsStatic || m.GetParameters().Length != 0) { skipped++; continue; }
                    try { m.Invoke(null, null); ok++; }
                    catch (Exception e) {
                        failed++;
                        UsefulTORStuffPlugin.Logger?.LogWarning($"[ResetSafetyNet] {m.DeclaringType?.FullName}: {e.InnerException?.Message ?? e.Message}");
                    }
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ResetSafetyNet] run failed: {e}");
            }
            UsefulTORStuffPlugin.Logger?.LogWarning(
                $"[ResetSafetyNet] resetVariables ran without its postfixes ({where}): ran {ok} of them here"
                + (failed > 0 ? $", {failed} failed" : "") + (skipped > 0 ? $", {skipped} skipped (parameters)" : "") + ".");
        }

        // ---- host: round start ----
        [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
        static class SelectRolesPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                canaryRan = false;
                ranThisRoundStart = false;
                // A drop seen at the game end stays: run before TOR's postfix, so the resets come
                // before TOR assigns the roles, as they normally would.
                if (dropKnown) { RunAll("round start, host, drop known"); ranThisRoundStart = true; }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    dropKnown = !canaryRan;
                    if (dropKnown && !ranThisRoundStart) RunAll("round start, host");
                } catch { }
            }
        }

        // ---- clients: TOR's ResetVaribles RPC (handled in TOR's HandleRpc postfix) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        static class ResetRpcPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix([HarmonyArgument(0)] byte callId) {
                if (callId == ResetRpcId) canaryRan = false;
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix([HarmonyArgument(0)] byte callId) {
                try {
                    if (callId != ResetRpcId) return;
                    dropKnown = !canaryRan;
                    if (dropKnown) RunAll("round start, reset RPC");
                } catch { }
            }
        }

        // ---- game end: note only ----
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() => canaryRan = false;

            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                dropKnown = !canaryRan;
                if (dropKnown)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[ResetSafetyNet] resetVariables postfixes did not run at the game end; they run at the next round start.");
            }
        }
    }
}
