// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Hat variants (2026-11-20, User: "if there are several variants of a skin, show only one when choosing and offer the
 * specific one in a menu after the click"). Mods register hats as variants of a group:
 *
 *   UsefulTORStuff.HatVariants.SetGroup(string hatProductId, string group, string label)
 *
 * (public static, meant to be called by reflection so the caller needs no hard dependency; group null or empty
 * removes the entry). In the hats tab every group of at least two hats inside one package shows ONE chip: the
 * variant worn right now, else the first one (TOR sorts by name). It carries a small "+N" mark; the other chips
 * are hidden and the grid of every package below closes up, package titles included. A click on the group chip
 * opens a small window in the game's look with all variants (picture and label); a pick equips like a normal
 * click (SelectHat + ClickEquip, so every ClickEquip prefix of other mods still decides), Esc or a click beside
 * the window closes it.
 *
 * Interplay:
 *   - TOR builds the tab (HatsTabPatches.OnEnablePrefix). This postfix runs at Priority.Normal, i.e. after
 *     StartupHats' TabBuilt (First) and before HatSearch (Low), so HatSearch records the grouped layout as the
 *     home positions and the shortened scroll bounds. Hidden variants sit on their group's slot.
 *   - With search text HatSearch shows every matching chip on its own, no grouping (SearchChanged hides the
 *     marks); when the text is cleared the group chip follows the worn hat again and HatSearch restores the
 *     homes with Hidden() deciding which chip of a group is visible.
 *   - StartupHats rebuilds an open tab when late hats arrive (OnEnable again): the layout is simply redone. An
 *     open variant window stays open; its pick is applied to the new chips.
 *   - The layout is only changed when this code reproduces TOR's own chip positions exactly; if not (another
 *     mod moved chips, TOR changed its grid) the tab stays untouched. Controller input keeps the full list (the
 *     window has no controller navigation).
 * Purely local UI: no RPC, no option.
 */

