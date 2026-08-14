// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SidekickKillJackal - new option "Sidekick Can Kill Jackal" (betrayal).
 *
 * TOR explicitly forbids the Sidekick from targeting the Jackal: PlayerControlFixedUpdatePatch
 * .sidekickSetTarget() adds Jackal.jackal to untargetablePlayers before calling setTarget(), so the
 * Jackal is skipped (PlayerControlPatch.cs:254-261). The kill itself has no Sidekick→Jackal block
 * (Helpers.checkMuderAttempt) and the sidekickKillButton only needs Sidekick.currentTarget.
 *
 * With this option ON we re-run the target selection in a postfix WITHOUT excluding the Jackal, so
 * the Sidekick can target (and then kill, via TOR's existing button) the Jackal like any other
 * player. Whether the betrayer is then promoted to Jackal stays governed by TOR's existing
 * "Sidekick Gets Promoted To Jackal On Jackal Death" option (RPC.cs:778) — we don't override it.
 *
 * sidekickSetTarget is private static, so it's patched via reflection; setTarget/setPlayerOutline
 * are public static in PlayerControlFixedUpdatePatch and called directly.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Patches;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SidekickKillJackal {
        public static CustomOption Option;  // Off/On toggle

        public static void CreateOptions() {
            try {
                // invertedParent: true → option is visible when jackalCanCreateSidekickFromImpostor
                // is OFF, meaning Fake-SK is possible. Only then does betraying the Jackal make sense.
                Option = CustomOption.Create(
                    1240, Types.Neutral, "Sidekick Can Kill Jackal",
                    false, CustomOptionHolder.jackalCanCreateSidekickFromImpostor,
                    invertedParent: true);
                UTSLocalization.BindOptionTitle(Option, "uts.sidekickkilljackal.option_name");

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.jackalCanCreateSidekickFromImpostor);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[SidekickKillJackal] Option created under Sidekick.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SidekickKillJackal] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var type = torAsm.GetType("TheOtherRoles.Patches.PlayerControlFixedUpdatePatch");
                var m = type?.GetMethod("sidekickSetTarget", BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[SidekickKillJackal] sidekickSetTarget not found — feature disabled.");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(SidekickKillJackal), nameof(Postfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[SidekickKillJackal] Patched sidekickSetTarget.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SidekickKillJackal] TryPatch failed: {e}");
            }
        }

        // Runs after TOR's sidekickSetTarget (which excluded the Jackal). Re-select including the
        // Jackal so the Sidekick can target it. Only when the option is on and Fake-SK is possible.
        public static void Postfix() {
            try {
                if (Option == null || !UTSGate.Bool(Option)) return;
                if (Sidekick.sidekick == null || Sidekick.sidekick != PlayerControl.LocalPlayer) return;
                if (!Sidekick.canKill) return;
                if (Jackal.canCreateSidekickFromImpostor) return; // Fake-SK not possible → no-op
                if (Jackal.jackal == null || Jackal.jackal.Data == null || Jackal.jackal.Data.IsDead) return;

                // Mirror TOR's only other exclusion (an un-grown Mini), but NOT the Jackal.
                var untargetable = new List<PlayerControl>();
                if (Mini.mini != null && !Mini.isGrownUp()) untargetable.Add(Mini.mini);

                var target = PlayerControlFixedUpdatePatch.setTarget(untargetablePlayers: untargetable);
                Sidekick.currentTarget = target;
                PlayerControlFixedUpdatePatch.setPlayerOutline(target, Palette.ImpostorRed);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SidekickKillJackal] Postfix failed: {e}");
            }
        }
    }
}
