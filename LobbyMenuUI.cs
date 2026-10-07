// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * LobbyMenuUI - one tile in the bottom-left lobby corner that opens a menu with every lobby panel.
 *
 * Until 2026-10-05 each panel (newcomer shield, early-death shield, session stats, replay, UC's colour
 * grant) brought its own wide button, and the row along the bottom edge ran across half the screen.
 * Now the panels register an entry here (label, visibility, click) and only this tile sits in the
 * corner. The mod sync tile stays on its own: it is a warning for guests, and its chat notice points
 * at it.
 *
 * Look (VanillaUI): the tile is the game's own square corner button with a three-bar glyph; the menu
 * is a lobby-pane card above it with one EDIT-style button per entry.
 *
 * Other mods: UTS publishes LobbyMenu.Add under AddKey as an
 * Action<string, int, Func<bool>, Func<string>, Action, Color> (id, order, visible, label, click,
 * colour). UC's colour grant uses it and falls back to its own button without UTS. For a UC from
 * before the menu, NextFreeX still points right of this tile so its button does not cover it.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class LobbyMenu {
        public const string AddKey = "UTS.LobbyMenu.Add";
        public const string NextFreeXKey = "UTS.LobbyButtons.NextFreeX";

        internal sealed class Entry {
            public string Id;
            public int Order;
            public Func<bool> Visible;
            public Func<string> Label;
            public Action Click;
            public Color Color;
        }

        internal static readonly List<Entry> Entries = new List<Entry>();

        /// <summary>Adds or replaces (same id) a menu entry. Lower order comes first.</summary>
        public static void Add(string id, int order, Func<bool> visible, Func<string> label, Action click, Color color) {
            if (string.IsNullOrEmpty(id) || label == null || click == null) return;
            Entries.RemoveAll(e => e.Id == id);
            Entries.Add(new Entry { Id = id, Order = order, Visible = visible, Label = label, Click = click, Color = color });
            Entries.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        internal static void Publish() {
            try {
                AppDomain.CurrentDomain.SetData(AddKey,
                    new Action<string, int, Func<bool>, Func<string>, Action, Color>(Add));
            } catch { }
        }

        internal static bool IsVisible(Entry e) {
            if (UIGallery.Active) return true;
            try { return e.Visible == null || e.Visible(); } catch { return false; }
        }

        internal static string LabelOf(Entry e) {
            try { return e.Label() ?? ""; } catch { return ""; }
        }
    }

    public class LobbyMenuUI : MonoBehaviour {
        public static LobbyMenuUI Instance { get; private set; }

        public LobbyMenuUI(IntPtr ptr) : base(ptr) { }

        private const float Gap = 12f, RowW = 380f, RowH = 54f, RowStep = 64f, Pad = 22f;

        private GameObject buttonRoot;
        private RectTransform buttonRect;
        private GameObject menuRoot;
        private RectTransform cardRect;
        private float openedAt;
        private float nextPoll;
        private string shownKey = "";
        private readonly List<(LobbyMenu.Entry Entry, TMPro.TextMeshProUGUI Text)> rows =
            new List<(LobbyMenu.Entry, TMPro.TextMeshProUGUI)>();

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            LobbyMenu.Publish();
        }

        public void Update() {
            bool overlay = SettingsOverlayView.OverlayOpen();
            if (overlay) {
                if (buttonRoot != null && buttonRoot.activeSelf) buttonRoot.SetActive(false);
                Close();
                return;
            }
            if (menuRoot != null) {
                Animate();
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            var visible = LobbyScreen.Exists
                ? LobbyMenu.Entries.Where(LobbyMenu.IsVisible).ToList()
                : new List<LobbyMenu.Entry>();
            bool show = visible.Count > 0;

            if (show && buttonRoot == null) BuildButton();
            Vector2 pos = UTSModSyncUI.LobbySlot(0);
            if (buttonRoot != null) {
                if (buttonRoot.activeSelf != show) buttonRoot.SetActive(show);
                if (buttonRect != null) buttonRect.anchoredPosition = pos;
            }
            // a UC from before the menu places its own button here: right of ours
            try { AppDomain.CurrentDomain.SetData(LobbyMenu.NextFreeXKey, pos.x + (show ? UTSModSyncUI.TileSize + 8f : 0f)); } catch { }

            if (menuRoot == null) return;
            if (!show) { Close(); return; }
            string key = string.Join("|", visible.Select(e => e.Id));
            if (key != shownKey) { Close(); Open(); return; }
            foreach (var (entry, text) in rows)
                if (text != null) VanillaUI.SetText(text, LobbyMenu.LabelOf(entry));
            if (cardRect != null) cardRect.anchoredPosition = CardPos();
        }

        [HideFromIl2Cpp]
        public void Toggle() {
            if (menuRoot != null) Close();
            else Open();
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (menuRoot != null) { Destroy(menuRoot); menuRoot = null; }
            cardRect = null;
            rows.Clear();
            shownKey = "";
        }

        // ====================================================================
        // The corner tile
        // ====================================================================
        [HideFromIl2Cpp]
        private void BuildButton() {
            try {
                buttonRoot = VanillaUI.Canvas("UTSLobbyMenuButton", 9000, false);
                var tile = VanillaUI.Tile(buttonRoot, UTSModSyncUI.TileSize, VanillaUI.GlyphMenu(), new Color(0.9f, 0.92f, 0.94f), Toggle);
                buttonRect = tile.GetComponent<RectTransform>();
                buttonRect.anchoredPosition = UTSModSyncUI.LobbySlot(0);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyMenu] button failed: {ex.Message}");
                if (buttonRoot != null) Destroy(buttonRoot);
                buttonRoot = null; buttonRect = null;
            }
        }

        // ====================================================================
        // The menu: a card above the tile, one button per entry
        // ====================================================================
        [HideFromIl2Cpp]
        private Vector2 CardPos() {
            Vector2 b = buttonRect != null ? buttonRect.anchoredPosition : UTSModSyncUI.LobbySlot(0);
            return new Vector2(b.x, b.y + UTSModSyncUI.TileSize + Gap);
        }

        [HideFromIl2Cpp]
        private void Open() {
            var visible = LobbyMenu.Entries.Where(LobbyMenu.IsVisible).ToList();
            if (visible.Count == 0) return;
            try {
                menuRoot = VanillaUI.Canvas("UTSLobbyMenu", 9400, true);

                // anywhere outside the card closes the menu
                var catcher = new GameObject("Outside");
                catcher.transform.SetParent(menuRoot.transform, false);
                VanillaUI.Stretch(catcher);
                catcher.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
                catcher.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Close);

                float head = 16f + 30f + 18f + 2f;   // Title(): top + size + rule
                float h = VanillaUI.FrameW + head + 14f + visible.Count * RowStep - (RowStep - RowH) + Pad + VanillaUI.FrameW;
                float w = RowW + 2 * (Pad + VanillaUI.FrameW);

                var card = VanillaUI.Panel(menuRoot, new Vector2(w, h), Vector2.zero, Vector2.zero, CardPos());
                cardRect = card.GetComponent<RectTransform>();
                float y = VanillaUI.Title(card, UTSLocalization.Tr("uts.lobbymenu.title"), 16f, 30f) - 14f;

                foreach (var e in visible) {
                    BuildRow(card, e, y);
                    y -= RowStep;
                }
                shownKey = string.Join("|", visible.Select(e => e.Id));
                openedAt = Time.unscaledTime;
                Animate();
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyMenu] menu failed: {ex.Message}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject card, LobbyMenu.Entry e, float y) {
            var click = e.Click;
            var btn = VanillaUI.Button(card, LobbyMenu.LabelOf(e), new Vector2(0, y), new Vector2(RowW, RowH), Saturated(e.Color),
                () => {
                    Close();
                    try { click(); }
                    catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyMenu] {e.Id} failed: {ex.Message}"); }
                }, out var text, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1));
            btn.name = "Row_" + e.Id;
            rows.Add((e, text));
        }

        // a short pop: 0.88 -> 1 with a little overshoot, from the tile's corner
        [HideFromIl2Cpp]
        private void Animate() {
            if (cardRect == null) return;
            float t = Mathf.Clamp01((Time.unscaledTime - openedAt) / 0.14f);
            float s = t >= 1f ? 1f : 0.88f + 0.12f * (1f + 2.2f * Mathf.Pow(t - 1f, 3) + 1.2f * Mathf.Pow(t - 1f, 2));
            cardRect.localScale = new Vector3(s, s, 1f);
        }

        // the entries' colours, lifted towards the game's bright flat buttons
        private static Color Saturated(Color c) {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            var r = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.1f), Mathf.Clamp(v * 1.45f, 0.55f, 0.92f));
            r.a = 1f;
            return r;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => Instance?.Close();
        }
    }
}
