// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * SessionStatsUI - the lobby panel of SessionStats, the same for host and guests (host's numbers).
 *
 * Left: every lobby player with rounds, wins, meetings survived, survived share and kills; a click on
 * a row shows that player on the right (yourself by default). Right: wins per team, the most played
 * roles and three pie charts - how their rounds ended, who killed them, whom they killed. Killer and
 * victim slices take the player's colour.
 *
 * Pies need no texture per chart: one cached circle sprite, drawn as stacked radial fills (largest
 * cumulative share at the bottom), so each slice shows between its neighbours' edges.
 *
 * Same VanillaUI canvas shape as EarlyDeathShieldUI; its lobby entry sits in the lobby menu
 * (LobbyMenuUI).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public class SessionStatsUI : MonoBehaviour {
        public static SessionStatsUI Instance { get; private set; }

        public SessionStatsUI(IntPtr ptr) : base(ptr) { }

        private static readonly Color ColOthers = new Color(0.45f, 0.45f, 0.5f);
        private static readonly Color ColEmpty = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color ColGold = new Color(1f, 0.82f, 0.3f);

        // How the rounds ended, in SessionStats.Cat* order.
        private static readonly Color[] CatColors = {
            new Color(0.35f, 0.8f, 0.45f),   // survived
            new Color(0.9f, 0.3f, 0.3f),     // killed
            new Color(0.95f, 0.7f, 0.25f),   // voted out
            new Color(0.7f, 0.45f, 0.95f),   // guessed
            new Color(0.4f, 0.6f, 0.85f),    // other death
            new Color(0.55f, 0.55f, 0.6f),   // own fault
        };
        private static readonly string[] CatKeys = {
            "uts.sessionstats.cat_survived", "uts.sessionstats.cat_killed", "uts.sessionstats.cat_voted",
            "uts.sessionstats.cat_guessed", "uts.sessionstats.cat_other", "uts.sessionstats.cat_self"
        };
        private static readonly string[] TeamKeys = {
            "uts.sessionstats.team_crew", "uts.sessionstats.team_imp", "uts.sessionstats.team_neutral"
        };
        private static readonly Color[] TeamColors = {
            new Color(0.55f, 0.85f, 1f), new Color(1f, 0.35f, 0.35f), new Color(0.75f, 0.75f, 0.8f)
        };

        private GameObject panelRoot;
        private float nextPoll;
        private float shownAt = -1f;
        private byte selected = byte.MaxValue;
        private bool titlesView;   // second tab: titles of the evening
        private bool ceremony;     // opened by the host's "show the titles to everyone"

        internal static BepInEx.Configuration.ConfigEntry<bool> DiagViewer;
        private float diagAt = -1f;
        private float diagTitlesAt;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            // the lobby entry lives in the lobby menu (LobbyMenuUI)
            LobbyMenu.Add("uts.sessionstats", 30, ShouldShow, () => UTSLocalization.Tr("uts.sessionstats.button"),
                          () => Instance?.Toggle(), VanillaUI.Blue);
            LobbyMenu.Add("uts.replay", 40, RoundReplay.ShouldShowButton, () => UTSLocalization.Tr("uts.replay.button"),
                          RoundReplay.OpenView, VanillaUI.Teal);
        }

        public void Update() {
            SessionStats.Tick();
            GhostKillFeed.Tick();
            RoundReplay.Tick();
            if (panelRoot != null && Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            DiagTick();

            bool diag = DiagViewer != null && DiagViewer.Value;
            if (panelRoot != null && !LobbyScreen.Exists && !diag) Close();
            if (panelRoot != null) {
                var t = SessionStats.LastTable;
                if (t != null && t.ReceivedAt != shownAt) Rebuild();
            }
        }

        [HideFromIl2Cpp]
        private static bool ShouldShow() {
            try {
                if (!LobbyScreen.Exists || AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return SessionStats.IsEnabled();
                // everyone else: only while the host's UTS actually runs it
                return UTSGate.Bool(SessionStats.Enabled);
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        public void Toggle() {
            if (panelRoot != null) { Close(); return; }
            // the shield panels share the screen centre
            NewcomerShieldUI.Instance?.Close();
            EarlyDeathShieldUI.Instance?.Close();
            SessionStats.RequestTable();
            Open();
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; LobbyPanelGuard.Closed(); }
            ceremony = false;
        }

        /// <summary>The end-of-evening panel: the titles tab, opened for everybody by the host.</summary>
        [HideFromIl2Cpp]
        public void OpenCeremony() {
            if (!LobbyScreen.Exists) return;
            NewcomerShieldUI.Instance?.Close();
            EarlyDeathShieldUI.Instance?.Close();
            RoundReplay.CloseView();
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; }
            titlesView = true;
            ceremony = true;
            SessionStats.RequestTable();
            Open();
            UsefulTORStuffPlugin.Logger?.LogInfo("[SessionStats] end-of-evening titles opened.");
        }

        /// <summary>True while the statistics can be opened here (lobby, feature on for this host).</summary>
        public static bool CanOpen() => ShouldShow();

        /// <summary>Opens the statistics with one player selected (from a name in the shield panels).</summary>
        [HideFromIl2Cpp]
        public void OpenFor(byte playerId) {
            if (!ShouldShow()) return;
            NewcomerShieldUI.Instance?.Close();
            EarlyDeathShieldUI.Instance?.Close();
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; }
            selected = playerId;
            titlesView = false;
            ceremony = false;
            SessionStats.RequestTable();
            Open();
        }

        /// <summary>
        /// Makes a player's name clickable: a transparent box over the given area that opens the
        /// statistics for that player. Nothing happens while the statistics are not available.
        /// </summary>
        [HideFromIl2Cpp]
        internal static void LinkName(GameObject row, Vector2 topLeft, Vector2 size, byte playerId) {
            if (!ShouldShow()) return;
            var box = VanillaUI.Box(row, topLeft, size, new Color(0, 0, 0, 0), VanillaUI.RadiusBox);
            VanillaUI.Clickable(box, () => Instance?.OpenFor(playerId));
        }

        /// <summary>Autotest (UIGallery): switch the open panel to the titles tab.</summary>
        [HideFromIl2Cpp]
        public void DiagShowTitles() { titlesView = true; if (panelRoot != null) Rebuild(); }

        [HideFromIl2Cpp]
        private void Rebuild() {
            bool keep = ceremony;
            Close();
            ceremony = keep;
            Open();
        }

        // ====================================================================
        // Panel
        // ====================================================================
        private const float PanelW = 1540, PanelH = 900, RowStep = 40, FooterH = 90;

        [HideFromIl2Cpp]
        private void Open() {
            try {
                var t = SessionStats.LastTable;
                shownAt = t != null ? t.ReceivedAt : -1f;

                panelRoot = VanillaUI.Canvas("UTSSessionStatsUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, Close);

                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(PanelW, PanelH));
                float y = VanillaUI.Title(panel, UTSLocalization.Tr(ceremony ? "uts.sessionstats.ceremony_title" : "uts.sessionstats.title"),
                                          16f, 34f, ceremony ? VanillaUI.Amber : (Color?)null);

                // Everyone of the session: the players here, and below them the ones who left
                // (User 2026-10-02: keep them, at the bottom), drawn in grey.
                var rows = new List<(SessionStats.Row Row, Who W)>();
                if (t != null && t.Complete)
                    foreach (var r in t.Rows.Values) {
                        var w = WhoOf(r.PlayerId, t);
                        if (w.Here || r.LeftName != null) rows.Add((r, w));
                    }
                // here first, then most rounds, then by name
                rows = rows.OrderByDescending(x => x.W.Here).ThenByDescending(x => x.Row.Rounds)
                           .ThenBy(x => x.W.Name, StringComparer.OrdinalIgnoreCase).ToList();
                currentTable = t;

                string sub = t == null || !t.Complete ? UTSLocalization.Tr("uts.sessionstats.waiting")
                           : t.SessionRounds == 0 ? UTSLocalization.Tr("uts.sessionstats.empty")
                           : UTSLocalization.Tr("uts.sessionstats.subtitle", t.SessionRounds);
                VanillaUI.Subtitle(panel, sub, y - 6f, 30f);

                if (rows.Count > 0) {
                    byte me = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
                    // tab switch, top right
                    VanillaUI.Button(panel, UTSLocalization.Tr(titlesView ? "uts.sessionstats.tab_stats" : "uts.sessionstats.tab_titles"),
                                     new Vector2(-VanillaUI.FrameW - 24, -VanillaUI.FrameW - 22), new Vector2(260, 44), VanillaUI.Teal,
                                     () => { titlesView = !titlesView; Rebuild(); },
                                     new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
                    if (titlesView) {
                        BuildTitles(panel, t, me);
                        // the host can turn the tab into the end-of-evening panel for everybody
                        if (!ceremony && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && t.Titles.Count > 0)
                            VanillaUI.Button(panel, UTSLocalization.Tr("uts.sessionstats.ceremony_button"),
                                             new Vector2(-VanillaUI.FrameW - 24, VanillaUI.FrameW + 16), new Vector2(320, 48), VanillaUI.Amber,
                                             SessionStats.SendCeremony, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
                    } else {
                        if (!rows.Any(x => x.Row.PlayerId == selected))
                            selected = rows.Any(x => x.Row.PlayerId == me) ? me : rows[0].Row.PlayerId;
                        BuildTable(panel, rows, me);
                        var sel = rows.First(x => x.Row.PlayerId == selected);
                        BuildDetail(panel, sel.Row, sel.W, sel.Row.PlayerId == me);
                    }
                }

                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.sessionstats.close"), Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SessionStats] panel failed: {ex}");
                Close();
            }
        }

        // Column x positions inside the left table (left edge of each column, width).
        private static readonly float[] ColX = { 16, 280, 360, 440, 555, 675 };
        private static readonly float[] ColW = { 260, 75, 75, 110, 115, 60 };
        private const float TableW = 750, TableX = 34, TopY = -128;

        // A player of the table: the live one if he is here, otherwise the name the host sent.
        private struct Who {
            public string Name;
            public Color Col;
            public bool Here;
        }

        private static readonly Color ColLeft = new Color(0.5f, 0.5f, 0.55f);
        private static SessionStats.Table currentTable;   // the table the panel shows (names of those who left)

        [HideFromIl2Cpp]
        private static Who WhoOf(byte id, SessionStats.Table t) {
            if (t != null && t.Rows.TryGetValue(id, out var row) && row.LeftName != null)
                return new Who { Name = row.LeftName, Col = ColLeft, Here = false };
            var pc = id < SessionStats.LeftIdBase ? TheOtherRoles.Helpers.playerById(id) : null;
            if (pc != null && pc.Data != null && !pc.Data.Disconnected)
                return new Who { Name = pc.Data.PlayerName ?? "?", Col = PlayerColor(pc), Here = true };
            return new Who { Name = "?", Col = ColOthers, Here = false };
        }

        [HideFromIl2Cpp]
        private void BuildTable(GameObject panel, List<(SessionStats.Row Row, Who W)> rows, byte me) {
            var head = VanillaUI.Box(panel, new Vector2(TableX, TopY), new Vector2(TableW, 30), new Color(0, 0, 0, 0));
            string[] heads = {
                "uts.sessionstats.col_player", "uts.sessionstats.col_rounds", "uts.sessionstats.col_wins",
                "uts.sessionstats.col_meetings", "uts.sessionstats.col_survived", "uts.sessionstats.col_kills"
            };
            for (int i = 0; i < heads.Length; i++)
                Cell(head, UTSLocalization.Tr(heads[i]), i, 13, VanillaUI.Muted, TMPro.FontStyles.Bold | TMPro.FontStyles.UpperCase);

            float y = TopY - 34;
            // rows shrink instead of falling off the panel once the players who left are listed too
            float space = PanelH + TopY - 34 - FooterH;
            float step = rows.Count > 0 ? Mathf.Clamp(space / rows.Count, 22f, RowStep) : RowStep;
            int maxRows = Mathf.FloorToInt(space / step);
            float font = step >= 34f ? 1f : 0.82f;
            foreach (var (r, w) in rows.Take(maxRows)) {
                bool isMe = r.PlayerId == me, isSel = r.PlayerId == selected;
                var row = VanillaUI.Box(panel, new Vector2(TableX, y), new Vector2(TableW, step - 4),
                                        isSel ? VanillaUI.FieldLight : VanillaUI.Field);
                byte id = r.PlayerId;
                VanillaUI.Clickable(row, () => { selected = id; Rebuild(); });
                if (isSel) {
                    // a teal edge on the selected row, like the game's selected tab
                    var edge = VanillaUI.Box(row, new Vector2(0, 0), new Vector2(5, step - 4), VanillaUI.Teal, 2);
                    edge.GetComponent<Image>().raycastTarget = false;
                }

                // colour dot + name; who left is grey and tagged
                float dotSize = Mathf.Min(18f, step - 10f);
                var dot = VanillaUI.Box(row, new Vector2(ColX[0], -(step - 4 - dotSize) / 2f), new Vector2(dotSize, dotSize), w.Col);
                dot.GetComponent<Image>().sprite = VanillaUI.Circle();
                string name = w.Name;
                if (isMe) name += " " + UTSLocalization.Tr("uts.sessionstats.you_tag");
                if (!w.Here) name += " " + UTSLocalization.Tr("uts.sessionstats.left_tag");
                Color text = w.Here ? Color.white : ColLeft;
                var nl = Cell(row, name, 0, 16 * font, isMe ? VanillaUI.Blue : text, TMPro.FontStyles.Bold);
                nl.rectTransform.anchoredPosition += new Vector2(26, 0);
                nl.rectTransform.sizeDelta -= new Vector2(26, 0);

                Cell(row, r.Rounds.ToString(), 1, 15 * font, text);
                Cell(row, r.Rounds > 0 ? $"{Pct(r.Wins, r.Rounds)}%" : "-", 2, 15 * font, text);
                Cell(row, r.Rounds > 0 ? r.MeetingsAvg.ToString("0.0") : "-", 3, 15 * font, text);
                Cell(row, r.ShareAvg >= 0f ? $"{Mathf.RoundToInt(r.ShareAvg * 100f)}%" : "-", 4, 15 * font, text);
                Cell(row, r.Kills.ToString(), 5, 15 * font, text);
                y -= step;
            }
        }

        private const float DetailX = 820, DetailW = 680;

        [HideFromIl2Cpp]
        private void BuildDetail(GameObject panel, SessionStats.Row r, Who w, bool isMe) {
            var d = VanillaUI.Box(panel, new Vector2(DetailX, TopY), new Vector2(DetailW, PanelH + TopY - FooterH), VanillaUI.Field, VanillaUI.RadiusBox);

            string name = w.Name + (isMe ? " " + UTSLocalization.Tr("uts.sessionstats.you_tag") : "")
                          + (!w.Here ? " " + UTSLocalization.Tr("uts.sessionstats.left_tag") : "");
            var dot = VanillaUI.Box(d, new Vector2(18, -16), new Vector2(26, 26), w.Col);
            dot.GetComponent<Image>().sprite = VanillaUI.Circle();
            VanillaUI.Text(d, name, 22, Color.white, new Vector2(54, -12), new Vector2(DetailW - 70, 34), TMPro.FontStyles.Bold);
            VanillaUI.Text(d, UTSLocalization.Tr("uts.sessionstats.detail_summary", r.Rounds, r.Wins, Pct(r.Wins, r.Rounds)),
                 15, VanillaUI.Muted, new Vector2(18, -50), new Vector2(DetailW - 36, 24));

            // wins per team, with a bar for the win rate
            float y = -84;
            for (int i = 0; i < 3; i++) {
                VanillaUI.Text(d, UTSLocalization.Tr("uts.sessionstats.team_line", UTSLocalization.Tr(TeamKeys[i]), r.TeamWins[i], r.TeamRounds[i]),
                     14, r.TeamRounds[i] > 0 ? Color.white : VanillaUI.Muted, new Vector2(18, y), new Vector2(300, 22));
                VanillaUI.Box(d, new Vector2(330, y - 6), new Vector2(300, 10), ColEmpty, 4);
                if (r.TeamRounds[i] > 0)
                    VanillaUI.Box(d, new Vector2(330, y - 6), new Vector2(Mathf.Max(10f, 300f * r.TeamWins[i] / r.TeamRounds[i]), 10), TeamColors[i], 4);
                y -= 26;
            }

            string roles = r.Roles.Count == 0 ? "-" : string.Join(", ", r.Roles.Select(x => $"{x.Role} x{x.Count}"));
            VanillaUI.Text(d, UTSLocalization.Tr("uts.sessionstats.roles", roles), 14, Color.white,
                 new Vector2(18, y - 4), new Vector2(DetailW - 36, 40));

            // three pies
            float py = y - 56;
            var deaths = new List<(float, Color, string)>();
            for (int i = 0; i < SessionStats.CatCount; i++)
                if (r.DeathCats[i] > 0) deaths.Add((r.DeathCats[i], CatColors[i], $"{UTSLocalization.Tr(CatKeys[i])} {r.DeathCats[i]}x"));
            PieBlock(d, UTSLocalization.Tr("uts.sessionstats.pie_deaths"), deaths, new Vector2(18, py));
            PieBlock(d, UTSLocalization.Tr("uts.sessionstats.pie_killedby"), Opponents(r.KilledBy), new Vector2(240, py));
            PieBlock(d, UTSLocalization.Tr("uts.sessionstats.pie_victims"), Opponents(r.Victims), new Vector2(462, py));
        }

        // ---- titles of the evening ----
        private const float CardW = 470, CardH = 200, CardGap = 20;

        [HideFromIl2Cpp]
        private void BuildTitles(GameObject panel, SessionStats.Table t, byte me) {
            if (t.Titles.Count == 0) {
                VanillaUI.Text(panel, UTSLocalization.Tr("uts.sessionstats.titles_none"), 16, VanillaUI.Muted,
                     new Vector2(TableX, TopY), new Vector2(PanelW - 60, 30), TMPro.FontStyles.Italic);
                return;
            }
            int i = 0;
            foreach (var ti in t.Titles) {
                int col = i % 3, row = i / 3;
                var card = VanillaUI.Box(panel, new Vector2(TableX + col * (CardW + CardGap), TopY - row * (CardH + CardGap)),
                               new Vector2(CardW, CardH), ti.Holders.Contains(me) ? VanillaUI.FieldLight : VanillaUI.Field);
                string key = $"uts.sessionstats.title_{ti.Id}";
                VanillaUI.Text(card, UTSLocalization.Tr(key + "_name"), 22, ColGold, new Vector2(18, -14), new Vector2(CardW - 36, 32),
                               TMPro.FontStyles.Bold | TMPro.FontStyles.UpperCase);
                VanillaUI.Text(card, TitleValue(ti), 14, VanillaUI.Muted, new Vector2(18, -50), new Vector2(CardW - 36, 40));
                float y = -98;
                if (ti.Id == SessionStats.TitleNemesis && ti.Holders.Count == 2) {
                    HolderLine(card, WhoOf(ti.Holders[0], t), y);
                    VanillaUI.Text(card, UTSLocalization.Tr("uts.sessionstats.nemesis_of"), 14, VanillaUI.Muted,
                         new Vector2(52, y - 30), new Vector2(CardW - 70, 22), TMPro.FontStyles.Italic);
                    HolderLine(card, WhoOf(ti.Holders[1], t), y - 56);
                } else {
                    foreach (byte h in ti.Holders) { HolderLine(card, WhoOf(h, t), y); y -= 32; }
                }
                i++;
            }
        }

        [HideFromIl2Cpp]
        private static string TitleValue(SessionStats.Title ti) {
            string key = $"uts.sessionstats.title_{ti.Id}_value";
            return ti.Id == SessionStats.TitleTalker
                ? UTSLocalization.Tr(key, (ti.Value / 100f).ToString("0.0"))
                : UTSLocalization.Tr(key, ti.Value);
        }

        [HideFromIl2Cpp]
        private static void HolderLine(GameObject card, Who w, float y) {
            var dot = VanillaUI.Box(card, new Vector2(18, y - 2), new Vector2(22, 22), w.Col);
            dot.GetComponent<Image>().sprite = VanillaUI.Circle();
            string name = w.Name == "?" ? UTSLocalization.Tr("uts.sessionstats.others")
                        : w.Here ? w.Name : $"{w.Name} {UTSLocalization.Tr("uts.sessionstats.left_tag")}";
            VanillaUI.Text(card, name, 18, w.Here ? Color.white : ColLeft,
                 new Vector2(52, y), new Vector2(CardW - 70, 28), TMPro.FontStyles.Bold);
        }

        [HideFromIl2Cpp]
        private static List<(float, Color, string)> Opponents(List<(byte Id, int Count)> list) {
            var res = new List<(float, Color, string)>();
            foreach (var (id, n) in list) {
                if (id == SessionStats.OthersId) {
                    res.Add((n, ColOthers, $"{UTSLocalization.Tr("uts.sessionstats.others")} {n}x"));
                    continue;
                }
                var w = WhoOf(id, currentTable);
                res.Add((n, w.Col, $"{w.Name} {n}x"));
            }
            return res;
        }

        private const float PieSize = 150;

        [HideFromIl2Cpp]
        private void PieBlock(GameObject parent, string title, List<(float Value, Color Col, string Text)> slices, Vector2 pos) {
            VanillaUI.Text(parent, title, 15, VanillaUI.TitleColor, pos, new Vector2(200, 22), TMPro.FontStyles.Bold | TMPro.FontStyles.UpperCase);
            var holder = VanillaUI.Box(parent, pos + new Vector2(25, -28), new Vector2(PieSize, PieSize), new Color(0, 0, 0, 0));
            float total = slices.Sum(s => s.Value);
            if (total <= 0f) {
                var e = VanillaUI.Box(holder, Vector2.zero, new Vector2(PieSize, PieSize), ColEmpty);
                e.GetComponent<Image>().sprite = VanillaUI.Circle();
                VanillaUI.Text(parent, UTSLocalization.Tr("uts.sessionstats.none"), 13, VanillaUI.Muted,
                     pos + new Vector2(0, -36 - PieSize), new Vector2(200, 20), TMPro.FontStyles.Italic);
                return;
            }
            // slice k covers [c(k-1), c(k)]: draw [0, c(k)] from the last slice to the first, each on top
            var cum = new float[slices.Count];
            float acc = 0f;
            for (int i = 0; i < slices.Count; i++) { acc += slices[i].Value / total; cum[i] = acc; }
            for (int i = slices.Count - 1; i >= 0; i--) {
                var go = VanillaUI.Box(holder, Vector2.zero, new Vector2(PieSize, PieSize), slices[i].Col);
                var img = go.GetComponent<Image>();
                img.sprite = VanillaUI.Circle();
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Radial360;
                img.fillOrigin = 2;   // Origin360.Top
                img.fillClockwise = true;
                img.fillAmount = i == slices.Count - 1 ? 1f : cum[i];
            }
            // legend
            float ly = -36 - PieSize;
            foreach (var s in slices) {
                var sq = VanillaUI.Box(parent, pos + new Vector2(0, ly - 4), new Vector2(12, 12), s.Col);
                sq.GetComponent<Image>().sprite = VanillaUI.Circle();
                int pct = Mathf.RoundToInt(s.Value / total * 100f);
                VanillaUI.Text(parent, $"{s.Text} ({pct}%)", 13, Color.white, pos + new Vector2(18, ly), new Vector2(190, 20));
                ly -= 20;
            }
        }

        [HideFromIl2Cpp]
        private void DiagTick() {
            if (DiagViewer == null || !DiagViewer.Value) return;
            if (ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) { diagAt = -1f; return; }
            if (diagAt < 0f) { diagAt = Time.realtimeSinceStartup + 10f; return; }
            if (diagTitlesAt > 0f && Time.realtimeSinceStartup >= diagTitlesAt) {
                diagTitlesAt = 0f;
                titlesView = true;
                Rebuild();
                string shot2 = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSSessionStats_titles_diag.png");
                ScreenCapture.CaptureScreenshot(shot2);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SessionStats] diag: titles tab opened, screenshot -> {shot2}");
                return;
            }
            if (diagAt == 0f || Time.realtimeSinceStartup < diagAt) return;
            diagAt = 0f;
            diagTitlesAt = Time.realtimeSinceStartup + 2f;
            SessionStats.DiagSample();
            GhostKillFeed.DiagSample();
            var others = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null && p != PlayerControl.LocalPlayer).ToList();
            selected = PlayerControl.LocalPlayer.PlayerId;
            Close();
            Open();
            string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSSessionStats_diag.png");
            ScreenCapture.CaptureScreenshot(shot);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[SessionStats] diag: viewer opened with sample numbers ({others.Count + 1} figures), screenshot -> {shot}");
        }

        // ---- helpers ----
        [HideFromIl2Cpp]
        private static int Pct(int a, int b) => b <= 0 ? 0 : Mathf.RoundToInt(100f * a / b);

        [HideFromIl2Cpp]
        internal static Color PlayerColor(PlayerControl pc) {
            try {
                int c = pc.Data.DefaultOutfit.ColorId;
                if (c >= 0 && c < Palette.PlayerColors.Length) return Palette.PlayerColors[c];
            } catch { }
            return ColOthers;
        }

        // Kept for RoundReplay and GhostKillFeed, which draw with these; new code uses VanillaUI directly.
        internal static Sprite Solid(Color color) => VanillaUI.Solid();
        internal static Sprite Circle() => VanillaUI.Circle();

        /// <summary>A rect anchored top-left of its parent, positioned by its top-left corner (plain, unrounded).</summary>
        [HideFromIl2Cpp]
        internal static GameObject Box(GameObject parent, Vector2 topLeft, Vector2 size, Color color) {
            var go = new GameObject("B");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = topLeft; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = VanillaUI.Solid();
            img.color = color;
            return go;
        }

        [HideFromIl2Cpp]
        internal static TMPro.TextMeshProUGUI Text(GameObject parent, string text, float size, TMPro.FontStyles style, Color color,
                                                  Vector2 topLeft, Vector2 box) =>
            VanillaUI.Text(parent, text, size, color, topLeft, box, style);

        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Cell(GameObject row, string text, int col, float size, Color color,
                                                  TMPro.FontStyles style = TMPro.FontStyles.Normal) =>
            VanillaUI.Label(row, text, size, color, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(ColX[col], 0), new Vector2(ColW[col], 0),
                  col == 0 ? TMPro.TextAlignmentOptions.Left : TMPro.TextAlignmentOptions.Center, style);

        [HideFromIl2Cpp]
        internal static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size, TMPro.FontStyles style, Color color,
                Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 sizeDelta, TMPro.TextAlignmentOptions align) =>
            VanillaUI.Label(parent, text, size, color, anchorMin, anchorMax, pivot, pos, sizeDelta, align, style);

        [HideFromIl2Cpp]
        internal static GameObject MakeButton(GameObject parent, string label, Vector2 pos, Vector2 size, Color color, Action onClick) =>
            VanillaUI.Button(parent, label, pos, size, color, onClick);

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); SessionStats.ClearTable(); }
        }
    }
}
