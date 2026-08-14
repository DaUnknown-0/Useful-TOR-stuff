// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModSyncUI - the lobby button and the mod sync panel.
 *
 * Built as a screen-space UGUI canvas, the same construction ModManagerUI uses (shared solid-colour
 * sprite cache, dim overlay, panel, rows). The world-space HudManager overlay pattern from
 * UCHelpMenu/RoleControlUI is deliberately NOT used here: there is no HudManager in the lobby
 * screen, and the lobby camera runs an orthographic size the world-space fit would have to special
 * case. A canvas simply works in every scene.
 *
 * Everything rendered here comes from the local catalog or from integers off the wire. No string
 * received over the network ever reaches a label, so nothing can inject TMP rich-text tags and
 * forge a row (the same reason the catalog carries the display names).
 *
 * Bulk vs. single click is not cosmetic - it is rule enforcement:
 *   the bulk button runs UTSModSync.BulkRows(), which excludes downgrades and, on a client with
 *   test versions hidden, prerelease targets. Those rows keep their own button.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public class UTSModSyncUI : MonoBehaviour {
        public static UTSModSyncUI Instance { get; private set; }

        public UTSModSyncUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solidSprites = new Dictionary<Color, Sprite>();

        // Palette, kept close to the Mod Manager's so the two feel like one product.
        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color ColPanel = new Color(0.1f, 0.1f, 0.15f, 0.98f);
        private static readonly Color ColRow = new Color(0.16f, 0.16f, 0.22f, 0.95f);
        private static readonly Color ColAccent = new Color(0.3f, 0.7f, 1f);
        private static readonly Color ColGood = new Color(0.62f, 1f, 0.63f);
        private static readonly Color ColWarn = new Color(1f, 0.82f, 0.5f);
        private static readonly Color ColBad = new Color(1f, 0.45f, 0.45f);
        private static readonly Color ColMuted = new Color(0.65f, 0.65f, 0.7f);
        private static readonly Color ColBtnGreen = new Color(0.2f, 0.6f, 0.25f, 0.95f);
        private static readonly Color ColBtnAmber = new Color(0.6f, 0.45f, 0.15f, 0.95f);
        private static readonly Color ColBtnRed = new Color(0.55f, 0.22f, 0.22f, 0.95f);
        private static readonly Color ColBtnGrey = new Color(0.3f, 0.3f, 0.38f, 0.95f);

        private static Sprite Solid(Color color) {
            if (solidSprites.TryGetValue(color, out var cached) && cached != null) return cached;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex);
            DontDestroyOnLoad(sprite);
            solidSprites[color] = sprite;
            return sprite;
        }

        private GameObject panelRoot;      // the modal panel, created on demand
        private GameObject lobbyButton;    // the small always-visible lobby entry point
        private TMPro.TextMeshProUGUI lobbyButtonText;
        private float nextPoll;

        // Row label references so the polling coroutine can update progress without a rebuild.
        private class RowRefs {
            public SyncRow Row;
            public TMPro.TextMeshProUGUI Status;
            public GameObject Button;
            public TMPro.TextMeshProUGUI ButtonText;
        }
        private readonly List<RowRefs> rowRefs = new List<RowRefs>();
        private TMPro.TextMeshProUGUI footerText;
        private GameObject bulkButton;
        private TMPro.TextMeshProUGUI bulkButtonText;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        // ---- lobby entry point ----

        // public, like every other Unity message in this plugin (LobbyPasswordGate): the Il2Cpp
        // class injector registers these by reflection and public is the shape that is known to work.
        public void Update() {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            // Leaving the lobby (round start, back to menu) must take the panel with it - a modal
            // canvas at sortingOrder 9500 would otherwise sit on top of the running game.
            // LobbyScreen.Exists, never GameStartManager.Instance: the getter constructs a blank
            // instance when none exists (see LobbyScreen in LobbyLeakGuard.cs).
            if (panelRoot != null && !LobbyScreen.Exists) Close();

            bool shouldShow = ShouldShowLobbyButton();
            if (shouldShow && lobbyButton == null) BuildLobbyButton();
            if (lobbyButton == null) return;

            if (lobbyButton.activeSelf != shouldShow) lobbyButton.SetActive(shouldShow);
            if (shouldShow && lobbyButtonText != null) lobbyButtonText.text = LobbyButtonLabel();
        }

        [HideFromIl2Cpp]
        private bool ShouldShowLobbyButton() {
            try {
                if (UsefulTORStuffPlugin.ModSyncEnabled != null && !UsefulTORStuffPlugin.ModSyncEnabled.Value)
                    return false;
                if (!LobbyScreen.Exists) return false;                    // lobby screen only
                if (AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return false;          // the host syncs nothing
                if (!UTSModSync.HostReported) return false;               // host has no mod sync
                return UTSModSync.HasAnythingToShow();
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        private string LobbyButtonLabel() {
            int n = UTSModSync.ActionableCount();
            return n > 0
                ? UTSLocalization.Tr("uts.modsync.lobby_button", n)
                : UTSLocalization.Tr("uts.modsync.lobby_button_info");
        }

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            try {
                lobbyButton = new GameObject("UTSModSyncLobbyButton");
                DontDestroyOnLoad(lobbyButton);

                var canvas = lobbyButton.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // Below the Mod Manager (9999) so it can never cover a modal dialog.
                canvas.sortingOrder = 9000;
                var scaler = lobbyButton.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                lobbyButton.AddComponent<GraphicRaycaster>();

                var btn = new GameObject("Btn");
                btn.transform.SetParent(lobbyButton.transform, false);
                var rt = btn.AddComponent<RectTransform>();
                // Bottom-left, clear of TOR's own lobby texts (which anchor top-left).
                rt.anchorMin = new Vector2(0, 0);
                rt.anchorMax = new Vector2(0, 0);
                rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(28, 28);
                rt.sizeDelta = new Vector2(330, 46);
                btn.AddComponent<Image>().sprite = Solid(new Color(0.2f, 0.45f, 0.7f, 0.95f));

                var to = new GameObject("T");
                to.transform.SetParent(btn.transform, false);
                var trt = to.AddComponent<RectTransform>();
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.sizeDelta = Vector2.zero;
                lobbyButtonText = to.AddComponent<TMPro.TextMeshProUGUI>();
                lobbyButtonText.text = LobbyButtonLabel();
                lobbyButtonText.fontSize = 18;
                lobbyButtonText.fontStyle = TMPro.FontStyles.Bold;
                lobbyButtonText.alignment = TMPro.TextAlignmentOptions.Center;
                lobbyButtonText.color = Color.white;

                btn.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Open);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] lobby button failed: {ex.Message}");
                lobbyButton = null;
            }
        }

        // ---- panel ----

        [HideFromIl2Cpp]
        public void Open() {
            if (panelRoot != null) return;
            try { Build(); this.StartCoroutine(CoRefresh()); }
            catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ModSync] panel failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; }
            rowRefs.Clear();
            footerText = null; bulkButton = null; bulkButtonText = null;
        }

        [HideFromIl2Cpp]
        private void Build() {
            panelRoot = new GameObject("UTSModSyncUI");
            DontDestroyOnLoad(panelRoot);

            var canvas = panelRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9500;
            var scaler = panelRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var raycaster = panelRoot.AddComponent<GraphicRaycaster>();
            raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.All;

            // Dim backdrop, click to close.
            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(panelRoot.transform, false);
            var brt = backdrop.AddComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.sizeDelta = Vector2.zero;
            backdrop.AddComponent<Image>().sprite = Solid(ColBackdrop);
            backdrop.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Close);

            var rows = UTSModSync.Rows();
            int visibleRows = 0;
            foreach (var r in rows) if (r.Action != SyncAction.None) visibleRows++;

            // The "unknown mods" note is a row of its own as far as the height is concerned.
            float height = Mathf.Clamp(300 + visibleRows * 58 + (UTSModSync.HostUnknownCount > 0 ? 34 : 0),
                                       340, 760);

            var panel = new GameObject("Panel");
            panel.transform.SetParent(panelRoot.transform, false);
            var prt = panel.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(900, height);
            panel.AddComponent<Image>().sprite = Solid(ColPanel);

            Label(panel, UTSLocalization.Tr("uts.modsync.title"), 30, TMPro.FontStyles.Bold,
                  ColAccent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                  new Vector2(0, -18), new Vector2(-40, 42), TMPro.TextAlignmentOptions.Center);

            Label(panel, UTSLocalization.Tr("uts.modsync.subtitle"), 14, TMPro.FontStyles.Normal,
                  ColMuted, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                  new Vector2(0, -60), new Vector2(-60, 40), TMPro.TextAlignmentOptions.Top);

            // Rows
            float y = -110;
            foreach (var row in rows) {
                if (row.Action == SyncAction.None) continue;
                BuildRow(panel, row, y);
                y -= 58;
            }

            if (UTSModSync.HostUnknownCount > 0) {
                Label(panel, UTSLocalization.Tr("uts.modsync.unknown_mods", UTSModSync.HostUnknownCount),
                      14, TMPro.FontStyles.Italic, ColMuted,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, y - 6), new Vector2(-60, 34), TMPro.TextAlignmentOptions.Left);
                y -= 34;
            }

            // Footer status line
            footerText = Label(panel, FooterMessage(), 15, TMPro.FontStyles.Normal, ColWarn,
                               new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0),
                               new Vector2(0, 74), new Vector2(-60, 60), TMPro.TextAlignmentOptions.Bottom);

            // Bulk + close
            var bulk = UTSModSync.BulkRows();
            bulkButton = MakeButton(panel, BulkLabel(bulk.Count), new Vector2(-160, 20),
                                    new Vector2(300, 44), ColBtnGreen, OnBulkClick, out bulkButtonText);
            if (bulk.Count == 0) bulkButton.SetActive(false);

            MakeButton(panel, UTSLocalization.Tr("uts.modsync.close"), new Vector2(180, 20),
                       new Vector2(220, 44), ColBtnGrey, Close, out _);
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, SyncRow row, float y) {
            var holder = new GameObject("Row" + row.Catalog.Id);
            holder.transform.SetParent(parent.transform, false);
            var rt = holder.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-50, 50);
            holder.AddComponent<Image>().sprite = Solid(ColRow);

            Label(holder, row.Catalog.DisplayName, 17, TMPro.FontStyles.Bold, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(14, 0), new Vector2(300, 0), TMPro.TextAlignmentOptions.Left);

            var status = Label(holder, "", 14, TMPro.FontStyles.Normal, ColMuted,
                               new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                               new Vector2(322, 0), new Vector2(340, 0), TMPro.TextAlignmentOptions.Left);

            var refs = new RowRefs { Row = row, Status = status };

            if (row.IsDownloadable) {
                TMPro.TextMeshProUGUI btnText;
                var color = row.Action == SyncAction.Downgrade ? ColBtnRed
                          : row.NeedsConfirm ? ColBtnAmber : ColBtnGreen;
                var captured = row;
                var btn = MakeButton(holder, RowButtonLabel(row), new Vector2(-14, 0), new Vector2(190, 36),
                                     color, () => OnRowClick(captured), out btnText,
                                     anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                                     pivot: new Vector2(1, 0.5f));
                refs.Button = btn;
                refs.ButtonText = btnText;
            }

            rowRefs.Add(refs);
            UpdateRow(refs);
        }

        // ---- row text ----

        [HideFromIl2Cpp]
        private void UpdateRow(RowRefs refs) {
            if (refs?.Status == null) return;
            var row = refs.Row;
            var job = FindJob(row.Catalog.Id);

            if (job != null && job.State == JobState.Working) {
                int blocks = Mathf.Clamp(Mathf.CeilToInt(job.Progress * 10), 0, 10);
                string bar = new string('#', blocks) + new string('.', 10 - blocks);
                refs.Status.color = ColAccent;
                refs.Status.text = UTSLocalization.Tr("uts.modsync.row_downloading", bar,
                                                      Mathf.RoundToInt(job.Progress * 100));
                if (refs.Button != null) refs.Button.SetActive(false);
                return;
            }
            if (job != null && job.State == JobState.Failed) {
                refs.Status.color = ColBad;
                refs.Status.text = UTSLocalization.Tr(job.ErrorKey ?? "uts.modsync.error_download");
                if (refs.Button != null) refs.Button.SetActive(true);
                return;
            }
            if (row.Fetched || (job != null && job.State == JobState.Done)) {
                refs.Status.color = ColGood;
                refs.Status.text = UTSLocalization.Tr("uts.modsync.row_done");
                if (refs.Button != null) refs.Button.SetActive(false);
                return;
            }

            switch (row.Action) {
                case SyncAction.Install:
                    refs.Status.color = ColWarn;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_missing", Ver(row.HostVersion));
                    break;
                case SyncAction.Upgrade:
                    refs.Status.color = ColWarn;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_upgrade",
                                                          Ver(row.LocalVersion), Ver(row.HostVersion));
                    break;
                case SyncAction.Downgrade:
                    refs.Status.color = ColBad;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_downgrade",
                                                          Ver(row.LocalVersion), Ver(row.HostVersion));
                    break;
                case SyncAction.Enable:
                    refs.Status.color = ColWarn;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_disabled");
                    break;
                case SyncAction.HostMissing:
                    refs.Status.color = ColMuted;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_host_missing");
                    break;
                default:
                    refs.Status.color = ColGood;
                    refs.Status.text = UTSLocalization.Tr("uts.modsync.row_ok", Ver(row.LocalVersion));
                    break;
            }

            // A prerelease target on a client that hides test versions gets said so explicitly - the
            // player should know they are about to install a test build, not just a "newer" one.
            if (row.IsDownloadable && row.NeedsConfirm && row.Action != SyncAction.Downgrade)
                refs.Status.text += "  " + UTSLocalization.Tr("uts.modsync.row_testbuild_note");
        }

        // Always the FULL version, unlike VersionDisplay.Format: that one hides the 4th component
        // when test versions are switched off, which would render the upgrade 1.3.3.7 -> 1.3.3.8 as
        // "1.3.3 -> 1.3.3". Here the exact version is the whole point of the row.
        [HideFromIl2Cpp]
        private static string Ver(Version v) {
            if (v == null) return "?";
            return v.Revision > 0
                ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
                : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        [HideFromIl2Cpp]
        private string RowButtonLabel(SyncRow row) {
            switch (row.Action) {
                case SyncAction.Install:   return UTSLocalization.Tr("uts.modsync.btn_install");
                case SyncAction.Upgrade:   return UTSLocalization.Tr("uts.modsync.btn_upgrade");
                case SyncAction.Downgrade: return UTSLocalization.Tr("uts.modsync.btn_downgrade");
                default:                   return UTSLocalization.Tr("uts.modsync.btn_install");
            }
        }

        [HideFromIl2Cpp]
        private string BulkLabel(int count) =>
            UTSLocalization.Tr("uts.modsync.btn_bulk", count);

        [HideFromIl2Cpp]
        private string FooterMessage() {
            var dl = UTSModDownloader.Instance;
            if (dl != null && dl.IsRunning) return UTSLocalization.Tr("uts.modsync.footer_working");
            if (UTSModSync.AnythingFetched) return UTSLocalization.Tr("uts.modsync.footer_restart");
            return UTSLocalization.Tr("uts.modsync.footer_hint");
        }

        [HideFromIl2Cpp]
        private static SyncJob FindJob(byte catalogId) {
            var dl = UTSModDownloader.Instance;
            if (dl == null) return null;
            SyncJob newest = null;
            foreach (var j in dl.Jobs) if (j.Catalog.Id == catalogId) newest = j;
            return newest;
        }

        // ---- actions ----

        [HideFromIl2Cpp]
        private void OnRowClick(SyncRow row) {
            var dl = UTSModDownloader.Instance;
            if (dl == null || row == null) return;
            dl.Enqueue(row);
            RefreshAll();
        }

        [HideFromIl2Cpp]
        private void OnBulkClick() {
            var dl = UTSModDownloader.Instance;
            if (dl == null) return;
            dl.EnqueueAll(UTSModSync.BulkRows());
            RefreshAll();
        }

        [HideFromIl2Cpp]
        private void RefreshAll() {
            foreach (var r in rowRefs) UpdateRow(r);
            if (footerText != null) footerText.text = FooterMessage();
            if (bulkButton != null) {
                var dl = UTSModDownloader.Instance;
                bool busy = dl != null && dl.IsRunning;
                int remaining = 0;
                foreach (var r in UTSModSync.BulkRows()) {
                    var job = FindJob(r.Catalog.Id);
                    if (job == null || job.State == JobState.Failed) remaining++;
                }
                bulkButton.SetActive(!busy && remaining > 0);
                if (bulkButtonText != null) bulkButtonText.text = BulkLabel(remaining);
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator CoRefresh() {
            while (panelRoot != null) {
                RefreshAll();
                yield return new WaitForSeconds(0.2f);
            }
        }

        // ---- tiny UGUI helpers ----

        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size,
                TMPro.FontStyles style, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                Vector2 pos, Vector2 sizeDelta, TMPro.TextAlignmentOptions align) {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = color;
            t.alignment = align; t.enableWordWrapping = true;
            return t;
        }

        [HideFromIl2Cpp]
        private GameObject MakeButton(GameObject parent, string label, Vector2 pos, Vector2 size,
                Color color, Action onClick, out TMPro.TextMeshProUGUI textOut,
                Vector2? anchorMin = null, Vector2? anchorMax = null, Vector2? pivot = null) {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin ?? new Vector2(0.5f, 0);
            rt.anchorMax = anchorMax ?? new Vector2(0.5f, 0);
            rt.pivot = pivot ?? new Vector2(0.5f, 0);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            go.AddComponent<Image>().sprite = Solid(color);

            var to = new GameObject("T");
            to.transform.SetParent(go.transform, false);
            var trt = to.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.sizeDelta = Vector2.zero;
            var t = to.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = label; t.fontSize = 16; t.fontStyle = TMPro.FontStyles.Bold;
            t.alignment = TMPro.TextAlignmentOptions.Center; t.color = Color.white;
            textOut = t;

            go.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
            return go;
        }
    }
}