using System;
using System.Collections.Generic;
using AmongUs.Data;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {
    public static class HatVariants {
        internal const string BadgeName = "UTS_VariantBadge";

        private sealed class Entry { public string Group, Label; }
        private static readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

        /// <summary>
        /// Registers a hat (TOR product id, e.g. "hat_My_Hat") as a variant of a group; label is its name in the
        /// variant window (null: the hat name). group null or empty removes the hat from its group. Takes effect the
        /// next time the hats tab opens.
        /// </summary>
        public static void SetGroup(string hatProductId, string group, string label) {
            if (string.IsNullOrEmpty(hatProductId)) return;
            group = Clean(group);
            if (group == null) { entries.Remove(hatProductId); return; }
            entries[hatProductId] = new Entry { Group = group, Label = Clean(label) };
        }

        /// <summary>The group a hat was registered in, or null.</summary>
        public static string GroupOf(string hatProductId) =>
            hatProductId != null && entries.TryGetValue(hatProductId, out var e) ? e.Group : null;

        private static string Clean(string s) {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Length > 40) s = s.Substring(0, 40);
            foreach (char c in s) if (char.IsControl(c) || c == '<') return null;
            return s;
        }

        // ---- layout state of the open tab ----------------------------------------------------------
        private sealed class Group {
            public string Name;
            public readonly List<ColorChip> Chips = new();
            public readonly List<HatData> Hats = new();
            public readonly List<string> Labels = new();
            public ColorChip Shown;
            public Vector3 Slot;
            public TextMeshPro Badge;
        }

        private static HatsTab tab;
        private static readonly List<Group> groups = new();
        private static readonly Dictionary<IntPtr, Group> byChip = new();
        private static bool searching;
        private static bool warned;

        /// <summary>For HatSearch: a variant chip that stays hidden while no search text is set.</summary>
        internal static bool Hidden(ColorChip chip) =>
            !searching && chip != null && byChip.TryGetValue(chip.Pointer, out var g) && g.Shown != null && g.Shown.Pointer != chip.Pointer;

        /// <summary>For HatSearch, on every change of the search text.</summary>
        internal static void SearchChanged(bool on) {
            if (groups.Count == 0) { searching = on; return; }
            ClosePicker();
            searching = on;
            if (!on) FollowWorn();
            foreach (var g in groups) if (g.Badge != null) g.Badge.gameObject.SetActive(!on);
        }

        internal static bool PickerOpen => picker != null;

        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.OnEnable))]
        private static class Build {
            [HarmonyPriority(Priority.Normal)]
            private static void Postfix(HatsTab __instance) {
                try { Layout(__instance); }
                catch (Exception e) {
                    groups.Clear(); byChip.Clear();
                    UsefulTORStuffPlugin.Logger?.LogWarning("[UTS/HatVariants] layout failed: " + e.Message);
                }
            }
        }

        private static string Worn() {
            try { return DataManager.Player.Customization.Hat; } catch { return null; }
        }

        private static void Layout(HatsTab t) {
            tab = t;
            groups.Clear();
            byChip.Clear();
            searching = false;
            if (entries.Count == 0 || t == null || t.ColorChips == null || t.scroller == null) return;
            if (ActiveInputManager.currentControlType != ActiveInputManager.InputType.Keyboard) return;

            int count = t.ColorChips.Count;
            var chips = new List<ColorChip>(count);
            var hats = new List<HatData>(count);
            var pkgs = new List<string>(count);
            for (int i = 0; i < count; i++) {
                var chip = t.ColorChips[i];
                HatData hat = null;
                try { hat = chip != null ? chip.Tag?.TryCast<HatData>() : null; } catch { }
                if (chip == null || hat == null) return;   // not TOR's tab as expected: leave it alone
                chips.Add(chip); hats.Add(hat); pkgs.Add(StartupHats.PackageOf(hat));
            }

            // groups: same group name inside the same package, at least two hats
            var map = new Dictionary<string, Group>();
            for (int i = 0; i < count; i++) {
                if (!entries.TryGetValue(hats[i].ProductId ?? "", out var e)) continue;
                string key = pkgs[i] + "\n" + e.Group;
                if (!map.TryGetValue(key, out var g)) { g = new Group { Name = e.Group }; map[key] = g; groups.Add(g); }
                g.Chips.Add(chips[i]); g.Hats.Add(hats[i]); g.Labels.Add(e.Label ?? hats[i].name);
            }
            groups.RemoveAll(g => g.Chips.Count < 2);
            if (groups.Count == 0) return;
            string worn = Worn();
            foreach (var g in groups) {
                int k = g.Hats.FindIndex(h => h.ProductId == worn);
                g.Shown = g.Chips[k < 0 ? 0 : k];
                foreach (var c in g.Chips) byChip[c.Pointer] = g;
            }

            // package titles: TOR puts them at x 2.25 straight into the list
            var titles = new List<Transform>();
            for (int i = 0; i < t.scroller.Inner.childCount; i++) {
                var ch = t.scroller.Inner.GetChild(i);
                if (ch.GetComponent<ColorChip>() == null && ch.GetComponent<TextMeshPro>() != null && Mathf.Abs(ch.localPosition.x - 2.25f) < 0.05f)
                    titles.Add(ch);
            }
            bool titled = titles.Count > 0;

            // TOR's grid again (HatsTabPatches.CreateHatPackage), once as built and once without the hidden variants
            int per = Math.Max(2, t.NumPerRow);
            var newPos = new Vector3[count];
            var titleMoves = new List<(Transform T, float Y)>();
            float oldY = t.YStart, newY = t.YStart;
            int start = 0;
            while (start < count) {
                int end = start;
                while (end < count && pkgs[end] == pkgs[start]) end++;
                float f = pkgs[start].Length == 0 ? 1f : 1.5f;      // Innersloth's hats (no TOR extension) sit closer
                float oldOff = oldY - (titled ? 0.8f * t.YOffset : 0f);
                float newOff = newY - (titled ? 0.8f * t.YOffset : 0f);
                foreach (var tr in titles) if (Mathf.Abs(tr.localPosition.y - oldY) < 0.01f) titleMoves.Add((tr, newY));
                int shown = 0;
                for (int i = start; i < end; i++) {
                    int idx = i - start;
                    var expect = new Vector2(t.XRange.Lerp(idx % per / (per - 1f)), oldOff - idx / per * f * t.YOffset);
                    var have = chips[i].transform.localPosition;
                    if (Mathf.Abs(have.x - expect.x) > 0.01f || Mathf.Abs(have.y - expect.y) > 0.01f) {
                        if (!warned) { warned = true; UsefulTORStuffPlugin.Logger?.LogWarning("[UTS/HatVariants] hats tab layout differs from TOR's grid, variants are not grouped."); }
                        groups.Clear(); byChip.Clear();
                        return;
                    }
                    if (Hidden(chips[i])) continue;
                    newPos[i] = new Vector3(t.XRange.Lerp(shown % per / (per - 1f)), newOff - shown / per * f * t.YOffset, have.z);
                    shown++;
                }
                oldY = oldOff - (end - start - 1) / per * f * t.YOffset - 1.75f;
                newY = newOff - (Math.Max(1, shown) - 1) / per * f * t.YOffset - 1.75f;
                start = end;
            }

            // apply: visible chips to their new place, hidden variants onto their group's slot
            for (int i = 0; i < count; i++) if (!Hidden(chips[i])) chips[i].transform.localPosition = newPos[i];
            foreach (var g in groups) {
                var slot = g.Slot = g.Shown.transform.localPosition;
                foreach (var c in g.Chips) {
                    if (c.Pointer != g.Shown.Pointer) { c.transform.localPosition = slot; c.gameObject.SetActive(false); }
                    var chip = c;
                    try {
                        chip.Button.OnClick.RemoveAllListeners();
                        chip.Button.OnClick.AddListener((Action)(() => Clicked(chip)));
                    } catch { }
                }
                g.Badge = MakeBadge(t, g.Shown, "+" + (g.Chips.Count - 1));
            }
            foreach (var (tr, y) in titleMoves) tr.localPosition = new Vector3(tr.localPosition.x, y, tr.localPosition.z);
            t.scroller.ContentYBounds.max = -(newY + 4.1f);
        }

        private static TextMeshPro MakeBadge(HatsTab t, ColorChip chip, string text) {
            try {
                // a copy of the chip's own caption (TOR's text template: font and mask material of the list)
                TextMeshPro source = null;
                for (int i = 0; i < chip.transform.childCount && source == null; i++) source = chip.transform.GetChild(i).GetComponent<TextMeshPro>();
                if (source == null) return null;
                var badge = Object.Instantiate(source, chip.transform);
                badge.gameObject.name = BadgeName;
                var tr = badge.GetComponent<TextTranslatorTMP>();
                if (tr != null) Object.DestroyImmediate(tr);
                badge.transform.localPosition = new Vector3(0.36f, 0.36f, -1.5f);
                badge.transform.localScale = Vector3.one * 0.8f;
                badge.alignment = TextAlignmentOptions.Center;
                badge.enableAutoSizing = false;
                badge.enableWordWrapping = false;
                string rich = "<b><color=#FFD84A>" + text + "</color></b>";
                badge.text = rich;
                try { badge.outlineWidth = 0.25f; badge.outlineColor = new Color32(0, 0, 0, 255); } catch { }
                // same trick as TOR's captions: set the text for a moment so nothing that starts with the copy resets it
                t.StartCoroutine(Effects.Lerp(0.1f, new Action<float>(_ => { if (badge != null) badge.SetText(rich); })));
                return badge;
            } catch { return null; }
        }

        // worn hat changed (pick, search, other path): the group chip follows it
        private static void FollowWorn() {
            string worn = Worn();
            if (worn == null) return;
            foreach (var g in groups) {
                int k = g.Hats.FindIndex(h => h.ProductId == worn);
                if (k >= 0) Show(g, g.Chips[k]);
            }
        }

        private static void Show(Group g, ColorChip chip) {
            if (g.Shown == null || chip == null || g.Shown.Pointer == chip.Pointer) return;
            var old = g.Shown;
            g.Shown = chip;
            if (!searching) chip.transform.localPosition = g.Slot;   // while searching HatSearch places the chips
            if (!searching) { old.gameObject.SetActive(false); chip.gameObject.SetActive(true); }
            if (g.Badge != null) {
                var lp = g.Badge.transform.localPosition;
                g.Badge.transform.SetParent(chip.transform, false);
                g.Badge.transform.localPosition = lp;
            }
        }

        private static void Clicked(ColorChip chip) {
            try {
                if (tab == null || chip == null) return;
                if (searching || !byChip.TryGetValue(chip.Pointer, out var g)) {
                    var hat = chip.Tag?.TryCast<HatData>();
                    if (hat != null) tab.SelectHat(hat);
                    tab.ClickEquip();
                    return;
                }
                OpenPicker(g);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning("[UTS/HatVariants] click failed: " + e.Message); }
        }

        // ---- the variant window ----------------------------------------------------------------------
        private static GameObject picker;
        private static int escFrame = -1;

        private static void ClosePicker() {
            if (picker != null) Object.Destroy(picker);
            picker = null;
        }

        private static void OpenPicker(Group g) {
            ClosePicker();
            const float tileW = 200f, tileH = 240f, gap = 18f, pad = 40f, head = 104f;
            int n = g.Hats.Count, cols = Math.Min(4, n), rows = (n + cols - 1) / cols;
            var size = new Vector2(Math.Max(480f, cols * tileW + (cols - 1) * gap + 2 * pad), head + rows * tileH + (rows - 1) * gap + pad);

            var root = VanillaUI.Canvas("UTS_HatVariants", 620, true);
            picker = root;
            var back = VanillaUI.Backdrop(root, ClosePicker);
            try { back.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f); } catch { }
            var panel = VanillaUI.CenterPanel(root, size);

            var title = GameUI.Text(panel, g.Name, GameUI.Brook, 42f, Color.white, TextAlignmentOptions.Left, FontStyles.UpperCase);
            title.enableAutoSizing = true; title.fontSizeMin = 20f; title.fontSizeMax = 42f;
            Place(title.gameObject, new Vector2(pad, -26f), new Vector2(size.x - 2 * pad - 70f, 56f));
            var close = GameUI.Close(panel, ClosePicker);
            try { var crt = close.GetComponent<RectTransform>(); crt.anchoredPosition = new Vector2(-34f, -34f); crt.sizeDelta = new Vector2(56f, 56f); } catch { }

            string worn = Worn();
            float rowW = cols * tileW + (cols - 1) * gap, x0 = (size.x - rowW) / 2f;
            for (int i = 0; i < n; i++) {
                int col = i % cols, row = i / cols;
                var hat = g.Hats[i];
                bool locked = false;
                try { var b = g.Chips[i].Button; locked = b == null || !b.enabled; } catch { }
                Tile(panel, new Vector2(x0 + col * (tileW + gap), -(head + row * (tileH + gap))), new Vector2(tileW, tileH),
                     hat, g.Labels[i], hat.ProductId == worn, locked);
            }
        }

        private static void Place(GameObject go, Vector2 topLeft, Vector2 size) {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeft; rt.sizeDelta = size;
        }

        private static void Tile(GameObject panel, Vector2 topLeft, Vector2 size, HatData hat, string label, bool worn, bool locked) {
            var tile = new GameObject("Variant");
            tile.transform.SetParent(panel.transform, false);
            tile.AddComponent<RectTransform>();
            Place(tile, topLeft, size);
            var img = tile.AddComponent<Image>();
            var box = GameUI.Sprite("BUTTON-CheckboxInactive");
            img.sprite = box ?? VanillaUI.RoundedSprite(VanillaUI.RadiusBox);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = box != null ? 2.2f : 1f;
            img.color = box != null ? (worn ? VanillaUI.Good : Color.white) : (worn ? VanillaUI.FieldLight : VanillaUI.Field);
            img.raycastTarget = true;

            var pic = new GameObject("Preview");
            pic.transform.SetParent(tile.transform, false);
            var prt = VanillaUI.Stretch(pic);
            prt.offsetMin = new Vector2(16f, 54f); prt.offsetMax = new Vector2(-16f, -14f);
            var pimg = pic.AddComponent<Image>();
            pimg.raycastTarget = false;
            var sprite = StartupHats.PreviewOf(hat);
            if (sprite != null) { pimg.sprite = sprite; pimg.preserveAspect = true; if (locked) pimg.color = GameUI.Dimmed; }
            else pimg.enabled = false;

            var name = GameUI.Text(tile, label, GameUI.BarlowSemi, 24f, locked ? GameUI.Dimmed : (worn ? VanillaUI.Good : Color.white), TextAlignmentOptions.Center);
            name.enableAutoSizing = true; name.fontSizeMin = 12f; name.fontSizeMax = 24f;
            var nrt = name.GetComponent<RectTransform>();
            nrt.anchorMin = new Vector2(0f, 0f); nrt.anchorMax = new Vector2(1f, 0f); nrt.pivot = new Vector2(0.5f, 0f);
            nrt.anchoredPosition = new Vector2(0f, 10f); nrt.sizeDelta = new Vector2(-24f, 40f);

            if (!locked) VanillaUI.Hover(tile, VanillaUI.RadiusBox, () => Choose(hat));
        }

        private static void Choose(HatData hat) {
            var t = tab;
            ClosePicker();
            if (t == null || hat == null) return;
            try { t.SelectHat(hat); t.ClickEquip(); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning("[UTS/HatVariants] equip failed: " + e.Message); }
            if (Worn() == hat.ProductId) FollowWorn();
        }

        // Esc closes the window (and only the window: the wardrobe must not close on the same key press)
        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.Update))]
        private static class Tick {
            private static void Postfix(HatsTab __instance) {
                if (picker == null || __instance != tab) return;
                try {
                    // the canvas stays alive until the end of the frame (Destroy), so ModalGuard still swallows the
                    // game's handling of this Esc
                    if (Input.GetKeyDown(KeyCode.Escape)) { escFrame = Time.frameCount; ClosePicker(); }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerCustomizationMenu), nameof(PlayerCustomizationMenu.Close))]
        private static class KeepWardrobe {
            private static bool Prefix() {
                try { if ((picker != null || escFrame == Time.frameCount) && Input.GetKeyDown(KeyCode.Escape)) { escFrame = Time.frameCount; ClosePicker(); return false; } } catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(InventoryTab), nameof(InventoryTab.OnDisable))]
        private static class TabClosed {
            private static void Postfix(InventoryTab __instance) {
                try { if (picker != null && (tab == null || __instance == null || __instance.Pointer == tab.Pointer)) ClosePicker(); } catch { }
            }
        }
    }
}
