// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsSearchUI - a "Search" tab in the game's own settings screens (User 2026-11-13, second version).
 *
 * The first version was a canvas panel of its own. The User wanted it to look exactly like Among Us, and a role
 * search to bring every setting of that role along. So the search now lives where the settings are:
 *
 *   - EDIT menu (host, GameSettingMenu): one more button in the left column under TOR's tab buttons. The tab is a
 *     copy of the game's "GAME SETTINGS TAB" like TOR's own tabs, filled with the same rows TOR builds
 *     (CustomOptions.cs createSettings: StringOption rows, CategoryHeaderMasked headers, same positions), so the
 *     minus and plus buttons look and feel the same. They change the value through TOR's updateSelection.
 *   - VIEW pane (everybody, LobbyViewSettingsPane): one more tab button in TOR's row; the hits are drawn with the
 *     game's info panels in two columns like TOR's drawTab.
 *
 * Both start with a field row (the query, the number of hits) and list the hits grouped under their section
 * header (the role, or a heading like "Role Counts"). A hit on a section (its role name) brings the whole section;
 * a hit on an option brings its sub-options too. Sub-options whose parent is off are listed dimmed, not hidden,
 * so a search for a role shows everything it has.
 *
 * Typing: while a search tab is on screen, the keyboard belongs to it (FeedbackInputLock: no hotkey of any mod
 * fires). Backspace deletes, Escape clears the query, a second Escape closes the screen. Ctrl+F in the lobby (or
 * the lobby menu entry) opens the view pane on the search tab, or switches the open edit menu to it.
 *
 * TOR stays untouched. Its rows are found through the option's optionBehaviour, ours are not registered there, so
 * TOR's StringOption prefixes ignore them; Initialize of our rows is skipped (it would read vanilla values), and
 * their buttons get our own click events. updateSelection's tail (refreshing "the active TOR tab") throws while
 * our tab is the active one; the value, its config entry and the share are done before that point.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Attributes;
