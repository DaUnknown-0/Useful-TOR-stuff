// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ImpostorCountRange - random impostor count (min/max) + Spy unlock + Jackal sidekick gating.
 *
 * 1) Random Impostor Count: instead of a fixed vanilla impostor count the host rolls
 *    rnd(min..max) (each 1-3) once per game, right before the vanilla role assignment.
 *    Vanilla assigns impostors on the HOST inside RoleManager.SelectRoles via
 *    IGameOptionsExtensions.GetAdjustedNumImpostors (TOR postfixes it to clamp(NumImpostors,1,3),
 *    RoleAssignmentPatch.cs:23-33). We arm a flag in a SelectRoles prefix and override the
 *    result with the rolled count in a lower-priority postfix that runs after TOR's, so the
 *    override is only live during the assignment window. The count is fixed for the whole game.
 *
 *    Secrecy: every other surface (lobby panel, intro "There are X Impostors") reads the
 *    vanilla NumImpostors setting, so the host enforces NumImpostors == configured max while
 *    in lobby (GameStartManager.Update, throttled). Crew therefore always sees the maximum;
 *    the actual roll never leaks. TOR's Validate postfix already un-does vanilla player-count
 *    clamping (RoleAssignmentPatch.cs:35-43).
 *
 *    Intro team leak: TOR hides the intro team lineup whenever the Spy is enabled AND more
 *    than 1 impostor was assigned (IntroPatch.cs:172). With a random count that condition
 *    itself leaks "there are 2+ imps", so our own lower-priority BeginCrewmate/BeginImpostor
 *    prefixes apply the same only-yourself lineup when the feature is active with max >= 2
 *    and only 1 impostor spawned.
 *
 * 2) Spy unlock: TOR only adds the Spy to the role pool when MORE than 1 impostor actually
 *    spawned (RoleAssignmentPatch.cs:155-158). With a random count the Spy must stay possible
 *    whenever the configured MAX is >= 2 (otherwise a Spy sighting would reveal the count), so
 *    a reflection postfix on RoleManagerSelectRolesPatch.getRoleAssignmentData adds the Spy to
 *    crewSettings when exactly 1 impostor spawned, max >= 2 and the Spy spawn rate is > 0.
 *    Limitation: TOR's Role Draft has its own hardcoded "impostorCount < 2" Spy filter
 *    (RoleDraft.cs:131) which we do not patch - in draft games the Spy stays gated on the
 *    actual count.
 *
 * 3) Jackal sidekick gating (both only when TOR's "Jackal Can Create A Sidekick" is ON;
 *    the two modes are mutually exclusive):
 *      - "Sidekick Only Fills A Missing Impostor" (sub-option of the range feature): the
 *        Jackal gets the sidekick button exactly when fewer impostors spawned than the
 *        configured max (guaranteed, the chance below is skipped). At full impostor count
 *        there is no sidekick button.
 *      - "Chance That The Jackal Can Create A Sidekick" (sub-option of TOR's sidekick toggle):
 *        rolled once per game by the host; at 100% the vanilla TOR path is untouched.
 *    The host decides after role assignment and broadcasts the verdict (RPC 244); each client
 *    sets Jackal.canCreateSidekick, which gates TOR's button (Buttons.cs:1036) and the intro
 *    "recruit a Sidekick" blurb (Helpers.cs:198). The reliable RPC arrives after TOR's
 *    ResetVaribles RPC (same ordered connection), so it overrides Jackal.clearAndReload().
 *    A Sidekick promoted to Jackal keeps TOR's own "promoted Jackal can create a Sidekick"
 *    behaviour (RPC.cs:725) - the per-game verdict does not re-apply after promotion.
 *
 * Requires every player to run this mod for the sidekick verdict (RPC) and the intro hide;
 * the impostor count roll itself is purely host-side and works with plain-TOR clients.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class ImpostorCountRange {
        // 244 is free: TOR uses 100-183, Unknown's Collection 190-210, our other mods 245-254
        // plus 104/105/139/167/200-202/250/251 (see ID-Registry.md).
        public const byte SidekickAllowedRpcId = 244;

        public static CustomOption OptionEnable;         // 1370
        public static CustomOption OptionMin;            // 1371
        public static CustomOption OptionMax;            // 1372
        public static CustomOption OptionSidekickRefill; // 1373
        public static CustomOption OptionSidekickChance; // 1374

        // Armed by the SelectRoles prefix on the host only; GetAdjustedNumImpostors is also
        // called outside the assignment (lobby UI, intro) and must NOT see the roll there.
        private static bool assignmentActive;
        private static int rolledCount;

        // TORMapOptions is internal to TOR, resolve its gameMode field once via reflection.
        private static FieldInfo torGameModeField;
        private static bool torGameModeResolved;

        public static void CreateOptions() {
            try {
                // IDs 1370-1374 (free per ID-Registry.md; keep unique across all our plugins,
                // duplicate ids scramble each other's selections via TOR's id-delta sync).
                OptionEnable = CustomOption.Create(
                    1370, Types.General, "Random Impostor Count", false);
                UTSLocalization.BindOptionTitle(OptionEnable, "uts.impcount.enable_option");

                OptionMin = CustomOption.Create(
                    1371, Types.General, "Minimum Impostors", 1f, 1f, 3f, 1f, OptionEnable);
                UTSLocalization.BindOptionTitle(OptionMin, "uts.impcount.min_option");

                OptionMax = CustomOption.Create(
                    1372, Types.General, "Maximum Impostors", 2f, 1f, 3f, 1f, OptionEnable);
                UTSLocalization.BindOptionTitle(OptionMax, "uts.impcount.max_option");

                OptionSidekickRefill = CustomOption.Create(
                    1373, Types.General, "Sidekick Only Fills A Missing Impostor", false, OptionEnable);
                UTSLocalization.BindOptionTitle(OptionSidekickRefill, "uts.impcount.refill_option");

                OptionSidekickChance = CustomOption.Create(
                    1374, Types.Neutral, "Chance That The Jackal Can Create A Sidekick",
                    100f, 0f, 100f, 10f, CustomOptionHolder.jackalCanCreateSidekick);
                UTSLocalization.BindOptionTitle(OptionSidekickChance, "uts.impcount.sidekick_chance_option");

                var opts = CustomOption.options;

                // Range block: right below TOR's role-count block (300-308) in the General tab.
                opts.Remove(OptionEnable);
                opts.Remove(OptionMin);
                opts.Remove(OptionMax);
                opts.Remove(OptionSidekickRefill);
                int idx = opts.IndexOf(CustomOptionHolder.crewmateRolesFill);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, OptionSidekickRefill);
                opts.Insert(idx + 1, OptionMax);
                opts.Insert(idx + 1, OptionMin);
                opts.Insert(idx + 1, OptionEnable);

                // Chance: directly under TOR's "Jackal Can Create A Sidekick" toggle.
                opts.Remove(OptionSidekickChance);
                idx = opts.IndexOf(CustomOptionHolder.jackalCanCreateSidekick);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, OptionSidekickChance);

                UsefulTORStuffPlugin.Logger?.LogInfo("[ImpostorCountRange] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] CreateOptions failed: {e}");
            }
        }

        // Spy unlock: getRoleAssignmentData lives in the internal RoleManagerSelectRolesPatch,
        // so it is patched via reflection (postfix adds the Spy to the crew pool).
        public static void TryPatch(Harmony harmony) {
            UTSRpc.Register(SidekickAllowedRpcId, HandleModuleRpc);
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var type = torAsm.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                var m = type?.GetMethod("getRoleAssignmentData", BindingFlags.Public | BindingFlags.Static);
                if (m == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[ImpostorCountRange] getRoleAssignmentData not found - Spy unlock disabled.");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(ImpostorCountRange), nameof(RoleDataPostfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[ImpostorCountRange] Patched getRoleAssignmentData (Spy unlock).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] TryPatch failed: {e}");
            }
        }

        // ---- shared state helpers -------------------------------------------------------------

        public static int EffectiveMax =>
            OptionMax == null ? 1 : Mathf.Clamp(Mathf.RoundToInt(OptionMax.getFloat()), 1, 3);

        public static int EffectiveMin {
            get {
                int min = OptionMin == null ? 1 : Mathf.Clamp(Mathf.RoundToInt(OptionMin.getFloat()), 1, 3);
                int max = EffectiveMax;
                return min > max ? max : min; // TOR convention for min/max pairs
            }
        }

        // Feature is on AND we are in a normal TOR game (custom gamemodes HideNSeek/PropHunt
        // pick their own impostor count, vanilla HideNSeek has no Normal GameMode).
        private static bool FeatureEnabled =>
            OptionEnable != null && OptionEnable.getBool() && IsNormalTorGame();

        private static bool IsNormalTorGame() {
            try {
                var mgr = GameOptionsManager.Instance;
                if (mgr == null || mgr.CurrentGameOptions == null) return false;
                if (mgr.CurrentGameOptions.GameMode != GameModes.Normal) return false;
                var gm = TorGameMode();
                return gm != CustomGamemodes.HideNSeek && gm != CustomGamemodes.PropHunt;
            } catch {
                return false;
            }
        }

        private static CustomGamemodes TorGameMode() {
            if (!torGameModeResolved) {
                torGameModeResolved = true;
                torGameModeField = typeof(CustomOption).Assembly
                    .GetType("TheOtherRoles.TORMapOptions")
                    ?.GetField("gameMode", BindingFlags.Public | BindingFlags.Static);
                if (torGameModeField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[ImpostorCountRange] TORMapOptions.gameMode not found - assuming Classic.");
            }
            return torGameModeField != null
                ? (CustomGamemodes)(int)torGameModeField.GetValue(null)
                : CustomGamemodes.Classic;
        }

        private static int CountAssignedImpostors() {
            return PlayerControl.AllPlayerControls.ToArray().ToList()
                .Where(x => x != null && x.Data != null && x.Data.Role != null && x.Data.Role.IsImpostor)
                .Count();
        }

        // ---- 1) the roll ------------------------------------------------------------------------

        [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
        private static class SelectRolesPatch {
            // Before vanilla assignment: roll and arm the override (host only; SelectRoles only
            // runs on the authority anyway, the AmHost check is belt and braces).
            [HarmonyPrefix]
            [HarmonyPriority(Priority.High)]
            public static void Prefix() {
                assignmentActive = false;
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!FeatureEnabled) return;
                    rolledCount = rnd.Next(EffectiveMin, EffectiveMax + 1);
                    assignmentActive = true;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] roll failed: {e}");
                }
            }

            // After TOR's postfix (default priority) finished the whole role assignment:
            // disarm the override and settle the per-game sidekick verdict.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                assignmentActive = false;
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!IsNormalTorGame()) return;
                    DecideSidekickAllowance();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] sidekick verdict failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(IGameOptionsExtensions), nameof(IGameOptionsExtensions.GetAdjustedNumImpostors))]
        private static class AdjustedNumImpostorsPatch {
            // Runs after TOR's postfix (default priority), which handles HideNSeek/PropHunt and
            // the vanilla-limit-ignoring clamp. Only live while SelectRoles runs on the host.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Low)]
            public static void Postfix(ref int __result) {
                if (assignmentActive) __result = Mathf.Clamp(rolledCount, 1, 3);
            }
        }

        // ---- secrecy: vanilla setting always shows the configured max ---------------------------

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        private static class LobbyEnforceMaxPatch {
            private static float nextCheck;

            public static void Postfix() {
                try {
                    if (Time.unscaledTime < nextCheck) return;
                    nextCheck = Time.unscaledTime + 1f;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (!FeatureEnabled) return;
                    var opts = GameOptionsManager.Instance.CurrentGameOptions;
                    int max = EffectiveMax;
                    if (opts.NumImpostors == max) return;
                    opts.SetInt(Int32OptionNames.NumImpostors, max);
                    GameManager.Instance?.LogicOptions?.SyncOptions();
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[ImpostorCountRange] Vanilla impostor count set to max ({max}).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[ImpostorCountRange] enforcing max failed: {e.Message}");
                }
            }
        }

        // ---- 2) Spy unlock ----------------------------------------------------------------------

        // Postfix on RoleManagerSelectRolesPatch.getRoleAssignmentData (reflection, see TryPatch).
        // TOR itself only adds the Spy when 2+ impostors actually spawned; with the range feature
        // the Spy must stay possible whenever the configured max allows 2+. Also runs for TOR's
        // RoleDraft pool preview, where the addition is harmless (the draft has its own gate).
        public static void RoleDataPostfix(object __result) {
            try {
                if (__result == null || !FeatureEnabled || EffectiveMax < 2) return;
                int spyRate = CustomOptionHolder.spySpawnRate.getSelection();
                if (spyRate == 0) return;

                var tr = Traverse.Create(__result);
                var impostors = tr.Property("impostors").GetValue() as List<PlayerControl>;
                var crewSettings = tr.Field("crewSettings").GetValue() as Dictionary<byte, int>;
                if (impostors == null || crewSettings == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[ImpostorCountRange] RoleAssignmentData fields not found - Spy unlock inactive.");
                    return;
                }
                if (impostors.Count != 1) return; // 2+: TOR already added the Spy; 0: nothing to hide

                byte spyId = (byte)RoleId.Spy;
                if (!crewSettings.ContainsKey(spyId)) crewSettings[spyId] = spyRate;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] Spy unlock failed: {e}");
            }
        }

        // ---- secrecy: intro team hide when the Spy stays possible at 1 impostor ------------------

        // TOR hides the intro lineup when the Spy is enabled AND 2+ impostors spawned
        // (IntroPatch.setupIntroTeamIcons). At 1 rolled impostor that very difference would
        // leak the count, so we apply the same only-yourself lineup. Priority.Low: runs after
        // TOR's prefix so we overwrite whatever it built.
        private static void HideTeamIfSpyPossible(ref Il2CppSystem.Collections.Generic.List<PlayerControl> team) {
            try {
                if (!FeatureEnabled || EffectiveMax < 2) return;
                if (CustomOptionHolder.spySpawnRate.getSelection() == 0) return;
                if (PlayerControl.LocalPlayer == null) return;
                if (CountAssignedImpostors() > 1) return; // TOR's own hide already applied

                var solo = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                solo.Add(PlayerControl.LocalPlayer);
                team = solo;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] intro hide failed: {e}");
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginCrewmate))]
        private static class BeginCrewmateHidePatch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Low)]
            public static void Prefix(ref Il2CppSystem.Collections.Generic.List<PlayerControl> teamToDisplay) {
                HideTeamIfSpyPossible(ref teamToDisplay);
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginImpostor))]
        private static class BeginImpostorHidePatch {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Low)]
            public static void Prefix(ref Il2CppSystem.Collections.Generic.List<PlayerControl> yourTeam) {
                HideTeamIfSpyPossible(ref yourTeam);
            }
        }

        // ---- 3) sidekick verdict ------------------------------------------------------------------

        // Host only, once per game, after role assignment. Refill mode and the chance are
        // mutually exclusive: refill (needs the range feature) wins and is guaranteed.
        private static void DecideSidekickAllowance() {
            if (CustomOptionHolder.jackalSpawnRate.getSelection() == 0) return;
            if (!CustomOptionHolder.jackalCanCreateSidekick.getBool()) return; // TOR master off

            bool allowed;
            if (FeatureEnabled && OptionSidekickRefill != null && OptionSidekickRefill.getBool()) {
                // Sidekick only fills a missing impostor slot.
                allowed = CountAssignedImpostors() < EffectiveMax;
            } else {
                if (OptionSidekickChance == null) return;
                int chance = Mathf.RoundToInt(OptionSidekickChance.getFloat());
                if (chance >= 100) return; // pure TOR behaviour, no override needed
                allowed = rnd.Next(1, 101) <= chance;
            }

            // LEGACY DUAL-SEND (see UTSRpc.cs): the payload goes out on the legacy callId 244 AND on
            // the consolidated channel 240. Classified IDEMPOTENT: ApplySidekickAllowed does nothing
            // but assign Jackal.canCreateSidekick, so a new build receiving both copies simply writes
            // the same boolean twice. The legacy half exists only for pre-240 builds and can be
            // deleted in a future breaking release.
            UTSRpc.SendDual(SidekickAllowedRpcId, SidekickAllowedRpcId, w => w.Write(allowed));
            ApplySidekickAllowed(allowed);
        }

        // Runs on every client (RPC) and on the host (local call). Arrives after TOR's
        // ResetVaribles RPC on the same ordered connection, so it survives clearAndReload.
        private static void ApplySidekickAllowed(bool allowed) {
            Jackal.canCreateSidekick = allowed && CustomOptionHolder.jackalCanCreateSidekick.getBool();
        }

        // Receiver on the consolidated channel (module byte 244). Registered from TryPatch.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                ApplySidekickAllowed(reader.ReadBoolean());
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ImpostorCountRange] verdict RPC read failed: {e}");
            }
        }

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 244 so messages from
        // pre-240 builds keep working. Idempotent, so receiving both copies is harmless. Delete
        // together with the legacy half of the send in a future breaking release.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        private static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != SidekickAllowedRpcId) return true;
                HandleModuleRpc(reader);
                return false;
            }
        }
    }
}
