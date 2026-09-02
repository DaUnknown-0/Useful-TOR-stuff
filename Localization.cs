// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UTSLocalization - localization engine for the whole TOR mod family.
 *
 * Scope: translates every user-visible mod string (TOR + UTS; UC/HostFix adopt the same
 * contract) into the 15 vanilla Among Us languages plus 10 extra ("tier B") languages the
 * game itself does not offer (tr, pl, cs, hu, ro, sv, fi, uk, id, vi). For tier B languages
 * the engine additionally overrides VANILLA strings through a TranslationController.GetString
 * postfix, fed by a StringNames->text table generated from a one-time in-game dump (see
 * DumpVanillaStrings below).
 *
 * Language resolution: "auto" follows the vanilla language setting
 * (DataManager.Settings.Language.CurrentLanguage, a SupportedLangs value; enum names double
 * as our tier-A language codes, lowercased). Tier B languages are only reachable through the
 * "ModLanguage" BepInEx config override because the vanilla picker cannot select them.
 * The override is deliberately NOT a CustomOption: CustomOptions are host-synced, language
 * is strictly per client.
 *
 * Tables: flat { "key": "text" } JSON maps embedded as
 * "UsefulTORStuff.Resources.Localization.<code>.json" (en is the reference table / fallback
 * chain: active language -> en -> hardcoded original). Users/communities can override or add
 * single keys via "BepInEx/config/UTSLocalization/<code>.json" without rebuilding the DLL.
 * JSON parsing uses the tiny flat-map parser below - deliberately no System.Text.Json
 * dependency (the BepInEx IL2CPP BCL set varies between installs).
 *
 * Cross-mod contract (same duplicate-the-helper convention as VersionDisplay/ModManagerCore,
 * BCL types only, no compile-time reference):
 *   AppDomain "UTS.Loc.ActiveCode" -> string   current language code, e.g. "german" / "tr"
 *   AppDomain "UTS.Loc.Epoch"      -> int      bumped on every (re)apply; other mods watch it
 *                                              and re-apply their own tables when it moves.
 * UC/HostFix ship their own "<Mod>.Resources.Localization.<code>.json" tables with uc.* /
 * hostfix.* keys and a small copy of this loader - they only need ActiveCode/Epoch from us.
 *
 * Change detection: TranslationController.SetLanguage postfix (fires on every vanilla
 * language switch) + ConfigEntry.SettingChanged for the ModLanguage override. No per-frame
 * polling. TOR-string application lives in LocalizationTOR.cs.
 */

