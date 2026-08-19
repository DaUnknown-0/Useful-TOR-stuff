// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsOverlayView - re-renders the settings text behind F1 (and the lobby settings list) so
 * every role and modifier appears in ITS OWN colour and the pages stay readable.
 *
 * WHAT WAS WRONG
 * The text comes from GameOptionsDataPatch.buildAllOptions (TOR, Modules/CustomOptions.cs:1077),
 * which delegates to buildOptionsOfType. Four separate reasons made almost everything white:
 *   1. Sub-options are hard-coded white: `Color c = isIrrelevant ? Color.grey : Color.white;`
 *      (CustomOptions.cs:1020). Every child line of every role goes through that one colour.
 *   2. Only TOR colours its own headers, by baking a <color> tag into the option NAME
 *      (`cs(Sheriff.color, "Sheriff")`, CustomOptionHolder.cs:595). Unknown's Collection,
 *      ChanceMod and this mod pass plain strings, so all of their roles render white.
 *   3. Every TOR impostor role literally shares one colour (Palette.ImpostorRed - verified for
 *      Morphling, Camouflager, Vampire, Eraser, Trickster, Cleaner, Warlock, Ninja, ...), so on the
 *      impostor page "role colour" alone cannot separate one block from the next.
 *   4. A role switched off still costs a full line, so half of a page can be 0% entries.
 *
 * WHAT THIS FILE DOES (the "variant B" layout)
 *   - Role/modifier headers in their real role colour, sub-options in a dimmed mix of that same
 *     colour, so a block reads as one unit without shouting.
 *   - Values in their own column (TMP <pos>), numbers neutral, On green, Off/0% dimmed. Off is
 *     deliberately NOT red: red is the impostor colour and would collide.
 *   - The redundant role-name prefix is stripped from children ("Tesla Charge Countdown (sec)"
 *     becomes "Charge Countdown (sec)").
 *   - Blocks are sorted by spawn chance, and roles at 0% collapse into one "Off: ..." line.
 *   - Roles from a sibling mod carry a short tag ([UC], [FF], [Chance], [NF]). The owner is read
 *     from the assembly that holds the option in a static field, NOT from the option-ID ranges the
 *     mods reserve: TOR itself creates ids inside those ranges (1100-1102 Shifter/Armored, 2001-2013
 *     for the guesser gamemode), and a range check labelled TOR's own settings as somebody else's.
 *   - Impostor roles that all share Palette.ImpostorRed alternate between two red tones for DISPLAY
 *     only. Role.color itself is never touched, and any role with its own colour keeps it. The tone
 *     follows display order, so it marks "next block", not identity.
 *
 * WHERE THE COLOURS COME FROM
 *   1. A snapshot of the <color> tags TOR bakes into its own option names, taken by option ID at
 *      load time. The snapshot is not a cache for speed, it is the only reliable reading: this
 *      mod's own localization replaces those names on every language switch, and the translated
 *      strings carry no markup ("tor.option.sheriffSpawnRate": "Sheriff"), so on any language but
 *      English the tag is simply gone by the time the overlay is drawn. The live name is still
 *      read first, so a role added after the snapshot is not lost.
 *   2. The AppDomain contract "UTS.OptionColors" (Dictionary<string,string>): "id:<optionId>" ->
 *      "RRGGBB", with the plain role name as a second key. Sibling mods fill it at load;
 *      Unknown's Collection registers its roles there (UCOptionColors.cs), so nothing has to be
 *      duplicated in this mod. The ID key is the one that survives translation.
 *   3. Otherwise the faction colour for that option type, which is still better than white.
 *
 * WHY A REBUILD AND NOT A STRING PATCH
 * The finished string has lost the parent/child relation, the option type and the numeric value.
 * Repairing it by pattern matching would guess exactly the facts this file needs, so the postfix
 * discards it and walks CustomOption.options itself, mirroring TOR's own visibility rules
 * (CustomOptions.cs:1002 and 1018) rather than inventing new ones.
 *
 * SAFETY
 *   - Page 1 (vanilla settings) is never touched, and neither are Hide N Seek / Prop Hunt, whose
 *     pages TOR builds from other option types.
 *   - Every path is wrapped: on ANY exception the original TOR string is left in place, and the
 *     failure is logged once instead of every frame.
 *   - Blocks stay separated by a blank line because TOR's HudManagerUpdate.Prefix2 splits the text
 *     on "\n\n" to fill up to four columns (CustomOptions.cs:1360). This layout produces FEWER
 *     lines than TOR's own, so it can only move away from that method's 4-column limit, never into it.
 *   - No option is gated through UTSGate: this changes nothing but the local text on the local
 *     screen, exactly like TorPerfFixes, so there is nothing a host could have to agree to.
 *   - Nothing here reads a Unity object from a per-frame path. "Is the overlay open" is answered by
 *     a timestamp this file sets itself, and the two lookup tables are built once on lobby join -
 *     both deliberately, because comparing an Il2Cpp object against null while the game is tearing
 *     down a scene, or running a foreign type's initializer at that moment, crashes the process
 *     rather than throwing something catchable.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class SettingsOverlayView {

        // AppDomain contract - sibling mods write "plain role name" -> "RRGGBB" (or "RRGGBBAA").
        // Public so another mod can be pointed at the literal string without guessing it.
        public const string AppKeyOptionColors = "UTS.OptionColors";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowModTags;
        internal static ConfigEntry<bool> ImpostorShades;
        internal static ConfigEntry<bool> AlignValues;
        internal static ConfigEntry<string> ZeroChanceRoles; // collapse | list | hide

        // ---- palette -------------------------------------------------------------------------
        private static readonly Color ValueNumber = new Color(0.86f, 0.89f, 0.92f);
        private static readonly Color ValueOn     = new Color(0.43f, 0.85f, 0.54f);
        private static readonly Color Dim         = new Color(0.53f, 0.55f, 0.58f);
        private static readonly Color Category    = new Color(204f / 255f, 204f / 255f, 0f); // TOR's own
        private static readonly Color NeutralInk  = new Color(0.85f, 0.87f, 0.90f);

        // Shades handed to roles that all carry Palette.ImpostorRed. Two tones, both unmistakably
        // red: adjacent blocks alternate between a bright and a deeper red. An earlier version
        // cycled through four tones including an orange one, which read as a different faction
        // rather than as "next block" - the hue has to stay impostor red, only the brightness moves.
        private static readonly Color[] ImpostorFamily = {
            new Color(1.00f, 0.28f, 0.26f), // bright red
            new Color(0.76f, 0.16f, 0.18f), // deep red
        };

        private const int MaxCollapsedLineChars = 42; // manual wrap: the TMPs have word wrap off
        private const int ValueColumnCap = 26;        // characters, see EmitBlocks

        // ---- config ---------------------------------------------------------------------------
        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("SettingsOverlay", "Enabled", true,
                "Re-render the F1 settings overlay and the lobby settings list: roles and modifiers "
                + "in their own colour, values in a column, roles at 0% collapsed into one line. "
                + "Off restores The Other Roles' original text.");
            ShowModTags = config.Bind("SettingsOverlay", "ShowModTags", true,
                "Mark roles that come from a sibling mod with a short tag ([UC], [FF], [Chance]).");
            ImpostorShades = config.Bind("SettingsOverlay", "ImpostorShades", true,
                "Give impostor roles alternating shades of red. They all share one colour in code, "
                + "so without this the whole impostor page is a single tone. Display only.");
            AlignValues = config.Bind("SettingsOverlay", "AlignValues", true,
                "Right-align the values in their own column. Turn this off if the values overlap "
                + "the names on your resolution or font.");
            ZeroChanceRoles = config.Bind("SettingsOverlay", "ZeroChanceRoles", "collapse",
                "What to do with roles at 0%: 'collapse' lists them in one dimmed line, 'list' keeps "
                + "one line each (dimmed), 'hide' drops them entirely.");
        }

        // ---- patch ----------------------------------------------------------------------------
        // GameOptionsDataPatch has no access modifier and is therefore internal to TOR's assembly
        // (CustomOptions.cs:960), so it cannot be named at compile time - resolved by reflection,
        // like TorPerfFixes' ShowHost wrap.
        public static void TryPatch(Harmony harmony) {
            try {
                Type type = typeof(CustomOption).Assembly.GetType("TheOtherRoles.GameOptionsDataPatch");
                MethodInfo build = type?.GetMethod("buildAllOptions", BindingFlags.Public | BindingFlags.Static);
                if (build == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[SettingsOverlayView] GameOptionsDataPatch.buildAllOptions not found - overlay left as TOR built it.");
                    return;
                }
                harmony.Patch(build, postfix: new HarmonyMethod(typeof(SettingsOverlayView), nameof(Postfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[SettingsOverlayView] settings overlay renderer installed.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsOverlayView] install failed: {e}");
            }
        }

        // ---- "is the F1 overlay open" ------------------------------------------------------------
        // A heartbeat, deliberately NOT a reflection read of TOR's `settingsTMPs` array. That array
        // holds Il2Cpp objects, and comparing one against null runs native Unity code on a handle
        // that may already have been freed - which is a crash, not an exception, and try/catch does
        // not save you from it. This is called from three per-frame Update() methods, including
        // during the lobby-to-game scene change, so it must not touch a game object at all.
        //
        // Instead: TOR's own HudManagerUpdate.Prefix2 rebuilds the overlay text through
        // buildAllOptions(hideExtras: true) for as long as the overlay is open, and nothing else
        // passes that flag. Our postfix stamps the time of each such call; if one arrived recently,
        // the overlay is up. TorPerfFixes throttles those rebuilds to one per 0.25s, so the window
        // below is comfortably longer. Worst case after closing F1: the HUD stays hidden for another
        // half second, which nobody can see and nothing depends on.
        private const float OverlayHeartbeatWindow = 0.6f;
        private static float lastOverlayBuild = float.NegativeInfinity;

        public static bool OverlayOpen() {
            return Time.realtimeSinceStartup - lastOverlayBuild < OverlayHeartbeatWindow;
        }

        private static bool loggedFailure;

        // hideExtras separates the two callers: TOR's own HudManagerUpdate.Prefix2 passes true for the
        // full-screen F1 overlay, while the lobby list arrives through the ToHudString postfix with
        // the default false. Only the overlay is wide enough for a value column.
        public static void Postfix(ref string __result, bool hideExtras) {
            try {
                // Heartbeat first, and outside the Enabled gate: only TOR's overlay prefix passes
                // hideExtras, so this is the one reliable "the overlay is on screen" signal, and the
                // HUD elements that read it must keep working even with the renderer switched off.
                if (hideExtras) lastOverlayBuild = Time.realtimeSinceStartup;

                if (Enabled == null || !Enabled.Value) return;

                // Hide N Seek and Prop Hunt build their pages from other option types entirely; TOR's
                // own text stays untouched there rather than being half-rendered by this file.
                CustomGamemodes mode = TorGameMode();
                if (mode != CustomGamemodes.Classic && mode != CustomGamemodes.Guesser) return;

                int page = TheOtherRolesPlugin.optionsPage;
                if (page <= 0 || page > 6) return; // page 0 is the vanilla settings block

                string built = BuildPage(page, mode, hideExtras);
                if (!string.IsNullOrEmpty(built)) __result = built;
            } catch (Exception e) {
                if (!loggedFailure) {
                    loggedFailure = true;
                    UsefulTORStuffPlugin.Logger?.LogError(
                        $"[SettingsOverlayView] render failed, falling back to TOR's own text: {e}");
                }
            }
        }

        // ==========================================================================================
        // Page building
        // ==========================================================================================

        private static string BuildPage(int page, CustomGamemodes mode, bool wideLayout) {
            List<List<Line>> blocks;
            string title;
            Color titleColor;

            switch (page) {
                case 1: title = "TOR SETTINGS";            titleColor = Category;                     blocks = BuildGeneral(mode);        break;
                case 2: title = "ROLE AND MODIFIER RATES"; titleColor = Category;                     blocks = BuildRates();              break;
                case 3: title = "IMPOSTOR ROLES";          titleColor = FactionColor(Types.Impostor); blocks = BuildType(Types.Impostor); break;
                case 4: title = "NEUTRAL ROLES";           titleColor = FactionColor(Types.Neutral);  blocks = BuildType(Types.Neutral);  break;
                case 5: title = "CREWMATE ROLES";          titleColor = FactionColor(Types.Crewmate); blocks = BuildType(Types.Crewmate); break;
                case 6: title = "MODIFIERS";               titleColor = FactionColor(Types.Modifier); blocks = BuildType(Types.Modifier); break;
                default: return null;
            }

            // Headline and footer are blocks like any other, so the column simulation in EmitBlocks
            // sees exactly the text TOR's Prefix2 will later split.
            blocks.Insert(0, new List<Line> {
                MakeLine(0, title, titleColor, Cs(Dim, $"  {page + 1}/7"), null, Dim, page > 9 ? 6 : 5)
            });
            // TOR's own footer is the only hint that TAB/number keys page through this, so it stays -
            // just dimmed, because it is not information about the lobby.
            blocks.Add(new List<Line> {
                MakeLine(0, $"TAB or page number for more... ({page + 1}/7)", Dim, "", null, Dim)
            });

            var sb = new StringBuilder();
            EmitBlocks(sb, blocks, wideLayout);
            return sb.ToString();
        }

        // ---- page 2: general settings ------------------------------------------------------------
        // Mirrors TOR's own special cases for the min/max role counts (CustomOptions.cs:1024-1064):
        // those eight options are shown as four ranges, not eight separate numbers.
        private static List<List<Line>> BuildGeneral(CustomGamemodes mode) {
            var pool = CustomOption.options.Where(o => o.type == Types.General).ToList();
            if (mode == CustomGamemodes.Guesser) {
                pool.AddRange(CustomOption.options.Where(o => o.type == Types.Guesser));
                var remove = new HashSet<int> { 308, 310, 311, 312, 313, 314, 315, 316, 317, 318 };
                pool = pool.Where(o => !remove.Contains(o.id)).ToList();
            } else {
                pool = pool.Where(o => o != CustomOptionHolder.crewmateRolesFill).ToList();
            }

            var blocks = new List<List<Line>>();
            foreach (var option in pool) {
                if (option.parent != null) continue;
                if (IsCountMax(option)) continue; // folded into the Min entry below

                var block = new List<Line>();
                if (option == CustomOptionHolder.crewmateRolesCountMin) {
                    block.Add(MakeLine(0, "Crewmate Roles", Category, "", CrewRoleCountValue(), ValueNumber));
                } else if (option == CustomOptionHolder.neutralRolesCountMin) {
                    block.Add(MakeLine(0, "Neutral Roles", Category, "",
                        RangeValue(CustomOptionHolder.neutralRolesCountMin, CustomOptionHolder.neutralRolesCountMax, -1), ValueNumber));
                } else if (option == CustomOptionHolder.impostorRolesCountMin) {
                    block.Add(MakeLine(0, "Impostor Roles", Category, "",
                        RangeValue(CustomOptionHolder.impostorRolesCountMin, CustomOptionHolder.impostorRolesCountMax, ImpostorCap()), ValueNumber));
                } else if (option == CustomOptionHolder.modifiersCountMin) {
                    block.Add(MakeLine(0, "Modifiers", Category, "",
                        RangeValue(CustomOptionHolder.modifiersCountMin, CustomOptionHolder.modifiersCountMax, -1), ValueNumber));
                } else {
                    string name = CleanName(option);
                    Color c = ColorOf(option, NeutralInk);
                    block.Add(MakeLine(0, name, c, ModTag(option), ValueOf(option), ValueColor(option)));
                    AppendChildren(block, option, pool, Mix(c, 0.45f), name, 1);
                }
                blocks.Add(block);
            }
            return blocks;
        }

        // ---- page 3: spawn rates -----------------------------------------------------------------
        // TOR's headerOnly pass: role headers only, plus the three sub-roles that carry their own
        // rate (Deputy 103, Sidekick 224, Prosecutor 358 - CustomOptions.cs:1003-1008).
        private static List<List<Line>> BuildRates() {
            var blocks = new List<List<Line>>();
            AddRateSection(blocks, Types.Impostor, "IMPOSTOR",
                RangeValue(CustomOptionHolder.impostorRolesCountMin, CustomOptionHolder.impostorRolesCountMax, ImpostorCap()));
            AddRateSection(blocks, Types.Neutral, "NEUTRAL",
                RangeValue(CustomOptionHolder.neutralRolesCountMin, CustomOptionHolder.neutralRolesCountMax, -1));
            AddRateSection(blocks, Types.Crewmate, "CREWMATE", CrewRoleCountValue());
            AddRateSection(blocks, Types.Modifier, "MODIFIER",
                RangeValue(CustomOptionHolder.modifiersCountMin, CustomOptionHolder.modifiersCountMax, -1));
            return blocks;
        }

        private static void AddRateSection(List<List<Line>> blocks, Types type, string title, string countValue) {
            var roots = CustomOption.options.Where(o => o.type == type && o.parent == null).ToList();
            if (roots.Count == 0) return;

            var active = new List<CustomOption>();
            var off = new List<CustomOption>();
            foreach (var option in roots) (IsOff(option) ? off : active).Add(option);
            SortForDisplay(active);

            var lines = new List<Line> { MakeLine(0, title, Category, "", countValue, Dim) };

            int shade = 0;
            foreach (var option in active) {
                Color c = DisplayColor(option, ref shade);
                string extra = ModifierExtras(option);
                string suffix = ModTag(option) + (extra.Length > 0 ? Cs(Dim, extra) : "");
                int suffixPlain = ModTagPlain(option).Length + extra.Length;
                lines.Add(MakeLine(1, CleanName(option), c, suffix, ValueOf(option), ValueColor(option), suffixPlain));

                foreach (var sub in SubRolesWithOwnRate(option))
                    lines.Add(MakeLine(2, CleanName(sub), Mix(c, 0.45f), "", ValueOf(sub), ValueColor(sub)));
            }

            AppendOffLines(lines, off, 1);
            blocks.Add(lines);
        }

        // ---- pages 4-7: one option type in full ---------------------------------------------------
        private static List<List<Line>> BuildType(Types type) {
            var pool = CustomOption.options.Where(o => o.type == type).ToList();
            var roots = pool.Where(o => o.parent == null).ToList();

            var active = new List<CustomOption>();
            var off = new List<CustomOption>();
            foreach (var option in roots) (IsOff(option) ? off : active).Add(option);
            SortForDisplay(active);

            var blocks = new List<List<Line>>();
            int shade = 0;
            foreach (var option in active) {
                Color c = DisplayColor(option, ref shade);
                string rootName = CleanName(option);
                var block = new List<Line> {
                    MakeLine(0, rootName, c, ModTag(option), ValueOf(option), ValueColor(option), ModTagPlain(option).Length)
                };
                AppendChildren(block, option, pool, Mix(c, 0.45f), rootName, 1);
                blocks.Add(block);
            }

            var tail = new List<Line>();
            AppendOffLines(tail, off, 0);
            if (tail.Count > 0) blocks.Add(tail);

            return blocks;
        }

        // Children (and their children) of one root, in TOR's own registration order, with TOR's own
        // visibility rule: a child is hidden while its parent sits at selection 0, unless the child
        // inverts that relation (CustomOptions.cs:1002/1018 - an inverted child stays visible either
        // way there, and this mirrors that rather than "improving" it).
        private static void AppendChildren(List<Line> block, CustomOption root, List<CustomOption> pool,
                                           Color childColor, string rootName, int indent) {
            foreach (var option in pool) {
                if (option.parent != root) continue;
                if (!IsVisible(option)) continue;
                block.Add(MakeLine(indent, StripRolePrefix(CleanName(option), rootName), childColor, "",
                                   ValueOf(option), ValueColor(option)));
                AppendChildren(block, option, pool, childColor, rootName, indent + 1);
            }
        }

        private static void AppendOffLines(List<Line> lines, List<CustomOption> off, int indent) {
            if (off.Count == 0) return;
            string mode = ZeroChanceRoles?.Value ?? "collapse";
            if (mode == "hide") return;

            if (mode == "list") {
                foreach (var option in off)
                    lines.Add(MakeLine(indent, CleanName(option), Dim, ModTag(option), ValueOf(option), Dim,
                                       ModTagPlain(option).Length));
                return;
            }

            // collapse: one dimmed entry, wrapped by hand because the TMPs have word wrap off.
            var names = off.Select(o => CleanName(o) + ModTagPlain(o)).ToList();
            var current = new StringBuilder("Off: ");
            var wrapped = new List<string>();
            for (int i = 0; i < names.Count; i++) {
                string piece = names[i] + (i < names.Count - 1 ? "," : "");
                if (current.Length > 5 && current.Length + 1 + piece.Length > MaxCollapsedLineChars) {
                    wrapped.Add(current.ToString());
                    current = new StringBuilder("     ");
                }
                if (current.Length > 5) current.Append(' ');
                current.Append(piece);
            }
            wrapped.Add(current.ToString());
            foreach (var text in wrapped)
                lines.Add(MakeLine(indent, text, Dim, "", null, Dim));
        }

        // ==========================================================================================
        // Line model and emission
        // ==========================================================================================

        // Name and value arrive fully colour-tagged; PlainLength is what the value column is measured
        // against, so it counts characters the player actually sees, never markup.
        private struct Line {
            public int Indent;
            public string Name;
            public string Value; // null = this line has no value column
            public int PlainLength;
        }

        private static Line MakeLine(int indent, string plainName, Color nameColor, string coloredSuffix,
                                     string value, Color valueColor, int suffixPlainLength = 0) {
            return new Line {
                Indent = indent,
                Name = Cs(nameColor, plainName) + coloredSuffix,
                Value = value == null ? null : Cs(valueColor, value),
                PlainLength = indent * 2 + plainName.Length + suffixPlainLength,
            };
        }

        // The value column is measured PER on-screen column, not per page. TOR's Prefix2 spreads the
        // blocks over up to four TextMeshPros (CustomOptions.cs:1360-1384), and one 40-character
        // option in the first of them must not push the column position of the last one off the right
        // edge of the screen. The split is therefore simulated here with Prefix2's own arithmetic,
        // and once four columns are in play the alignment is dropped entirely: at that point TOR's
        // own layout is already at the edge of the screen and every saved character counts.
        private static void EmitBlocks(StringBuilder sb, List<List<Line>> blocks, bool wideLayout) {
            // The lobby list is a narrow, scrolling column; a value column measured for the
            // full-screen overlay would push the numbers out of its visible area, so it keeps the
            // values inline.
            bool align = wideLayout && (AlignValues == null || AlignValues.Value);

            var columnOfBlock = SimulateColumnSplit(blocks, out int columnCount);
            if (columnCount >= 4) align = false;

            // Per column: the longest visible name, capped so a runaway option keeps its value inline
            // instead of pushing the column off the panel. <pos> only moves text to an absolute
            // offset, so a column placed before the name ends would overlap it.
            var posTagOfColumn = new string[columnCount];
            var capOfColumn = new int[columnCount];
            if (align) {
                var longest = new int[columnCount];
                for (int i = 0; i < blocks.Count; i++)
                    foreach (var line in blocks[i])
                        if (line.Value != null && line.PlainLength > longest[columnOfBlock[i]])
                            longest[columnOfBlock[i]] = line.PlainLength;
                for (int c = 0; c < columnCount; c++) {
                    capOfColumn[c] = Math.Min(longest[c], ValueColumnCap);
                    posTagOfColumn[c] =
                        $"<pos={(capOfColumn[c] * 0.55f + 0.8f).ToString("0.##", CultureInfo.InvariantCulture)}em>";
                }
            }

            for (int i = 0; i < blocks.Count; i++) {
                int col = columnOfBlock[i];
                foreach (var line in blocks[i]) {
                    if (line.Indent > 0) sb.Append(' ', line.Indent * 2);
                    sb.Append(line.Name);
                    if (line.Value != null) {
                        if (align && line.PlainLength <= capOfColumn[col]) sb.Append(posTagOfColumn[col]);
                        else sb.Append(Cs(Dim, ":")).Append(' ');
                        sb.Append(line.Value);
                    }
                    sb.Append('\n');
                }
                sb.Append('\n'); // blank line: block separator AND the split point Prefix2 needs
            }
        }

        // Mirrors HudManagerUpdate.Prefix2 exactly, including that it measures with Helpers.lineCount
        // (a '\n' count, so a block of n lines counts as n-1) and that a block starting a new column
        // is prefixed with newlines.
        private static int[] SimulateColumnSplit(List<List<Line>> blocks, out int columnCount) {
            var column = new int[blocks.Count];
            int current = 0;      // lineCount of the column's text so far
            int col = 0;
            for (int i = 0; i < blocks.Count; i++) {
                int blockLines = Math.Max(0, blocks[i].Count - 1);
                if (blockLines + current < 43) {
                    current += blockLines + 2;
                } else {
                    col++;
                    current = blockLines + 4;
                }
                column[i] = col;
            }
            columnCount = col + 1;
            return column;
        }

        // ==========================================================================================
        // Option helpers
        // ==========================================================================================

        private static bool IsVisible(CustomOption option) {
            for (var child = option; child?.parent != null; child = child.parent)
                if (child.parent.getSelection() == 0 && !child.invertedParent) return false;
            return true;
        }

        // A spawn rate is recognised by its VALUES, not by comparing the array against
        // CustomOptionHolder.rates: the localization layer replaces `selections` with a fresh array
        // whenever a translation exists for that value list, so a reference check silently stops
        // matching (which is why the first build listed every 0% role separately instead of
        // collapsing it). Reading the strings works no matter who rebuilt the array.
        private static bool IsRate(CustomOption option) {
            if (option?.selections == null || option.selections.Length < 2) return false;
            foreach (var value in option.selections) {
                int ignored;
                if (!(value is string text) || !TryPercent(text, out ignored)) return false;
            }
            return true;
        }

        private static bool TryPercent(string value, out int percent) {
            percent = 0;
            if (string.IsNullOrEmpty(value) || value[value.Length - 1] != '%') return false;
            return int.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out percent);
        }

        private static int RatePercent(CustomOption option) {
            int percent;
            return TryPercent(ValueOf(option), out percent) ? percent : -1;
        }

        private static bool IsOff(CustomOption option) => IsRate(option) && RatePercent(option) == 0;

        // Roles by descending chance; anything that is not a spawn rate at all (a global setting that
        // happens to live on this page) stays on top, where a setting belongs. Ties keep TOR's own
        // registration order, so the page never reshuffles itself between two equal percentages.
        private static void SortForDisplay(List<CustomOption> options) {
            var sorted = options
                .Select((o, i) => new { o, i })
                .OrderByDescending(t => IsRate(t.o) ? RatePercent(t.o) : int.MaxValue)
                .ThenBy(t => t.i)
                .Select(t => t.o)
                .ToList();
            options.Clear();
            options.AddRange(sorted);
        }

        private static IEnumerable<CustomOption> SubRolesWithOwnRate(CustomOption root) {
            foreach (var option in CustomOption.options) {
                if (option.parent != root) continue;
                if (option.id != 103 && option.id != 224 && option.id != 358) continue;
                if (!IsVisible(option)) continue;
                yield return option;
            }
        }

        private static string ValueOf(CustomOption option) {
            object value = option.selections[option.selection];
            // TOR's float options accumulate step error, so the raw ToString() can read "1,6000003"
            // (with a comma, on a German locale). Two decimals, invariant, fixes both.
            if (value is float f) return f.ToString("0.##", CultureInfo.InvariantCulture);
            return value?.ToString() ?? "";
        }

        private static Color ValueColor(CustomOption option) {
            string value = ValueOf(option);
            if (value == "On") return ValueOn;
            if (value == "Off" || value == "0%") return Dim;
            return ValueNumber;
        }

        private static string ModifierExtras(CustomOption option) {
            // Same information TOR shows on the rates page (CustomOptions.cs:969-979).
            if (option.type != Types.Modifier || option.getSelection() == 0) return "";
            if (option == CustomOptionHolder.modifierLover)
                return $" (1 Evil: {CustomOptionHolder.modifierLoverImpLoverRate.getSelection() * 10}%)";
            var quantity = CustomOption.options
                .Where(o => o.parent == option && o.name.Contains("Quantity")).ToList();
            return quantity.Count == 1 ? $" ({quantity[0].getQuantity()})" : "";
        }

        private static int ImpostorCap() {
            try {
                var options = GameOptionsManager.Instance?.currentGameOptions;
                return options == null ? -1 : options.NumImpostors;
            } catch {
                return -1;
            }
        }

        private static string CrewRoleCountValue() {
            int min = CustomOptionHolder.crewmateRolesCountMin.getSelection();
            int max = CustomOptionHolder.crewmateRolesCountMax.getSelection();
            string prefix = "";
            if (CustomOptionHolder.crewmateRolesFill.getBool()) {
                int impostors = Math.Max(0, ImpostorCap());
                int crewCount = PlayerControl.AllPlayerControls.Count - impostors;
                int minNeutral = CustomOptionHolder.neutralRolesCountMin.getSelection();
                int maxNeutral = CustomOptionHolder.neutralRolesCountMax.getSelection();
                if (minNeutral > maxNeutral) minNeutral = maxNeutral;
                min = Math.Max(0, crewCount - maxNeutral);
                max = Math.Max(0, crewCount - minNeutral);
                prefix = "Fill: ";
            }
            if (min > max) min = max;
            return prefix + (min == max ? $"{max}" : $"{min} - {max}");
        }

        private static string RangeValue(CustomOption minOption, CustomOption maxOption, int cap) {
            int min = minOption.getSelection();
            int max = maxOption.getSelection();
            if (cap >= 0 && max > cap) max = cap;
            if (min > max) min = max;
            return min == max ? $"{max}" : $"{min} - {max}";
        }

        private static bool IsCountMax(CustomOption option) =>
            option == CustomOptionHolder.crewmateRolesCountMax
            || option == CustomOptionHolder.neutralRolesCountMax
            || option == CustomOptionHolder.impostorRolesCountMax
            || option == CustomOptionHolder.modifiersCountMax;

        // ==========================================================================================
        // Names, tags and colours
        // ==========================================================================================

        private static string CleanName(CustomOption option) {
            string name = StripTags(option.name);
            if (name.StartsWith("- ")) name = name.Substring(2); // added by CustomOption's constructor
            return name.Replace("\n", " ").Trim();
        }

        // "Tesla Charge Countdown (sec)" under the Tesla block is just "Charge Countdown (sec)".
        private static string StripRolePrefix(string name, string rootName) {
            if (rootName.Length == 0 || name.Length <= rootName.Length + 1) return name;
            if (!name.StartsWith(rootName + " ", StringComparison.Ordinal)) return name;
            string stripped = name.Substring(rootName.Length + 1).Trim();
            return stripped.Length == 0 ? name : stripped;
        }

        // Which mod an option belongs to. NOT by ID range: TOR itself creates ids inside the ranges
        // the companion mods reserve (1100-1102 Shifter/Armored/CanShiftMedicShield, 2001-2013 for
        // the guesser gamemode), so a range check labels TOR's own settings as somebody else's -
        // which is exactly what the first build did. The owner is instead read the way the CLR knows
        // it: every mod keeps its options in its own static fields, so scanning each mod assembly's
        // static CustomOption / CustomOption[] / object fields names the creator with certainty.
        private static readonly Dictionary<string, string> TagByAssembly = new Dictionary<string, string> {
            { "UnknownsCollection",  " [UC]" },
            { "UsefulTORStuff",      " [FF]" },
            { "TOR-ChanceModifier",  " [Chance]" },
            { "Nightfall",           " [NF]" },
        };

        // Built ONCE, from the lobby-join hook - never lazily from the render path. Reading a static
        // field runs that type's initializer if it has not run yet, and a mod's type initializer can
        // load sprites or touch Unity objects; doing that in the middle of the lobby-to-game scene
        // change is how you get a native crash with nothing in the log. On join, every plugin has
        // long finished loading and the game is idle.
        private static Dictionary<int, string> ownerTagById;

        public static void ScanOptionOwners() {
            var map = new Dictionary<int, string>();
            try {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    string tag;
                    string name;
                    try { name = asm.GetName().Name; } catch { continue; }
                    if (!TagByAssembly.TryGetValue(name, out tag)) continue;

                    // A merged assembly (Unknown's Collection ships ILRepacked) can fail to load a
                    // few types; the exception still carries the ones that DID load, and dropping
                    // the whole assembly over one bad type would cost every tag it owns.
                    Type[] types;
                    try {
                        types = asm.GetTypes();
                    } catch (ReflectionTypeLoadException partial) {
                        types = partial.Types;
                    } catch {
                        continue;
                    }
                    foreach (var type in types) {
                        if (type == null) continue;
                        FieldInfo[] fields;
                        try {
                            fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        } catch { continue; }
                        foreach (var field in fields) {
                            // Only fields that are already typed as an option. `object` fields were
                            // read here at first (Nightfall keeps its option untyped so it can run
                            // without TOR at all) - but every extra field read is another type
                            // initializer that might run for the first time, so Nightfall is handled
                            // by its published id constant below instead.
                            if (field.FieldType != typeof(CustomOption)
                                && field.FieldType != typeof(CustomOption[])) continue;
                            object value;
                            try { value = field.GetValue(null); } catch { continue; }
                            if (value is CustomOption single) map[single.id] = tag;
                            else if (value is CustomOption[] many)
                                foreach (var o in many) if (o != null) map[o.id] = tag;
                        }
                    }
                }

                // Nightfall's one option (NightfallOptions.OptionId) - a plain int constant, so
                // reading it cannot drag any Unity work along.
                try {
                    var nf = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Nightfall")
                        ?.GetType("Nightfall.NightfallOptions");
                    var idField = nf?.GetField("OptionId", BindingFlags.Public | BindingFlags.Static);
                    if (idField != null && idField.GetValue(null) is int nfId) map[nfId] = " [NF]";
                } catch { /* Nightfall not installed, or renamed - it simply gets no tag */ }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsOverlayView] owner scan failed: {e}");
            }
            ownerTagById = map;
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[SettingsOverlayView] option owners resolved for {map.Count} option(s).");
        }

        private static string ModTagPlain(CustomOption option) {
            if (ShowModTags != null && !ShowModTags.Value) return "";
            if (ownerTagById == null) return ""; // scan runs on lobby join; no guessing before that
            string tag;
            return ownerTagById.TryGetValue(option.id, out tag) ? tag : "";
        }

        private static string ModTag(CustomOption option) {
            string tag = ModTagPlain(option);
            return tag.Length == 0 ? "" : Cs(Dim, tag);
        }

        private static Color DisplayColor(CustomOption option, ref int shadeIndex) {
            Color c = ColorOf(option, FactionColor(option.type));
            bool wantShades = ImpostorShades == null || ImpostorShades.Value;
            if (wantShades && IsImpostorRed(c)) {
                c = ImpostorFamily[shadeIndex % ImpostorFamily.Length];
                shadeIndex++;
            }
            return c;
        }

        private static Color ColorOf(CustomOption option, Color fallback) {
            Color parsed;
            if (TryParseTagColor(option.name, out parsed)) return parsed; // untranslated name, tag intact
            if (snapshot.TryGetValue(option.id, out parsed)) return parsed;

            var registry = AppDomain.CurrentDomain.GetData(AppKeyOptionColors) as Dictionary<string, string>;
            if (registry != null) {
                string hex;
                if (registry.TryGetValue("id:" + option.id, out hex) && TryParseHex(hex, out parsed)) return parsed;
                if (registry.TryGetValue(CleanName(option), out hex) && TryParseHex(hex, out parsed)) return parsed;
            }

            return fallback;
        }

        // ---- colour snapshot ---------------------------------------------------------------------
        // Taken while TOR's option names still carry their <color> tags, i.e. BEFORE this mod's
        // localization rewrites them. Re-running it only adds IDs that are not known yet, so a mod
        // that registers its options after us is still picked up, and a translated name (no tag) can
        // never overwrite a colour that was read correctly earlier.
        private static readonly Dictionary<int, Color> snapshot = new Dictionary<int, Color>();

        public static void SnapshotColors() {
            try {

                foreach (var option in CustomOption.options) {
                    if (option == null || snapshot.ContainsKey(option.id)) continue;
                    Color parsed;
                    if (TryParseTagColor(option.name, out parsed)) snapshot[option.id] = parsed;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsOverlayView] colour snapshot failed: {e}");
            }
        }

        private static Color FactionColor(Types type) {
            switch (type) {
                case Types.Impostor: return Palette.ImpostorRed;
                case Types.Neutral:  return new Color(0.70f, 0.72f, 0.76f);
                case Types.Crewmate: return new Color(0.55f, 0.78f, 1.00f);
                case Types.Modifier: return new Color(0.78f, 0.72f, 0.95f);
                default:             return NeutralInk;
            }
        }

        private static bool IsImpostorRed(Color c) {
            Color red = Palette.ImpostorRed;
            return Mathf.Abs(c.r - red.r) < 0.02f && Mathf.Abs(c.g - red.g) < 0.02f && Mathf.Abs(c.b - red.b) < 0.02f;
        }

        // Sub-options keep their role's hue but step back toward the panel grey, so a block reads as
        // one unit and the header still leads it.
        private static Color Mix(Color c, float towardsPanel) =>
            Color.Lerp(c, new Color(0.55f, 0.57f, 0.60f), towardsPanel);

        // ---- string helpers ---------------------------------------------------------------------
        private static string Cs(Color c, string s) =>
            $"<color=#{ToByte(c.r):X2}{ToByte(c.g):X2}{ToByte(c.b):X2}{ToByte(c.a):X2}>{s}</color>";

        private static byte ToByte(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

        private static string StripTags(string s) {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (char ch in s) {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool TryParseTagColor(string name, out Color color) {
            color = Color.white;
            if (string.IsNullOrEmpty(name)) return false;
            int at = name.IndexOf("<color=#", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int start = at + "<color=#".Length;
            int end = name.IndexOf('>', start);
            if (end <= start) return false;
            return TryParseHex(name.Substring(start, end - start), out color);
        }

        private static bool TryParseHex(string hex, out Color color) {
            color = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) return false;
            int r, g, b;
            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            color = new Color(r / 255f, g / 255f, b / 255f);
            return true;
        }

        // Both tables are refreshed here and nowhere else: on a lobby join every plugin has finished
        // loading, the game is idle, and nothing is being torn down. The colour snapshot is additive
        // (a name whose <color> tag the localization already stripped can never overwrite a colour
        // that was read correctly at load), the owner scan is rebuilt from scratch.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyJoinPatch {
            public static void Postfix() {
                SnapshotColors();
                ScanOptionOwners();
            }
        }

        // ---- TOR gamemode (internal type, resolved once) ------------------------------------------
        private static bool gameModeResolved;
        private static FieldInfo gameModeField;

        private static CustomGamemodes TorGameMode() {
            if (!gameModeResolved) {
                gameModeResolved = true;
                gameModeField = typeof(CustomOption).Assembly
                    .GetType("TheOtherRoles.TORMapOptions")
                    ?.GetField("gameMode", BindingFlags.Public | BindingFlags.Static);
                if (gameModeField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[SettingsOverlayView] TORMapOptions.gameMode not found - assuming Classic.");
            }
            return gameModeField != null
                ? (CustomGamemodes)(int)gameModeField.GetValue(null)
                : CustomGamemodes.Classic;
        }
    }
}
