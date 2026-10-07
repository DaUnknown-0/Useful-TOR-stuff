// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModSyncUI - the lobby tile and the mod sync panel.
 *
 * Built as a screen-space UGUI canvas from VanillaUI (lobby-pane card, EDIT-style buttons, the game's
 * corner tile). The world-space HudManager overlay pattern from UCHelpMenu/RoleControlUI is
 * deliberately NOT used here: the lobby camera runs an orthographic size the world-space fit would
 * have to special case. A canvas simply works in every scene.
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

        private GameObject panelRoot;      // the modal panel, created on demand
        private GameObject lobbyButton;    // the small always-visible lobby entry point
        private TMPro.TextMeshProUGUI badgeText;
        private Image badgeImage;
        private float nextPoll;

        public static bool ButtonShown { get; private set; }

        /*
         * The bottom-left corner: the mod sync tile while it is shown, then the lobby menu tile
         * (LobbyMenuUI), which holds every other lobby panel since 2026-10-05. Tiles are the size of
         * the game's own corner buttons and sit 10 apart.
         */
        public const float TileSize = 92f, LobbyRowY = 24f, LobbySlotStep = TileSize + 10f;

        public static Vector2 LobbySlot(int slot) =>
            new Vector2(24f + LobbySlotStep * ((ButtonShown ? 1 : 0) + slot), LobbyRowY);

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
            // The F1 settings overlay covers the whole screen and this button sits on top of its
            // text. Checked before the poll throttle below so it steps aside in the same frame F1 is
            // pressed instead of lingering for up to half a second.
            if (lobbyButton != null && lobbyButton.activeSelf && SettingsOverlayView.OverlayOpen()) {
                lobbyButton.SetActive(false);
                ButtonShown = false;
                return;
            }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            // Leaving the lobby (round start, back to menu) must take the panel with it - a modal
            // canvas at sortingOrder 9500 would otherwise sit on top of the running game.
            // LobbyScreen.Exists, never GameStartManager.Instance: the getter constructs a blank
            // instance when none exists (see LobbyScreen in LobbyLeakGuard.cs).
            if (panelRoot != null && !LobbyScreen.Exists) Close();

            bool shouldShow = ShouldShowLobbyButton();
            if (shouldShow && lobbyButton == null) BuildLobbyButton();
            ButtonShown = shouldShow && lobbyButton != null;
            if (lobbyButton == null) return;

            if (lobbyButton.activeSelf != shouldShow) lobbyButton.SetActive(shouldShow);
            if (shouldShow) UpdateBadge();
            if (shouldShow && !UTSModSync.NoticeShown) PostNotice();
        }

        // Once per lobby, a local chat line pointing at the tile: the tile alone sits in a
        // corner and was easy to miss (2026-09-23). Only this client sees it.
        [HideFromIl2Cpp]
        private static void PostNotice() {
            try {
                // Never through HudManager.Instance blindly: the getter constructs a blank one when
                // none exists (same trap as GameStartManager, see LobbyScreen).
                if (!DestroyableSingleton<HudManager>.InstanceExists) return;
                var hud = HudManager.Instance;
                if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
                UTSModSync.NoticeShown = true;
                hud.Chat.AddChat(PlayerControl.LocalPlayer,
                    UTSLocalization.Tr("uts.modsync.notice_chat", UTSModSync.ActionableCount()));
            } catch (Exception ex) {
                UTSModSync.NoticeShown = true;
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] chat notice failed: {ex.Message}");
            }
        }

        [HideFromIl2Cpp]
        private bool ShouldShowLobbyButton() {
            try {
                if (UsefulTORStuffPlugin.ModSyncEnabled != null && !UsefulTORStuffPlugin.ModSyncEnabled.Value)
                    return false;
                if (SettingsOverlayView.OverlayOpen()) return false;       // F1 overlay owns the screen
                if (!LobbyScreen.Exists) return false;                    // lobby screen only
                if (AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return false;          // the host syncs nothing
                if (!UTSModSync.HostReported) return false;               // host has no mod sync
                // After a download the button stays: it is the way back to the "restart needed" line
                // (audit 04.10.: it vanished with the last download).
                return UTSModSync.HasAnythingToShow() || UTSModSync.AnythingFetched;
            } catch { return false; }
        }

        // The badge on the tile: the number of mods to act on, "!" once a restart is due, nothing
        // when there is only information to see.
        [HideFromIl2Cpp]
        private void UpdateBadge() {
            if (badgeText == null || badgeImage == null) return;
            int n = UTSModSync.ActionableCount();
            string label = n > 0 ? n.ToString() : UTSModSync.AnythingFetched ? "!" : "";
            bool show = label.Length > 0;
            if (badgeImage.gameObject.activeSelf != show) badgeImage.gameObject.SetActive(show);
            badgeImage.color = n > 0 ? VanillaUI.Red : VanillaUI.Amber;
            VanillaUI.SetText(badgeText, label);
        }

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            try {
                // Below the Mod Manager (9999) so it can never cover a modal dialog.
                lobbyButton = VanillaUI.Canvas("UTSModSyncLobbyButton", 9000, false);
                var tile = VanillaUI.Tile(lobbyButton, TileSize, VanillaUI.GlyphDownload(), VanillaUI.Blue, Open);
                tile.GetComponent<RectTransform>().anchoredPosition = new Vector2(24f, LobbyRowY);

                // badge, top right of the tile
                var badge = new GameObject("Badge");
                badge.transform.SetParent(tile.transform, false);
                var brt = VanillaUI.Rect(badge, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0.5f, 0.5f));
                brt.anchoredPosition = new Vector2(-10f, -10f); brt.sizeDelta = new Vector2(30f, 30f);
                badgeImage = badge.AddComponent<Image>();
                badgeImage.sprite = VanillaUI.Circle();
                badgeImage.color = VanillaUI.Red;
                badgeImage.raycastTarget = false;
                badgeText = VanillaUI.Label(badge, "", 17f, Color.white, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                                            Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Center, TMPro.FontStyles.Bold);
                UpdateBadge();
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

        private const float Inset = 34f, RowH = 54f, RowStep = 62f;

        [HideFromIl2Cpp]
        private void Build() {
            panelRoot = VanillaUI.Canvas("UTSModSyncUI", 9500, true);
            VanillaUI.Backdrop(panelRoot, Close);

            var rows = UTSModSync.Rows();
            int visibleRows = 0;
            foreach (var r in rows) if (r.Action != SyncAction.None) visibleRows++;

            // The "unknown mods" note is a row of its own as far as the height is concerned.
            float height = Mathf.Clamp(310 + visibleRows * RowStep + (UTSModSync.HostUnknownCount > 0 ? 34 : 0), 360, 800);
            var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(960, height));

            float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.modsync.title"));
            VanillaUI.Subtitle(panel, UTSLocalization.Tr("uts.modsync.subtitle"), y - 6f, 44f);
            y -= 58f;

            foreach (var row in rows) {
                if (row.Action == SyncAction.None) continue;
                BuildRow(panel, row, y);
                y -= RowStep;
            }

            if (UTSModSync.HostUnknownCount > 0) {
                VanillaUI.Label(panel, UTSLocalization.Tr("uts.modsync.unknown_mods", UTSModSync.HostUnknownCount),
                      14, VanillaUI.Muted, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, y - 4), new Vector2(-2 * Inset, 34), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Italic);
                y -= 34;
            }

            // Footer status line above the buttons
            footerText = VanillaUI.Label(panel, FooterMessage(), 15, VanillaUI.Warn,
                               new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0),
                               new Vector2(0, VanillaUI.FrameW + 76), new Vector2(-2 * Inset, 60), TMPro.TextAlignmentOptions.Bottom);

            // Bulk + close
            var bulk = UTSModSync.BulkRows();
            bulkButton = VanillaUI.Button(panel, BulkLabel(bulk.Count), new Vector2(-150, VanillaUI.FrameW + 16),
                                    new Vector2(320, 48), VanillaUI.Teal, OnBulkClick, out bulkButtonText);
            if (bulk.Count == 0) bulkButton.SetActive(false);

            VanillaUI.Button(panel, UTSLocalization.Tr("uts.modsync.close"), new Vector2(170, VanillaUI.FrameW + 16),
                       new Vector2(240, 48), VanillaUI.Grey, Close);
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, SyncRow row, float y) {
            var holder = VanillaUI.Row(parent, y, RowH, Inset);
            holder.name = "Row" + row.Catalog.Id;

            VanillaUI.Label(holder, row.Catalog.DisplayName, 17, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(16, 0), new Vector2(300, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);

            var status = VanillaUI.Label(holder, "", 14, VanillaUI.Muted,
                               new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                               new Vector2(324, 0), new Vector2(350, 0), TMPro.TextAlignmentOptions.Left);

            var refs = new RowRefs { Row = row, Status = status };

            if (row.IsDownloadable) {
                var color = row.Action == SyncAction.Downgrade ? VanillaUI.Red
                          : row.NeedsConfirm ? VanillaUI.Amber : VanillaUI.Green;
                var captured = row;
                var btn = VanillaUI.Button(holder, RowButtonLabel(row), new Vector2(-10, 0), new Vector2(190, RowH - 12),
                                     color, () => OnRowClick(captured), out var btnText,
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
                refs.Status.color = VanillaUI.Blue;
                VanillaUI.SetText(refs.Status, UTSLocalization.Tr("uts.modsync.row_downloading", bar,
                                                      Mathf.RoundToInt(job.Progress * 100)));
                if (refs.Button != null) refs.Button.SetActive(false);
                return;
            }
            if (job != null && job.State == JobState.Failed) {
                refs.Status.color = VanillaUI.Bad;
                VanillaUI.SetText(refs.Status, UTSLocalization.Tr(job.ErrorKey ?? "uts.modsync.error_download"));
                if (refs.Button != null) refs.Button.SetActive(true);
                return;
            }
            if (row.Fetched || (job != null && job.State == JobState.Done)) {
                refs.Status.color = VanillaUI.Good;
                VanillaUI.SetText(refs.Status, UTSLocalization.Tr("uts.modsync.row_done"));
                if (refs.Button != null) refs.Button.SetActive(false);
                return;
            }

            string text;
            switch (row.Action) {
                case SyncAction.Install:
                    refs.Status.color = VanillaUI.Warn;
                    text = UTSLocalization.Tr("uts.modsync.row_missing", Ver(row.HostVersion));
                    break;
                case SyncAction.Upgrade:
                    refs.Status.color = VanillaUI.Warn;
                    text = UTSLocalization.Tr("uts.modsync.row_upgrade", Ver(row.LocalVersion), Ver(row.HostVersion));
                    break;
                case SyncAction.Downgrade:
                    refs.Status.color = VanillaUI.Bad;
                    text = UTSLocalization.Tr("uts.modsync.row_downgrade", Ver(row.LocalVersion), Ver(row.HostVersion));
                    break;
                case SyncAction.Enable:
                    refs.Status.color = VanillaUI.Warn;
                    text = UTSLocalization.Tr("uts.modsync.row_disabled");
                    break;
                case SyncAction.HostMissing:
                    refs.Status.color = VanillaUI.Muted;
                    text = UTSLocalization.Tr("uts.modsync.row_host_missing");
                    break;
                default:
                    refs.Status.color = VanillaUI.Good;
                    text = UTSLocalization.Tr("uts.modsync.row_ok", Ver(row.LocalVersion));
                    break;
            }

            // A prerelease target on a client that hides test versions gets said so explicitly - the
            // player should know they are about to install a test build, not just a "newer" one.
            if (row.IsDownloadable && row.NeedsConfirm && row.Action != SyncAction.Downgrade)
                text += "  " + UTSLocalization.Tr("uts.modsync.row_testbuild_note");
            VanillaUI.SetText(refs.Status, text);
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
            if (footerText != null) VanillaUI.SetText(footerText, FooterMessage());
            if (bulkButton != null) {
                var dl = UTSModDownloader.Instance;
                bool busy = dl != null && dl.IsRunning;
                int remaining = 0;
                foreach (var r in UTSModSync.BulkRows()) {
                    var job = FindJob(r.Catalog.Id);
                    if (job == null || job.State == JobState.Failed) remaining++;
                }
                bulkButton.SetActive(!busy && remaining > 0);
                if (bulkButtonText != null) VanillaUI.SetText(bulkButtonText, BulkLabel(remaining));
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator CoRefresh() {
            while (panelRoot != null) {
                RefreshAll();
                yield return new WaitForSeconds(0.2f);
            }
        }
    }
}
