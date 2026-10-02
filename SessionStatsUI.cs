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
 * Same screen-space canvas shape as EarlyDeathShieldUI; its lobby button sits in the shared
 * bottom-left column one row above the early-death button.
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
        /// <summary>Read by EarlyDeathShieldUI for the shared lobby-button column.</summary>
        public static bool ButtonShown { get; private set; }

        public SessionStatsUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solidSprites = new Dictionary<Color, Sprite>();
        private static Sprite circle;

        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color ColPanel = new Color(0.1f, 0.12f, 0.16f, 0.98f);
        private static readonly Color ColRow = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ColRowSel = new Color(0.35f, 0.75f, 1f, 0.22f);
        private static readonly Color ColAccent = new Color(0.45f, 0.8f, 1f);
        private static readonly Color ColMuted = new Color(0.65f, 0.65f, 0.7f);
        private static readonly Color ColBtnGrey = new Color(0.3f, 0.3f, 0.38f, 0.95f);
        private static readonly Color ColButton = new Color(0.16f, 0.42f, 0.6f, 0.95f);
        private static readonly Color ColOthers = new Color(0.45f, 0.45f, 0.5f);
        private static readonly Color ColEmpty = new Color(1f, 1f, 1f, 0.08f);

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
        private GameObject lobbyButton;
        private RectTransform lobbyButtonRect;
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
        }

        public void Update() {
            SessionStats.Tick();
            GhostKillFeed.Tick();
            RoundReplay.Tick();

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            DiagTick();

            bool diag = DiagViewer != null && DiagViewer.Value;
            if (panelRoot != null && !LobbyScreen.Exists && !diag) Close();
            if (panelRoot != null) {
                var t = SessionStats.LastTable;
                if (t != null && t.ReceivedAt != shownAt) Rebuild();
            }

            bool show = ShouldShow() && !SettingsOverlayView.OverlayOpen();
            ButtonShown = show;
            if (show && lobbyButton == null) BuildLobbyButton();
            if (lobbyButton == null) return;
            if (lobbyButton.activeSelf != show) lobbyButton.SetActive(show);
            if (!show) return;
            // the shared bottom-left column: above the newcomer and early-death buttons that are shown
            int rows = (NewcomerShieldUI.ButtonShown ? 1 : 0) + (EarlyDeathShieldUI.ButtonShown ? 1 : 0);
            if (lobbyButtonRect != null) lobbyButtonRect.anchoredPosition = new Vector2(28, 84 + 54 * rows);
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
        private void BuildLobbyButton() {
            try {
                lobbyButton = new GameObject("UTSSessionStatsButton");
                DontDestroyOnLoad(lobbyButton);
                var canvas = lobbyButton.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9000;
                var scaler = lobbyButton.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                lobbyButton.AddComponent<GraphicRaycaster>();

                var btn = new GameObject("Btn");
                btn.transform.SetParent(lobbyButton.transform, false);
                lobbyButtonRect = btn.AddComponent<RectTransform>();
                lobbyButtonRect.anchorMin = Vector2.zero; lobbyButtonRect.anchorMax = Vector2.zero;
                lobbyButtonRect.pivot = Vector2.zero;
                lobbyButtonRect.anchoredPosition = new Vector2(28, 84);
                lobbyButtonRect.sizeDelta = new Vector2(330, 46);
                btn.AddComponent<Image>().sprite = Solid(ColButton);
                var t = Label(btn, UTSLocalization.Tr("uts.sessionstats.button"), 18, TMPro.FontStyles.Bold, Color.white,
                              Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                              TMPro.TextAlignmentOptions.Center);
                btn.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Toggle);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] lobby button failed: {ex.Message}");
                lobbyButton = null;
            }
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
        private const float PanelW = 1500, PanelH = 860, RowStep = 40;

        [HideFromIl2Cpp]
        private void Open() {
            try {
                var t = SessionStats.LastTable;
                shownAt = t != null ? t.ReceivedAt : -1f;

                panelRoot = new GameObject("UTSSessionStatsUI");
                LobbyPanelGuard.Track(panelRoot);
                DontDestroyOnLoad(panelRoot);
                var canvas = panelRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9500;
                var scaler = panelRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                panelRoot.AddComponent<GraphicRaycaster>().blockingObjects = GraphicRaycaster.BlockingObjects.All;

                var backdrop = new GameObject("Backdrop");
                backdrop.transform.SetParent(panelRoot.transform, false);
                var brt = backdrop.AddComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.sizeDelta = Vector2.zero;
                backdrop.AddComponent<Image>().sprite = Solid(ColBackdrop);
                backdrop.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Close);

                var panel = new GameObject("Panel");
                panel.transform.SetParent(panelRoot.transform, false);
                var prt = panel.AddComponent<RectTransform>();
                prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(PanelW, PanelH);
                panel.AddComponent<Image>().sprite = Solid(ColPanel);

                Label(panel, UTSLocalization.Tr(ceremony ? "uts.sessionstats.ceremony_title" : "uts.sessionstats.title"), 28, TMPro.FontStyles.Bold,
                      ceremony ? new Color(1f, 0.82f, 0.3f) : ColAccent,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -18), new Vector2(-40, 40),
                      TMPro.TextAlignmentOptions.Center);

                var rows = new List<(SessionStats.Row Row, PlayerControl Pc)>();
                if (t != null && t.Complete)
                    foreach (var r in t.Rows.Values) {
                        var pc = TheOtherRoles.Helpers.playerById(r.PlayerId);
                        if (pc == null || pc.Data == null || pc.Data.Disconnected) continue;
                        rows.Add((r, pc));
                    }
                // most rounds first, then by name
                rows = rows.OrderByDescending(x => x.Row.Rounds).ThenBy(x => x.Pc.Data.PlayerName).ToList();

                string sub = t == null || !t.Complete ? UTSLocalization.Tr("uts.sessionstats.waiting")
                           : t.SessionRounds == 0 ? UTSLocalization.Tr("uts.sessionstats.empty")
                           : UTSLocalization.Tr("uts.sessionstats.subtitle", t.SessionRounds);
                Label(panel, sub, 15, TMPro.FontStyles.Normal, ColMuted,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -60), new Vector2(-80, 30),
                      TMPro.TextAlignmentOptions.Top);

                if (rows.Count > 0) {
                    byte me = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
                    // tab switch, top right
                    var tab = MakeButton(panel, UTSLocalization.Tr(titlesView ? "uts.sessionstats.tab_stats" : "uts.sessionstats.tab_titles"),
                                         Vector2.zero, new Vector2(260, 40), ColButton, () => { titlesView = !titlesView; Rebuild(); });
                    var trt = tab.GetComponent<RectTransform>();
                    trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1, 1);
                    trt.anchoredPosition = new Vector2(-30, -20);
                    if (titlesView) {
                        BuildTitles(panel, t, me);
                        // the host can turn the tab into the end-of-evening panel for everybody
                        if (!ceremony && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && t.Titles.Count > 0) {
                            var show = MakeButton(panel, UTSLocalization.Tr("uts.sessionstats.ceremony_button"), Vector2.zero,
                                                  new Vector2(300, 44), new Color(0.6f, 0.45f, 0.1f, 0.95f), SessionStats.SendCeremony);
                            var srt = show.GetComponent<RectTransform>();
                            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(1, 0);
                            srt.anchoredPosition = new Vector2(-30, 20);
                        }
                    } else {
                        if (!rows.Any(x => x.Row.PlayerId == selected))
                            selected = rows.Any(x => x.Row.PlayerId == me) ? me : rows[0].Row.PlayerId;
                        BuildTable(panel, rows, me);
                        var sel = rows.First(x => x.Row.PlayerId == selected);
                        BuildDetail(panel, sel.Row, sel.Pc, sel.Row.PlayerId == me);
                    }
                }

                MakeButton(panel, UTSLocalization.Tr("uts.sessionstats.close"), new Vector2(0, 20), new Vector2(240, 44),
                           ColBtnGrey, Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SessionStats] panel failed: {ex}");
                Close();
            }
        }

        // Column x positions inside the left table (left edge of each column, width).
        private static readonly float[] ColX = { 16, 270, 350, 430, 545, 665 };
        private static readonly float[] ColW = { 250, 75, 75, 110, 115, 60 };
        private const float TableW = 740, TableX = 30, TopY = -110;

        [HideFromIl2Cpp]
        private void BuildTable(GameObject panel, List<(SessionStats.Row Row, PlayerControl Pc)> rows, byte me) {
            var head = Box(panel, new Vector2(TableX, TopY), new Vector2(TableW, 30), new Color(0, 0, 0, 0));
            string[] heads = {
                "uts.sessionstats.col_player", "uts.sessionstats.col_rounds", "uts.sessionstats.col_wins",
                "uts.sessionstats.col_meetings", "uts.sessionstats.col_survived", "uts.sessionstats.col_kills"
            };
            for (int i = 0; i < heads.Length; i++)
                Cell(head, UTSLocalization.Tr(heads[i]), i, 13, ColMuted, TMPro.FontStyles.Bold);

            float y = TopY - 34;
            int maxRows = Mathf.FloorToInt((PanelH - 110 - 34 - 80) / RowStep);
            foreach (var (r, pc) in rows.Take(maxRows)) {
                bool isMe = r.PlayerId == me, isSel = r.PlayerId == selected;
                var row = Box(panel, new Vector2(TableX, y), new Vector2(TableW, RowStep - 4),
                              isSel ? ColRowSel : isMe ? new Color(0.45f, 0.8f, 1f, 0.1f) : ColRow);
                byte id = r.PlayerId;
                row.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => { selected = id; Rebuild(); }));

                // colour dot + name
                var dot = Box(row, new Vector2(ColX[0], -9), new Vector2(18, 18), PlayerColor(pc));
                dot.GetComponent<Image>().sprite = Circle();
                string name = pc.Data.PlayerName ?? "?";
                if (isMe) name += " " + UTSLocalization.Tr("uts.sessionstats.you_tag");
                var nl = Cell(row, name, 0, 16, isMe ? ColAccent : Color.white, TMPro.FontStyles.Bold);
                nl.rectTransform.anchoredPosition += new Vector2(26, 0);
                nl.rectTransform.sizeDelta -= new Vector2(26, 0);

                Cell(row, r.Rounds.ToString(), 1, 15, Color.white);
                Cell(row, r.Rounds > 0 ? $"{Pct(r.Wins, r.Rounds)}%" : "-", 2, 15, Color.white);
                Cell(row, r.Rounds > 0 ? r.MeetingsAvg.ToString("0.0") : "-", 3, 15, Color.white);
                Cell(row, r.ShareAvg >= 0f ? $"{Mathf.RoundToInt(r.ShareAvg * 100f)}%" : "-", 4, 15, Color.white);
                Cell(row, r.Kills.ToString(), 5, 15, Color.white);
                y -= RowStep;
            }
        }

        private const float DetailX = 800, DetailW = 670;

        [HideFromIl2Cpp]
        private void BuildDetail(GameObject panel, SessionStats.Row r, PlayerControl pc, bool isMe) {
            var d = Box(panel, new Vector2(DetailX, TopY), new Vector2(DetailW, 640), new Color(1f, 1f, 1f, 0.03f));

            string name = (pc.Data.PlayerName ?? "?") + (isMe ? " " + UTSLocalization.Tr("uts.sessionstats.you_tag") : "");
            var dot = Box(d, new Vector2(18, -16), new Vector2(26, 26), PlayerColor(pc));
            dot.GetComponent<Image>().sprite = Circle();
            Text(d, name, 22, TMPro.FontStyles.Bold, Color.white, new Vector2(54, -12), new Vector2(DetailW - 70, 34));
            Text(d, UTSLocalization.Tr("uts.sessionstats.detail_summary", r.Rounds, r.Wins, Pct(r.Wins, r.Rounds)),
                 15, TMPro.FontStyles.Normal, ColMuted, new Vector2(18, -50), new Vector2(DetailW - 36, 24));

            // wins per team, with a bar for the win rate
            float y = -84;
            for (int i = 0; i < 3; i++) {
                Text(d, UTSLocalization.Tr("uts.sessionstats.team_line", UTSLocalization.Tr(TeamKeys[i]), r.TeamWins[i], r.TeamRounds[i]),
                     14, TMPro.FontStyles.Normal, r.TeamRounds[i] > 0 ? Color.white : ColMuted, new Vector2(18, y), new Vector2(300, 22));
                Box(d, new Vector2(330, y - 6), new Vector2(300, 10), ColEmpty);
                if (r.TeamRounds[i] > 0)
                    Box(d, new Vector2(330, y - 6), new Vector2(300f * r.TeamWins[i] / r.TeamRounds[i], 10), TeamColors[i]);
                y -= 26;
            }

            string roles = r.Roles.Count == 0 ? "-" : string.Join(", ", r.Roles.Select(x => $"{x.Role} x{x.Count}"));
            Text(d, UTSLocalization.Tr("uts.sessionstats.roles", roles), 14, TMPro.FontStyles.Normal, Color.white,
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
        private const float CardW = 460, CardH = 200, CardGap = 20;

        [HideFromIl2Cpp]
        private void BuildTitles(GameObject panel, SessionStats.Table t, byte me) {
            if (t.Titles.Count == 0) {
                Text(panel, UTSLocalization.Tr("uts.sessionstats.titles_none"), 16, TMPro.FontStyles.Italic, ColMuted,
                     new Vector2(TableX, TopY), new Vector2(PanelW - 60, 30));
                return;
            }
            int i = 0;
            foreach (var ti in t.Titles) {
                int col = i % 3, row = i / 3;
                var card = Box(panel, new Vector2(TableX + col * (CardW + CardGap), TopY - row * (CardH + CardGap)),
                               new Vector2(CardW, CardH),
                               ti.Holders.Contains(me) ? new Color(0.45f, 0.8f, 1f, 0.12f) : new Color(1f, 1f, 1f, 0.05f));
                string key = $"uts.sessionstats.title_{ti.Id}";
                Text(card, UTSLocalization.Tr(key + "_name"), 22, TMPro.FontStyles.Bold, ColAccent, new Vector2(18, -14), new Vector2(CardW - 36, 32));
                Text(card, TitleValue(ti), 14, TMPro.FontStyles.Normal, ColMuted, new Vector2(18, -50), new Vector2(CardW - 36, 40));
                float y = -98;
                if (ti.Id == SessionStats.TitleNemesis && ti.Holders.Count == 2) {
                    var a = TheOtherRoles.Helpers.playerById(ti.Holders[0]);
                    var b = TheOtherRoles.Helpers.playerById(ti.Holders[1]);
                    HolderLine(card, a, y);
                    Text(card, UTSLocalization.Tr("uts.sessionstats.nemesis_of"), 14, TMPro.FontStyles.Italic, ColMuted,
                         new Vector2(52, y - 30), new Vector2(CardW - 70, 22));
                    HolderLine(card, b, y - 56);
                } else {
                    foreach (byte h in ti.Holders) { HolderLine(card, TheOtherRoles.Helpers.playerById(h), y); y -= 32; }
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
        private static void HolderLine(GameObject card, PlayerControl pc, float y) {
            var dot = Box(card, new Vector2(18, y - 2), new Vector2(22, 22), pc != null ? PlayerColor(pc) : ColOthers);
            dot.GetComponent<Image>().sprite = Circle();
            Text(card, pc?.Data?.PlayerName ?? UTSLocalization.Tr("uts.sessionstats.others"), 18, TMPro.FontStyles.Bold, Color.white,
                 new Vector2(52, y), new Vector2(CardW - 70, 28));
        }

        [HideFromIl2Cpp]
        private static List<(float, Color, string)> Opponents(List<(byte Id, int Count)> list) {
            var res = new List<(float, Color, string)>();
            foreach (var (id, n) in list) {
                if (id == SessionStats.OthersId) {
                    res.Add((n, ColOthers, $"{UTSLocalization.Tr("uts.sessionstats.others")} {n}x"));
                    continue;
                }
                var pc = TheOtherRoles.Helpers.playerById(id);
                res.Add((n, pc != null ? PlayerColor(pc) : ColOthers, $"{(pc?.Data?.PlayerName ?? "?")} {n}x"));
            }
            return res;
        }

        private const float PieSize = 150;

        [HideFromIl2Cpp]
        private void PieBlock(GameObject parent, string title, List<(float Value, Color Col, string Text)> slices, Vector2 pos) {
            Text(parent, title, 15, TMPro.FontStyles.Bold, ColAccent, pos, new Vector2(200, 22));
            var holder = Box(parent, pos + new Vector2(25, -28), new Vector2(PieSize, PieSize), new Color(0, 0, 0, 0));
            float total = slices.Sum(s => s.Value);
            if (total <= 0f) {
                var e = Box(holder, Vector2.zero, new Vector2(PieSize, PieSize), ColEmpty);
                e.GetComponent<Image>().sprite = Circle();
                Text(parent, UTSLocalization.Tr("uts.sessionstats.none"), 13, TMPro.FontStyles.Italic, ColMuted,
                     pos + new Vector2(0, -36 - PieSize), new Vector2(200, 20));
                return;
            }
            // slice k covers [c(k-1), c(k)]: draw [0, c(k)] from the last slice to the first, each on top
            var cum = new float[slices.Count];
            float acc = 0f;
            for (int i = 0; i < slices.Count; i++) { acc += slices[i].Value / total; cum[i] = acc; }
            for (int i = slices.Count - 1; i >= 0; i--) {
                var go = Box(holder, Vector2.zero, new Vector2(PieSize, PieSize), slices[i].Col);
                var img = go.GetComponent<Image>();
                img.sprite = Circle();
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Radial360;
                img.fillOrigin = 2;   // Origin360.Top
                img.fillClockwise = true;
                img.fillAmount = i == slices.Count - 1 ? 1f : cum[i];
            }
            // legend
            float ly = -36 - PieSize;
            foreach (var s in slices) {
                var sq = Box(parent, pos + new Vector2(0, ly - 4), new Vector2(12, 12), s.Col);
                sq.GetComponent<Image>().sprite = Circle();
                int pct = Mathf.RoundToInt(s.Value / total * 100f);
                Text(parent, $"{s.Text} ({pct}%)", 13, TMPro.FontStyles.Normal, Color.white, pos + new Vector2(18, ly), new Vector2(190, 20));
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

        internal static Sprite Solid(Color color) {
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

        // White anti-aliased disc; tinted by Image.color. Built once, never destroyed.
        internal static Sprite Circle() {
            if (circle != null) return circle;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float r = n / 2f - 1f;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) {
                    float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(false, true);
            circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex);
            DontDestroyOnLoad(circle);
            return circle;
        }

        /// <summary>A rect anchored top-left of its parent, positioned by its top-left corner.</summary>
        [HideFromIl2Cpp]
        internal static GameObject Box(GameObject parent, Vector2 topLeft, Vector2 size, Color color) {
            var go = new GameObject("B");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = topLeft; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = Solid(Color.white);
            img.color = color;
            return go;
        }

        [HideFromIl2Cpp]
        internal static TMPro.TextMeshProUGUI Text(GameObject parent, string text, float size, TMPro.FontStyles style, Color color,
                                                  Vector2 topLeft, Vector2 box) =>
            Label(parent, text, size, style, color, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), topLeft, box,
                  TMPro.TextAlignmentOptions.TopLeft);

        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Cell(GameObject row, string text, int col, float size, Color color,
                                                  TMPro.FontStyles style = TMPro.FontStyles.Normal) =>
            Label(row, text, size, style, color, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(ColX[col], 0), new Vector2(ColW[col], 0),
                  col == 0 ? TMPro.TextAlignmentOptions.Left : TMPro.TextAlignmentOptions.Center);

        [HideFromIl2Cpp]
        internal static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size, TMPro.FontStyles style, Color color,
                Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 sizeDelta, TMPro.TextAlignmentOptions align) {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = color;
            t.alignment = align; t.enableWordWrapping = true;
            t.raycastTarget = false;
            return t;
        }

        [HideFromIl2Cpp]
        internal static GameObject MakeButton(GameObject parent, string label, Vector2 pos, Vector2 size, Color color, Action onClick) {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            go.AddComponent<Image>().sprite = Solid(color);
            Label(go, label, 15, TMPro.FontStyles.Bold, Color.white, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                  Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Center);
            go.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
            return go;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); SessionStats.ClearTable(); }
        }
    }
}
