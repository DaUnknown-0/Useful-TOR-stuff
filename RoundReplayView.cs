// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * RoundReplayView - the replay's event tools (User 2026-10-02: "a better view for events, filters").
 *
 *  - Filter row above the map: one switch per event kind. Tasks and devices start off, they are most
 *    of the noise. Meetings always stay, they split the round.
 *  - The player chips under the map double as the player filter: the events he did or suffered, or
 *    (host only, who has the line-of-sight data) also the ones whose spot he could see.
 *  - Event list right of the map: follows the playback, a click starts 2 s ahead of the event, the
 *    wheel scrolls it (and keeps it from following for a few seconds).
 *  - Previous/next event (buttons and arrow keys), optionally stop at every kill.
 *  - The timeline split into phases by the meetings; "M1", "M2"... jump to a meeting.
 *  - On the map: halos on the current event's actor (yellow) and target (red), a pulse on its spot,
 *    the chosen player's trail over the last 10 s.
 *  - Meeting card: every meeting's vote, recorded on every client from MeetingHud.VotingComplete,
 *    holds the playback for 5 s (User 2026-10-02). With anonymous votes on, only the counts.
 *
 * The filters are static, so a second look at the replay keeps them.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static partial class RoundReplay {

        // ====================================================================
        // Filters
        // ====================================================================
        private static readonly string[] CatKeys = {
            "uts.replay.cat.kills", "uts.replay.cat.abilities", "uts.replay.cat.vents", "uts.replay.cat.sabotage",
            "uts.replay.cat.looks", "uts.replay.cat.tasks", "uts.replay.cat.devices"
        };
        private static readonly int[] CatColorKind = { EvDeath, EvAbility, EvVent, EvSabotage, EvLook, EvTask, EvDevice };
        private static readonly bool[] catOn = { true, true, true, true, true, false, false };
        private static int pmode = 1;           // with a player chosen: 0 all events, 1 his own, 2 also what he saw (host)
        private static bool stopAtKill;

        private static int CatOf(int kind) {
            switch (kind) {
                case EvDeath: case EvExile: return 0;
                case EvAbility: return 1;
                case EvVent: return 2;
                case EvSabotage: case EvDoor: return 3;
                case EvLook: return 4;
                case EvTask: return 5;
                case EvDevice: return 6;
                default: return -1;
            }
        }

        private static bool Pass(Ev e) {
            if (e.Kind == EvMeeting) return true;
            int c = CatOf(e.Kind);
            if (c >= 0 && !catOn[c]) return false;
            if (persp == 255 || pmode == 0) return true;
            if (e.Actor == persp || e.Target == persp) return true;
            return pmode == 2 && e.Seen != null && e.Seen.Contains(persp);
        }

        private static readonly List<Ev> shownEvs = new List<Ev>();
        private static readonly Dictionary<Ev, int> meetingNo = new Dictionary<Ev, int>();
        private static readonly List<(Ev E, GameObject Tick)> ticks = new List<(Ev, GameObject)>();
        private static long filterSig = -1;

        private static void RefreshFilter() {
            long sig = persp | (long)pmode << 8 | (long)events.Count << 16;
            for (int c = 0; c < catOn.Length; c++) if (catOn[c]) sig |= 1L << (52 + c);
            if (sig == filterSig) return;
            filterSig = sig;
            shownEvs.Clear();
            shownEvs.AddRange(events.Where(Pass).OrderBy(e => e.T));
            meetingNo.Clear();
            int n = 0;
            foreach (var e in events.Where(e => e.Kind == EvMeeting).OrderBy(e => e.T)) meetingNo[e] = ++n;
            foreach (var (e, tick) in ticks) {
                if (tick == null) continue;
                bool on = Pass(e);
                if (tick.activeSelf != on) tick.SetActive(on);
            }
            if (jumpTo != null && !shownEvs.Contains(jumpTo)) jumpTo = null;
            listDirty = true;
        }

        private static void CycleMode() {
            if (persp == 255) return;
            pmode = (pmode + 1) % (HostData ? 3 : 2);
        }

        private static string ModeText() {
            var t = persp != 255 ? tracks.FirstOrDefault(x => x.Id == persp) : null;
            if (t == null) return UTSLocalization.Tr("uts.replay.mode_pick");
            return pmode == 0 ? UTSLocalization.Tr("uts.replay.mode_all", Plain(t))
                 : pmode == 1 ? UTSLocalization.Tr("uts.replay.mode_own", Plain(t))
                 : UTSLocalization.Tr("uts.replay.mode_seen", Plain(t));
        }

        // ====================================================================
        // Jumping
        // ====================================================================
        private const float JumpLead = 2f;
        private static Ev jumpTo;     // the event a jump aims at, until the playback reaches it

        private static void Seek(float t) {
            HideCard();
            jumpTo = null;
            viewT = Mathf.Clamp(t, 0f, Duration);
        }

        private static void JumpTo(Ev e) {
            Seek(e.T - JumpLead);
            jumpTo = e;
            playing = true;
        }

        // The event the list marks: the jump target until the playback reaches it, otherwise the
        // newest shown event at or before the playback.
        private static int CurrentIndex() {
            if (jumpTo != null) {
                if (viewT <= jumpTo.T + 0.01f) {
                    int j = shownEvs.IndexOf(jumpTo);
                    if (j >= 0) return j;
                }
                jumpTo = null;
            }
            int k = -1;
            for (int i = 0; i < shownEvs.Count; i++) {
                if (shownEvs[i].T <= viewT + 0.01f) k = i;
                else break;
            }
            return k;
        }

        private static void StepEvent(int dir) {
            if (shownEvs.Count == 0) return;
            int cur = CurrentIndex();
            int target;
            if (dir > 0) target = cur + 1;
            // well past the current event, "back" first replays that one
            else target = cur >= 0 && jumpTo == null && viewT - shownEvs[cur].T > JumpLead + 1f ? cur : cur - 1;
            if (target < 0 || target >= shownEvs.Count) return;
            JumpTo(shownEvs[target]);
        }

        // Events the playback just ran over: a meeting with a recorded vote holds for the card, a kill
        // pauses when that switch is on.
        private static void CrossCheck(float before) {
            foreach (var e in shownEvs) {
                if (e.T <= before) continue;
                if (e.T > viewT) break;
                if (e.Kind == EvMeeting && !string.IsNullOrEmpty(e.Result)) { viewT = e.T; ShowCard(e); return; }
                if (stopAtKill && e.Kind == EvDeath) { viewT = e.T; playing = false; return; }
            }
        }

        // ====================================================================
        // Panel parts
        // ====================================================================
        private static readonly List<(int Cat, Image Img, TMPro.TextMeshProUGUI Txt, Color Col)> catChips =
            new List<(int, Image, TMPro.TextMeshProUGUI, Color)>();
        private static Image killStopImg;
        private static TMPro.TextMeshProUGUI killStopLabel, modeLabel, listInfo;

        private static void BuildFilterRow(GameObject panel) {
            catChips.Clear();
            const float y = -90, w = 150, gap = 6;
            var lbl = SessionStatsUI.Text(panel, UTSLocalization.Tr("uts.replay.filter"), 15, TMPro.FontStyles.Bold, new Color(0.65f, 0.65f, 0.7f),
                                          new Vector2(MapX, y - 5), new Vector2(84, 24));
            lbl.enableWordWrapping = false;
            for (int c = 0; c < CatKeys.Length; c++) {
                int cc = c;
                var chip = SessionStatsUI.MakeButton(panel, UTSLocalization.Tr(CatKeys[c]), Vector2.zero, new Vector2(w, 30), Color.white,
                                                     () => catOn[cc] = !catOn[cc]);
                Place(chip, new Vector2(MapX + 88 + c * (w + gap), y));
                var txt = chip.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (txt != null) {
                    txt.enableAutoSizing = true; txt.fontSizeMin = 9; txt.fontSizeMax = 14;
                    txt.enableWordWrapping = false;
                    txt.outlineWidth = 0.25f; txt.outlineColor = new Color32(0, 0, 0, 255);
                }
                catChips.Add((c, chip.GetComponent<Image>(), txt, TickColor(CatColorKind[c])));
            }
        }

        // ---- event list ----
        private const float ListX = MapX + MapW + 20, ListW = PanelW - ListX - 30, ListH = 710, RowH = 28;
        private static RectTransform listRt;
        private static readonly List<(GameObject Row, Image Bg, Image Stripe, TMPro.TextMeshProUGUI Time, TMPro.TextMeshProUGUI Text)> listRows =
            new List<(GameObject, Image, Image, TMPro.TextMeshProUGUI, TMPro.TextMeshProUGUI)>();
        private static int listTop, listCur = -2;
        private static bool listDirty = true;
        private static float userScrollAt = -10f;

        private static void BuildEventList(GameObject panel) {
            listRows.Clear();
            var list = SessionStatsUI.Box(panel, new Vector2(ListX, MapY), new Vector2(ListW, ListH), new Color(0f, 0f, 0f, 0.25f));
            listRt = list.GetComponent<RectTransform>();
            var mb = SessionStatsUI.MakeButton(list, "", Vector2.zero, new Vector2(ListW, 30), new Color(0.3f, 0.3f, 0.38f, 0.95f), CycleMode);
            Place(mb, Vector2.zero);
            modeLabel = mb.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (modeLabel != null) { modeLabel.enableAutoSizing = true; modeLabel.fontSizeMin = 10; modeLabel.fontSizeMax = 14; modeLabel.enableWordWrapping = false; }

            const float rowsTop = -38;
            int n = Mathf.FloorToInt((ListH - 38 - 28) / RowH);
            for (int r = 0; r < n; r++) {
                int rr = r;
                var row = SessionStatsUI.Box(list, new Vector2(0, rowsTop - r * RowH), new Vector2(ListW, RowH - 2), new Color(1f, 1f, 1f, 0.04f));
                row.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => ClickRow(rr)));
                var stripe = SessionStatsUI.Box(row, Vector2.zero, new Vector2(5, RowH - 2), Color.white).GetComponent<Image>();
                stripe.raycastTarget = false;
                var time = SessionStatsUI.Text(row, "", 13, TMPro.FontStyles.Bold, new Color(0.65f, 0.65f, 0.7f), new Vector2(11, 0), new Vector2(44, RowH - 2));
                time.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                var text = SessionStatsUI.Text(row, "", 13, TMPro.FontStyles.Normal, Color.white, new Vector2(58, 0), new Vector2(ListW - 64, RowH - 2));
                text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                text.enableWordWrapping = false;
                text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                listRows.Add((row, row.GetComponent<Image>(), stripe, time, text));
            }
            listInfo = SessionStatsUI.Text(list, "", 13, TMPro.FontStyles.Normal, new Color(0.65f, 0.65f, 0.7f), new Vector2(8, -ListH + 24), new Vector2(ListW - 16, 20));
            listInfo.enableWordWrapping = false;
            filterSig = -1; listTop = 0; listCur = -2; listDirty = true; userScrollAt = -10f;
        }

        private static void ClickRow(int r) {
            int idx = listTop + r;
            if (idx >= 0 && idx < shownEvs.Count) JumpTo(shownEvs[idx]);
        }

        /// <summary>The wheel over the list scrolls it. False when the mouse is elsewhere.</summary>
        private static bool ListWheel(float wheel) {
            if (listRt == null || !RectTransformUtility.RectangleContainsScreenPoint(listRt, Input.mousePosition, null)) return false;
            listTop = Mathf.Clamp(listTop - (wheel > 0 ? 3 : -3), 0, Mathf.Max(0, shownEvs.Count - listRows.Count));
            userScrollAt = Time.realtimeSinceStartup;
            listDirty = true;
            return true;
        }

        private static void RefreshList(int cur) {
            if (cur != listCur) { listCur = cur; listDirty = true; }
            int rows = listRows.Count;
            // follow the playback, unless the list was scrolled by hand just now
            if (cur >= 0 && Time.realtimeSinceStartup - userScrollAt > 4f && (cur < listTop || cur >= listTop + rows - 2)) {
                int top = Mathf.Clamp(cur - 3, 0, Mathf.Max(0, shownEvs.Count - rows));
                if (top != listTop) { listTop = top; listDirty = true; }
            }
            if (!listDirty) return;
            listDirty = false;
            for (int r = 0; r < rows; r++) {
                var (row, bg, stripe, time, text) = listRows[r];
                int idx = listTop + r;
                if (shownEvs.Count == 0 && r == 0) {
                    row.SetActive(true);
                    bg.color = new Color(1f, 1f, 1f, 0.02f); stripe.color = new Color(0, 0, 0, 0);
                    time.text = ""; text.text = UTSLocalization.Tr("uts.replay.list_empty"); text.alpha = 0.6f;
                    continue;
                }
                bool on = idx < shownEvs.Count;
                if (row.activeSelf != on) row.SetActive(on);
                if (!on) continue;
                var e = shownEvs[idx];
                bool meet = e.Kind == EvMeeting;
                time.text = Clock(e.T);
                meetingNo.TryGetValue(e, out int mn);
                text.text = meet ? UTSLocalization.Tr("uts.replay.list_meeting", mn, e.Text) : e.Text;
                text.alpha = idx > cur ? 0.55f : 1f;
                stripe.color = TickColor(e.Kind);
                bg.color = idx == cur ? new Color(0.35f, 0.75f, 1f, 0.3f)
                         : meet ? new Color(1f, 0.85f, 0.3f, 0.14f)
                         : new Color(1f, 1f, 1f, 0.04f);
            }
            if (listInfo != null) {
                int shown = shownEvs.Count(e => e.Kind != EvMeeting), all = events.Count(e => e.Kind != EvMeeting);
                listInfo.text = UTSLocalization.Tr("uts.replay.list_count", shown, all);
            }
        }

        // ---- timeline phases ----
        private static void BuildPhases(GameObject bar) {
            float dur = Mathf.Max(1f, Duration), from = 0f;
            int k = 0;
            var cuts = events.Where(e => e.Kind == EvMeeting).Select(e => e.T).OrderBy(t => t).ToList();
            cuts.Add(dur);
            foreach (var to in cuts) {
                // every second phase a shade lighter, so the meetings split the bar visibly
                if (k % 2 == 1 && to > from) {
                    var s = SessionStatsUI.Box(bar, new Vector2(BarW * from / dur, 0), new Vector2(BarW * (to - from) / dur, 12), new Color(1f, 1f, 1f, 0.1f));
                    s.GetComponent<Image>().raycastTarget = false;
                }
                from = to; k++;
            }
        }

        private static void BuildPhaseLabels(GameObject panel, float cy) {
            float dur = Mathf.Max(1f, Duration);
            int n = 0;
            foreach (var e in events.Where(e => e.Kind == EvMeeting).OrderBy(e => e.T).ToList()) {
                var ev = e;
                n++;
                var b = SessionStatsUI.MakeButton(panel, "M" + n, Vector2.zero, new Vector2(40, 20), new Color(0.55f, 0.45f, 0.12f, 0.95f), () => JumpTo(ev));
                Place(b, new Vector2(BarX + BarW * e.T / dur - 20, cy - 44));
                var txt = b.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (txt != null) txt.fontSize = 12;
            }
        }

        // ---- meeting card ----
        private const float CardW = 660, CardH = 470, CardHold = 5f;
        private static GameObject card;
        private static TMPro.TextMeshProUGUI cardText;
        private static RectTransform cardFill;
        private static float cardUntil = -1f;
        private static bool cardPinned;     // paused while the card was up: it stays until play

        private static bool CardHolding() => cardUntil > 0f;
        private static bool CardShown() => card != null && card.activeSelf;

        private static void BuildMeetingCard(GameObject panel) {
            card = SessionStatsUI.Box(panel, new Vector2(MapX + (MapW - CardW) / 2f, MapY - 70), new Vector2(CardW, CardH), new Color(0.06f, 0.07f, 0.1f, 0.96f));
            card.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)HideCard);
            SessionStatsUI.Box(card, Vector2.zero, new Vector2(CardW, 6), new Color(1f, 0.85f, 0.3f)).GetComponent<Image>().raycastTarget = false;
            cardText = SessionStatsUI.Text(card, "", 18, TMPro.FontStyles.Normal, Color.white, new Vector2(24, -22), new Vector2(CardW - 48, CardH - 70));
            cardText.enableAutoSizing = true; cardText.fontSizeMin = 11; cardText.fontSizeMax = 18;
            var hint = SessionStatsUI.Text(card, UTSLocalization.Tr("uts.replay.mr.hint"), 13, TMPro.FontStyles.Normal, new Color(0.65f, 0.65f, 0.7f),
                                           new Vector2(24, -CardH + 36), new Vector2(CardW - 48, 20));
            hint.enableWordWrapping = false;
            SessionStatsUI.Box(card, new Vector2(0, -CardH + 6), new Vector2(CardW, 6), new Color(1f, 1f, 1f, 0.1f)).GetComponent<Image>().raycastTarget = false;
            var fill = SessionStatsUI.Box(card, new Vector2(0, -CardH + 6), new Vector2(CardW, 6), new Color(1f, 0.85f, 0.3f, 0.9f));
            fill.GetComponent<Image>().raycastTarget = false;
            cardFill = fill.GetComponent<RectTransform>();
            card.SetActive(false);
            cardUntil = -1f; cardPinned = false;
        }

        private static void ShowCard(Ev e) {
            if (card == null || cardText == null) return;
            meetingNo.TryGetValue(e, out int n);
            // guests only know that a meeting began (the reporter is host data): no second "Meeting" line
            string who = e.Text == UTSLocalization.Tr("uts.replay.meeting") ? "" : $"\n{e.Text}";
            cardText.text = $"<b><color=#FFD94D>{UTSLocalization.Tr("uts.replay.mr.title", n)}</color></b>{who}\n\n{e.Result}";
            card.SetActive(true);
            cardUntil = Time.realtimeSinceStartup + CardHold;
            cardPinned = false;
        }

        private static void HideCard() {
            cardUntil = -1f; cardPinned = false;
            if (card != null && card.activeSelf) card.SetActive(false);
        }

        /// <summary>The play button while the card is up: pause pins it, play goes on.</summary>
        private static bool CardPlayToggle() {
            if (CardHolding()) { cardUntil = -1f; cardPinned = true; playing = false; return true; }
            if (cardPinned) { HideCard(); playing = true; return true; }
            return false;
        }

        // ---- map marks ----
        private const int TrailN = 20;      // every second sample of the last 10 s
        private static readonly List<(RectTransform Rt, Image Img)> trail = new List<(RectTransform, Image)>();
        private static (RectTransform Rt, Image Img) haloA, haloT, spot;

        private static void BuildMapMarks() {
            trail.Clear();
            for (int k = 0; k < TrailN; k++) trail.Add(Disc(9f));
            spot = Disc(34f);
            haloA = Disc(36f);
            haloA.Img.color = new Color(1f, 0.85f, 0.2f, 0.9f);
            haloT = Disc(36f);
            haloT.Img.color = new Color(1f, 0.25f, 0.25f, 0.9f);
        }

        private static (RectTransform, Image) Disc(float size) {
            var go = SessionStatsUI.Box(mapArea, Vector2.zero, new Vector2(size, size), Color.white);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.GetComponent<Image>();
            img.sprite = SessionStatsUI.Circle();
            img.raycastTarget = false;
            go.SetActive(false);
            return (rt, img);
        }

        private static void SetMark((RectTransform Rt, Image Img) m, bool on, Vector2 at) {
            if (m.Rt == null) return;
            if (m.Rt.gameObject.activeSelf != on) m.Rt.gameObject.SetActive(on);
            if (on) m.Rt.anchoredPosition = at;
        }

        private static void MapMarks(int si, int cur) {
            // the chosen player's trail
            var pt = persp != 255 ? tracks.FirstOrDefault(t => t.Id == persp) : null;
            for (int k = 0; k < trail.Count; k++) {
                int s = si - 2 * (k + 1);
                bool on = pt != null && s >= 0 && s < pt.P.Count && !float.IsNaN(pt.P[s].x);
                SetMark(trail[k], on, on ? ToUi(pt.P[s]) : Vector2.zero);
                if (on) { var c = pt.Col; c.a = 0.75f * (1f - (float)k / trail.Count); trail[k].Img.color = c; }
            }
            // the current event: the jump target ahead, or the newest one within 3 s
            Ev fe = jumpTo ?? (cur >= 0 && cur < shownEvs.Count && viewT - shownEvs[cur].T < 3f ? shownEvs[cur] : null);
            if (CardShown()) fe = null;
            bool spotOn = fe != null && !float.IsNaN(fe.Pos.x);
            SetMark(spot, spotOn, spotOn ? ToUi(fe.Pos) : Vector2.zero);
            if (spotOn) {
                var c = TickColor(fe.Kind);
                c.a = 0.35f + 0.2f * Mathf.Sin(Time.realtimeSinceStartup * 6f);
                spot.Img.color = c;
                float sc = 1f + 0.25f * Mathf.Sin(Time.realtimeSinceStartup * 6f);
                spot.Rt.localScale = new Vector3(sc, sc, 1f);
            }
            Halo(haloA, fe?.Actor ?? 255);
            Halo(haloT, fe?.Target ?? 255);
        }

        private static void Halo((RectTransform Rt, Image Img) h, byte id) {
            bool on = id != 255 && dots.TryGetValue(id, out var d) && d.Dot.gameObject.activeSelf;
            SetMark(h, on, on ? dots[id].Dot.anchoredPosition : Vector2.zero);
            if (on) h.Rt.localScale = dots[id].Dot.localScale;
        }

        // ====================================================================
        // Per frame (from Animate)
        // ====================================================================
        private static void ViewAnimate(int si, float u) {
            if (cardUntil > 0f) {
                float left = cardUntil - Time.realtimeSinceStartup;
                if (left <= 0f) HideCard();
                else if (cardFill != null) cardFill.sizeDelta = new Vector2(CardW * left / CardHold, 6);
            }
            foreach (var (c, img, txt, col) in catChips) {
                bool on = catOn[c];
                if (img != null) img.color = on ? new Color(col.r * 0.8f, col.g * 0.8f, col.b * 0.8f, 0.95f) : new Color(0.25f, 0.25f, 0.3f, 0.8f);
                if (txt != null) txt.alpha = on ? 1f : 0.5f;
            }
            if (killStopImg != null) killStopImg.color = stopAtKill ? new Color(0.7f, 0.2f, 0.2f, 0.95f) : new Color(0.3f, 0.3f, 0.38f, 0.95f);
            if (killStopLabel != null) {
                string ks = UTSLocalization.Tr(stopAtKill ? "uts.replay.killstop_on" : "uts.replay.killstop_off");
                if (killStopLabel.text != ks) killStopLabel.text = ks;
            }
            if (modeLabel != null) {
                string ml = ModeText();
                if (modeLabel.text != ml) modeLabel.text = ml;
            }
            int cur = CurrentIndex();
            RefreshList(cur);
            MapMarks(si, cur);
        }

        private static void ViewDestroy() {
            card = null; cardText = null; cardFill = null; cardUntil = -1f; cardPinned = false;
            listRows.Clear(); listRt = null; listInfo = null; modeLabel = null;
            catChips.Clear(); ticks.Clear(); trail.Clear();
            haloA = default; haloT = default; spot = default;
            killStopImg = null; killStopLabel = null;
            jumpTo = null; filterSig = -1;
        }

        // ====================================================================
        // Recording: the vote of every meeting (every client, VotingComplete runs everywhere)
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
        static class VoteResultPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix([HarmonyArgument(0)] Il2CppStructArray<MeetingHud.VoterState> states,
                                      [HarmonyArgument(1)] NetworkedPlayerInfo exiled, [HarmonyArgument(2)] bool tie) {
                try { RecordVote(states, exiled, tie); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] vote record failed: {e.Message}"); }
            }
        }

        private const byte VoteDead = 252, VoteSkip = 253;     // 254 missed, 255 not voted

        private static void RecordVote(Il2CppStructArray<MeetingHud.VoterState> states, NetworkedPlayerInfo exiled, bool tie) {
            if (!recording || states == null) return;
            var meeting = events.LastOrDefault(e => e.Kind == EvMeeting);
            if (meeting == null) return;
            bool anon = false;
            try { anon = GameManager.Instance != null && GameManager.Instance.LogicOptions.GetAnonymousVotes(); } catch { }

            var votes = new Dictionary<byte, List<byte>>();
            var skips = new List<byte>();
            var none = new List<byte>();
            for (int k = 0; k < states.Length; k++) {
                var s = states[k];
                byte voter = s.VoterId, target = s.VotedForId;
                if (target == VoteDead) continue;
                var vp = TheOtherRoles.Helpers.playerById(voter);
                if (vp == null || vp.Data == null || vp.Data.IsDead || vp.Data.Disconnected) continue;
                if (target == VoteSkip) skips.Add(voter);
                else if (target > VoteSkip) none.Add(voter);
                else {
                    if (!votes.TryGetValue(target, out var l)) votes[target] = l = new List<byte>();
                    l.Add(voter);
                }
            }

            string Voters(List<byte> ids) => anon ? "" : " - " + string.Join(", ", ids.Select(id => WhoId(id, false)));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in votes.OrderByDescending(kv => kv.Value.Count))
                sb.AppendLine(UTSLocalization.Tr("uts.replay.mr.votes_for", WhoId(kv.Key), kv.Value.Count) + Voters(kv.Value));
            if (skips.Count > 0) sb.AppendLine(UTSLocalization.Tr("uts.replay.mr.skip", skips.Count) + Voters(skips));
            if (none.Count > 0) sb.AppendLine(UTSLocalization.Tr("uts.replay.mr.none_voted", none.Count) + Voters(none));
            if (votes.Count == 0 && skips.Count == 0 && none.Count == 0) sb.AppendLine(UTSLocalization.Tr("uts.replay.mr.no_votes"));
            if (anon) sb.AppendLine(UTSLocalization.Tr("uts.replay.mr.anonymous"));
            sb.AppendLine();
            sb.Append(exiled != null ? UTSLocalization.Tr("uts.replay.mr.ejected", WhoId(exiled.PlayerId))
                    : tie ? UTSLocalization.Tr("uts.replay.mr.tie") : UTSLocalization.Tr("uts.replay.mr.nobody"));
            meeting.Result = sb.ToString();
        }
    }
}
