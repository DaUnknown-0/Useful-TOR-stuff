using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using Random = System.Random;

namespace UsefulTORStuff {
    internal static class AuditFixB_Presets {
        // Audit 16.11. TOR-003: Fungle anhängen, Submerged bleibt Preset6.
        internal static void EnsurePresets() {
            var option = CustomOptionHolder.presetSelection;
            if (option == null || option.selections.Length != 7) return;
            CustomOptionHolder.presets = CustomOptionHolder.presets.Concat(new[] { "Random Preset Fungle" }).ToArray();
            option.selections = option.selections.Concat(new object[] { "Random Preset Fungle" }).ToArray();
        }

        internal static int RandomPreset(int originalIndex) => originalIndex switch { 6 => 7, 7 => 6, _ => originalIndex };

        public static void ApplyRandomPreset(CustomOption option, int originalIndex, bool notifyUsers) {
            EnsurePresets();
            int index = RandomPreset(originalIndex);
            if (CustomOption.preset != index) option.updateSelection(index, notifyUsers);
        }

        private static double mapRoll;
        private static bool hasMapRoll;

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        private static class PrepareStartPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                if (!PacedOptionSync.StartDeferred) hasMapRoll = false;
            }
        }
        // Audit 16.11. UTS Teil 2: Ein wegen Sync verzögerter Start würfelt nicht erneut.
        public static double NextMapRoll(Random random) {
            if (!hasMapRoll) { mapRoll = random.NextDouble(); hasMapRoll = true; }
            return mapRoll;
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MenuPatch {
            public static void Prefix() => EnsurePresets();
        }

        [HarmonyPatch]
        private static class RandomMapPatch {
            private static MethodBase TargetMethod() => AccessTools.Method(
                TorType.Get("TheOtherRoles.Patches.GameStartManagerPatch+GameStartManagerBeginGame"), "Prefix");

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                var update = AccessTools.Method(typeof(CustomOption), nameof(CustomOption.updateSelection));
                var next = AccessTools.Method(typeof(Random), nameof(Random.NextDouble));
                int replacements = 0;
                foreach (var instruction in instructions) {
                    if (instruction.Calls(update)) {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(AuditFixB_Presets), nameof(ApplyRandomPreset));
                        replacements++;
                    } else if (instruction.Calls(next)) {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(AuditFixB_Presets), nameof(NextMapRoll));
                        replacements++;
                    }
                    yield return instruction;
                }
                if (replacements != 2) throw new InvalidOperationException("TOR-003: Karten-Presetpfad hat sich geändert");
            }
        }

        // Audit 16.11. TOR-023: Geschlossenes Menü und leere Tabs vor GetComponent abfangen.
        public static GameOptionsMenu SafeMenu(GameObject tab) {
            if (tab == null) return null;
            var menu = tab.GetComponent<GameOptionsMenu>();
            if (menu == null || menu.Children == null || menu.Children.Count == 0) return null;
            return CustomOption.options.Any(o => o.optionBehaviour == menu.Children[0]) ? menu : null;
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.updateSelection))]
        private static class SelectionPatch {
            public static void Prefix() => EnsurePresets();

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                int replacements = 0;
                foreach (var instruction in instructions) {
                    if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(GameObject)
                        && method.Name == nameof(GameObject.GetComponent) && method.IsGenericMethod
                        && method.GetGenericArguments().SequenceEqual(new[] { typeof(GameOptionsMenu) })) {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(AuditFixB_Presets), nameof(SafeMenu));
                        replacements++;
                    }
                    yield return instruction;
                }
                if (replacements != 1) throw new InvalidOperationException("TOR-023: Menüpfad hat sich geändert");
            }
        }

        // Audit 16.11. TOR-045: Vor dem Speichern und vor dem Laden denselben Schlüssel binden.
        // Vorhandenes GameOptions ist maßgeblich, VanillaOptions wird nur bei leerem Ziel migriert.
        private static void BindCanonicalVanilla() {
            var config = TheOtherRolesPlugin.Instance.Config;
            string section = $"Preset{CustomOption.preset}";
            var canonical = config.Bind(section, "GameOptions", "");
            var legacy = config.Bind(section, "VanillaOptions", "");
            if (string.IsNullOrEmpty(canonical.Value) && !string.IsNullOrEmpty(legacy.Value))
                canonical.Value = legacy.Value;
            CustomOption.vanillaSettings = canonical;
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.saveVanillaOptions))]
        private static class SaveVanillaPatch {
            public static void Prefix() => BindCanonicalVanilla();
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.loadVanillaOptions))]
        private static class LoadVanillaPatch {
            public static void Prefix() => BindCanonicalVanilla();
        }
    }
}