using TheOtherRoles;
using TMPro;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    // =====================================================================================================
    // What is found
    // =====================================================================================================
    internal static class SettingsSearch {
        internal sealed class Group {
            public CustomOption Header;           // the section's header option (null: no header before it)
            public string Title;
            public readonly List<CustomOption> Rows = new List<CustomOption>();
        }

        internal static string Query = "";
        internal const int MaxRows = 150;           // more than that is not a search any more; the count says so

        private static Dictionary<int, string> haystack;
        private static Dictionary<int, CustomOption> sectionOf;
        private static int builtFor = -1;

        internal static string SectionTitle(CustomOption header) =>
            header == null ? "" : (!string.IsNullOrEmpty(header.heading) ? header.heading : header.name);

        private static void Build() {
            if (haystack != null && builtFor == CustomOption.options.Count) return;
            haystack = new Dictionary<int, string>();
            sectionOf = new Dictionary<int, CustomOption>();
            var current = new Dictionary<Types, CustomOption>();
            foreach (var o in CustomOption.options) {
                if (o == null) continue;
                if (o.isHeader) current[o.type] = o;
                current.TryGetValue(o.type, out var sec);
                sectionOf[o.id] = sec;
                if (o.id == 0 || o.selections == null || o.selections.Length == 0) continue;
                var parts = new List<string>();
                for (var p = o; p != null; p = p.parent) parts.Add(SettingsOverlayView.CleanName(p));
                if (sec != null) { parts.Add(SettingsOverlayView.CleanName(sec)); parts.Add(sec.heading ?? ""); }
                parts.Add(o.type.ToString());
                parts.Add(SettingsOverlayView.ModTagPlain(o));
                if (!string.IsNullOrEmpty(o.heading)) parts.Add(o.heading);
                haystack[o.id] = StripTags(string.Join(" ", parts)).ToLowerInvariant();
            }
            builtFor = CustomOption.options.Count;
        }

        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "");

        // the tabs TOR shows in this game mode (CustomOptions.cs createSettingTabs); the rest has no menu row
        private static bool TypeShown(Types t) {
            var mode = SettingsOverlayView.TorGameMode();
            switch (mode) {
                case CustomGamemodes.HideNSeek: return t == Types.HideNSeekMain || t == Types.HideNSeekRoles;
                case CustomGamemodes.PropHunt: return t == Types.PropHunt;
                default:
                    if (t == Types.Guesser) return mode == CustomGamemodes.Guesser;
                    return t == Types.General || t == Types.Impostor || t == Types.Neutral || t == Types.Crewmate || t == Types.Modifier;
            }
        }

        private static readonly HashSet<int> GuesserHidden = new HashSet<int> { 310, 311, 312, 313, 314, 315, 316, 317, 318 };

        /// <summary>The hits for the current query, grouped by section, in menu order. total = rows before the cap.</summary>
        internal static List<Group> Find(out int total) {
            total = 0;
            var groups = new List<Group>();
            var words = (Query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return groups;
            Build();
            bool guesserMode = SettingsOverlayView.TorGameMode() == CustomGamemodes.Guesser;

            var hit = new HashSet<int>();
            foreach (var o in CustomOption.options) {
                if (o == null || !haystack.TryGetValue(o.id, out var hay) || !TypeShown(o.type)) continue;
                if (guesserMode && GuesserHidden.Contains(o.id)) continue;
                bool all = true;
                foreach (var w in words) if (!hay.Contains(w)) { all = false; break; }
                if (all) hit.Add(o.id);
            }
            if (hit.Count == 0) return groups;

            // sub-options of every hit come along (a role found by name brings all its settings)
            var byId = new Dictionary<Group, int>();
            Group last = null;
            foreach (var o in CustomOption.options) {
                if (o == null || !haystack.ContainsKey(o.id) || !TypeShown(o.type)) continue;
                if (guesserMode && GuesserHidden.Contains(o.id)) continue;
                bool take = hit.Contains(o.id);
                for (var p = o.parent; !take && p != null; p = p.parent) take = hit.Contains(p.id);
                if (!take) continue;
                total++;
                if (total > MaxRows) continue;
                sectionOf.TryGetValue(o.id, out var sec);
                if (last == null || last.Header != sec || sec == null && last.Rows.Count > 0 && last.Rows[0].type != o.type) {
                    last = new Group { Header = sec, Title = sec != null ? SectionTitle(sec) : o.type.ToString() };
                    groups.Add(last);
                }
                last.Rows.Add(o);
            }
            return groups;
        }

        /// <summary>Row title the way TOR writes it (a role's own rate row reads "Spawn Chance").</summary>
        internal static string RowTitle(CustomOption o) {
            if (o.isHeader && string.IsNullOrEmpty(o.heading)
                && (o.type == Types.Neutral || o.type == Types.Crewmate || o.type == Types.Impostor || o.type == Types.Modifier))
                return "Spawn Chance";
            return o.name;
        }

        internal static string ValueText(CustomOption o) {
            try { return o.selections[o.selection].ToString(); } catch { return ""; }
        }

        internal static bool Live(CustomOption o) => SettingsOverlayView.IsVisible(o);

        internal static string FieldText(bool caret) =>
            Query.Length == 0
                ? $"<color=#9AA3AD>{UTSLocalization.Tr("uts.settingssearch.hint")}</color>"
                : Query.Replace("<", "").Replace(">", "") + (caret ? "|" : " ");

        internal static string CountText(int total) =>
            Query.Trim().Length == 0 ? "" : total == 0 ? "0"
                : total > MaxRows ? $"{MaxRows}+" : total.ToString();

        // ---- TOR internals (the patch classes are internal to TOR) ------------------------------------------
        private static FieldInfo editTabs, editButtons, viewButtons;
        internal static List<GameObject> EditTabs => Read<List<GameObject>>(ref editTabs, "TheOtherRoles.GameOptionsMenuStartPatch", "currentTabs");
        internal static List<PassiveButton> EditButtons => Read<List<PassiveButton>>(ref editButtons, "TheOtherRoles.GameOptionsMenuStartPatch", "currentButtons");
        internal static List<PassiveButton> ViewButtons => Read<List<PassiveButton>>(ref viewButtons, "TheOtherRoles.LobbyViewSettingsPatch", "currentButtons");

        private static T Read<T>(ref FieldInfo f, string type, string field) where T : class {
            try {
                if (f == null) f = typeof(CustomOption).Assembly.GetType(type)?.GetField(field, BindingFlags.Public | BindingFlags.Static);
                return f?.GetValue(null) as T;
            } catch { return null; }
        }

        /// <summary>Host: change a value through TOR, the way TOR's own plus and minus buttons do.</summary>
        internal static void Step(CustomOption option, int dir) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (option == null || option.id == 0 || option.selections == null || option.selections.Length == 0) return;
            bool hasRow = option.optionBehaviour != null;
            int sel = option.selection + dir;
            if (sel < 0 || sel >= option.selections.Length) return;     // TOR's buttons stop at the ends too
            try { option.updateSelection(sel); } catch (Exception e) {
                // the tail refreshes "the active TOR tab", and ours is the active one: value, entry and share are done
                if (option.selection != sel) {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SettingsSearch] updateSelection({option.id}): {e.GetType().Name}");
                    option.selection = sel;
                }
            }
            if (!hasRow) {
                // TOR shares only through its menu row; an option without one goes out like the web editor's changes
                try { if (option.entry != null) option.entry.Value = option.selection; } catch { }
                try { PacedOptionSync.SendPaced(new[] { option }); } catch { }
            }
        }
    }

    // =====================================================================================================
    // Edit menu (host)
    // =====================================================================================================
    internal static class SettingsSearchEdit {
        internal static GameSettingMenu Menu;
        internal static GameObject Tab;
        internal static PassiveButton Button;
        internal static GameOptionsMenu Gom;
        private static bool switching;
        private static readonly HashSet<IntPtr> ours = new HashSet<IntPtr>();
        private static StringOption field;
        internal static bool Dirty;

        internal static bool Visible => Tab != null && Tab.activeInHierarchy;

        [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Start))]
        private static class StartPatch {
            [HarmonyPriority(Priority.Low)]
            [HarmonyAfter("me.eisbison.theotherroles")]
            public static void Postfix(GameSettingMenu __instance) {
                try { Create(__instance); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] edit tab: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.ChangeTab))]
        private static class ChangeTabPatch {
            public static void Postfix(bool previewOnly) {
                if (previewOnly || switching) return;
                try {
                    if (Tab != null) Tab.SetActive(false);
                    if (Button != null) Button.SelectButton(false);
                } catch { }
            }
        }

        // our rows are not TOR's: the vanilla Initialize would read game options into them
        [HarmonyPatch(typeof(StringOption), nameof(StringOption.Initialize))]
        private static class InitPatch {
            public static bool Prefix(StringOption __instance) => !ours.Contains(__instance.Pointer);
        }

        private static void Create(GameSettingMenu menu) {
            Menu = menu;
            ours.Clear();
            var torButtons = SettingsSearch.EditButtons;
            if (torButtons == null || torButtons.Count == 0) return;     // vanilla Hide and Seek: TOR adds nothing
            var template = GameObject.Find("GAME SETTINGS TAB");
            var buttonTemplate = GameObject.Find("GameSettingsButton");
            var leftPanel = GameObject.Find("LeftPanel");
            if (template == null || buttonTemplate == null || leftPanel == null) return;

            Tab = UnityEngine.Object.Instantiate(template, template.transform.parent);
            Tab.name = "UTSSearchSettings";
            Gom = Tab.GetComponent<GameOptionsMenu>();
            Clear();
            Tab.SetActive(false);

            // the button: one step further than TOR's last one, same look
            var lastTor = torButtons.Where(b => b != null).Select(b => b.transform).ToList();
            var go = UnityEngine.Object.Instantiate(buttonTemplate, leftPanel.transform);
            go.name = "UTSSearchSettingsButton";
            if (lastTor.Count >= 2) {
                var a = lastTor[lastTor.Count - 2].localPosition; var b = lastTor[lastTor.Count - 1].localPosition;
                go.transform.localPosition = b + (b - a);
            } else if (lastTor.Count == 1) go.transform.localPosition = lastTor[0].localPosition + Vector3.down * 0.5f;
            Button = go.GetComponent<PassiveButton>();
            string label = UTSLocalization.Tr("uts.settingssearch.tab");
            var text = go.transform.FindChild("FontPlacer")?.GetComponentInChildren<TextMeshPro>();
            menu.StartCoroutine(Effects.Lerp(2f, new Action<float>(p => { if (text != null) text.text = label; })));
            Button.OnClick.RemoveAllListeners();
            Button.OnClick.AddListener((Action)Show);
            Button.OnMouseOut.RemoveAllListeners();
            Button.OnMouseOver.RemoveAllListeners();
            Button.SelectButton(false);
            Dirty = true;
        }

        internal static void Show() {
            if (Menu == null || Tab == null) return;
            try {
                switching = true;
                Menu.ChangeTab(3, false);       // TOR: hides its tabs and the game's, shows its first one
            } catch { } finally { switching = false; }
            try {
                var tabs = SettingsSearch.EditTabs; var buttons = SettingsSearch.EditButtons;
                if (tabs != null) foreach (var t in tabs) if (t != null) t.SetActive(false);
                if (buttons != null) foreach (var b in buttons) if (b != null) b.SelectButton(false);
                Tab.SetActive(true);
                Button?.SelectButton(true);
                Dirty = true;
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] show: {e.Message}"); }
        }

        private static void Clear() {
            foreach (var child in Gom.Children) if (child != null) UnityEngine.Object.Destroy(child.gameObject);
            Gom.Children.Clear();
            var c = Gom.settingsContainer;
            for (int i = c.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(c.GetChild(i).gameObject);
            ours.Clear();
            field = null;
        }

        // ---- rows, as TOR's createSettings builds them --------------------------------------------------
        private static CategoryHeaderMasked Header(string title, float y) {
            var h = UnityEngine.Object.Instantiate(Gom.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, Gom.settingsContainer);
            h.SetHeader(StringNames.ImpostorsCategory, 20);
            h.Title.text = title;
            h.Title.outlineColor = Color.white;
            h.Title.outlineWidth = 0.2f;
            h.transform.localScale = Vector3.one * 0.63f;
            h.transform.localPosition = new Vector3(-0.903f, y, -2f);
            return h;
        }

        private static StringOption Row(string title, string value, float y) {
            var row = UnityEngine.Object.Instantiate(Gom.stringOptionOrigin, Vector3.zero, Quaternion.identity, Gom.settingsContainer);
            ours.Add(row.Pointer);
            row.transform.localPosition = new Vector3(0.952f, y, -2f);
            row.SetClickMask(Gom.ButtonClickMask);
            foreach (var sr in row.GetComponentsInChildren<SpriteRenderer>(true)) sr.material.SetInt(PlayerMaterial.MaskLayer, 20);
            foreach (var tmp in row.GetComponentsInChildren<TextMeshPro>(true)) {
                tmp.fontMaterial.SetFloat("_StencilComp", 3f);
                tmp.fontMaterial.SetFloat("_Stencil", 20f);
            }
            row.OnValueChanged = new Action<OptionBehaviour>(o => { });
            row.TitleText.text = title;
            if (title.Length > 25) row.TitleText.fontSize = 2.2f;
            if (title.Length > 40) row.TitleText.fontSize = 2f;
            row.Value = row.oldValue = 0;
            row.ValueText.text = value;
            Gom.Children.Add(row);
            return row;
        }

        private static void Wire(GameOptionButton b, Action a) {
            if (b == null) return;
            b.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            b.OnClick.AddListener(a);
        }

        internal static void Render() {
            if (Gom == null) return;
            Dirty = false;
            Clear();
            var groups = SettingsSearch.Find(out int total);
            bool host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
            float num = 1.5f;
            Header(UTSLocalization.Tr("uts.settingssearch.title"), num);
            num -= 0.63f;
            field = Row(SettingsSearch.FieldText(true), SettingsSearch.CountText(total), num);
            if (field.MinusBtn != null) field.MinusBtn.gameObject.SetActive(false);
            if (field.PlusBtn != null) field.PlusBtn.gameObject.SetActive(false);
            field.TitleText.enableAutoSizing = true;
            field.TitleText.fontSizeMin = 1.6f; field.TitleText.fontSizeMax = 2.6f;
            num -= 0.45f;

            foreach (var g in groups) {
                num -= 0.15f;
                Header(g.Title, num);
                num -= 0.63f;
                foreach (var o in g.Rows) {
                    var row = Row(SettingsSearch.RowTitle(o), SettingsSearch.ValueText(o), num);
                    if (!SettingsSearch.Live(o)) {
                        row.TitleText.color = new Color(1f, 1f, 1f, 0.45f);
                        row.ValueText.color = new Color(1f, 1f, 1f, 0.45f);
                    }
                    var opt = o;
                    if (host) {
                        Wire(row.MinusBtn, () => { SettingsSearch.Step(opt, -1); Dirty = true; });
                        Wire(row.PlusBtn, () => { SettingsSearch.Step(opt, +1); Dirty = true; });
                        row.MinusBtn?.SetInteractable(opt.selection > 0);
                        row.PlusBtn?.SetInteractable(opt.selection < opt.selections.Length - 1);
                    } else row.SetAsPlayer();
                    num -= 0.45f;
                }
            }
            Gom.scrollBar.SetYBoundsMax(Math.Max(0f, -num - 1.65f));
        }

        internal static void Caret(bool on) {
            try { if (field != null) field.TitleText.text = SettingsSearch.FieldText(on); } catch { }
        }
    }

    // =====================================================================================================
    // View pane (everybody)
    // =====================================================================================================
    internal static class SettingsSearchView {
        internal const StringNames Category = (StringNames)770077;   // not a real tab: TOR's patches leave it alone
        internal static LobbyViewSettingsPane Pane;
        internal static PassiveButton Button;
        internal static bool Dirty;
        private static ViewSettingsInfoPanel field;

        internal static bool Visible =>
            Pane != null && Pane.gameObject.activeInHierarchy && Pane.currentTab == Category;

        [HarmonyPatch(typeof(LobbyViewSettingsPane), nameof(LobbyViewSettingsPane.Update))]
        private static class UpdatePatch {
            [HarmonyPriority(Priority.Low)]
            [HarmonyAfter("me.eisbison.theotherroles")]
            public static void Postfix(LobbyViewSettingsPane __instance) {
                try {
                    Pane = __instance;
                    if (RowStale()) Create(__instance);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(LobbyViewSettingsPane), nameof(LobbyViewSettingsPane.ChangeTab))]
        private static class ChangeTabPatch {
            public static void Postfix(LobbyViewSettingsPane __instance, StringNames category) {
                try {
                    if (Button != null && Button) Button.SelectButton(category == Category);
                    if (category == Category) { __instance.taskTabButton?.SelectButton(false); Dirty = true; }
                } catch { }
            }
        }

        // the game's SetTab draws its own settings for a tab it does not know; ours replaces them in the same frame
        [HarmonyPatch(typeof(LobbyViewSettingsPane), nameof(LobbyViewSettingsPane.SetTab))]
        private static class SetTabPatch {
            public static void Postfix(LobbyViewSettingsPane __instance) {
                if (__instance.currentTab != Category) return;
                Pane = __instance;
                try { Render(); } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] view: {e.Message}"); }
            }
        }

        private static PassiveButton squeezedFor;   // TOR's first tab button when the row was squeezed

        private static void Create(LobbyViewSettingsPane pane) {
            var tor = SettingsSearch.ViewButtons;
            if (tor == null || tor.Count == 0) return;                 // TOR has not built its row yet
            var buttons = tor.Where(b => b != null && b).ToList();
            if (buttons.Count == 0) return;
            if (Button != null && Button) UnityEngine.Object.Destroy(Button.gameObject);
            var list = buttons.Select(b => b.transform).ToList();
            var template = list[list.Count - 1].gameObject;
            var go = UnityEngine.Object.Instantiate(template, template.transform.parent);
            go.name = "ViewUTSSearch";
            list.Add(go.transform);

            // The row is full (User 13.11.: the tab stuck out on the right). All tabs share the width TOR's tabs had:
            // n tabs at step s become n+1 tabs at step s*n/(n+1), each that much narrower, the label scaled evenly.
            int n = list.Count - 1;
            float s = n >= 2 ? list[1].localPosition.x - list[0].localPosition.x : 1.75f;
            float f = n / (float)(n + 1);
            float left = list[0].localPosition.x - s / 2f;
            for (int i = 0; i < list.Count; i++) {
                var t = list[i];
                var pos = list[0].localPosition;
                pos.x = left + s * f / 2f + i * s * f;
                t.localPosition = pos;
                var sc = t.localScale; sc.x *= f; t.localScale = sc;
                var font = t.FindChild("FontPlacer");
                if (font != null) { var fs = font.localScale; fs.y *= f; font.localScale = fs; }
            }
            squeezedFor = buttons[0];

            Button = go.GetComponent<PassiveButton>();
            string label = UTSLocalization.Tr("uts.settingssearch.tab");
            var text = go.transform.FindChild("FontPlacer")?.GetComponentInChildren<TextMeshPro>();
            if (text != null) text.text = label;
            pane.StartCoroutine(Effects.Lerp(2f, new Action<float>(p => { if (text != null) text.text = label; })));
            Button.OnClick.RemoveAllListeners();
            Button.OnClick.AddListener((Action)(() => { try { Pane?.ChangeTab(Category); } catch { } }));
            Button.OnMouseOut.RemoveAllListeners();
            Button.OnMouseOver.RemoveAllListeners();
            Button.SelectButton(pane.currentTab == Category);
        }

        // TOR rebuilds its row (game mode change): squeeze the new one
        internal static bool RowStale() {
            if (Button == null || !Button) return true;
            var tor = SettingsSearch.ViewButtons;
            var first = tor?.FirstOrDefault(b => b != null && b);
            return first != null && (squeezedFor == null || !squeezedFor || first.Pointer != squeezedFor.Pointer);
        }

        internal static void Render() {
            var pane = Pane;
            if (pane == null) return;
            Dirty = false;
            for (int j = 0; j < pane.settingsInfo.Count; j++) if (pane.settingsInfo[j] != null) UnityEngine.Object.Destroy(pane.settingsInfo[j]);
            pane.settingsInfo.Clear();
            field = null;

            var groups = SettingsSearch.Find(out int total);
            float num = 1.44f;
            int headers = 0, lines = 0, panels = 0, singles = 1;

            // the field: query on the left, number of hits on the right
            HeaderAt(pane, UTSLocalization.Tr("uts.settingssearch.title"), num); headers++;
            num -= 1.05f;
            field = Panel(pane, UTSLocalization.Tr("uts.settingssearch.field"), SettingsSearch.FieldText(true), -8.95f, num, true);
            field.settingText.enableAutoSizing = true;
            field.settingText.fontSizeMin = 1f;
            field.settingText.fontSizeMax = field.settingText.fontSize;
            Panel(pane, UTSLocalization.Tr("uts.settingssearch.found"), SettingsSearch.CountText(total), -3f, num, true);
            lines++; panels += 2;

            foreach (var g in groups) {
                num -= 0.85f;
                HeaderAt(pane, g.Title, num); headers++;
                num -= 1.05f;
                int i = 0;
                foreach (var o in g.Rows) {
                    float x;
                    if (i % 2 == 0) { lines++; x = -8.95f; if (i > 0) num -= 0.85f; } else x = -3f;
                    Panel(pane, SettingsSearch.RowTitle(o), SettingsSearch.ValueText(o), x, num, SettingsSearch.Live(o));
                    panels++;
                    i++;
                }
                if (i % 2 != 0) singles++;
            }
            float spacing = (headers * 1.05f + lines * 0.85f) / Math.Max(1, headers + lines) * 1.01f;
            pane.scrollBar.CalculateAndSetYBounds(pane.settingsInfo.Count + singles * 2 + headers, 2f, 5f, spacing);
        }

        private static void HeaderAt(LobbyViewSettingsPane pane, string title, float y) {
            var h = UnityEngine.Object.Instantiate(pane.categoryHeaderOrigin);
            h.SetHeader(StringNames.ImpostorsCategory, 61);
            h.Title.text = title;
            h.Title.outlineColor = Color.white;
            h.Title.outlineWidth = 0.2f;
            h.transform.SetParent(pane.settingsContainer);
            h.transform.localScale = Vector3.one;
            h.transform.localPosition = new Vector3(-9.77f, y, -2f);
            pane.settingsInfo.Add(h.gameObject);
        }

        private static ViewSettingsInfoPanel Panel(LobbyViewSettingsPane pane, string title, string value, float x, float y, bool live) {
            var p = UnityEngine.Object.Instantiate(pane.infoPanelOrigin);
            p.transform.SetParent(pane.settingsContainer);
            p.transform.localScale = Vector3.one;
            p.transform.localPosition = new Vector3(x, y, -2f);
            p.SetInfo(StringNames.ImpostorsCategory, value, 61);
            p.titleText.text = title;
            if (!live) {
                p.titleText.color = new Color(1f, 1f, 1f, 0.45f);
                p.settingText.color = new Color(1f, 1f, 1f, 0.45f);
            }
            pane.settingsInfo.Add(p.gameObject);
            return p;
        }

        internal static void Caret(bool on) {
            try { if (field != null) field.settingText.text = SettingsSearch.FieldText(on); } catch { }
        }

        /// <summary>Opens the view pane (the lobby's VIEW button) on the search tab.</summary>
        // The pane is known here only after its first Update, and when it comes up it selects the overview itself:
        // so it is taken from the lobby info pane, and the tab switch waits two frames (autotest 13.11.: it stayed
        // on the overview).
        internal static void Open() {
            try {
                var info = LobbyInfoPane.Instance;
                if (Pane == null && info != null) Pane = info.LobbyViewSettingsPane;
                if (Pane == null || !Pane.gameObject.activeInHierarchy) {
                    if (info == null) return;
                    bool host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
                    var view = (host ? info.HostViewButton : info.ClientViewButton)?.TryCast<PassiveButton>();
                    view?.OnClick?.Invoke();
                    if (Pane == null) Pane = info.LobbyViewSettingsPane;
                }
                SettingsSearchUI.Instance?.Later(2, () => { try { Pane?.ChangeTab(Category); } catch { } });
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] open view: {e.Message}"); }
        }

        internal static void Close() {
            try { Pane?.backButton?.OnClick?.Invoke(); } catch { }
        }
    }

    // values change while the search shows them (the host's own buttons, a share from the host, a preset)
    [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.updateSelection))]
    internal static class SettingsSearchValuePatch {
        public static void Postfix() {
            SettingsSearchView.Dirty = true;
            SettingsSearchEdit.Dirty = true;
        }
    }

    // =====================================================================================================
    // Keyboard, refresh, lobby entry
    // =====================================================================================================
    public class SettingsSearchUI : MonoBehaviour {
        public static SettingsSearchUI Instance { get; private set; }
        public SettingsSearchUI(IntPtr ptr) : base(ptr) { }

        private float caretBlink, editRedrawAt;
        private bool caretOn, lockedByUs;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            LobbyMenu.Add("uts.settingssearch", 12, ShouldShow, () => UTSLocalization.Tr("uts.settingssearch.button"),
                          () => Instance?.Toggle(), VanillaUI.Teal);
        }

        [HideFromIl2Cpp]
        private static bool ShouldShow() => LobbyScreen.Exists && AmongUsClient.Instance != null;

        public void Update() {
            try {
                bool edit = SettingsSearchEdit.Visible, view = !edit && SettingsSearchView.Visible;
                if (!edit && !view) {
                    Unlock();
                    if (LobbyScreen.Exists && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                        && Input.GetKeyDown(KeyCode.F) && !ChatOpen()) Toggle();
                    return;
                }
                if (ChatOpen()) { Unlock(); return; }
                if (!FeedbackInputLock.Engaged) { FeedbackInputLock.Engage("settingssearch"); lockedByUs = FeedbackInputLock.Engaged; }

                bool changed = false, close = false;
                using (FeedbackInputLock.Own()) {
                    if (Input.GetKeyDown(KeyCode.Escape)) {
                        if (SettingsSearch.Query.Length > 0) { SettingsSearch.Query = ""; changed = true; }
                        else close = true;
                    }
                    string typed = Input.inputString;
                    if (!string.IsNullOrEmpty(typed)) {
                        string q = SettingsSearch.Query;
                        foreach (char ch in typed) {
                            if (ch == '\b') { if (q.Length > 0) q = q.Substring(0, q.Length - 1); }
                            else if (ch == '\n' || ch == '\r' || ch == 27) { }
                            else if (!char.IsControl(ch) && q.Length < 40) q += ch;
                        }
                        if (q != SettingsSearch.Query) { SettingsSearch.Query = q; changed = true; }
                    }
                }
                if (close) {
                    Unlock();
                    if (edit) SettingsSearchEdit.Menu?.Close(); else SettingsSearchView.Close();
                    return;
                }
                if (changed) { SettingsSearchEdit.Dirty = true; SettingsSearchView.Dirty = true; caretOn = true; caretBlink = 0f; }

                if (edit && SettingsSearchEdit.Dirty && Time.unscaledTime >= editRedrawAt) {
                    // a rebuild instantiates every row: at most ten a second while typing fast
                    editRedrawAt = Time.unscaledTime + 0.1f;
                    SettingsSearchEdit.Render();
                } else if (view && SettingsSearchView.Dirty) SettingsSearchView.Render();

                caretBlink += Time.unscaledDeltaTime;
                if (caretBlink > 0.5f) {
                    caretBlink = 0f; caretOn = !caretOn;
                    if (edit) SettingsSearchEdit.Caret(caretOn); else SettingsSearchView.Caret(caretOn);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] {e.Message}");
            }
        }

        public void OnDisable() => Unlock();

        [HideFromIl2Cpp]
        private void Unlock() {
            if (lockedByUs) { lockedByUs = false; FeedbackInputLock.Release(); }
        }

        [HideFromIl2Cpp]
        private static bool ChatOpen() {
            try { var hud = HudManager.Instance; return hud != null && hud.Chat != null && hud.Chat.IsOpenOrOpening; }
            catch { return false; }
        }

        /// <summary>Lobby menu entry / Ctrl+F: the open edit menu switches to its search tab, otherwise the view pane opens on it.</summary>
        [HideFromIl2Cpp]
        public void Toggle() {
            if (SettingsSearchEdit.Menu != null && SettingsSearchEdit.Menu.gameObject.activeInHierarchy && SettingsSearchEdit.Tab != null)
                SettingsSearchEdit.Show();
            else if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) StartCoroutine(OpenEdit().WrapToIl2Cpp());
            else SettingsSearchView.Open();
        }

        // host: the lobby's EDIT button, then (once the menu's Start has built TOR's tabs and ours) the search tab
        [HideFromIl2Cpp]
        private IEnumerator OpenEdit() {
            DiagOpenEdit();
            float until = Time.unscaledTime + 4f;     // the menu's Start builds every TOR row in one frame: give it time
            while (Time.unscaledTime < until) {
                yield return null;
                if (SettingsSearchEdit.Menu != null && SettingsSearchEdit.Menu.gameObject.activeInHierarchy && SettingsSearchEdit.Tab != null) {
                    SettingsSearchEdit.Show();
                    yield break;
                }
            }
            SettingsSearchView.Open();      // no edit menu came up: at least the view
        }

        [HideFromIl2Cpp]
        internal void Later(int frames, Action a) => StartCoroutine(LaterCo(frames, a).WrapToIl2Cpp());

        [HideFromIl2Cpp]
        private static IEnumerator LaterCo(int frames, Action a) {
            for (int i = 0; i < frames; i++) yield return null;
            a();
        }

        /// <summary>Autotest (UIGallery): type a query as if by keyboard.</summary>
        [HideFromIl2Cpp]
        public void DiagQuery(string q) {
            SettingsSearch.Query = q ?? "";
            SettingsSearchEdit.Dirty = true; SettingsSearchView.Dirty = true;
        }

        /// <summary>Autotest: opens the host's edit menu (the lobby's EDIT button) on the search tab.</summary>
        [HideFromIl2Cpp]
        public void DiagOpenEdit() {
            try {
                var info = LobbyInfoPane.Instance;
                info?.EditButton?.TryCast<PassiveButton>()?.OnClick?.Invoke();
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsSearch] diag edit: {e.Message}"); }
        }

        [HideFromIl2Cpp]
        public void Close() {
            Unlock();
            try { if (SettingsSearchEdit.Visible) SettingsSearchEdit.Menu?.Close(); } catch { }
            try { if (SettingsSearchView.Visible) SettingsSearchView.Close(); } catch { }
        }
    }
}
