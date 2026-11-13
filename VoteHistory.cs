// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * VoteHistory - the results of the earlier meetings of this round, in the meeting (User 2026-11-13).
 *
 * WHAT IS SHOWN
 * Exactly what the viewer saw when that meeting's results came up, nothing more:
 *   - Anonymous votes off: who voted for whom ("Blue (2): Red, Green").
 *   - Anonymous votes on: only how many votes each plate and Skip got ("Blue (2)").
 *   - The viewer could see the colours anyway: TOR's own rule in MeetingHudBloopAVoteIconPatch
 *     (MeetingPatch.cs:143): anonymous off, OR dead with ghostsSeeVotes, OR the Mayor with
 *     "Mayor Can See Vote Colors" and enough completed tasks. A Mayor gets names from the first meeting
 *     after reaching the task count on; the meetings before stay counts only ("from the moment he can
 *     see it", User).
 *
 * WHERE IT COMES FROM
 * A postfix on MeetingHud.BloopAVoteIcon, the one call that draws every vote icon. So the history holds
 * what was drawn, after every rewrite that happens before: Swapper swap (TOR draws on the swapped plate),
 * Mayor double vote (two icons, shown as x2), the Mayor's split vote (MayorSplitVote bloops the second
 * icon through the same method), Chance's vote multiplier (rewrites TOR's draw list). The visibility
 * test runs once per meeting at the first icon, like TOR's per icon test that cannot change in between.
 * Names: the name the plate showed. A plate with no name (a mode that hides names) falls back to the
 * colour name, so the history never tells more than the plates did.
 *
 * Local only, nothing is sent. Cleared at round start and on lobby join. Option 1352 (General tab, on by
 * default); it reveals nothing that was not on screen, so it is exempt from the UTS host gate.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.UI;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class VoteHistory {
        public const byte SkipId = 253;
        private const int MaxLines = 24;

        public static CustomOption Option;

        private sealed class Vote { public string Name; public string Hex; }          // Name null = anonymous
        private sealed class Target { public byte Id; public string Name; public string Hex; public readonly List<Vote> Votes = new(); }
        private sealed class Meeting { public int No; public bool Named; public readonly List<Target> Targets = new(); }

        private static readonly List<Meeting> meetings = new();
        private static int meetingNo;
        private static Meeting current;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(1352, Types.General, "Vote History In Meetings", true);
                UTSLocalization.BindOptionTitle(Option, "uts.votehistory.option_name");
                UTSGate.MarkAlwaysActive(Option);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[VoteHistory] CreateOptions failed: {e}");
            }
        }

        private static bool Enabled => Option != null && UTSGate.Bool(Option);

        // ---- visibility: TOR's rule from MeetingHudBloopAVoteIconPatch ------------------------------
        private static System.Reflection.FieldInfo fiGhostsSeeVotes;
        private static bool ghostsResolved;

        private static bool GhostsSeeVotes() {
            try {
                if (!ghostsResolved) {
                    ghostsResolved = true;
                    fiGhostsSeeVotes = typeof(CustomOption).Assembly.GetType("TheOtherRoles.TORMapOptions")
                        ?.GetField("ghostsSeeVotes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                return fiGhostsSeeVotes != null && (bool)fiGhostsSeeVotes.GetValue(null);
            } catch { return false; }
        }

        internal static bool CanSeeVoters() {
            try {
                if (!GameManager.Instance.LogicOptions.GetAnonymousVotes()) return true;
                var lp = PlayerControl.LocalPlayer;
                if (lp == null || lp.Data == null) return false;
                if (lp.Data.IsDead && GhostsSeeVotes()) return true;
                return Mayor.mayor != null && Mayor.mayor.PlayerId == lp.PlayerId && Mayor.canSeeVoteColors
                       && TasksHandler.taskInfo(lp.Data).Item1 >= Mayor.tasksNeededToSeeVoteColors;
            } catch { return false; }
        }

        // ---- names as the plates showed them ----------------------------------------------------------
        private static readonly Regex Tags = new Regex("<[^>]*>");

        private static (string name, string hex) Who(MeetingHud hud, byte id) {
            var info = GameData.Instance != null ? GameData.Instance.GetPlayerById(id) : null;
            int colorId = info != null && info.DefaultOutfit != null ? info.DefaultOutfit.ColorId : -1;
            string hex = "FFFFFF";
            if (colorId >= 0 && colorId < Palette.PlayerColors.Length) hex = ColorUtility.ToHtmlStringRGB(Readable(Palette.PlayerColors[colorId]));
            string plate = null;
            try {
                foreach (var a in hud.playerStates)
                    if (a != null && a.TargetPlayerId == id) { plate = a.NameText != null ? a.NameText.text : null; break; }
            } catch { }
            string shown = plate == null ? null : Tags.Replace(plate, "").Split('\n')[0].Trim();
            string name;
            if (plate != null && string.IsNullOrWhiteSpace(shown)) {
                // the plate hides the name: say no more than the icon colour does
                name = "?";
                try { if (colorId >= 0) name = Palette.GetColorName(colorId); } catch { }
            } else {
                name = info != null ? info.PlayerName : "?";
            }
            return ((name ?? "?").Replace("<", "").Replace(">", ""), hex);
        }

        // dark player colours (black, brown, maroon) on the dark card: lift them a little
        private static Color Readable(Color c) {
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return lum >= 0.4f ? c : Color.Lerp(c, Color.white, 0.45f);
        }

        // ---- recording ----------------------------------------------------------------------------------
        private static void Record(MeetingHud hud, NetworkedPlayerInfo voter, Transform parent) {
            if (!Enabled || hud == null || voter == null || parent == null) return;
            if (current == null) {
                current = new Meeting { No = meetingNo, Named = CanSeeVoters() };
                meetings.Add(current);
            }
            byte target;
            if (hud.SkippedVoting != null && parent.Pointer == hud.SkippedVoting.transform.Pointer) target = SkipId;
            else {
                var area = parent.GetComponent<PlayerVoteArea>();
                if (area == null) return;
                target = area.TargetPlayerId;
            }
            var t = current.Targets.FirstOrDefault(x => x.Id == target);
            if (t == null) {
                t = new Target { Id = target };
                if (target == SkipId) { t.Name = UTSLocalization.Tr("uts.votehistory.skip"); t.Hex = "B4BDC9"; }
                else (t.Name, t.Hex) = Who(hud, target);
                current.Targets.Add(t);
            }
            var v = new Vote();
            if (current.Named) (v.Name, v.Hex) = Who(hud, voter.PlayerId);
            t.Votes.Add(v);
            dirty = true;
            RefreshIfOpen();
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.BloopAVoteIcon))]
        private static class BloopPatch {
            public static void Postfix(MeetingHud __instance, NetworkedPlayerInfo __0, Transform __2) {
                try { Record(__instance, __0, __2); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[VoteHistory] record failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        private static class MeetingStartPatch {
            public static void Postfix() {
                meetingNo++;
                current = null;
                try { ShowButton(Enabled && meetings.Count > 0); } catch { }
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
        private static class MeetingEndPatch {
            public static void Postfix() { try { Hide(); } catch { } }
        }

        private static void Clear() { meetings.Clear(); meetingNo = 0; current = null; dirty = true; Hide(); }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        private static class RoundStartResetPatch { public static void Postfix() => Clear(); }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyResetPatch { public static void Postfix() => Clear(); }

        // ---- text -----------------------------------------------------------------------------------------
        internal static string BuildText() {
            var lines = new List<string>();
            foreach (var m in Enumerable.Reverse(meetings)) {
                if (lines.Count >= MaxLines) break;
                lines.Add($"<color=#5FD8CF><b>{UTSLocalization.Tr("uts.votehistory.meeting", m.No)}</b></color>");
                foreach (var t in m.Targets.OrderBy(x => x.Id == SkipId ? 1 : 0).ThenByDescending(x => x.Votes.Count)) {
                    if (lines.Count >= MaxLines) break;
                    string head = $"<color=#{t.Hex}>{t.Name}</color> ({t.Votes.Count})";
                    var named = t.Votes.Where(v => v.Name != null)
                        .GroupBy(v => v.Name + "\u0001" + v.Hex)
                        .Select(g => $"<color=#{g.First().Hex}>{g.First().Name}</color>" + (g.Count() > 1 ? $" x{g.Count()}" : ""))
                        .ToList();
                    int anon = t.Votes.Count(v => v.Name == null);
                    if (anon > 0 && named.Count > 0) named.Add($"<color=#8C9AAD>{UTSLocalization.Tr("uts.votehistory.anon", anon)}</color>");
                    lines.Add(named.Count > 0 ? $"   {head}: {string.Join(", ", named)}" : $"   {head}");
                }
            }
            return string.Join("\n", lines);
        }

        // ---- UI: a button in the top left corner of the meeting, below the task bar (it stays on screen in the meeting and the button used to cover it, User 13.11.), a card below the button
        private static GameObject root, card, button;
        private static TMPro.TextMeshProUGUI text, buttonText;
        private static bool open, dirty = true;
        private static string shown = "";

        private static void ShowButton(bool show) {
            if (!show) { Hide(); return; }
            if (root == null) Build();
            if (root == null) return;
            root.SetActive(true);
            card.SetActive(open);
            if (open) Render();
        }

        private static void Hide() {
            if (root != null) root.SetActive(false);
        }

        private static void Toggle() {
            open = !open;
            if (card != null) card.SetActive(open);
            if (open) Render();
        }

        private static void Render() {
            if (text == null) return;
            if (!dirty && shown.Length > 0) return;
            string body = BuildText();
            if (body != shown) { VanillaUI.SetText(text, body); shown = body; }
            dirty = false;
        }

        // results came in while the card is open: show them right away (called from the bloop postfix path)
        internal static void RefreshIfOpen() { if (open && root != null && root.activeSelf) Render(); }

        private static void Build() {
            try {
                root = VanillaUI.Canvas("UTSVoteHistory", 7950, false);
                button = VanillaUI.Button(root, UTSLocalization.Tr("uts.votehistory.button"), new Vector2(24, -104), new Vector2(210, 48),
                    VanillaUI.Teal, Toggle, out buttonText, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), 24f);

                card = VanillaUI.Rounded(root, new Color(VanillaUI.Body.r, VanillaUI.Body.g, VanillaUI.Body.b, 0.92f), VanillaUI.RadiusBox + 4);
                card.name = "Card";
                var ring = VanillaUI.Ring(card, VanillaUI.Frame, VanillaUI.RadiusBox + 4, 3);
                VanillaUI.Stretch(ring);
                ring.AddComponent<LayoutElement>().ignoreLayout = true;
                var rt = card.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(24, -164);
                rt.sizeDelta = new Vector2(560, 0);
                var fit = card.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var layout = card.AddComponent<VerticalLayoutGroup>();
                var pad = new RectOffset(); pad.left = 16; pad.right = 16; pad.top = 12; pad.bottom = 12;
                layout.padding = pad;
                layout.spacing = 6;
                layout.childControlHeight = true; layout.childControlWidth = true;
                layout.childForceExpandHeight = false;

                var tab = new GameObject("TitleTab");
                tab.transform.SetParent(card.transform, false);
                tab.AddComponent<RectTransform>();
                var le = tab.AddComponent<LayoutElement>();
                le.preferredHeight = 34; le.minHeight = 34;
                var timg = tab.AddComponent<Image>();
                timg.sprite = VanillaUI.Slant(); timg.type = Image.Type.Sliced; timg.color = VanillaUI.Teal; timg.raycastTarget = false;
                var title = VanillaUI.Label(tab, UTSLocalization.Tr("uts.votehistory.title"), 24, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
                title.rectTransform.offsetMin = new Vector2(14, 0); title.rectTransform.offsetMax = new Vector2(-150, 0);
                VanillaUI.Heading(title);

                var t = new GameObject("T");
                t.transform.SetParent(card.transform, false);
                t.AddComponent<RectTransform>();
                text = t.AddComponent<TMPro.TextMeshProUGUI>();
                text.fontSize = 19;
                text.color = Color.white;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = true;
                text.raycastTarget = false;
                var font = VanillaUI.Font();
                if (font != null) text.font = font;
                shown = "";
                card.SetActive(false);
                root.SetActive(false);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[VoteHistory] UI failed: {e.Message}");
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
            }
        }
    }
}
