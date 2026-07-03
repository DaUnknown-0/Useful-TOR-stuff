// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsShare - rounds out TOR's built-in settings copy/paste (the Copy/Paste icons in the TOR
 * settings tabs) into a full export/import for the host.
 *
 * What TOR already does: copyToClipboard() puts "<version>!<base64 custom options>!<base64 vanilla
 * options>" into the clipboard - that string covers EVERY CustomOption (TOR's AND every loaded mod's,
 * they all live in CustomOption.options) plus the Among-Us-native GameOptions. pasteFromClipboard()
 * applies it with per-option fallback: unknown option ids (a mod that isn't loaded / an option that
 * no longer exists) are logged and skipped, options missing from the string keep their current value.
 *
 * What this module adds:
 *   1. FRESHNESS - TOR's export uses the vanillaSettings snapshot, which is only refreshed on preset
 *      switches. A Prefix on copyToClipboard re-snapshots the CURRENT native settings first, so the
 *      exported string always contains what the host sees in the menu right now.
 *   2. FILE EXPORT - a Postfix writes the exported string to BepInEx/config/TOR-SettingsExports/
 *      (latest.txt + a timestamped copy), so setups survive the clipboard and can be shared as files.
 *   3. FILE IMPORT FALLBACK - a Prefix on pasteFromClipboard: when the clipboard is empty/blank, the
 *      last exported file (latest.txt) is loaded into the clipboard first, then TOR's own paste
 *      logic (including all its fallbacks) runs unchanged.
 */

using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UsefulTORStuff {
    public static class SettingsShare {
        private static string ExportDir =>
            Path.Combine(BepInEx.Paths.ConfigPath, "TOR-SettingsExports");
        private static string LatestPath => Path.Combine(ExportDir, "latest.txt");

        private static void PostChat(string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null && PlayerControl.LocalPlayer != null)
                    hud.Chat.AddChat(PlayerControl.LocalPlayer, text);
            } catch { }
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.copyToClipboard))]
        static class CopyPatch {
            // 1. Freshness: snapshot the CURRENT native game options into vanillaSettings before TOR
            // builds the export string (otherwise it exports the last preset-switch snapshot).
            public static void Prefix() {
                try {
                    if (GameManager.Instance != null && GameManager.Instance.LogicOptions != null)
                        CustomOption.saveVanillaOptions();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsShare] vanilla snapshot failed: {e.Message}");
                }
            }

            // 2. File export: persist the clipboard string.
            public static void Postfix() {
                try {
                    string data = GUIUtility.systemCopyBuffer;
                    if (string.IsNullOrWhiteSpace(data)) return;
                    Directory.CreateDirectory(ExportDir);
                    File.WriteAllText(LatestPath, data);
                    string stamped = Path.Combine(ExportDir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                    File.WriteAllText(stamped, data);
                    PostChat($"Settings exported: copied to clipboard AND saved to\n{stamped}");
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SettingsShare] settings exported to {stamped}.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] file export failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.pasteFromClipboard))]
        static class PastePatch {
            // 3. File fallback: an empty clipboard falls back to the last exported file, then TOR's
            // own paste (with its per-option unknown-id fallback) runs unchanged.
            public static void Prefix() {
                try {
                    if (!string.IsNullOrWhiteSpace(GUIUtility.systemCopyBuffer)) return;
                    if (!File.Exists(LatestPath)) return;
                    GUIUtility.systemCopyBuffer = File.ReadAllText(LatestPath);
                    PostChat("Clipboard was empty - importing the last exported settings file instead.");
                    UsefulTORStuffPlugin.Logger?.LogInfo("[SettingsShare] paste fell back to latest.txt.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] file import fallback failed: {e}");
                }
            }
        }
    }
}
