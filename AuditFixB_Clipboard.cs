using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-024/025: Vollständige Validierung vor jeder Zustandsänderung.
    internal static class AuditFixB_Clipboard {
        private static bool CanImport => AmongUsClient.Instance == null || AmongUsClient.Instance.AmHost;

        private static List<(CustomOption Option, int Selection)> Validate(byte[] bytes) {
            var known = CustomOption.options.Where(o => o != null).ToDictionary(o => o.id);
            var result = new List<(CustomOption, int)>();
            foreach (var row in AuditFixB_OptionCodec.Decode(bytes)) {
                if (!known.TryGetValue(row.Id, out var option)) continue;
                if (row.Selection >= (option.selections?.Length ?? 0))
                    throw new InvalidDataException($"Auswahl außerhalb des Bereichs: Option {row.Id}");
                result.Add((option, row.Selection));
            }
            return result;
        }

        private static void Refresh(CustomOption option) {
            if (option.optionBehaviour is not StringOption widget || widget == null) return;
            widget.oldValue = widget.Value = option.selection;
            if (widget.ValueText != null) widget.ValueText.text = option.selections[option.selection].ToString();
        }

        private static void Apply(List<(CustomOption Option, int Selection)> rows) {
            var config = TheOtherRolesPlugin.Instance.Config;
            // Bindings vor der Übernahme vorbereiten. Ein unbekannter Eintrag erzeugt keine Config.
            var staged = rows.Select(row => (row.Option, row.Selection,
                Entry: config.Bind($"Preset{CustomOption.preset}", row.Option.id.ToString(), row.Option.defaultSelection))).ToList();
            var previous = staged.Select(row => (row.Option, row.Option.selection, row.Option.entry,
                Entry: row.Entry, Value: row.Entry.Value)).ToList();
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try {
                foreach (var row in staged) {
                    row.Option.entry = row.Entry;
                    row.Option.selection = row.Selection;
                    row.Entry.Value = row.Selection;
                }
                if (save) config.Save();
            } catch {
                foreach (var old in previous) {
                    old.Option.entry = old.entry;
                    old.Option.selection = old.selection;
                    old.Entry.Value = old.Value;
                }
                throw;
            } finally { config.SaveOnConfigSet = save; }
            foreach (var row in rows) {
                try { row.Option.onChange?.Invoke(); Refresh(row.Option); }
                catch (Exception ex) { Log(ex); }
            }
        }

        private static byte[] Serialize() => AuditFixB_OptionCodec.Encode(CustomOption.options
            .Where(o => o != null && o.id != 0).Select(o => (o.id, o.selection)));

        private static void Log(Exception ex) => UsefulTORStuffPlugin.Logger?.LogWarning($"[Clipboard] {ex.Message}");

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.serializeOptions))]
        private static class SerializePatch {
            public static bool Prefix(ref byte[] __result) { __result = Serialize(); return false; }
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.deserializeOptions))]
        private static class DeserializePatch {
            public static bool Prefix(byte[] inputValues, ref int __result) {
                __result = 0;
                if (!CanImport) return false;
                try { Apply(Validate(inputValues)); __result = 2; }
                catch (Exception ex) { Log(ex); }
                return false;
            }
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.copyToClipboard))]
        private static class CopyPatch {
            public static bool Prefix() {
                try {
                    // Der aktuelle Vanilla-Zustand gehört ins Clipboard, kein veralteter Blob.
                    string vanilla = Convert.ToBase64String(GameOptionsManager.Instance.gameOptionsFactory
                        .ToBytes(GameManager.Instance.LogicOptions.currentGameOptions, false));
                    var bytes = Serialize();
                    // Der Marker außerhalb Base64 lässt alte TOR-Importer sauber vor Anwendung
                    // abbrechen. Kleine Werte bleiben vollständig mit reinem TOR kompatibel.
                    string marker = AuditFixB_OptionCodec.Extended(bytes) ? "UTS2:" : "";
                    GUIUtility.systemCopyBuffer = $"{TheOtherRolesPlugin.VersionString}!{marker}{Convert.ToBase64String(bytes)}!{vanilla}";
                } catch (Exception ex) { Log(ex); }
                return false;
            }
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.pasteFromClipboard))]
        private static class PastePatch {
            public static bool Prefix(ref int __result) {
                __result = 0;
                if (!CanImport) return false;
                try {
                    string text = GUIUtility.systemCopyBuffer ?? "";
                    if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Clipboard zu groß");
                    var parts = text.Split('!');
                    if (parts.Length != 3 || !Version.TryParse(parts[0], out var version))
                        throw new InvalidDataException("Ungültiges Clipboard-Format");
                    string encoded = parts[1];
                    bool marked = encoded.StartsWith("UTS2:", StringComparison.Ordinal);
                    if (marked) encoded = encoded.Substring(5);
                    byte[] bytes = Convert.FromBase64String(encoded);
                    if (marked && !AuditFixB_OptionCodec.Extended(bytes))
                        throw new InvalidDataException("Ungültige Formatkennung");
                    var rows = Validate(bytes);
                    IGameOptions vanilla = null;
                    bool compatibleVanilla = version >= new Version(4, 6, 0) && parts[2].Length > 0;
                    if (compatibleVanilla) {
                        vanilla = GameOptionsManager.Instance.gameOptionsFactory.FromBytes(Convert.FromBase64String(parts[2]));
                        if (vanilla == null || vanilla.Version < 8)
                            throw new InvalidDataException("Ungültige Vanilla-Einstellungen");
                    }
                    // Auch ein defekter Vanilla-Blob wird vor dem Optionsimport abgewiesen.
                    Apply(rows);
                    if (vanilla != null) {
                        var manager = GameOptionsManager.Instance;
                        manager.GameHostOptions = vanilla;
                        manager.CurrentGameOptions = vanilla;
                        GameManager.Instance.LogicOptions.SetGameOptions(vanilla);
                        CustomOption.saveVanillaOptions();
                        GameManager.Instance.LogicOptions.SyncOptions();
                    }
                    CustomOption.ShareOptionSelections();
                    __result = 2 + (vanilla != null ? 1 : 0);
                } catch (Exception ex) { Log(ex); }
                return false;
            }
        }
    }
}
