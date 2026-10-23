// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MayorSplitVote - the Mayor may give his second vote to somebody else (User 23.10.: "Als Mayor soll
 * man optional seine Votes splitten koennen").
 *
 * TOR counts the Mayor's vote twice for the same target (MeetingCalculateVotesPatch.CalculateVotes:
 * additionalVotes = 2 while Mayor.voteTwice) and draws a second icon next to the first
 * (MeetingHudPopulateVotesPatch). Host option 1285 "Mayor Can Split Votes" (under the Mayor) gives the
 * living Mayor a small "2" button on every living player's plate: it marks where his SECOND vote goes.
 * His normal vote stays his first vote. Click the same plate again to take the mark back; without a
 * mark, or with "Double Vote: Off" (TOR's own single-vote toggle), everything is TOR's.
 *
 * - Sync: the Mayor sends the mark to everybody (module 234 on channel 240, only accepted from the
 *   Mayor himself, only before the results). The host counts with it, every client draws with it.
 * - Count (host): postfix on TOR's CalculateVotes (managed, but it has loops and runs a few times per
 *   meeting; same hook as Unknown's Collection's Necromancer): one vote moves from his main target to
 *   the marked one, both through the Swapper's swap like TOR's own count. An entry that drops to 0 is
 *   removed - TOR's MaxPair starts at int.MinValue and a lingering 0 could win (Necromancer lesson).
 * - Results: TOR's PopulateResults prefix replaces the whole method, and HarmonyX runs every prefix
 *   anyway, so a Priority.First prefix switches Mayor.voteTwice off for TOR's drawing (one icon on the
 *   main target), and the postfix puts it back and bloops the second icon on the marked plate.
 * - Only when everyone has this mod (latched at meeting start): a player without it would see the
 *   double icon on the main target while the host counted the split.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MayorSplitVote {
        public const byte SplitRpcId = 234;
        private const byte None = 255, Skip = 253;

        public static CustomOption Option;

        private static byte splitTarget = None;          // the plate the Mayor marked in this meeting
        private static bool everyoneHasMod = true;       // latched at meeting start
        private static bool voteTwiceHeld;               // PopulateResults: Mayor.voteTwice switched off for TOR's drawing
        private static byte drawOn = None;               // ... and the plate the second icon goes to
        private static NetworkedPlayerInfo drawVoter;
        private static readonly Dictionary<byte, SpriteRenderer> marks = new Dictionary<byte, SpriteRenderer>();

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(1285, Types.Crewmate, "Mayor Can Split Votes", false, CustomOptionHolder.mayorSpawnRate);
                UTSLocalization.BindOptionTitle(Option, "uts.mayorsplit.option");
                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.mayorChooseSingleVote);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MayorSplit] option creation failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(SplitRpcId, HandleRpc);

        public static void TryPatch(Harmony harmony) {
            try {
                var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch+MeetingCalculateVotesPatch");
                var m = t == null ? null : AccessTools.Method(t, "CalculateVotes");
                if (m == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MayorSplit] CalculateVotes not found - the split stays off.");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(MayorSplitVote), nameof(CalculateVotesPostfix)));
                countPatched = true;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MayorSplit] vote patch failed: {e}");
            }
        }

        private static bool countPatched;

        // ---- conditions ----

        private static bool Enabled() =>
            countPatched && Option != null && UTSGate.Bool(Option) && everyoneHasMod
            && Mayor.mayor != null && Mayor.mayor.Data != null && !Mayor.mayor.Data.IsDead && !Mayor.mayor.Data.Disconnected
            && Mayor.voteTwice;

        private static bool Alive(byte id) {
            var p = Helpers.playerById(id);
            return p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;
        }

        private static bool ValidVote(byte v) => v != 252 && v != 254 && v != None;

        // the Swapper's swap, exactly where TOR applies it (both plates present, Swapper alive)
        private static byte Swap(byte id, MeetingHud hud) {
            try {
                if (id == Skip || Swapper.swapper == null || Swapper.swapper.Data == null || Swapper.swapper.Data.IsDead) return id;
                byte a = Swapper.playerId1, b = Swapper.playerId2;
                if (a == None || b == None || hud == null) return id;
                bool hasA = false, hasB = false;
                foreach (var ps in hud.playerStates) { if (ps.TargetPlayerId == a) hasA = true; if (ps.TargetPlayerId == b) hasB = true; }
                if (!hasA || !hasB) return id;
                return id == a ? b : id == b ? a : id;
            } catch { return id; }
        }

        /// <summary>The marked target if the split applies to this vote result (255 = no split). Used by the
        /// results drawing and the round replay's meeting card.</summary>
        internal static byte SplitFor(Il2CppStructArray<MeetingHud.VoterState> states) {
            try {
                if (!Enabled() || splitTarget == None || states == null || !Alive(splitTarget) && splitTarget != Skip) return None;
                foreach (var s in states) {
                    if (s.VoterId != Mayor.mayor.PlayerId) continue;
                    byte main = s.SkippedVote ? Skip : s.VotedForId;
                    if (!ValidVote(main) || main == splitTarget) return None;
                    return splitTarget;
                }
            } catch { }
            return None;
        }

        // ---- count (host) ----

        public static void CalculateVotesPostfix([HarmonyArgument(0)] MeetingHud hud, ref Dictionary<byte, int> __result) {
            try {
                if (__result == null || hud == null || splitTarget == None || !Enabled() || !Alive(splitTarget)) return;
                var mine = hud.playerStates.FirstOrDefault(ps => ps != null && ps.TargetPlayerId == Mayor.mayor.PlayerId);
                if (mine == null || mine.AmDead || !ValidVote(mine.VotedFor) || mine.VotedFor == splitTarget) return;
                byte main = Swap(mine.VotedFor, hud), second = Swap(splitTarget, hud);
                if (!__result.TryGetValue(main, out int n) || n < 2) return;     // TOR did not count him twice there
                if (n - 1 <= 0) __result.Remove(main); else __result[main] = n - 1;
                __result[second] = (__result.TryGetValue(second, out int m) ? m : 0) + 1;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[MayorSplit] Mayor's second vote: {main} -> {second}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MayorSplit] count failed: {e}");
            }
        }

        // ---- results drawing ----

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.PopulateResults))]
        static class ResultsPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(MeetingHud __instance, [HarmonyArgument(0)] Il2CppStructArray<MeetingHud.VoterState> states) {
                try {
                    byte target = SplitFor(states);
                    if (target == None) return;
                    drawOn = Swap(target, __instance);
                    drawVoter = Mayor.mayor.Data;
                    Mayor.voteTwice = false;     // TOR draws his vote once
                    voteTwiceHeld = true;
                } catch { }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MeetingHud __instance) {
                if (!voteTwiceHeld) return;
                voteTwiceHeld = false;
                Mayor.voteTwice = true;
                try {
                    if (drawVoter == null || drawVoter.IsDead) return;
                    Transform parent = null;
                    if (drawOn == Skip) parent = __instance.SkippedVoting != null ? __instance.SkippedVoting.transform : null;
                    else {
                        var pva = __instance.playerStates.FirstOrDefault(ps => ps != null && ps.TargetPlayerId == drawOn);
                        if (pva != null) parent = pva.transform;
                    }
                    if (parent == null) return;
                    var spread = parent.GetComponent<VoteSpreader>();
                    int index = spread != null && spread.Votes != null ? spread.Votes.Count : 0;
                    __instance.BloopAVoteIcon(drawVoter, index, parent);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[MayorSplit] second icon failed: {e.Message}");
                }
            }
        }

        // ---- the marks on the plates (Mayor only) ----

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MeetingHud __instance) {
                splitTarget = None;
                marks.Clear();
                if (voteTwiceHeld) { voteTwiceHeld = false; Mayor.voteTwice = true; }
                try { everyoneHasMod = UsefulVersionHandshake.EveryoneHasMod(); } catch { everyoneHasMod = true; }
                try {
                    if (!Enabled() || Mayor.mayor != PlayerControl.LocalPlayer) return;
                    bool guesserHere = HandleGuesser.isGuesserGm && HandleGuesser.isGuesser(PlayerControl.LocalPlayer.PlayerId);
                    foreach (var pva in __instance.playerStates) {
                        if (pva == null || pva.AmDead) continue;
                        var template = pva.Buttons != null ? pva.Buttons.transform.Find("CancelButton") : null;
                        if (template == null) continue;
                        var go = UnityEngine.Object.Instantiate(template.gameObject);
                        go.name = "UTS_MayorSplitMark";
                        go.transform.SetParent(pva.transform);
                        go.transform.position = template.position;
                        go.transform.localPosition = new Vector3(guesserHere ? -0.5f : -0.95f, 0.03f, -1.3f);
                        var sr = go.GetComponent<SpriteRenderer>();
                        sr.sprite = Swapper.getCheckSprite();
                        AddDigit(go, sr);
                        var pb = go.GetComponent<PassiveButton>();
                        pb.OnClick.RemoveAllListeners();
                        byte id = pva.TargetPlayerId;
                        pb.OnClick.AddListener((Action)(() => OnMark(__instance, id)));
                        marks[id] = sr;
                    }
                    Paint();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MayorSplit] marks failed: {e}");
                }
            }
        }

        // a small "2" on the check mark: it is the second vote
        private static void AddDigit(GameObject mark, SpriteRenderer sr) {
            try {
                var src = HudManager.Instance != null ? HudManager.Instance.TaskPanel?.taskText : null;
                var go = new GameObject("Digit") { layer = mark.layer };
                go.transform.SetParent(mark.transform, false);
                go.transform.localPosition = new Vector3(0.2f, -0.18f, -0.1f);
                var t = go.AddComponent<TMPro.TextMeshPro>();
                if (src != null) t.font = src.font;
                t.text = "2"; t.fontSize = 2.2f; t.alignment = TMPro.TextAlignmentOptions.Center;
                t.outlineWidth = 0.25f; t.outlineColor = Color.black;
                t.rectTransform.sizeDelta = new Vector2(0.5f, 0.5f);
                t.sortingOrder = sr.sortingOrder + 1;
            } catch { }
        }

        private static void OnMark(MeetingHud hud, byte id) {
            try {
                if (hud == null || hud.state == MeetingHud.VoteStates.Results || !Enabled() || Mayor.mayor != PlayerControl.LocalPlayer) return;
                byte next = splitTarget == id ? None : id;
                Apply(next);
                if (UTSRpc.CanSend) {
                    var w = UTSRpc.Begin(SplitRpcId);
                    w.Write(next);
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MayorSplit] mark failed: {e}");
            }
        }

        private static void HandleRpc(MessageReader reader) {
            byte target = reader.ReadByte();
            var sender = UTSRpc.Sender;
            if (sender == null || Mayor.mayor == null || sender.PlayerId != Mayor.mayor.PlayerId) return;   // only the Mayor himself
            var hud = MeetingHud.Instance;
            if (hud == null || hud.state == MeetingHud.VoteStates.Results) return;
            Apply(target);
        }

        private static void Apply(byte target) {
            splitTarget = target;
            Paint();
            UsefulTORStuffPlugin.Logger?.LogInfo($"[MayorSplit] second vote mark: {(target == None ? "none" : target.ToString())}.");
        }

        private static void Paint() {
            foreach (var kv in marks) {
                if (kv.Value == null) continue;
                kv.Value.color = kv.Key == splitTarget ? Mayor.color : new Color(1f, 1f, 1f, 0.35f);
            }
        }

        // hide the marks when they mean nothing (results, Mayor dead, TOR's single vote chosen)
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class MeetingUpdatePatch {
            public static void Postfix(MeetingHud __instance) {
                if (marks.Count == 0) return;
                try {
                    bool show = __instance.state != MeetingHud.VoteStates.Results && Enabled();
                    foreach (var sr in marks.Values)
                        if (sr != null && sr.gameObject.activeSelf != show) sr.gameObject.SetActive(show);
                } catch { }
            }
        }
    }
}
