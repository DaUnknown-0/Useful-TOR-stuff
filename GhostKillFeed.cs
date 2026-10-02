// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * GhostKillFeed - a running list of every death of the round, for dead players only (User 2026-10-02).
 *
 * "Red (Sheriff) killed Blue (Medic)", "Green (Jester) was voted out", "Pink (Guesser) guessed Lime" ...
 * Every death type TOR records, newest on top, at most eight lines. Shown while the local player is
 * dead and no meeting is on screen; living players never see it.
 *
 * Source: TOR's GameHistory.deadPlayers, which every client fills for every death (DeathTimeHistory
 * reads the same list at round end). The first entry per victim is the real death, later ones are
 * TOR bookkeeping (an exile followed by a "Kill" without a killer). Roles are read the moment the entry
 * appears, and only when the viewer's own TOR setting "Ghosts See Roles" is on: the feed must not show
 * a ghost more than TOR itself would.
 *
 * Option 1396 (General tab, off by default); guests follow the host's setting through UTSGate.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.UI;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class GhostKillFeed {

        public static CustomOption Enabled;

        private const int MaxLines = 8;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1396, Types.General, "Kill Feed For Ghosts", false, null, true);
                UTSLocalization.BindOptionTitle(Enabled, "uts.killfeed.option_name");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[GhostKillFeed] CreateOptions failed: {e}");
            }
        }

        private static readonly List<string> lines = new List<string>();     // newest first
        private static readonly HashSet<byte> seen = new HashSet<byte>();
        private static float nextTick;
        private static GameObject root;
        private static TMPro.TextMeshProUGUI text;
        private static string shown = "";
        private static bool diagHold;   // autotest: keep the sample lines on screen

        // Driven from SessionStatsUI.Update (a plugin MonoBehaviour, so no foreign patch can skip it).
        public static void Tick() {
            try {
                if (diagHold || Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 0.25f;

                var client = AmongUsClient.Instance;
                bool inRound = client != null && ShipStatus.Instance != null && client.IsGameStarted;
                if (!inRound) { Reset(); return; }

                bool on = UTSGate.Bool(Enabled);
                if (on) Collect();
                var lp = PlayerControl.LocalPlayer;
                bool show = on && lp != null && lp.Data != null && lp.Data.IsDead
                            && MeetingHud.Instance == null && ExileController.Instance == null && lines.Count > 0;
                Show(show);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[GhostKillFeed] tick failed: {e.Message}");
                nextTick = Time.realtimeSinceStartup + 5f;
            }
        }

        private static void Reset() {
            if (lines.Count == 0 && seen.Count == 0 && (root == null || !root.activeSelf)) return;
            lines.Clear();
            seen.Clear();
            Show(false);
        }

        private static void Collect() {
            var list = DeathTimeHistory.TorDeadPlayers();
            if (list == null) return;
            foreach (var dp in list) {
                if (dp?.player == null || !seen.Add(dp.player.PlayerId)) continue;
                lines.Insert(0, Line(dp));
                if (lines.Count > MaxLines) lines.RemoveAt(lines.Count - 1);
            }
        }

        // ---- text ----

        private static System.Reflection.FieldInfo fiGhostsSeeRoles;
        private static bool ghostsResolved;

        // The viewer's own TOR setting (BepInEx config, mirrored into TORMapOptions.ghostsSeeRoles).
        private static bool GhostsSeeRoles() {
            try {
                if (!ghostsResolved) {
                    ghostsResolved = true;
                    fiGhostsSeeRoles = typeof(CustomOption).Assembly.GetType("TheOtherRoles.TORMapOptions")
                        ?.GetField("ghostsSeeRoles", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                return fiGhostsSeeRoles == null || (bool)fiGhostsSeeRoles.GetValue(null);
            } catch { return true; }
        }

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        internal static string Who(PlayerControl p, bool roles) {
            if (p == null || p.Data == null) return "?";
            string name = (p.Data.PlayerName ?? "?").Replace("<", "").Replace(">", "");
            Color col = Color.white;
            try {
                int id = p.Data.DefaultOutfit.ColorId;
                if (id >= 0 && id < Palette.PlayerColors.Length) col = Palette.PlayerColors[id];
            } catch { }
            string s = $"<color=#{Hex(col)}>{name}</color>";
            if (!roles) return s;
            try {
                var info = RoleInfo.getRoleInfoForPlayer(p, false)?.FirstOrDefault(i => i != null && !i.isModifier);
                if (info != null) s += $" <size=85%><color=#{Hex(info.color)}>({info.name})</color></size>";
            } catch { }
            return s;
        }

        private static string Line(DeadPlayer dp) => Describe(dp, GhostsSeeRoles());

        /// <summary>One death as a coloured sentence; also used by RoundReplay (roles always on there,
        /// the replay only opens after the round).</summary>
        internal static string Describe(DeadPlayer dp, bool roles) {
            var victim = dp.player;
            var killer = dp.killerIfExisting;
            string v = Who(victim, roles);
            string k = killer != null ? Who(killer, roles) : null;
            bool self = killer != null && killer.PlayerId == victim.PlayerId;
            string key;
            switch (dp.deathReason) {
                case DeadPlayer.CustomDeathReason.Exile:         key = "exile"; break;
                case DeadPlayer.CustomDeathReason.Disconnect:    key = "disconnect"; break;
                case DeadPlayer.CustomDeathReason.LoverSuicide:  key = "lover"; break;
                case DeadPlayer.CustomDeathReason.LawyerSuicide: key = "lawyer"; break;
                case DeadPlayer.CustomDeathReason.Shift:         key = "shift"; break;
                case DeadPlayer.CustomDeathReason.Guess:         key = self ? "guess_self" : k == null ? "died" : "guess"; break;
                case DeadPlayer.CustomDeathReason.Bomb:          key = self ? "bomb_self" : k == null ? "died" : "bomb"; break;
                case DeadPlayer.CustomDeathReason.Arson:         key = k == null ? "died" : "arson"; break;
                case DeadPlayer.CustomDeathReason.WitchExile:    key = k == null ? "died" : "witch"; break;
                default:                                         key = self ? "misfire" : k == null ? "died" : "kill"; break;
            }
            return UTSLocalization.Tr("uts.killfeed." + key, v, k ?? "?");
        }

        // ---- UI: one screen-space text block on the right ----

        private static void Show(bool show) {
            if (!show) {
                if (root != null && root.activeSelf) root.SetActive(false);
                return;
            }
            if (root == null) Build();
            if (root == null) return;
            if (!root.activeSelf) root.SetActive(true);
            string body = $"<b>{UTSLocalization.Tr("uts.killfeed.title")}</b>\n" + string.Join("\n", lines);
            if (body != shown) { text.text = body; shown = body; }
        }

        private static void Build() {
            try {
                root = new GameObject("UTSGhostKillFeed");
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 8000;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                var bg = new GameObject("Bg");
                bg.transform.SetParent(root.transform, false);
                var rt = bg.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
                rt.anchoredPosition = new Vector2(-24, -230);
                rt.sizeDelta = new Vector2(560, 0);
                var img = bg.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0.55f);
                img.raycastTarget = false;
                var fit = bg.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var layout = bg.AddComponent<VerticalLayoutGroup>();
                var pad = new RectOffset(); pad.left = 14; pad.right = 14; pad.top = 10; pad.bottom = 10;
                layout.padding = pad;
                layout.childControlHeight = true; layout.childControlWidth = true;
                layout.childForceExpandHeight = false;

                var t = new GameObject("T");
                t.transform.SetParent(bg.transform, false);
                t.AddComponent<RectTransform>();
                text = t.AddComponent<TMPro.TextMeshProUGUI>();
                text.fontSize = 18;
                text.color = Color.white;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = true;
                text.raycastTarget = false;
                shown = "";
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[GhostKillFeed] UI failed: {e.Message}");
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>Autotest: sample lines, shown regardless of death state.</summary>
        public static void DiagSample() {
            lines.Clear();
            diagHold = true;
            var ps = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null).ToList();
            if (ps.Count < 3) return;
            string W(int i) => Who(ps[i % ps.Count], true);
            lines.Add(UTSLocalization.Tr("uts.killfeed.kill", W(1), W(2)));
            lines.Add(UTSLocalization.Tr("uts.killfeed.exile", W(3), "?"));
            lines.Add(UTSLocalization.Tr("uts.killfeed.guess", W(4), W(0)));
            lines.Add(UTSLocalization.Tr("uts.killfeed.misfire", W(5), W(5)));
            lines.Add(UTSLocalization.Tr("uts.killfeed.bomb", W(6), W(2)));
            if (root == null) Build();
            if (root != null) root.GetComponent<Canvas>().sortingOrder = 9700;   // above the stats panel for the screenshot
            if (root != null) { root.SetActive(true); text.text = $"<b>{UTSLocalization.Tr("uts.killfeed.title")}</b>\n" + string.Join("\n", lines); }
        }
    }
}