using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace UsefulTORStuff {
    public static class UTSLocalization {
        public const string ActiveCodeKey = "UTS.Loc.ActiveCode";
        public const string EpochKey = "UTS.Loc.Epoch";

        // Tier A: SupportedLangs enum names, lowercased (verified against
        // AmongUs.GameLibs.Steam 2024.10.29: English=0, Latam=1, Brazilian=2, Portuguese=3,
        // Korean=4, Russian=5, Dutch=6, Filipino=7, French=8, German=9, Italian=10,
        // Japanese=11, Spanish=12, SChinese=13, TChinese=14, Irish=15).
        public static readonly string[] TierACodes = {
            "en", "latam", "brazilian", "portuguese", "korean", "russian", "dutch",
            "filipino", "french", "german", "italian", "japanese", "spanish",
            "schinese", "tchinese", "irish"
        };
        // Tier B: languages vanilla Among Us does not offer. Mod strings work out of the box;
        // vanilla strings additionally need a translated dump table (vanilla.<code>.json).
        // RTL scripts (ar/he) and scripts with unclear SDF-font coverage (hi/th) are
        // deliberately excluded until TMP rendering is verified.
        public static readonly string[] TierBCodes = {
            "tr", "pl", "cs", "hu", "ro", "sv", "fi", "uk", "id", "vi"
        };

        public static ConfigEntry<string> ModLanguage;   // "auto" or any code above
        public static ConfigEntry<bool> DumpVanillaStrings;

        private static readonly Dictionary<string, string> english = new();
        private static readonly Dictionary<string, string> active = new();
        // StringNames enum name -> translated vanilla text; only loaded for tier B languages.
        private static readonly Dictionary<string, string> vanillaActive = new();
        // CustomOptions created by UTS itself register here so a language switch can re-title
        // them; TOR's own options are handled via reflection in LocalizationTOR.
        private static readonly List<(WeakReference optRef, string key)> boundOptions = new();
        // (optRef, keys, originalSelections) - originals captured on first apply; keys is
        // either [onePipeListKey] or one key per selection value.
        private static readonly List<(WeakReference optRef, string[] keys, object[] originals)> boundSelections = new();

        public static string ActiveCode { get; private set; } = "en";
        // PERF: cached alongside ActiveCode instead of an Array.IndexOf over string codes per
        // call - the vanilla GetString postfix (LocalizationTOR) asks this on EVERY vanilla text
        // lookup, which is the hottest path this mod sits on.
        public static bool TierBActive { get; private set; }
        public static event Action LanguageApplied;

        private static void BindConfig(ConfigFile config) {
            ModLanguage = config.Bind("Localization", "ModLanguage", "auto",
                "Language for all mod texts (and, for languages Among Us itself does not offer, "
                + "also the vanilla texts). \"auto\" follows the game language. Codes: "
                + string.Join(", ", TierACodes) + " (game languages), "
                + string.Join(", ", TierBCodes) + " (extra languages).");
            DumpVanillaStrings = config.Bind("Localization", "DumpVanillaStrings", true,
                "Write a one-time dump of all vanilla StringNames texts (current game language) "
                + "to BepInEx/config/UTSLocalization/. Source material for translating vanilla "
                + "texts into the extra (non-vanilla) languages.");
        }

        public static void Initialize(ConfigFile config) {
            BindConfig(config);
            LoadTable("en", english);
            ModLanguage.SettingChanged += (_, __) => Reapply("config change");
            Reapply("initial load");
        }

        /// Tables only - no TOR mutation, no option bindings, nothing that touches the game.
        /// For the "this mod is switched off but the Mod Manager still has to work" path: the
        /// manager needs its own labels translated, and that is all it may do.
        public static void InitializeDisplayOnly(ConfigFile config) {
            BindConfig(config);
            LoadTable("en", english);
            var code = ResolveCode();
            LoadTable(code, active);
            ActiveCode = code;
            TierBActive = Array.IndexOf(TierBCodes, code) >= 0;
        }

        // ---------- public lookup API ----------

        /// Translated text for a key; falls back active -> en -> the key itself.
        public static string Tr(string key) {
            if (active.TryGetValue(key, out var t) && t.Length > 0) return t;
            if (english.TryGetValue(key, out var e) && e.Length > 0) return e;
            return key;
        }

        public static string Tr(string key, params object[] args) {
            var t = Tr(key);
            try { return string.Format(t, args); }
            catch (FormatException) { return t; }
        }

        /// Null when neither the active nor the English table knows the key
        /// (callers keep their hardcoded original in that case).
        public static string TrOrNull(string key) {
            if (active.TryGetValue(key, out var t) && t.Length > 0) return t;
            if (english.TryGetValue(key, out var e) && e.Length > 0) return e;
            return null;
        }

        /// Translated vanilla text (tier B only) for a StringNames enum name, else null.
        public static string VanillaOrNull(string stringName) =>
            vanillaActive.TryGetValue(stringName, out var t) && t.Length > 0 ? t : null;

        /// English source text of a key (reverse-mapping helper, e.g. for text-keyed surfaces).
        public static string EnglishOrNull(string key) =>
            english.TryGetValue(key, out var e) ? e : null;

        /// All keys of the English reference table with a given prefix.
        public static IEnumerable<KeyValuePair<string, string>> EnglishEntries(string prefix) {
            foreach (var kv in english)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) yield return kv;
        }

        /// Registers a UTS-created CustomOption title: applied now and on every language
        /// switch. Child options keep TOR's "- " prefix convention automatically.
        public static void BindOptionTitle(object customOption, string key) {
            boundOptions.Add((new WeakReference(customOption), key));
            ApplyBoundOption(customOption, key);
        }

        /// Same for a string-valued selections list. Pass ONE key whose table value is a
        /// "|"-joined list, or one key PER selection value - either way the translated
        /// value count must match the original list length (else the list stays English).
        public static void BindOptionSelections(object customOption, params string[] keys) {
            if (keys == null || keys.Length == 0) return;
            boundSelections.Add((new WeakReference(customOption), keys, null));
            ApplyBoundSelections(boundSelections.Count - 1);
        }

        // ---------- language switching ----------

        public static void Reapply(string reason) {
            var code = ResolveCode();
            LoadTable(code, active);
            vanillaActive.Clear();
            bool tierB = Array.IndexOf(TierBCodes, code) >= 0;
            if (tierB)
                LoadTable("vanilla." + code, vanillaActive);
            ActiveCode = code;
            TierBActive = tierB;

            foreach (var (optRef, key) in boundOptions)
                if (optRef.Target is object opt) ApplyBoundOption(opt, key);
            for (int bi = 0; bi < boundSelections.Count; bi++) ApplyBoundSelections(bi);
            try { LocalizationTOR.Apply(); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[Loc] TOR apply failed: {e.Message}"); }

            try {
                AppDomain.CurrentDomain.SetData(ActiveCodeKey, code);
                int epoch = AppDomain.CurrentDomain.GetData(EpochKey) is int i ? i : 0;
                AppDomain.CurrentDomain.SetData(EpochKey, epoch + 1);
            } catch { }
            try { LanguageApplied?.Invoke(); } catch { }
            UsefulTORStuffPlugin.Logger?.LogInfo($"[Loc] language \"{code}\" applied ({active.Count} keys, {reason})");
        }

        private static string ResolveCode() {
            var cfg = ModLanguage?.Value?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(cfg) && cfg != "auto"
                && (Array.IndexOf(TierACodes, cfg) >= 0 || Array.IndexOf(TierBCodes, cfg) >= 0))
                return cfg;
            try {
                var lang = AmongUs.Data.DataManager.Settings.Language.CurrentLanguage;
                var code = lang.ToString().ToLowerInvariant();
                return code == "english" ? "en" : code;
            } catch {
                return "en"; // DataManager not ready yet; SetLanguage postfix re-applies later
            }
        }

        private static void ApplyBoundSelections(int index) {
            var (optRef, keys, originals) = boundSelections[index];
            if (optRef.Target is not object opt) return;
            try {
                var selField = opt.GetType().GetField("selections",
                    BindingFlags.Public | BindingFlags.Instance);
                if (selField == null) return;
                if (originals == null) {
                    originals = selField.GetValue(opt) as object[];
                    if (originals == null) return;
                    boundSelections[index] = (optRef, keys, originals);
                }
                string[] parts;
                if (keys.Length == 1) {
                    var tr = TrOrNull(keys[0]);
                    if (tr == null) { selField.SetValue(opt, originals); return; }
                    parts = tr.Split('|');
                } else {
                    parts = new string[keys.Length];
                    for (int i = 0; i < keys.Length; i++) {
                        parts[i] = TrOrNull(keys[i]);
                        if (parts[i] == null) { selField.SetValue(opt, originals); return; }
                    }
                }
                if (parts.Length != originals.Length) return;
                var arr = new object[parts.Length];
                for (int i = 0; i < parts.Length; i++) arr[i] = parts[i];
                selField.SetValue(opt, arr);
            } catch { }
        }

        private static void ApplyBoundOption(object customOption, string key) {
            var tr = TrOrNull(key);
            if (tr == null) return;
            try {
                var nameField = customOption.GetType().GetField("name",
                    BindingFlags.Public | BindingFlags.Instance);
                if (nameField?.GetValue(customOption) is string cur) {
                    bool child = cur.StartsWith("- ", StringComparison.Ordinal);
                    nameField.SetValue(customOption, child ? "- " + tr : tr);
                }
            } catch { }
        }

        // ---------- table loading ----------

        private static void LoadTable(string code, Dictionary<string, string> into) {
            into.Clear();
            try {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream(
                    $"UsefulTORStuff.Resources.Localization.{code}.json");
                if (s != null) {
                    using var r = new StreamReader(s, Encoding.UTF8);
                    ParseFlatJson(r.ReadToEnd(), into);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Loc] embedded table {code} failed: {e.Message}");
            }
            // user/community overrides win over embedded texts, key by key
            try {
                var path = Path.Combine(OverrideDir(), code + ".json");
                if (File.Exists(path)) ParseFlatJson(File.ReadAllText(path, Encoding.UTF8), into);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Loc] override table {code} failed: {e.Message}");
            }
        }

        public static string OverrideDir() {
            var dir = Path.Combine(Paths.ConfigPath, "UTSLocalization");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------- minimal flat-map JSON ----------
        // Accepts exactly one top-level object whose values are strings; anything else is
        // skipped defensively. Kept dependency-free on purpose (see file header).

        public static void ParseFlatJson(string json, Dictionary<string, string> into) {
            int i = 0, n = json.Length;
            void SkipWs() { while (i < n && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++; }
            string ParseString() {
                // json[i] == '"'
                var sb = new StringBuilder();
                i++;
                while (i < n) {
                    char c = json[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= n) break;
                    char e = json[i++];
                    switch (e) {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= n && ushort.TryParse(json.Substring(i, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out var cp)) {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                    }
                }
                return sb.ToString();
            }
            if (n > 0 && json[0] == '﻿') i = 1; // BOM
            SkipWs();
            if (i >= n || json[i] != '{') return;
            i++;
            while (true) {
                SkipWs();
                if (i >= n) return;
                if (json[i] == '}') return;
                if (json[i] == ',') { i++; continue; }
                if (json[i] != '"') return; // malformed - bail out with what we have
                var key = ParseString();
                SkipWs();
                if (i >= n || json[i] != ':') return;
                i++;
                SkipWs();
                if (i < n && json[i] == '"') {
                    into[key] = ParseString();
                } else {
                    // non-string value: skip one token defensively (until , or } at depth 0)
                    int depth = 0;
                    while (i < n) {
                        char c = json[i];
                        if (c == '{' || c == '[') depth++;
                        else if (c == '}' || c == ']') { if (depth == 0) break; depth--; }
                        else if (c == ',' && depth == 0) break;
                        i++;
                    }
                }
            }
        }

        public static string EscapeJson(string s) {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
