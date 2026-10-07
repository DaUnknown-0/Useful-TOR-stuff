// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * NewcomerShieldUI - the host's lobby panel for the newcomer kill shield.
 *
 * Shows every player in the lobby with the shield they would get next round, and lets the host flip
 * that by hand. The automatic rule (friend code never seen this session) covers the normal case;
 * this is for the ones it cannot know about - somebody who reinstalled, or a player the group simply
 * agrees should get a free round.
 *
 * Host only, and only in the lobby. A screen-space VanillaUI canvas like UTSModSyncUI, for the same
 * reason: the lobby camera's orthographic size would make a world-space overlay a special case.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public class NewcomerShieldUI : MonoBehaviour {
        public static NewcomerShieldUI Instance { get; private set; }

        public NewcomerShieldUI(IntPtr ptr) : base(ptr) { }

        private GameObject panelRoot;
        private float nextPoll;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            // the lobby entry lives in the lobby menu (LobbyMenuUI)
            LobbyMenu.Add("uts.newcomershield", 10, () => Instance != null && Instance.ShouldShow(),
                          () => Instance != null ? Instance.ButtonLabel() : "", () => Instance?.Toggle(),
                          VanillaUI.Green);
        }

        // public, like every other Unity message in this plugin (see UTSModSyncUI).
        public void Update() {
            // The feature's own driver: lobby preview and round-start assignment. It lives on this
            // MonoBehaviour and NOT on a Harmony postfix precisely so no other mod's throwing patch
            // can ever keep it from running (see the NewcomerShield header). Every frame, before
            // this component's own poll throttle; Tick throttles itself.
            NewcomerShield.Tick();

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            // LobbyScreen.Exists, never GameStartManager.Instance: that getter CONSTRUCTS a blank
            // GameStartManager when none exists (LobbyScreen in LobbyLeakGuard.cs has the whole
            // story), and this component polling it from boot onwards is how v1.3.3.15 planted the
            // phantom that degraded every session since.
            if (panelRoot != null && !LobbyScreen.Exists) Close();
        }

        [HideFromIl2Cpp]
        private bool ShouldShow() {
            try {
                if (!LobbyScreen.Exists) return false;                       // lobby only
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return false;
                return NewcomerShield.Enabled != null && NewcomerShield.Enabled.getBool();
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        private int CountShielded() {
            int n = 0;
            try {
                foreach (var p in PlayerControl.AllPlayerControls.ToArray())
                    if (p != null && NewcomerShield.WouldShield(p)) n++;
            } catch { }
            return n;
        }

        [HideFromIl2Cpp]
        private string ButtonLabel() =>
            UTSLocalization.Tr("uts.newcomershield.lobby_button", CountShielded());

        [HideFromIl2Cpp]
        public void Toggle() {
            if (panelRoot != null) Close();
            else Open();
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; LobbyPanelGuard.Closed(); }
        }

        // Title block (title + subtitle) and footer (Close button) heights inside the panel.
        private const float HeadH = 128f, FooterH = 90f, MaxPanelH = 1010f, Inset = 34f;

        [HideFromIl2Cpp]
        private void Open() {
            try {
                // Shares the screen centre with the early-death panel: only one at a time.
                EarlyDeathShieldUI.Instance?.Close();

                panelRoot = VanillaUI.Canvas("UTSNewcomerShieldUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, Close);

                var players = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();
                // Rows shrink so a full lobby still fits with the Close button (the old 780 cap pushed
                // rows under it from about 11 players on, same fix as EarlyDeathShieldUI.RowStep).
                float step = players.Count <= 0 ? 56f : Mathf.Clamp((MaxPanelH - HeadH - FooterH) / players.Count, 32f, 56f);
                float height = Mathf.Clamp(HeadH + players.Count * step + FooterH, 340, MaxPanelH);

                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(860, height));
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.newcomershield.title"));
                VanillaUI.Subtitle(panel, UTSLocalization.Tr("uts.newcomershield.subtitle"), y - 6f, 40f);
                y = -HeadH;

                foreach (var p in players) {
                    BuildRow(panel, p, y, step - 6);
                    y -= step;
                }

                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.newcomershield.close"), Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] panel failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, PlayerControl p, float y, float rowH) {
            var holder = VanillaUI.Row(parent, y, rowH, Inset);

            // colour dot + name
            float dot = Mathf.Min(18f, rowH - 10f);
            var d = VanillaUI.Box(holder, new Vector2(14, -(rowH - dot) / 2f), new Vector2(dot, dot), SessionStatsUI.PlayerColor(p));
            d.GetComponent<UnityEngine.UI.Image>().sprite = VanillaUI.Circle();
            string name = p.Data?.PlayerName ?? "?";
            VanillaUI.Label(holder, name, 17, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(14 + dot + 10, 0), new Vector2(280, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);

            bool shielded = NewcomerShield.WouldShield(p);
            bool manual = NewcomerShield.IsManual(p);
            string state = shielded
                ? UTSLocalization.Tr(manual ? "uts.newcomershield.state_manual" : "uts.newcomershield.state_new")
                : UTSLocalization.Tr("uts.newcomershield.state_known");
            VanillaUI.Label(holder, state, 14, shielded ? VanillaUI.Good : VanillaUI.Muted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(330, 0), new Vector2(270, 0), TMPro.TextAlignmentOptions.Left);

            var captured = p;
            VanillaUI.Button(holder, UTSLocalization.Tr(shielded
                           ? "uts.newcomershield.btn_unprotect" : "uts.newcomershield.btn_protect"),
                       new Vector2(-10, 0), new Vector2(190, Mathf.Min(40f, rowH - 8f)),
                       shielded ? VanillaUI.Red : VanillaUI.Green,
                       () => { NewcomerShield.ToggleManual(captured); Rebuild(); },
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                       pivot: new Vector2(1, 0.5f));
        }

        // The rows carry state, so a change redraws the whole panel rather than patching labels.
        [HideFromIl2Cpp]
        private void Rebuild() {
            Close();
            Open();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); }
        }
    }
}
