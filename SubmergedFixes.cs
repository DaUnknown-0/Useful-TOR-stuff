// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SubmergedFixes - compatibility fixes for the Submerged map (found by SubmergedSelfTest, 24.09.).
 *
 * TOR's SubmergedCompatibility.RepairOxygen (the Engineer's remote O2 fix, run on every client via
 * CustomRPC.EngineerFixSubmergedOxygen) sends RpcRepairSystem(130, 64) and then calls Submerged's
 * SubmarineOxygenSystem.RepairDamage(PlayerControl, byte) by reflection with a boxed INT 64. Reflection
 * does not narrow int to byte, so that second call throws ArgumentException ("Object of type
 * 'System.Int32' cannot be converted to type 'System.Byte'"), and TOR only catches
 * NullReferenceException: the exception leaves the Engineer's button handler and the RPC handler on
 * every other client. The mask itself already arrived through the RPC, so the replacement keeps that
 * and does the local call with a real byte, swallowing any failure.
 *
 * Submerged's licence allows Harmony patches for mod compatibility; nothing here patches Submerged
 * itself, only TOR's bridge to it.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using System.Linq;

namespace UsefulTORStuff {
    internal static class SubmergedFixes {
        private static bool resolved;
        private static System.Reflection.PropertyInfo o2Instance;
        private static System.Reflection.MethodInfo o2Repair;

        [HarmonyPatch(typeof(SubmergedCompatibility), nameof(SubmergedCompatibility.RepairOxygen))]
        private static class RepairOxygenPatch {
            private static bool Prefix() {
                if (!SubmergedCompatibility.Loaded) return false;
                try { ShipStatus.Instance.RpcRepairSystem((SystemTypes)130, 64); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SubmergedFixes] O2 mask RPC failed: {e.Message}"); }
                try {
                    if (!resolved) {
                        resolved = true;
                        var t = SubmergedCompatibility.Types?.FirstOrDefault(x => x.Name == "SubmarineOxygenSystem" && x.Namespace == "Submerged.Systems.Oxygen");
                        if (t != null) {
                            o2Instance = AccessTools.Property(t, "Instance");
                            o2Repair = AccessTools.Method(t, "RepairDamage");
                        }
                    }
                    var inst = o2Instance?.GetValue(null);
                    if (inst != null && o2Repair != null)
                        o2Repair.Invoke(inst, new object[] { PlayerControl.LocalPlayer, (byte)64 });
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[SubmergedFixes] local O2 mask failed: {e.Message}");
                }
                return false;
            }
        }

        // TOR hooks Object.Destroy(GameObject) (ExileControllerWrapUpPatch.Prefix) to catch the end of
        // Submerged's exile cutscene and spawn-in. Its FungleSecurity branch checks obj != null, the
        // Submerged branch below it does not, so on Submerged a Destroy(null) would throw out of that
        // prefix. Preventive: the 8 NullReferenceExceptions per round the selftest logs there come from
        // the spawn-in branch's Chameleon.lastMoved, which only freeplay leaves null (TOR fills it in
        // resetVariables at role assignment). Destroy(null) is a no-op, so skipping TOR's prefix for a
        // null object changes nothing else. The class is internal to TOR -> by name.
        [HarmonyPatch]
        private static class TorDestroyPrefixNullGuard {
            private static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method("TheOtherRoles.Patches.ExileControllerWrapUpPatch:Prefix", new[] { typeof(UnityEngine.GameObject) });

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(UnityEngine.GameObject obj) => obj != null;
        }
    }
}
