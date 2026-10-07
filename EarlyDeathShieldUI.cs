// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * EarlyDeathShieldUI - the host's lobby panel for the pink early-death shield, and the driver of
 * both the shield and the death-time clock.
 *
 * Every lobby player with their recorded rounds, their average survived share, how that compares to
 * the lobby average, and whether they get the shield next round. The host can force the shield on or
 * off per player; "Auto" hands the decision back to the numbers. Everybody else gets the same entry
 * with a read-only view of the host's numbers (their own line highlighted), sent by the host. Same
 * VanillaUI canvas shape as NewcomerShieldUI; its lobby entry sits in the lobby menu.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public class EarlyDeathShieldUI : MonoBehaviour {
        public static EarlyDeathShieldUI Instance { get; private set; }

        public EarlyDeathShieldUI(IntPtr ptr) : base(ptr) { }

        private GameObject panelRoot;
        private float nextPoll;
        private bool viewerOpen;
        // Autotest: im Freeplay die Ansicht fuer Nicht-Hosts mit Beispielzahlen oeffnen (Lobby-Test braucht
        // sonst einen zweiten Spieler)
        internal static BepInEx.Configuration.ConfigEntry<bool> DiagViewer;
        private float diagAt = -1f;
        private float viewerStatsAt = -1f;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            // the lobby entry lives in the lobby menu (LobbyMenuUI)
            LobbyMenu.Add("uts.earlydeath", 20, () => Instance != null && Instance.ShouldShow(),
                          () => Instance != null ? Instance.ButtonLabel() : "", () => Instance?.Toggle(),
                          VanillaUI.Pink);
            // badge on the menu tile (host only): players who get the pink shield next round
            LobbyMenu.Badges["uts.earlydeath"] = () => {
                try { return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost ? EarlyDeathShield.Evaluate().ShieldCount : 0; }
                catch { return 0; }
            };
        }

        public void Update() {
            // The drivers, every frame and before any early return (both throttle themselves where
            // needed; the clock must not, it measures meeting boundaries).
            DeathTimeHistory.Tick();
            EarlyDeathShield.Tick();
            if (panelRoot != null && Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;
            DiagTick();

            if (panelRoot != null && !LobbyScreen.Exists && !(DiagViewer != null && DiagViewer.Value)) Close();
            if (panelRoot != null && viewerOpen) {
                var st = EarlyDeathShield.LastStats;
                if (st != null && st.ReceivedAt != viewerStatsAt) { Close(); OpenViewer(); }
            }
        }

        [HideFromIl2Cpp]
        private bool ShouldShow() {
            try {
                if (!LobbyScreen.Exists || AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return EarlyDeathShield.Enabled != null && EarlyDeathShield.Enabled.getBool();
                // everyone else: only while the host's UTS actually runs the shield
                return UTSGate.Bool(EarlyDeathShield.Enabled);
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        private string ButtonLabel() =>
            AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                ? UTSLocalization.Tr("uts.earlydeath.lobby_button", EarlyDeathShield.Evaluate().ShieldCount)
                : UTSLocalization.Tr("uts.earlydeath.stats_button");

        [HideFromIl2Cpp]
        public void Toggle() {
            if (panelRoot != null) Close();
            else if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) Open();
            else { EarlyDeathShield.RequestStats(); OpenViewer(); }
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; LobbyPanelGuard.Closed(); }
            viewerOpen = false;
        }

        /*
         * Both panels grew 52 per player up to a fixed cap (820 / 860), so a full lobby ran past it:
         * the last row sat under the Close button and further rows fell off the panel (User
         * 2026-10-02, 14 players). Now the rows shrink instead, so title, every row and the Close
         * button always fit the 1080 reference height.
         */
        private const float MaxPanelH = 1010, FooterH = 90, FullStep = 56, MinStep = 32, Inset = 34f;

        private static float RowStep(int rows, float top) =>
            rows <= 0 ? FullStep : Mathf.Clamp((MaxPanelH - top - FooterH) / rows, MinStep, FullStep);

        [HideFromIl2Cpp]
        private void Open() {
            try {
                // Newcomer and early-death panel share the screen centre: only one at a time.
                NewcomerShieldUI.Instance?.Close();
                SessionStatsUI.Instance?.Close();

                panelRoot = VanillaUI.Canvas("UTSEarlyDeathShieldUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, Close);

                var ev = EarlyDeathShield.Evaluate();
                const float top = 160f;
                float step = RowStep(ev.Rows.Count, top);
                float height = Mathf.Clamp(top + ev.Rows.Count * step + FooterH, 370, MaxPanelH);

                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(1160, height));   // room for the forced rows' meetings button
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.earlydeath.title"));
                VanillaUI.Subtitle(panel, UTSLocalization.Tr("uts.earlydeath.subtitle"), y - 6f, 40f);

                string average = ev.HasAverage
                    ? UTSLocalization.Tr("uts.earlydeath.average",
                          Mathf.RoundToInt(ev.LobbyMean * 100f), ev.ThresholdPercent,
                          Mathf.RoundToInt(ev.LobbyMean * ev.ThresholdPercent))
                    : UTSLocalization.Tr("uts.earlydeath.no_average", 3, ev.MinRounds);
                VanillaUI.Label(panel, average, 15, Color.white,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -top + 30f), new Vector2(-2 * Inset, 26), TMPro.TextAlignmentOptions.Top, TMPro.FontStyles.Bold);

                y = -top;
                foreach (var v in ev.Rows) {
                    BuildRow(panel, v, ev, y, step - 6);
                    y -= step;
                }

                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.earlydeath.close"), Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] panel failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, EarlyDeathShield.Verdict v, EarlyDeathShield.Evaluation ev, float y, float rowH) {
            var holder = VanillaUI.Row(parent, y, rowH, Inset);
            float btnH = Mathf.Min(40f, rowH - 8f);

            VanillaUI.Label(holder, v.Name, 17, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(16, 0), new Vector2(220, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
            SessionStatsUI.LinkName(holder, new Vector2(4, -3), new Vector2(228, rowH - 6), v.PlayerId);

            string stats = StatsText(v.Qualified, v.Stat.Rounds, v.Stat.Mean, ev.MinRounds, ev.HasAverage, ev.LobbyMean);
            VanillaUI.Label(holder, stats, 14, v.Qualified ? Color.white : VanillaUI.Muted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(242, 0), new Vector2(300, 0), TMPro.TextAlignmentOptions.Left);

            string state = v.Override == DeathTimeHistory.OverrideOn ? "uts.earlydeath.state_forced"
                         : v.Override == DeathTimeHistory.OverrideOff ? "uts.earlydeath.state_excluded"
                         : v.Shield ? "uts.earlydeath.state_auto" : "uts.earlydeath.state_none";
            VanillaUI.Label(holder, UTSLocalization.Tr(state), 14, v.Shield ? VanillaUI.Pink : VanillaUI.Muted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(548, 0), new Vector2(170, 0), TMPro.TextAlignmentOptions.Left);

            var captured = v;
            VanillaUI.Button(holder, UTSLocalization.Tr(v.Shield
                           ? "uts.earlydeath.btn_unprotect" : "uts.earlydeath.btn_protect"),
                       new Vector2(-10, 0), new Vector2(136, btnH),
                       v.Shield ? VanillaUI.Red : VanillaUI.Pink,
                       () => { EarlyDeathShield.ToggleOverride(captured); Rebuild(); },
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                       pivot: new Vector2(1, 0.5f));

            if (v.Override != DeathTimeHistory.OverrideAuto)
                VanillaUI.Button(holder, UTSLocalization.Tr("uts.earlydeath.btn_auto"),
                           new Vector2(-154, 0), new Vector2(84, btnH), VanillaUI.Grey,
                           () => { EarlyDeathShield.ClearOverride(captured); Rebuild(); },
                           anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                           pivot: new Vector2(1, 0.5f));

            // forced only: how long the shield lasts, 1 -> 2 -> 3 meetings (User 2026-10-02)
            if (v.Override == DeathTimeHistory.OverrideOn)
                VanillaUI.Button(holder, UTSLocalization.Tr("uts.earlydeath.btn_meetings", v.Meetings),
                           new Vector2(-246, 0), new Vector2(130, btnH), VanillaUI.Teal,
                           () => { EarlyDeathShield.CycleMeetings(captured); Rebuild(); },
                           anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                           pivot: new Vector2(1, 0.5f));
        }

        /// <summary>"12 rounds, survives 45%, 82% of the average" - the ratio only once there is an average.</summary>
        [HideFromIl2Cpp]
        private static string StatsText(bool qualified, int rounds, float mean, int minRounds, bool hasAverage, float lobbyMean) {
            if (!qualified) return UTSLocalization.Tr("uts.earlydeath.stats_few", rounds, minRounds);
            int pct = Mathf.RoundToInt(mean * 100f);
            if (!hasAverage || lobbyMean <= 0.001f) return UTSLocalization.Tr("uts.earlydeath.stats", rounds, pct);
            return UTSLocalization.Tr("uts.earlydeath.stats_ratio", rounds, pct, Mathf.RoundToInt(mean / lobbyMean * 100f));
        }

        /// <summary>Autotest (UIGallery): the guests' viewer, whoever we are.</summary>
        [HideFromIl2Cpp]
        public void DiagOpenViewer() => OpenViewer();

        /// <summary>Read-only view for everyone who is not the host: the host's numbers, own line highlighted.</summary>
        [HideFromIl2Cpp]
        private void OpenViewer() {
            try {
                NewcomerShieldUI.Instance?.Close();
                SessionStatsUI.Instance?.Close();
                viewerOpen = true;
                var st = EarlyDeathShield.LastStats;
                viewerStatsAt = st != null ? st.ReceivedAt : -1f;

                panelRoot = VanillaUI.Canvas("UTSEarlyDeathStatsUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, Close);

                // only rows of players who are still here, in lobby order
                var rows = new List<(EarlyDeathShield.StatsRow Row, string Name, bool Me)>();
                byte me = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
                if (st != null)
                    foreach (var r in st.Rows) {
                        var pc = TheOtherRoles.Helpers.playerById(r.PlayerId);
                        if (pc == null || pc.Data == null || pc.Data.Disconnected) continue;
                        rows.Add((r, pc.Data.PlayerName ?? "?", r.PlayerId == me));
                    }
                const float top = 190f;
                float step = RowStep(rows.Count, top);
                float height = Mathf.Clamp(top + rows.Count * step + FooterH, 400, MaxPanelH);

                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(1000, height));    // room for "forced, until meeting 2"
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.earlydeath.viewer_title"));
                VanillaUI.Subtitle(panel, UTSLocalization.Tr("uts.earlydeath.viewer_subtitle"), y - 6f, 40f);

                if (st == null) {
                    VanillaUI.Label(panel, UTSLocalization.Tr("uts.earlydeath.waiting"), 16, Color.white,
                          new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, -top + 50f), new Vector2(-2 * Inset, 30), TMPro.TextAlignmentOptions.Top, TMPro.FontStyles.Bold);
                } else {
                    string average = st.HasAverage
                        ? UTSLocalization.Tr("uts.earlydeath.average", Mathf.RoundToInt(st.LobbyMean * 100f), st.ThresholdPercent,
                                             Mathf.RoundToInt(st.LobbyMean * st.ThresholdPercent))
                        : UTSLocalization.Tr("uts.earlydeath.no_average", 3, st.MinRounds);
                    VanillaUI.Label(panel, average, 15, Color.white,
                          new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, -top + 60f), new Vector2(-2 * Inset, 26), TMPro.TextAlignmentOptions.Top, TMPro.FontStyles.Bold);

                    // the own comparison in one sentence, above the table
                    var mine = rows.Find(x => x.Me);
                    string you = mine.Row == null ? ""
                        : !mine.Row.Qualified ? UTSLocalization.Tr("uts.earlydeath.you_few", mine.Row.Rounds, st.MinRounds)
                        : st.HasAverage && st.LobbyMean > 0.001f
                            ? UTSLocalization.Tr("uts.earlydeath.you", Mathf.RoundToInt(mine.Row.Mean * 100f),
                                                 Mathf.RoundToInt(mine.Row.Mean / st.LobbyMean * 100f))
                            : UTSLocalization.Tr("uts.earlydeath.you_noavg", Mathf.RoundToInt(mine.Row.Mean * 100f));
                    VanillaUI.Label(panel, you, 15, VanillaUI.Pink,
                          new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                          new Vector2(0, -top + 32f), new Vector2(-2 * Inset, 26), TMPro.TextAlignmentOptions.Top, TMPro.FontStyles.Bold);

                    y = -top;
                    foreach (var (r, name, isMe) in rows) {
                        BuildViewerRow(panel, r, name, isMe, st, y, step - 6);
                        y -= step;
                    }
                }

                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.earlydeath.close"), Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] stats viewer failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildViewerRow(GameObject parent, EarlyDeathShield.StatsRow r, string name, bool isMe,
                                    EarlyDeathShield.StatsTable st, float y, float rowH) {
            var holder = VanillaUI.Row(parent, y, rowH, Inset, isMe ? VanillaUI.FieldLight : VanillaUI.Field);

            VanillaUI.Label(holder, isMe ? $"{name} {UTSLocalization.Tr("uts.earlydeath.you_tag")}" : name, 17,
                  isMe ? VanillaUI.Pink : Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(16, 0), new Vector2(240, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
            SessionStatsUI.LinkName(holder, new Vector2(4, -3), new Vector2(248, rowH - 6), r.PlayerId);
            VanillaUI.Label(holder, StatsText(r.Qualified, r.Rounds, r.Mean, st.MinRounds, st.HasAverage, st.LobbyMean), 14,
                  r.Qualified ? Color.white : VanillaUI.Muted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(264, 0), new Vector2(380, 0), TMPro.TextAlignmentOptions.Left);
            // same states as the host panel, host decisions openly marked
            string state = r.Override == DeathTimeHistory.OverrideOn ? "uts.earlydeath.state_forced"
                         : r.Override == DeathTimeHistory.OverrideOff ? "uts.earlydeath.state_excluded"
                         : r.Shield ? "uts.earlydeath.state_auto" : "uts.earlydeath.state_none";
            string stateText = UTSLocalization.Tr(state);
            if (r.Override == DeathTimeHistory.OverrideOn && r.Meetings > 1)
                stateText = UTSLocalization.Tr("uts.earlydeath.until_meeting", stateText, r.Meetings);
            VanillaUI.Label(holder, stateText, 14, r.Shield ? VanillaUI.Pink : VanillaUI.Muted,
                  new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f),
                  new Vector2(-16, 0), new Vector2(250, 0), TMPro.TextAlignmentOptions.Right);
        }

        [HideFromIl2Cpp]
        private void DiagTick() {
            if (DiagViewer == null || !DiagViewer.Value) return;
            if (ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) { diagAt = -1f; return; }
            if (diagAt < 0f) { diagAt = Time.realtimeSinceStartup + 10f; return; }
            if (diagAt == 0f || Time.realtimeSinceStartup < diagAt) return;
            diagAt = 0f;
            EarlyDeathShield.DiagSampleStats();
            OpenViewer();
            UsefulTORStuffPlugin.Logger?.LogInfo("[EarlyDeathShield] diag: stats viewer opened with sample numbers");
        }

        [HideFromIl2Cpp]
        private void Rebuild() {
            EarlyDeathShield.RefreshPreview();
            Close();
            Open();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); EarlyDeathShield.ClearStats(); }
        }
    }
}
