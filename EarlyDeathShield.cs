// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EarlyDeathShield - the pink shield: whoever keeps getting killed early gets the newcomer's
 * treatment, a round in which nobody can kill them before the first meeting.
 *
 * The newcomer shield answers "who has never played with us"; this one answers "who never gets to
 * play". Some players are simply the first body in round after round - a quiet player nobody
 * suspects, the one who always does electrical first - and spend most of the evening as a ghost.
 *
 * WHO GETS IT
 * DeathTimeHistory keeps, per player, the share of each round they survived before somebody else
 * killed them. A player with at least "Minimum Recorded Rounds" rounds counts as QUALIFIED. The
 * LOBBY AVERAGE is the mean of the qualified players currently in the lobby (at least three, or
 * there is no meaningful average and nobody is picked automatically). Whoever sits below
 * "Threshold" percent of that average is a candidate; the lowest ones are picked, at most
 * "Maximum Shielded Players" of them. Relative, not absolute: in a lobby with two aggressive
 * impostor mains everybody dies earlier, and the shield should still go to the one who dies
 * earliest of all.
 *
 * Shielded rounds are recorded like any other. The shield only buys the opening minutes, so a
 * player who really keeps dying early stays low, and one who only had a bad week climbs out of it
 * - the shield regulates itself instead of sticking forever to whoever once qualified.
 *
 * HOST OVERRIDE
 * The lobby panel (EarlyDeathShieldUI) lets the host force the shield on or off per player. Unlike
 * the newcomer's one-round marks these are persistent (stored in DeathTimeHistory's file), because
 * the rule they override is persistent too: "never shield X" should not need clicking every lobby.
 *
 * VISIBLE IN THE LOBBY
 * Exactly like the newcomer shield: the host broadcasts the list while the lobby is open and
 * refreshes it whenever it changes, and UTSShieldOutlines paints the pink outline for everyone.
 *
 * LIFETIME AND ENFORCEMENT
 * Deliberately the newcomer's, layer for layer (see NewcomerShield.cs for the reasoning behind each):
 *  - assigned at the end of the intro by the host, over module byte 239;
 *  - dropped when the first meeting opens, or after it when "Shield During The First Meeting" keeps
 *    it alive through that meeting (vote/guess blocks live in NewcomerMeetingProtection.cs);
 *  - enforcement 0: TOR's setTarget untargetable list (peaceful abilities excepted, ShieldPeaceGate);
 *  - enforcement 1: vanilla CheckMurder on the host;
 *  - enforcement 2: Helpers.checkMuderAttempt postfix on the killer's client.
 * The lover cascade stays untouched for the same reason as there.
 *
 * Options 1383-1388, module byte 239 on UTSRpc.CallId = 240. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class EarlyDeathShield {

        // ---- Options (1383-1388) ----
        public static CustomOption Enabled;
        public static CustomOption NotifyKiller;
        public static CustomOption MeetingProtection;
        public static CustomOption Threshold;
        public static CustomOption MinRounds;
        public static CustomOption MaxShielded;

        private static readonly string[] ThresholdValues = { "40%", "50%", "60%", "70%", "80%" };

        // Fewer qualified players than this and there is no lobby average worth comparing against.
        private const int MinQualifiedForAverage = 3;

        // ---- Everyone: who is shielded in THIS round (player ids, valid until the first meeting) ----
        private static readonly HashSet<byte> shielded = new HashSet<byte>();

        private const byte RpcId = UsefulTORStuffPlugin.EarlyDeathShieldRpcId;
        private const byte SubSetShields = 0;   // count, ids...
        private const byte SubClear      = 1;

        public static bool Active => shielded.Count > 0;
        public static bool IsShielded(byte playerId) => shielded.Contains(playerId);

        // Pink, as asked. Not TOR's pink player colour (238,84,187) and brighter than the Lovers'
        // heart pink, so it reads as an outline and not as a cosmetic.
        public static readonly Color ShieldColor = new Color32(255, 90, 205, byte.MaxValue);

        // Selection 0 = off (shield ends at meeting start), 1 = votes, 2 = guesses, 3 = both.
        private static int MeetingMode {
            get { try { return MeetingProtection == null ? 0 : UTSGate.Sel(MeetingProtection); } catch { return 0; } }
        }
        public static bool SurvivesMeeting => MeetingMode > 0;
        public static bool BlocksVotes => MeetingMode == 1 || MeetingMode == 3;
        public static bool BlocksGuesses => MeetingMode == 2 || MeetingMode == 3;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1383, Types.General,
                    "Protect Players Who Often Die Early", false, null, true);
                NotifyKiller = CustomOption.Create(1384, Types.General,
                    "Tell The Killer Why The Kill Failed", true, Enabled);
                // Explicit constructor for the selection lists, for the reason NewcomerShield gives
                // (the Create(string[]) overload hardcodes its default).
                MeetingProtection = new CustomOption(1385, Types.General,
                    "Shield During The First Meeting",
                    new string[] { "Ends Before The Meeting", "Blocks Votes", "Blocks Guesses", "Blocks Both" },
                    "Ends Before The Meeting", Enabled, false);
                Threshold = new CustomOption(1386, Types.General,
                    "Shield Below % Of The Lobby Average", ThresholdValues, "60%", Enabled, false);
                MinRounds = CustomOption.Create(1387, Types.General,
                    "Minimum Recorded Rounds", 5f, 3f, 20f, 1f, Enabled);
                MaxShielded = CustomOption.Create(1388, Types.General,
                    "Maximum Shielded Players", 1f, 1f, 3f, 1f, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.earlydeath.option_name");
                UTSLocalization.BindOptionTitle(NotifyKiller, "uts.earlydeath.option_notify");
                UTSLocalization.BindOptionTitle(MeetingProtection, "uts.earlydeath.option_meeting");
                UTSLocalization.BindOptionSelections(MeetingProtection, "uts.earlydeath.option_meeting_values");
                UTSLocalization.BindOptionTitle(Threshold, "uts.earlydeath.option_threshold");
                UTSLocalization.BindOptionTitle(MinRounds, "uts.earlydeath.option_minrounds");
                UTSLocalization.BindOptionTitle(MaxShielded, "uts.earlydeath.option_max");
                UsefulTORStuffPlugin.Logger?.LogInfo("[EarlyDeathShield] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(RpcId, HandleModuleRpc);

        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        private static bool IsEnabled() {
            try { return Enabled != null && Enabled.getBool(); } catch { return false; }
        }

        // ====================================================================
        // The decision (host side). One evaluation covers the whole lobby: the average depends on
        // everybody present, so a single player's verdict cannot be computed in isolation.
        // ====================================================================
        public sealed class Verdict {
            public byte PlayerId;
            public string Code;
            public string Name;
            public DeathTimeHistory.Stat Stat;
            public int Override;
            public bool Qualified;
            public bool AutoPick;
            public bool Shield;
        }

        public sealed class Evaluation {
            public readonly List<Verdict> Rows = new List<Verdict>();
            public float LobbyMean;        // mean of the qualified players' means; 0 without an average
            public bool HasAverage;
            public int MinRounds;
            public int ThresholdPercent;
            public int ShieldCount => Rows.Count(r => r.Shield);
            public Verdict Of(byte playerId) => Rows.FirstOrDefault(r => r.PlayerId == playerId);
        }

        public static int ThresholdPercent {
            get {
                try { return 40 + 10 * Mathf.Clamp(Threshold?.selection ?? 2, 0, ThresholdValues.Length - 1); }
                catch { return 60; }
            }
        }

        public static Evaluation Evaluate() {
            var ev = new Evaluation();
            try {
                ev.MinRounds = MinRounds == null ? 5 : Mathf.RoundToInt(MinRounds.getFloat());
                ev.ThresholdPercent = ThresholdPercent;
                int max = MaxShielded == null ? 1 : Mathf.RoundToInt(MaxShielded.getFloat());

                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    string code = NewcomerShield.CodeOf(p);
                    if (string.IsNullOrEmpty(code)) continue;
                    var stat = DeathTimeHistory.StatOf(code);
                    ev.Rows.Add(new Verdict {
                        PlayerId = p.PlayerId,
                        Code = code,
                        Name = p.Data.PlayerName ?? "?",
                        Stat = stat,
                        Override = DeathTimeHistory.OverrideOf(code),
                        Qualified = stat.Rounds >= ev.MinRounds
                    });
                }

                var qualified = ev.Rows.Where(r => r.Qualified).ToList();
                ev.HasAverage = qualified.Count >= MinQualifiedForAverage;
                if (ev.HasAverage) {
                    ev.LobbyMean = qualified.Average(r => r.Stat.Mean);
                    float limit = ev.LobbyMean * ev.ThresholdPercent / 100f;
                    foreach (var r in qualified
                                 .Where(r => r.Override == DeathTimeHistory.OverrideAuto && r.Stat.Mean < limit)
                                 .OrderBy(r => r.Stat.Mean)
                                 .Take(Math.Max(0, max)))
                        r.AutoPick = true;
                }

                foreach (var r in ev.Rows)
                    r.Shield = r.Override == DeathTimeHistory.OverrideOn
                               || (r.Override == DeathTimeHistory.OverrideAuto && r.AutoPick);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[EarlyDeathShield] evaluation failed: {e.Message}");
            }
            return ev;
        }

        // ---- host overrides (lobby panel) ----

        // Flips whatever the player's CURRENT state is: shielded becomes forced off, unshielded
        // becomes forced on - the button always does what its label says (NewcomerShield's rule).
        public static void ToggleOverride(Verdict v) {
            if (v == null) return;
            DeathTimeHistory.SetOverride(v.Code, v.Name,
                v.Shield ? DeathTimeHistory.OverrideOff : DeathTimeHistory.OverrideOn);
        }

        public static void ClearOverride(Verdict v) {
            if (v == null) return;
            DeathTimeHistory.SetOverride(v.Code, v.Name, DeathTimeHistory.OverrideAuto);
        }

        // ---- RPC ----
        private static void SendSetShields(List<byte> ids) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubSetShields);
                w.Write((byte)Math.Min(ids.Count, 255));
                for (int i = 0; i < ids.Count && i < 255; i++) w.Write(ids[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetShields(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] send failed: {e}");
            }
        }

        private static void SendClear() {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] clear failed: {e}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSetShields: {
                        int n = reader.ReadByte();
                        var ids = new List<byte>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadByte());
                        if (UTSRpc.RequireHost("EarlyDeathShield.SetShields")) ApplySetShields(ids);
                        break;
                    }
                    case SubClear:
                        if (UTSRpc.RequireHost("EarlyDeathShield.Clear")) ApplyClear();
                        break;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] rpc failed: {e}");
            }
        }

        private static void ApplySetShields(List<byte> ids) {
            shielded.Clear();
            foreach (byte id in ids) shielded.Add(id);
            if (shielded.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EarlyDeathShield] {shielded.Count} player(s) shielded until the first meeting.");
        }

        private static void ApplyClear() {
            if (shielded.Count == 0) return;
            shielded.Clear();
            UsefulTORStuffPlugin.Logger?.LogInfo("[EarlyDeathShield] shield list cleared by the host.");
        }

        // ====================================================================
        // The driver, called from EarlyDeathShieldUI.Update (never a Harmony postfix - see the
        // NewcomerShield header for why).
        // ====================================================================
        private static float nextTick;
        private static bool roundSeen;
        private static float roundSeenAt;
        private static bool assignedThisRound;
        private static bool introOverSeen;
        private static bool firstMeetingSeen;
        private const float IntroFallbackSeconds = 10f;

        public static void Tick() {
            try {
                if (Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 0.5f;

                var client = AmongUsClient.Instance;
                if (client == null) return;

                bool inRound = ShipStatus.Instance != null && client.IsGameStarted;
                if (!inRound) {
                    roundSeen = false;
                    introOverSeen = false;
                    assignedThisRound = false;
                    // LobbyScreen.Exists, never GameStartManager.Instance (LobbyLeakGuard.cs).
                    if (client.AmHost && LobbyScreen.Exists) LobbyPreviewTick();
                    return;
                }

                if (!roundSeen) { roundSeen = true; roundSeenAt = Time.realtimeSinceStartup; }

                // Same two lifetimes as the newcomer shield (see its Tick for the long version).
                bool meetingRunning = MeetingHud.Instance != null || ExileController.Instance != null;
                bool dropNow = SurvivesMeeting ? (firstMeetingSeen && !meetingRunning)
                                               : (MeetingHud.Instance != null);
                if (dropNow && shielded.Count > 0) {
                    if (client.AmHost) SendClear();
                    else shielded.Clear();
                }

                if (assignedThisRound || !client.AmHost) return;
                if (!IsEnabled()) { assignedThisRound = true; return; }
                if (!introOverSeen
                    && Time.realtimeSinceStartup - roundSeenAt < IntroFallbackSeconds) return;

                assignedThisRound = true;
                AssignShields();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] tick failed: {e}");
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)]
        static class IntroOverMarkerPatch {
            public static void Prefix() => introOverSeen = true;
        }

        private static void AssignShields() {
            try {
                var ev = Evaluate();
                var ids = new List<byte>();
                foreach (var r in ev.Rows) {
                    if (!r.Shield) continue;
                    var p = Helpers.playerById(r.PlayerId);
                    if (p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
                    ids.Add(r.PlayerId);
                }

                // Always logged: the proof the decision ran, and the numbers behind it.
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EarlyDeathShield] round start: {ev.Rows.Count(r => r.Qualified)} qualified player(s)"
                    + (ev.HasAverage ? $", lobby average {ev.LobbyMean * 100f:F0}% survived, limit {ev.ThresholdPercent}%" : ", no average yet")
                    + $", {ids.Count} to shield"
                    + (ids.Count > 0 ? ": " + string.Join(", ", ev.Rows.Where(r => ids.Contains(r.PlayerId))
                          .Select(r => $"{r.Name} ({r.Stat.Mean * 100f:F0}%, {r.Stat.Rounds} rounds"
                                       + (r.Override == DeathTimeHistory.OverrideOn ? ", forced" : "") + ")")) : "")
                    + ".");

                if (ids.Count > 0) SendSetShields(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] round start failed: {e}");
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    firstMeetingSeen = true;
                    if (SurvivesMeeting || shielded.Count == 0) return;
                    if (AmHost()) SendClear();
                    else shielded.Clear();
                } catch { }
            }
        }

        // ====================================================================
        // Lobby preview: broadcast whenever the list changes (join, leave, option, override).
        // ====================================================================
        private static string lastPreviewKey = "";

        // Forces the next preview tick to resend even an unchanged list - after an override click
        // the host wants to see the outline change at once, not half a second later.
        public static void RefreshPreview() { lastPreviewKey = "?"; nextTick = 0f; }

        private static void LobbyPreviewTick() {
            try {
                var ids = new List<byte>();
                if (IsEnabled()) {
                    foreach (var r in Evaluate().Rows)
                        if (r.Shield) ids.Add(r.PlayerId);
                }
                ids.Sort();
                string key = string.Join(",", ids);
                if (key == lastPreviewKey) return;
                lastPreviewKey = key;

                if (ids.Count > 0) SendSetShields(ids);
                else SendClear();
            } catch { }
        }

        // ====================================================================
        // Enforcement 1: vanilla kills, host-authoritative
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
        static class CheckMurderPatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (shielded.Count == 0 || target == null) return true;
                    if (!shielded.Contains(target.PlayerId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[EarlyDeathShield] blocked a vanilla kill on {target.Data?.PlayerName} (shielded).");
                    return false;
                } catch { return true; }
            }
        }

        // ====================================================================
        // Enforcement 2: every TOR/UC role kill, on the killer's own client. Postfix, and only a
        // real kill result is rewritten (AntiStartKill's refinement): SuppressKill is refused
        // already, and a BlankKill kills nobody.
        // ====================================================================
        private static float lastNotifyAt = -10f;

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class CheckMurderAttemptPatch {
            public static void Postfix(PlayerControl killer, PlayerControl target,
                                       ref MurderAttemptResult __result) {
                try {
                    if (shielded.Count == 0 || target == null) return;
                    if (!shielded.Contains(target.PlayerId)) return;
                    if (__result != MurderAttemptResult.PerformKill
                        && __result != MurderAttemptResult.DelayVampireKill) return;

                    __result = MurderAttemptResult.SuppressKill;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[EarlyDeathShield] blocked a role kill on {target.Data?.PlayerName} (shielded).");

                    // Feedback for the killer on his own client, throttled so a Vampire hammering
                    // the bite button does not flood his chat.
                    if (NotifyKiller != null && NotifyKiller.getBool()
                        && killer != null && PlayerControl.LocalPlayer != null
                        && killer.PlayerId == PlayerControl.LocalPlayer.PlayerId
                        && Time.realtimeSinceStartup - lastNotifyAt >= 1.5f) {
                        lastNotifyAt = Time.realtimeSinceStartup;
                        var hud = HudManager.Instance;
                        if (hud != null && hud.Chat != null)
                            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                                UTSLocalization.Tr("uts.earlydeath.kill_blocked"));
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 0: a shielded player cannot even be TARGETED (the Thief rationale in
        // NewcomerShield.cs), except by peaceful abilities and by a Jackal who can still recruit.
        // ====================================================================
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch),
                      nameof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch.setTarget))]
        static class SetTargetPatch {
            public static void Prefix(ref List<PlayerControl> untargetablePlayers) {
                try {
                    if (shielded.Count == 0) return;
                    if (ShieldPeaceGate.Peaceful) return;
                    if (NewcomerShield.JackalCanRecruitNow()) return;
                    var list = untargetablePlayers != null
                        ? new List<PlayerControl>(untargetablePlayers) : new List<PlayerControl>();
                    foreach (byte id in shielded) {
                        var p = Helpers.playerById(id);
                        if (p != null && !list.Contains(p)) list.Add(p);
                    }
                    untargetablePlayers = list;
                } catch { }
            }
        }

        // ====================================================================
        // Resets
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                shielded.Clear();
                firstMeetingSeen = false;
                lastPreviewKey = "";
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            // Player ids are per connection; the overrides live on in the history file by code.
            public static void Postfix() {
                shielded.Clear();
                firstMeetingSeen = false;
                lastPreviewKey = "";
            }
        }
    }
}
