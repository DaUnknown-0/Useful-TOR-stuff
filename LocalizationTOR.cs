// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LocalizationTOR - applies UTSLocalization tables to TOR's data at runtime.
 *
 * TOR has no localization layer of its own; every string is a hardcoded English literal.
 * Instead of patching every display method we MUTATE TOR's string-holding fields in place
 * (the original source stays untouched):
 *
 *  - RoleInfo.<field>.name / .introDescription / .shortDescription: public, non-readonly
 *    fields on ~50 public static singleton instances. Every TOR surface (intro, HUD role
 *    text, meeting, guesser lists, ...) reads these at render time, so one mutation pass
 *    covers them all. Keys: tor.role.<field>.name/.intro/.desc (modifiers: tor.modifier.*;
 *    .intro and .desc fall back to each other because many roles share one text).
 *  - CustomOptionHolder.<field>.name: mutation reaches ALL direct readers (settings tabs,
 *    F1 overlay, settings summary, the GetStringPatch 6000er toast path) - a GetString hook
 *    alone would miss the direct reads (CustomOptions.cs:491/522/743/998/1022/1068).
 *    TOR's ctor prefixes child options with "- " (CustomOptions.cs:61); we preserve that.
 *  - option.heading / string-only option.selections: translated by exact English-text
 *    match against the tor.heading.* / tor.optionvalue.* tables (these have no stable
 *    field mapping). Float selections (object[] of boxed floats) are never touched.
 *
 * Originals are captured once on first Apply, so switching languages (including back to
 * English) always re-derives from the pristine text. Role NAMES are intentionally kept
 * English in all shipped tables (v1 decision: cross-language lobbies stay mutually
 * intelligible); the mechanism translates them all the same if a table provides values.
 *
 * Vanilla coverage for tier-B languages (languages the game does not offer): a postfix on
 * TranslationController.GetString(StringNames, ...) swaps in texts from the
 * vanilla.<code>.json table. NOTE the HarmonyX pitfall: TOR's GetStringPatch prefix
 * returns false (skips the native method) but postfixes still run - which is exactly why
 * this is a postfix and why it must leave the >= 6000 fake IDs alone (those carry the
 * already-translated option names).
 *
 * Language-change detection: TranslationController.SetLanguage postfix (covers boot +
 * vanilla picker). The one-time vanilla string dump for translators also lives here
 * (MainMenuManager.Start, so the TranslationController is fully initialised).
 */

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TheOtherRoles;

namespace UsefulTORStuff {
    public static class LocalizationTOR {
        private class RoleOriginal { public string name, intro, shortDesc; }
        private static readonly Dictionary<string, RoleOriginal> roleOriginals = new();
        private class OptionOriginal { public string name, heading; public object[] selections; }
        private static readonly Dictionary<string, OptionOriginal> optionOriginals = new();
        private static bool dumped;

        public static void Apply() {
            ApplyRoles();
            ApplyOptions();
        }

        // ---------- RoleInfo ----------

        private static void ApplyRoles() {
            foreach (var field in typeof(RoleInfo).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                if (field.FieldType != typeof(RoleInfo)) continue;
                if (field.GetValue(null) is not RoleInfo ri) continue;

                if (!roleOriginals.TryGetValue(field.Name, out var orig)) {
                    orig = new RoleOriginal { name = ri.name, intro = ri.introDescription, shortDesc = ri.shortDescription };
                    roleOriginals[field.Name] = orig;
                }
                string r = "tor.role." + field.Name, m = "tor.modifier." + field.Name;
                ri.name = TrAny(r + ".name", m + ".name") ?? orig.name;
                ri.introDescription = TrAny(r + ".intro", m + ".intro", r + ".desc", m + ".desc") ?? orig.intro;
                ri.shortDescription = TrAny(r + ".desc", m + ".desc", r + ".intro", m + ".intro") ?? orig.shortDesc;
            }
        }

        private static string TrAny(params string[] keys) {
            foreach (var k in keys) {
                var t = UTSLocalization.TrOrNull(k);
                if (t != null) return t;
            }
            return null;
        }

        // ---------- CustomOptions ----------

        private static void ApplyOptions() {
            // text-keyed maps for headings and selection lists (English -> translated)
            var headingMap = BuildTextMap("tor.heading.");
            var valueMap = BuildTextMap("tor.optionvalue.");

            foreach (var field in typeof(CustomOptionHolder).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                if (field.FieldType != typeof(CustomOption)) continue;
                if (field.GetValue(null) is not CustomOption opt) continue;

                if (!optionOriginals.TryGetValue(field.Name, out var orig)) {
                    orig = new OptionOriginal { name = opt.name, heading = opt.heading, selections = opt.selections };
                    optionOriginals[field.Name] = orig;
                }

                bool child = orig.name.StartsWith("- ", StringComparison.Ordinal);
                var tr = UTSLocalization.TrOrNull("tor.option." + field.Name);
                opt.name = tr != null ? (child ? "- " + tr : tr) : orig.name;

                if (!string.IsNullOrEmpty(orig.heading))
                    opt.heading = headingMap.TryGetValue(orig.heading, out var h) ? h : orig.heading;

                opt.selections = TranslateSelections(orig.selections, valueMap);
            }
        }

