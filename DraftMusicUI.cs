// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * DraftMusicUI - the panel for the player's own music, in five tabs: Role Draft (DraftMusic), menu music
 * (MenuMusic), lobby music (LobbyMusic), meetings (MeetingMusic) and Unknown's Collection's reactor music
 * (ReactorTrack). Every tab has its own choice and its own playlist (MusicSlot); the track list is the
 * same: the base row (TOR's draft track, the game's theme, "off" for meetings, the default reactor score),
 * random, the playlist, the kinds of built-in music (MusicLibrary) and up to twelve own .ogg/.mp3/.wav files
 * from BepInEx\draft-music. Each entry can be listened to (music channel, until stopped or the panel closes),
 * chosen, and put on the tab's playlist. Clicking a kind opens its versions: switch one or several on (one of
 * them plays at random), listen, delete one, download it again. "Load music" downloads every version that is
 * neither here nor deleted; nothing is downloaded without the player asking. Reached through the lobby menu and, outside a lobby, through the
 * "Music" button in TOR's Mod Options (MusicOptionsButton); every player, because the choice is local.
 *
 * Autotest: config "Diagnostics / DraftMusic Test" first checks the music library in the main menu (Mod Manager
 * Music tab, Load music, delete one version, Load music leaves it out, its own download brings it back), then
 * starts freeplay on The Skeld, opens the panel and the versions of a kind, previews a track and every own file,
 * then starts TOR's draft track under a simulated draft and logs whether the chosen clip took over
 * and whether TOR's own stop ended it. Shots land in <game>/UTSShots/draftmusic_*.png.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public class DraftMusicUI : MonoBehaviour {
        public static DraftMusicUI Instance { get; private set; }

        public DraftMusicUI(IntPtr ptr) : base(ptr) { }

        private GameObject panelRoot;
        private int shownVersion = -1;
        private float nextPoll;

        internal static bool DiagActive;

        // 0 = Role Draft, 1 = menu, 2 = lobby, 3 = meetings, 4 = reactor
        internal static int Tab;
        // the track list is paged: the three fixed rows (base, random, playlist) always, then PageSize tracks
        internal static int Page;
        // the kind whose versions are shown instead of the list (null: the list)
        internal static string OpenKind;
        private const int PageSize = 12;
        private static MusicSlot SlotOf(int tab) => tab switch {
            1 => MenuMusic.Slot, 2 => LobbyMusic.Slot, 3 => MeetingMusic.Slot, 4 => ReactorTrack.Slot, _ => DraftMusic.Slot
        };

        /// Opened from TOR's Mod Options outside a lobby: the panel (and its previews) may live there.
        internal static bool OpenOutsideLobby { get; private set; }
        private static Action onClosed;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            DraftMusic.PreviewLevel = () => SlotOf(Tab).Level;      // a preview plays at the open tab's level
            LobbyMenu.Add("uts.draftmusic", 50, () => LobbyScreen.Exists,
                          () => UTSLocalization.Tr("uts.music.lobby_button"),
                          () => Instance?.Toggle(), VanillaUI.Pink);
        }

        public void Update() {
            if (panelRoot == null) { if (blocked.Count > 0) UnblockBackground(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            VanillaSlider.Tick();
            // redraw when a clip got ready, failed, or the preview changed (never under a held slider)
            if (DraftMusic.Version != shownVersion && !VanillaSlider.Dragging) { Rebuild(); return; }
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (!LobbyScreen.Exists && !DiagActive && !OpenOutsideLobby) Close();
        }

        [HideFromIl2Cpp]
        public void Toggle() {
            if (panelRoot != null) Close();
            else Open();
        }

        [HideFromIl2Cpp]
        public void Close() {
            DraftMusic.StopPreview();
            Teardown();
            UnblockBackground();
            OpenOutsideLobby = false;
            var after = onClosed;
            onClosed = null;
            try { after?.Invoke(); } catch { }
        }

        /// From TOR's Mod Options: anywhere, and back to that popup when closed.
        [HideFromIl2Cpp]
        public void OpenFromOptions(Action whenClosed) {
            if (panelRoot != null) Teardown();
            OpenOutsideLobby = true;
            onClosed = whenClosed;
            Open();
        }

        private void Teardown() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; LobbyPanelGuard.Closed(); }
        }

        [HideFromIl2Cpp]
        private void Rebuild() {
            Teardown();
            Build();
        }

        [HideFromIl2Cpp]
        public void Open() {
            NewcomerShieldUI.Instance?.Close();
            EarlyDeathShieldUI.Instance?.Close();
            DraftMusic.Refresh();
            Build();
            BlockBackground();
        }

        // The backdrop swallows UGUI clicks only; the game's own buttons (main menu, options, lobby, HUD) are
        // collider-based PassiveButtons and kept reacting under the panel (user, 15.10., opened through the
        // settings). Same remedy as the Mod Manager: switch them off while the panel is open, and switch back
        // on only those we switched off.
        private static readonly List<PassiveButton> blocked = new List<PassiveButton>();

        [HideFromIl2Cpp]
        private void BlockBackground() {
            if (blocked.Count > 0 || panelRoot == null) return;
            try {
                foreach (var b in FindObjectsOfType<PassiveButton>())
                    if (b != null && b.enabled && !b.transform.IsChildOf(panelRoot.transform)) { b.enabled = false; blocked.Add(b); }
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[DraftMusicUI] background block: {e.Message}"); }
        }

        [HideFromIl2Cpp]
        private static void UnblockBackground() {
            foreach (var b in blocked) try { if (b != null) b.enabled = true; } catch { }
            blocked.Clear();
        }

        [HideFromIl2Cpp]
        private void ShowTab(int tab) {
            if (Tab == tab) return;
            Tab = tab;
            Page = 0;
            OpenKind = null;
            DraftMusic.Changed();          // redraw
        }

        private const float HeadH = 246f, FooterH = 100f, MaxPanelH = 1040f, Inset = 34f, Width = 1080f;
        private const float ButtonW = 136f, ButtonGap = 10f;

        private struct Choice {
            public string Key, Name, Style;
            public bool CanListen, CanList;   // CanList: a track that can go on the playlist
            public bool IsKind;               // a kind of built-in music: the row opens its versions
            public bool Missing;              // a kind with no version on this computer
        }

        [HideFromIl2Cpp]
        private List<Choice> Choices(MusicSlot slot) {
            var list = new List<Choice>();
            if (Tab == 0)
                list.Add(new Choice { Key = DraftMusic.Tor, Name = UTSLocalization.Tr("uts.draftmusic.original"),
                                      Style = UTSLocalization.Tr("uts.draftmusic.original_style"), CanListen = DraftMusic.TorClip() != null,
                                      CanList = true });
            else if (Tab == 1)
                list.Add(new Choice { Key = MenuMusic.Original, Name = UTSLocalization.Tr("uts.draftmusic.original"),
                                      Style = UTSLocalization.Tr("uts.music.menu_original_style"), CanList = true });
            else if (Tab == 2)
                list.Add(new Choice { Key = LobbyMusic.Original, Name = UTSLocalization.Tr("uts.draftmusic.original"),
                                      Style = UTSLocalization.Tr("uts.music.lobby_original_style"), CanList = true });
            else if (Tab == 3)
                list.Add(new Choice { Key = MeetingMusic.Off, Name = UTSLocalization.Tr("uts.music.off"),
                                      Style = UTSLocalization.Tr("uts.music.off_style") });
            else
                list.Add(new Choice { Key = ReactorTrack.Default, Name = UTSLocalization.Tr("uts.music.reactor_default"),
                                      Style = UTSLocalization.Tr("uts.music.reactor_default_style"), CanList = true });
            list.Add(new Choice { Key = DraftMusic.Random, Name = UTSLocalization.Tr("uts.draftmusic.random"),
                                  Style = UTSLocalization.Tr(Tab == 0 ? "uts.draftmusic.random_style" : "uts.music.random_style") });
            int n = slot.PlaylistCount;
            list.Add(new Choice { Key = DraftMusic.Playlist, Name = UTSLocalization.Tr("uts.draftmusic.playlist"),
                                  Style = n > 0 ? UTSLocalization.Tr("uts.draftmusic.playlist_style", n)
                                                : UTSLocalization.Tr("uts.draftmusic.playlist_empty") });
            foreach (var k in MusicLibrary.Kinds) list.Add(KindChoice(k));
            foreach (var tr in DraftMusic.Files()) list.Add(new Choice { Key = tr.Key, Name = tr.Title, Style = UTSLocalization.Tr("uts.draftmusic.own_file"), CanListen = true, CanList = true });
            // tracks other plugins added for this tab (UTS.Music.AddTrack)
            foreach (var tr in DraftMusic.ExternalFor(slot.Name))
                list.Add(new Choice { Key = tr.Key, Name = tr.Title,
                                      Style = string.IsNullOrEmpty(tr.Style) ? UTSLocalization.Tr("uts.music.ext_style") : tr.Style,
                                      CanListen = true, CanList = true });
            return list;
        }

        // A kind's row: its name, and what plays: the one switched-on version, or how many are on.
        [HideFromIl2Cpp]
        private static Choice KindChoice(MusicLibrary.Kind k) {
            bool here = MusicLibrary.ReadyCount(k) > 0;
            string style;
            if (!here) style = k.Versions.Any(v => MusicLibrary.StateOf(v) is MusicLibrary.Dl.Queued or MusicLibrary.Dl.Running)
                                   ? UTSLocalization.Tr("uts.music.downloading_short") : UTSLocalization.Tr("uts.music.not_loaded");
            else {
                var on = k.Versions.Where(MusicLibrary.IsOn).ToList();
                style = on.Count == 1 ? on[0].Title : UTSLocalization.Tr("uts.music.n_versions", on.Count, k.Versions.Length);
            }
            return new Choice { Key = k.Key, Name = k.Style + (k.Versions.Length > 1 ? $" ({k.Versions.Length})" : ""), Style = style,
                                CanListen = MusicLibrary.Playable(k), CanList = true, IsKind = true, Missing = !here };
        }

        [HideFromIl2Cpp]
        private void Build() {
            try {
                var slot = SlotOf(Tab);
                var openKind = MusicLibrary.KindOf(OpenKind);
                if (openKind == null) OpenKind = null;
                var all = Choices(slot);
                int tracks = all.Count - 3, pages = Mathf.Max(1, (tracks + PageSize - 1) / PageSize);
                Page = Mathf.Clamp(Page, 0, pages - 1);
                var choices = all.GetRange(0, 3);
                choices.AddRange(all.GetRange(3 + Page * PageSize, Mathf.Min(PageSize, tracks - Page * PageSize)));

                panelRoot = VanillaUI.Canvas("UTSDraftMusicUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, Close);

                // the panel keeps the height of a full page, so it does not jump while paging
                int rows = 3 + Mathf.Min(PageSize, tracks);
                float step = Mathf.Clamp((MaxPanelH - HeadH - FooterH) / rows, 40f, 56f);
                float height = HeadH + rows * step + FooterH;
                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(Width, height));
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.music.title"), 16f, 34f, VanillaUI.Pink);
                string sub = Tab switch {
                    1 => "uts.music.sub_menu", 2 => "uts.music.sub_lobby", 3 => "uts.music.sub_meeting",
                    4 => "uts.music.sub_reactor", _ => "uts.draftmusic.subtitle"
                };
                VanillaUI.Subtitle(panel, openKind != null ? UTSLocalization.Tr("uts.music.versions_sub", openKind.Style) : UTSLocalization.Tr(sub),
                                   y - 6f, 40f);
                VanillaUI.CloseButton(panel, "", Close);

                // tabs
                string[] tabKeys = { "uts.music.tab_draft", "uts.music.tab_menu", "uts.music.tab_lobby", "uts.music.tab_meeting",
                                     "uts.music.tab_reactor" };
                float tabW = (Width - 2 * Inset - (tabKeys.Length - 1) * 10f) / tabKeys.Length;
                for (int k = 0; k < tabKeys.Length; k++) {
                    int kk = k;
                    VanillaUI.Button(panel, UTSLocalization.Tr(tabKeys[k]), new Vector2(Inset + k * (tabW + 10f), -122f), new Vector2(tabW, 46f),
                                     k == Tab ? VanillaUI.TealBright : VanillaUI.Grey, () => ShowTab(kk),
                                     anchorMin: new Vector2(0, 1), anchorMax: new Vector2(0, 1), pivot: new Vector2(0, 1));
                }
                BuildVolume(panel, slot);
                y = -HeadH;

                if (openKind != null) BuildVersions(panel, openKind, y, step - 6f, step);
                else foreach (var c in choices) {
                    BuildRow(panel, slot, c, y, step - 6f);
                    y -= step;
                }

                // footer: load the built-in music (only on request), the folder for own files
                BuildLoadButton(panel);
                VanillaUI.Button(panel, UTSLocalization.Tr("uts.draftmusic.folder"), new Vector2(Inset + 282f, 28f), new Vector2(220f, 50f),
                                 VanillaUI.Blue, DraftMusic.OpenFolder,
                                 anchorMin: new Vector2(0, 0), anchorMax: new Vector2(0, 0), pivot: new Vector2(0, 0));
                VanillaUI.Label(panel, UTSLocalization.Tr("uts.draftmusic.folder_hint"), 14f, VanillaUI.Muted,
                                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0),
                                new Vector2(Inset + 516f, 28f), new Vector2(-(Inset + 516f) - Inset - (pages > 1 && openKind == null ? 250f : 0f), 50f),
                                TMPro.TextAlignmentOptions.Left);
                if (pages > 1 && openKind == null) {
                    var br = new Vector2(1, 0);
                    VanillaUI.Button(panel, "<", new Vector2(-Inset - 170f, 28f), new Vector2(60f, 50f),
                                     Page > 0 ? VanillaUI.Blue : VanillaUI.Grey, () => { if (Page > 0) { Page--; DraftMusic.Changed(); } },
                                     anchorMin: br, anchorMax: br, pivot: br);
                    VanillaUI.Label(panel, $"{Page + 1} / {pages}", 18f, Color.white, br, br, br,
                                    new Vector2(-Inset - 70f, 28f), new Vector2(100f, 50f), TMPro.TextAlignmentOptions.Center,
                                    TMPro.FontStyles.Bold);
                    VanillaUI.Button(panel, ">", new Vector2(-Inset, 28f), new Vector2(60f, 50f),
                                     Page < pages - 1 ? VanillaUI.Blue : VanillaUI.Grey,
                                     () => { if (Page < pages - 1) { Page++; DraftMusic.Changed(); } },
                                     anchorMin: br, anchorMax: br, pivot: br);
                }

                shownVersion = DraftMusic.Version;
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DraftMusic] panel failed: {ex}");
                Teardown();
            }
        }

        // The tab's volume: a slider in percent of the game's music volume, heard while dragging, saved on release.
        [HideFromIl2Cpp]
        private void BuildVolume(GameObject panel, MusicSlot slot) {
            const float top = -182f;
            VanillaUI.Text(panel, UTSLocalization.Tr("uts.music.volume"), 18f, Color.white, new Vector2(Inset + 18f, top - 4f),
                           new Vector2(170f, 40f), TMPro.FontStyles.Bold);
            var value = VanillaUI.Text(panel, Percent(slot.Percent01), 18f, Color.white, new Vector2(Inset + 820f, top - 4f),
                                       new Vector2(90f, 40f), TMPro.FontStyles.Bold, TMPro.TextAlignmentOptions.TopRight);
            VanillaUI.Text(panel, UTSLocalization.Tr("uts.music.volume_hint"), 14f, VanillaUI.Muted,
                           new Vector2(Inset + 186f, top - 34f), new Vector2(760f, 24f));
            // 0..100 % of full volume, the same scale on every tab; only the starting point differs
            VanillaSlider.Create(panel, new Vector2(Inset + 190f, top - 4f), 600f, 16f, slot.Percent01, VanillaUI.Pink,
                v => { slot.LivePercent = Snap(v); VanillaUI.SetText(value, Percent(slot.Percent01)); },
                v => {
                    try { if (slot.Volume != null) slot.Volume.Value = Mathf.RoundToInt(Snap(v) * 100f); } catch { }
                    slot.LivePercent = null;
                });
        }

        private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)} %";
        // steps of 5 %
        private static float Snap(float v) => Mathf.Round(Mathf.Clamp01(v) * 20f) / 20f;

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, MusicSlot slot, Choice c, float y, float rowH) {
            bool selected = slot.Selected == c.Key;
            bool failed = DraftMusic.Failed(c.Key);
            var holder = VanillaUI.Row(parent, y, rowH, Inset, selected ? VanillaUI.FieldLight : (Color?)null);
            if (c.IsKind) {
                var kindKey = c.Key;
                VanillaUI.Clickable(holder, () => { OpenKind = kindKey; DraftMusic.Changed(); });   // the buttons lie on top
            }

            VanillaUI.Label(holder, c.Name, 18f, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(18f, 0), new Vector2(300f, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
            VanillaUI.Label(holder, failed ? UTSLocalization.Tr("uts.draftmusic.cannot_play") : c.Style, 14f,
                  failed ? VanillaUI.Bad : c.Missing ? VanillaUI.Warn : VanillaUI.Muted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(330f, 0), new Vector2(230f, 0), TMPro.TextAlignmentOptions.Left);

            float bh = Mathf.Min(40f, rowH - 8f);
            var key = c.Key;

            // right to left: choose, listen, playlist
            float x = -10f;
            VanillaUI.Button(holder, UTSLocalization.Tr(selected ? "uts.draftmusic.selected" : "uts.draftmusic.use"),
                       new Vector2(x, 0), new Vector2(ButtonW, bh), selected ? VanillaUI.Teal : (failed ? VanillaUI.Grey : VanillaUI.Green),
                       () => { if (!DraftMusic.Failed(key)) slot.Selected = key; },
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f), pivot: new Vector2(1, 0.5f));
            x -= ButtonW + ButtonGap;

            if (c.CanListen && !failed) {
                bool playing = DraftMusic.PreviewPlaying(key);
                bool pending = DraftMusic.PreviewPending(key);
                string label = playing ? "uts.draftmusic.stop" : pending ? "uts.draftmusic.loading" : "uts.draftmusic.listen";
                VanillaUI.Button(holder, UTSLocalization.Tr(label), new Vector2(x, 0), new Vector2(ButtonW, bh),
                           playing ? VanillaUI.Amber : pending ? VanillaUI.Grey : VanillaUI.Blue,
                           () => DraftMusic.TogglePreview(key),
                           anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f), pivot: new Vector2(1, 0.5f));
            }
            x -= ButtonW + ButtonGap;

            // a track that cannot play may still be taken off the playlist, never put on it
            bool listed = slot.OnPlaylist(key);
            if (!c.CanList || (failed && !listed)) return;
            VanillaUI.Button(holder, UTSLocalization.Tr(listed ? "uts.draftmusic.playlist_in" : "uts.draftmusic.playlist_add"),
                       new Vector2(x, 0), new Vector2(ButtonW, bh), listed ? VanillaUI.Pink : VanillaUI.Grey,
                       () => slot.TogglePlaylist(key),
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f), pivot: new Vector2(1, 0.5f));
        }

        // "Load music": every version that is neither here nor deleted. Clicking it is the player's go-ahead; the
        // button names the size first.
        [HideFromIl2Cpp]
        private void BuildLoadButton(GameObject panel) {
            var missing = MusicLibrary.Missing();
            string label; Color color; Action click = null;
            if (MusicLibrary.Busy) {
                label = UTSLocalization.Tr("uts.music.loading_left", MusicLibrary.Pending);
                color = VanillaUI.Grey;
            } else if (missing.Count > 0) {
                label = UTSLocalization.Tr("uts.music.load", MusicLibrary.Mb(MusicLibrary.Bytes(missing)));
                color = VanillaUI.Pink;
                click = MusicLibrary.DownloadAll;
            } else {
                label = UTSLocalization.Tr("uts.music.loaded");
                color = VanillaUI.Grey;
            }
            VanillaUI.Button(panel, label, new Vector2(Inset, 28f), new Vector2(270f, 50f), color, () => click?.Invoke(),
                             anchorMin: new Vector2(0, 0), anchorMax: new Vector2(0, 0), pivot: new Vector2(0, 0));
        }

        // The versions of one kind: back, then one row per version.
        [HideFromIl2Cpp]
        private void BuildVersions(GameObject panel, MusicLibrary.Kind kind, float y, float rowH, float step) {
            VanillaUI.Button(panel, UTSLocalization.Tr("uts.music.back"), new Vector2(Inset, y), new Vector2(200f, Mathf.Min(44f, rowH)),
                             VanillaUI.Blue, () => { OpenKind = null; DraftMusic.Changed(); },
                             anchorMin: new Vector2(0, 1), anchorMax: new Vector2(0, 1), pivot: new Vector2(0, 1));
            y -= step;
            foreach (var v in kind.Versions) {
                BuildVersionRow(panel, v, y, rowH);
                y -= step;
            }
        }

        private const float VersionButtonW = 120f;

        [HideFromIl2Cpp]
        private void BuildVersionRow(GameObject parent, DraftMusic.Track v, float y, float rowH) {
            bool on = MusicLibrary.IsOn(v), ready = MusicLibrary.Ready(v), failed = DraftMusic.Failed(v.Key);
            var dl = MusicLibrary.StateOf(v);
            var holder = VanillaUI.Row(parent, y, rowH, Inset, on ? VanillaUI.FieldLight : (Color?)null);
            VanillaUI.Label(holder, v.Title, 18f, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(18f, 0), new Vector2(300f, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
            string status; Color sc = VanillaUI.Muted;
            if (failed) { status = UTSLocalization.Tr("uts.draftmusic.cannot_play"); sc = VanillaUI.Bad; }
            else if (dl == MusicLibrary.Dl.Running) { status = UTSLocalization.Tr("uts.music.downloading", Mathf.RoundToInt(MusicLibrary.Progress(v) * 100f)); sc = VanillaUI.Blue; }
            else if (dl == MusicLibrary.Dl.Queued) { status = UTSLocalization.Tr("uts.music.queued"); sc = VanillaUI.Blue; }
            else if (dl == MusicLibrary.Dl.Failed) { status = UTSLocalization.Tr("uts.music.download_failed"); sc = VanillaUI.Bad; }
            else if (ready) { status = MusicLibrary.Mb(v.Size) + " MB"; sc = VanillaUI.Good; }   // here: the row is narrow
            else if (MusicLibrary.IsDeleted(v)) { status = UTSLocalization.Tr("uts.music.deleted"); sc = VanillaUI.Warn; }
            else { status = UTSLocalization.Tr("uts.music.not_loaded"); sc = VanillaUI.Warn; }
            VanillaUI.Label(holder, status, 14f, sc,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(330f, 0), new Vector2(150f, 0), TMPro.TextAlignmentOptions.Left);

            float bh = Mathf.Min(40f, rowH - 8f), x = -10f;
            var right = new Vector2(1, 0.5f);
            void Btn(string key, Color color, Action click) {
                if (key != null)
                    VanillaUI.Button(holder, UTSLocalization.Tr(key), new Vector2(x, 0), new Vector2(VersionButtonW, bh), color, click,
                                     anchorMin: right, anchorMax: right, pivot: right);
                x -= VersionButtonW + ButtonGap;
            }
            // right to left: only this, on/off, listen, delete or download
            bool onlyThis = on && MusicLibrary.OnCount(MusicLibrary.KindOf(v.KindKey)) == 1;
            Btn("uts.music.only", onlyThis ? VanillaUI.Teal : VanillaUI.Green, () => MusicLibrary.Only(v));
            Btn(on ? "uts.music.on" : "uts.music.off_short", on ? VanillaUI.Teal : VanillaUI.Grey, () => MusicLibrary.Toggle(v));
            if (ready && !failed) {
                bool playing = DraftMusic.PreviewKey == v.Key, pending = DraftMusic.PreviewPending(v.Key);
                Btn(playing ? "uts.draftmusic.stop" : pending ? "uts.draftmusic.loading" : "uts.draftmusic.listen",
                    playing ? VanillaUI.Amber : pending ? VanillaUI.Grey : VanillaUI.Blue, () => DraftMusic.TogglePreview(v.Key));
            } else Btn(null, default, null);
            if (ready) Btn("uts.music.delete", VanillaUI.Red, () => MusicLibrary.Delete(v));
            else if (dl != MusicLibrary.Dl.Running && dl != MusicLibrary.Dl.Queued) Btn("uts.music.download", VanillaUI.Pink, () => MusicLibrary.Download(v));
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); }
        }

        // ====================================================================
        // Autotest (freeplay)
        // ====================================================================
        private static bool diagFired, diagMenuFired;

        // main menu: wait for the login, then freeplay on The Skeld (same route as UIGallery)
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagMenuPatch {
            private static void Postfix(MainMenuManager __instance) {
                if (diagMenuFired || DraftMusic.DiagTest == null || !DraftMusic.DiagTest.Value || UIGallery.Active) return;
                diagMenuFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, DiagMenu());
            }
        }

        private static IEnumerator DiagMenu() {
            float t0 = Time.time;
            while (true) {
                bool loggedIn = false;
                try { loggedIn = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (loggedIn || Time.time - t0 > 45f) break;
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            yield return DiagLibrary();
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { DiagLog("no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap(MapNames.Skeld);
                DiagLog("freeplay started");
            } catch (Exception e) { DiagLog("freeplay start failed: " + e.Message); }
        }

        private static IEnumerator WaitDownloads(float limit) {
            float t0 = Time.realtimeSinceStartup;
            yield return null;
            while (MusicLibrary.Busy && Time.realtimeSinceStartup - t0 < limit) yield return null;
        }

        private static string LibraryState() {
            var all = MusicLibrary.AllVersions.ToList();
            return $"{all.Count(MusicLibrary.Ready)}/{all.Count} here, {all.Count(MusicLibrary.IsDeleted)} deleted, "
                   + $"{all.Count(v => MusicLibrary.StateOf(v) == MusicLibrary.Dl.Failed)} failed, missing {MusicLibrary.Missing().Count}";
        }

        // Main menu: the Mod Manager's Music tab, Load music, delete and download one version again.
        private static IEnumerator DiagLibrary() {
            DiagLog("library before: " + LibraryState());
            var mm = ModManagerUI.Instance;
            if (mm != null) { mm.Show(); mm.DiagShowTab(3); yield return new WaitForSeconds(1f); yield return DiagShot("mm_music_before"); }
            float t0 = Time.realtimeSinceStartup;
            MusicLibrary.DownloadAll();
            yield return WaitDownloads(240f);
            DiagLog($"load music: {LibraryState()} after {Time.realtimeSinceStartup - t0:F1} s");
            yield return new WaitForSeconds(0.6f);
            if (mm != null) { yield return DiagShot("mm_music_after"); mm.Hide(); }

            var v = MusicLibrary.Version("noir-a");
            MusicLibrary.Delete(v);
            DiagLog($"deleted noir-a: here {MusicLibrary.Ready(v)}, file {System.IO.File.Exists(MusicLibrary.PathOf(v))}, marked {MusicLibrary.IsDeleted(v)}");
            MusicLibrary.DownloadAll();
            yield return WaitDownloads(30f);
            DiagLog($"load music after delete: noir-a here {MusicLibrary.Ready(v)} (expected False)");
            MusicLibrary.Download(v);
            yield return WaitDownloads(30f);
            DiagLog($"download noir-a again: here {MusicLibrary.Ready(v)}, marked {MusicLibrary.IsDeleted(v)}");

            // versions: only one, then two on, then back to the defaults
            var k = MusicLibrary.KindOf("synthwave");
            MusicLibrary.Only(k.Versions[1]);
            string only = string.Join(",", k.Versions.Where(MusicLibrary.IsOn).Select(x => x.Key));
            MusicLibrary.Toggle(k.Versions[2]);
            string two = string.Join(",", k.Versions.Where(MusicLibrary.IsOn).Select(x => x.Key));
            var picks = new HashSet<string>();
            for (int i = 0; i < 40; i++) picks.Add(DraftMusic.Find("synthwave")?.Key ?? "null");
            DiagLog($"versions: only -> {only}; plus one -> {two}; 40 picks -> {string.Join(",", picks.OrderBy(x => x))}");
            MusicLibrary.Toggle(k.Versions[2]);
            MusicLibrary.Toggle(k.Versions[0]);
            MusicLibrary.Toggle(k.Versions[1]);
            DiagLog("versions back: " + string.Join(",", k.Versions.Where(MusicLibrary.IsOn).Select(x => x.Key)));
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class DiagPatch {
            private static void Postfix(HudManager __instance) {
                if (diagFired || DraftMusic.DiagTest == null || !DraftMusic.DiagTest.Value) return;
                var ac = AmongUsClient.Instance;
                if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
                diagFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, DiagRun());
            }
        }

        private static void DiagLog(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[DraftMusicTest] " + s);

        internal static IEnumerator DiagShot(string tag) {
            yield return new WaitForEndOfFrame();
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"draftmusic_{tag}.png");
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
                DiagLog($"shot {tag} -> {file}");
            } catch (Exception e) { DiagLog($"shot {tag} failed: {e.Message}"); }
        }

        private static AudioSource TorSource() {
            try {
                var tor = DraftMusic.TorClip();
                var sm = SoundManager.instance;
                if (tor != null && sm != null && sm.allSources.TryGetValue(tor, out var src)) return src;
            } catch { }
            return null;
        }

        private static IEnumerator DiagRun() {
            yield return new WaitForSeconds(6f);
            DiagActive = true;
            string oldChoice = DraftMusic.Selected;
            DiagLog($"begin, choice '{oldChoice}', tor clip {(DraftMusic.TorClip() != null ? DraftMusic.TorClip().name : "missing")}");

            var ui = Instance;
            if (ui != null) {
                ui.Open(); yield return new WaitForSeconds(1f); yield return DiagShot("panel");
                OpenKind = "synthwave"; DraftMusic.Changed(); yield return new WaitForSeconds(0.6f); yield return DiagShot("versions");
                OpenKind = null; DraftMusic.Changed(); yield return new WaitForSeconds(0.3f);
            }

            // preview: decode on the worker, then play on the music channel
            float t0 = Time.realtimeSinceStartup;
            DraftMusic.TogglePreview("noir");
            while (!DraftMusic.PreviewPlaying("noir") && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            DiagLog($"preview noir: {(DraftMusic.PreviewPlaying("noir") ? "playing " + DraftMusic.PreviewKey : "NOT playing")} after {Time.realtimeSinceStartup - t0:F2} s");
            yield return new WaitForSeconds(1.5f);
            yield return DiagShot("preview");
            DraftMusic.StopPreview();
            yield return new WaitForSeconds(0.5f);

            // own files (.ogg/.mp3/.wav): each one once through the decoder and onto the music channel
            foreach (var f in DraftMusic.Files(true)) {
                t0 = Time.realtimeSinceStartup;
                DraftMusic.TogglePreview(f.Key);
                while (DraftMusic.PreviewKey != f.Key && !DraftMusic.Failed(f.Key) && Time.realtimeSinceStartup - t0 < 20f) yield return null;
                var clip = DraftMusic.Get(f);
                DiagLog($"own file {f.Key}: {(DraftMusic.PreviewKey == f.Key ? "playing" : "NOT playing")}"
                        + $"{(DraftMusic.Failed(f.Key) ? " (" + DraftMusic.FailReason(f.Key) + ")" : "")}"
                        + $"{(clip != null ? $", {clip.length:F1} s, {clip.channels} ch, {clip.frequency} Hz" : "")}"
                        + $" after {Time.realtimeSinceStartup - t0:F2} s");
                yield return new WaitForSeconds(0.4f);
                DraftMusic.StopPreview();
                yield return new WaitForSeconds(0.2f);
            }
            if (ui != null) { ui.Close(); ui.Open(); yield return new WaitForSeconds(0.5f); yield return DiagShot("panel_files"); }
            for (int k = 1; k <= 4; k++) {
                Tab = k;
                DraftMusic.Changed();
                yield return new WaitForSeconds(0.5f);
                yield return DiagShot("tab" + k);
            }
            Tab = 0;
            Page = 1;
            DraftMusic.Changed();
            yield return new WaitForSeconds(0.5f);
            yield return DiagShot("page2");
            Page = 0;
            DraftMusic.Changed();

            // reactor: the contract Unknown's Collection asks when a meltdown starts
            string oldReactor = ReactorTrack.Selected;
            ReactorTrack.Selected = "epic";
            t0 = Time.realtimeSinceStartup;
            while (DraftMusic.Get(DraftMusic.Peek("epic")) == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            yield return null;
            object given = null;
            try { given = (AppDomain.CurrentDomain.GetData("Unknowns.Music.ReactorLoop") as Func<object>)?.Invoke(); } catch { }
            DiagLog($"reactor contract: {(given is AudioClip rc ? "clip '" + rc.name + "'" : given == null ? "null" : given.GetType().Name)}");
            ReactorTrack.Selected = oldReactor;

            // a simulated draft with "synthwave": TOR starts its own track, the driver has to take over
            DraftMusic.Selected = "synthwave";
            if (ui != null) ui.Close();
            DraftMusic.DiagForceDraft = true;
            t0 = Time.realtimeSinceStartup;
            TheOtherRoles.SoundEffectsManager.play("draft", 1f, true, true);
            string swapped = null;
            while (Time.realtimeSinceStartup - t0 < 15f) {
                var src = TorSource();
                if (src != null && src.clip != null && src.clip != DraftMusic.TorClip()) { swapped = src.clip.name; break; }
                yield return null;
            }
            DiagLog($"draft source after {Time.realtimeSinceStartup - t0:F2} s: {(swapped ?? "still TOR's clip")}");
            yield return new WaitForSeconds(2f);
            var s1 = TorSource();
            DiagLog($"draft source playing {s1?.isPlaying}, loop {s1?.loop}, clip '{s1?.clip?.name}', time {s1?.time:F1}, volume {s1?.volume:F2}");
            DraftMusic.Slot.LivePercent = 0.5f;
            yield return new WaitForSeconds(0.3f);
            DiagLog($"draft volume at 50 %: {TorSource()?.volume:F2}");
            DraftMusic.Slot.LivePercent = null;

            // TOR's own stop has to end it (SoundManager drops the source from allSources), and the end
            // of the draft leaves no source behind that still carries our clip
            TheOtherRoles.SoundEffectsManager.stop("draft");
            yield return null;
            DiagLog($"after TOR stop: source {(s1 == null ? "destroyed" : s1.isPlaying ? "STILL PLAYING" : "stopped")}, "
                    + $"still listed {TorSource() != null}");
            DraftMusic.DiagForceDraft = false;
            yield return new WaitForSeconds(0.3f);
            var s2 = TorSource();
            DiagLog($"after draft end: {(s2 == null ? "no source for TOR's clip" : s2.clip == DraftMusic.TorClip() ? "source has TOR's clip" : "SOURCE STILL HAS OURS")}");

            // "Original": the source plays TOR's track again
            DraftMusic.Selected = DraftMusic.Tor;
            DraftMusic.DiagForceDraft = true;
            TheOtherRoles.SoundEffectsManager.play("draft", 1f, true, true);
            yield return new WaitForSeconds(1f);
            var s3 = TorSource();
            DiagLog($"original: clip '{s3?.clip?.name}', TOR's {(s3 != null && s3.clip == DraftMusic.TorClip())}, playing {s3?.isPlaying}");
            TheOtherRoles.SoundEffectsManager.stop("draft");
            DraftMusic.DiagForceDraft = false;
            yield return new WaitForSeconds(0.3f);

            DraftMusic.Selected = oldChoice;

            // meeting: a real one in freeplay, the music has to fade in on the music channel
            string oldMeeting = MeetingMusic.Selected;
            MeetingMusic.Selected = "lofi";
            t0 = Time.realtimeSinceStartup;
            while (DraftMusic.Get(DraftMusic.Peek("lofi")) == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            try { PlayerControl.LocalPlayer.CmdReportDeadBody(null); } catch (Exception e) { DiagLog("meeting start failed: " + e.Message); }
            yield return new WaitForSeconds(5f);
            AudioSource ms = null;
            try {
                var lofi = DraftMusic.Get(DraftMusic.Find(MeetingMusic.CurrentKey));
                if (lofi != null && SoundManager.instance.allSources.TryGetValue(lofi, out var s)) ms = s;
            } catch { }
            DiagLog($"meeting: hud {MeetingHud.Instance != null}, music {(ms == null ? "NONE" : $"playing {ms.isPlaying}, volume {ms.volume:F2}, clip '{ms.clip?.name}'")}");
            yield return DiagShot("meeting");
            MeetingMusic.Slot.LivePercent = 0.5f;
            yield return new WaitForSeconds(0.5f);
            DiagLog($"meeting volume at 50 %: {(ms != null ? ms.volume.ToString("F2") : "-")}");
            MeetingMusic.Slot.LivePercent = null;
            MeetingMusic.Selected = oldMeeting;

            DiagActive = false;
            DiagLog("done");
        }
    }
}