        private static object[] TranslateSelections(object[] originals, Dictionary<string, string> valueMap) {
            if (originals == null || originals.Length == 0) return originals;
            if (!originals.All(o => o is string)) return originals; // float options: hands off
            var joined = string.Join("|", originals.Cast<string>());
            if (!valueMap.TryGetValue(joined, out var translated)) return originals;
            var parts = translated.Split('|');
            if (parts.Length != originals.Length) return originals; // malformed table entry
            return parts.Cast<object>().ToArray();
        }

        private static Dictionary<string, string> BuildTextMap(string prefix) {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in UTSLocalization.EnglishEntries(prefix))
                map[kv.Value] = UTSLocalization.Tr(kv.Key);
            return map;
        }

        // ---------- patches ----------

        // Fires on boot (initial language load) and on every change in the vanilla picker.
        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.SetLanguage))]
        private static class SetLanguagePatch {
            public static void Postfix() => UTSLocalization.Reapply("vanilla SetLanguage");
        }

        // AUDIT-2026-08-15: IDs that TOR's own ExileControllerMessagePatch postfix fills in
        // dynamically (per-exiled-player role text, Jester-win impostor-count hiding, the
        // tiebreaker suffix). Because of the BepInDependency on TOR, this postfix always runs
        // AFTER that one and would otherwise clobber the composed text with the static
        // vanilla.<code>.json template - losing the role reveal / impostor-count hiding /
        // tiebreaker note whenever a tier-B language is active. Leave these untouched here;
        // ExileControllerMessagePatch already produced the correct (English) text and tier-B
        // players get the same dynamic composition as everyone else.
        private static readonly HashSet<StringNames> DynamicVanillaIds = new() {
            StringNames.ExileTextPN, StringNames.ExileTextSN, StringNames.ExileTextPP, StringNames.ExileTextSP,
            StringNames.ImpostorsRemainP, StringNames.ImpostorsRemainS,
        };

        // Tier-B vanilla text override. Postfix on the same overload TOR's GetStringPatch
        // prefixes (see file header for the prefix/postfix reasoning).
        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.GetString),
            typeof(StringNames), typeof(Il2CppReferenceArray<Il2CppSystem.Object>))]
        private static class GetStringPatch {
            // PERF: this postfix runs on EVERY vanilla GetString call (HUD labels, task list, menus -
            // many per frame). Enum.ToString() on a StringNames value is a reflection-backed name
            // lookup plus a fresh string each time, so the names are resolved once and kept. The
            // tier-B gate (a cached bool) goes first: for every tier-A player the postfix is then
            // a single field read.
            private static readonly Dictionary<StringNames, string> nameCache = new Dictionary<StringNames, string>();

            private static string NameOf(StringNames id) {
                if (!nameCache.TryGetValue(id, out var name)) {
                    name = id.ToString();
                    nameCache[id] = name;
                }
                return name;
            }

            public static void Postfix(StringNames id, Il2CppReferenceArray<Il2CppSystem.Object> parts, ref string __result) {
                if (!UTSLocalization.TierBActive) return;
                if ((int)id >= 6000) return; // TOR's fake IDs: already-translated option names
                if (DynamicVanillaIds.Contains(id)) return; // dynamically composed by TOR, see above
                var t = UTSLocalization.VanillaOrNull(NameOf(id));
                if (t == null) return;
                if (parts != null && parts.Length > 0) {
                    try {
                        var args = new object[parts.Length];
                        for (int i = 0; i < parts.Length; i++) args[i] = parts[i]?.ToString() ?? "";
                        __result = string.Format(t, args);
                        return;
                    } catch (FormatException) { return; } // keep the (English) original
                }
                __result = t;
            }
        }

        // One-time translator source dump: every defined StringNames value in the CURRENT
        // game language. Runs in the main menu so the TranslationController is initialised.
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DumpPatch {
            public static void Postfix() {
                if (dumped || UTSLocalization.DumpVanillaStrings?.Value != true) return;
                dumped = true;
                try { DumpVanilla(); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[Loc] vanilla dump failed: {e.Message}"); }
            }
        }

        private static void DumpVanilla() {
            string lang;
            try { lang = AmongUs.Data.DataManager.Settings.Language.CurrentLanguage.ToString().ToLowerInvariant(); }
            catch { lang = "unknown"; }
            var path = Path.Combine(UTSLocalization.OverrideDir(), $"vanilla_dump_{lang}.json");
            if (File.Exists(path)) return;

            var tc = DestroyableSingleton<TranslationController>.Instance;
            if (tc == null) return;
            var sb = new StringBuilder(1 << 20);
            sb.Append("{\n \"__language\": \"").Append(lang).Append("\"");
            int count = 0;
            foreach (StringNames id in Enum.GetValues(typeof(StringNames))) {
                string text;
                try { text = tc.GetString(id, new Il2CppReferenceArray<Il2CppSystem.Object>(0)); }
                catch { continue; }
                if (string.IsNullOrEmpty(text) || text == "STRMISS") continue;
                sb.Append(",\n \"").Append(UTSLocalization.EscapeJson(id.ToString()))
                  .Append("\": \"").Append(UTSLocalization.EscapeJson(text)).Append('"');
                count++;
            }
            sb.Append("\n}\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[Loc] vanilla dump written: {count} strings -> {path}");
        }
    }
}
