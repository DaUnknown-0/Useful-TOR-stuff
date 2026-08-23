// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * NewcomerShield - somebody joining your group for the first time cannot be killed before the first
 * meeting of their first round.
 *
 * Being murdered in the opening thirty seconds of the very first round you ever play, before anyone
 * has explained what a task even is, is the single worst first impression this game can make. This
 * gives newcomers exactly one round to find their feet, and it ends at the first meeting - not at
 * the end of the round - so it never decides a game.
 *
 * WHO IS "NEW"
 * The host keeps a set of FRIEND CODES that have already played a round with him. Friend codes are
 * the only identifier that survives a round and a lobby change; PlayerId and ClientId are both tied
 * to the connection and get reshuffled. The set lives in memory only: restarting the game forgets
 * everyone, which is exactly the intent ("new to this session"). The host can also mark people by
 * hand in the lobby, for the case the automatic rule does not catch (a friend who reinstalled, a
 * player everybody agrees should get a free round).
 *
 * WHY IT IS ENFORCED IN TWO PLACES
 * Among Us and TOR kill through two entirely different paths:
 *
 *  1. VANILLA impostor kills are genuinely host-authoritative: the killer's client sends
 *     CmdCheckMurder, the HOST runs CheckMurder and only then broadcasts the murder. A prefix there
 *     stops the kill even if the killer's client knows nothing about this feature. That is the hard
 *     guarantee, and it is the reason this lives in a host-side mod.
 *
 *  2. TOR ROLE kills (Sheriff, Jackal, Vampire, Werewolf, every Unknown's Collection role...) do NOT
 *     ask the host. They run Helpers.checkMuderAttempt on the KILLER's own client and then broadcast
 *     the result - the same path that carries the Medic shield and TOR's own "shield first kill".
 *     The host cannot intercept those without undoing a kill after the fact, so the rule is applied
 *     on every client that has this mod. In a lobby where everyone runs the mods that is complete.
 *
 * Either way the AUTHORITY over the list stays with the host: he decides who is shielded and hands
 * the ids out over RPC. No client can shield itself.
 *
 * WHY THE ASSIGNMENT AND THE PREVIEW RUN OFF THEIR OWN TICK, NOT OFF HARMONY POSTFIXES
 * The first build hung both on Priority.Low POSTFIXES (IntroCutscene.OnDestroy for the round
 * start, GameStartManager.Update for the lobby preview). That is a dependency on every OTHER
 * patch of the same method finishing cleanly: Harmony runs the original and all postfixes inside
 * one compiled replacement, in priority order, and an exception thrown by an earlier postfix
 * unwinds past every later one. A finalizer (LobbyLeakGuard has them on GameStartManager) only
 * swallows the exception at the method boundary; it cannot resurrect the skipped postfixes. On
 * this install TOR's GameStartManagerUpdatePatch.Postfix throws EVERY FRAME over the broken
 * lobby screen (LogOutput 2026-08-14: "GameStartManager.Update threw ... suppressed"), so the
 * preview postfix never ran once. The playtest proved it: no preview, no outline, no
 * "[NewcomerShield] ... shielded" line.
 *
 * So both now run from NewcomerShieldUI.Update, a plain MonoBehaviour on the plugin's own
 * GameObject (the UTSModSyncUI pattern): no foreign patch can throw it away. The only Harmony
 * hook left in the decision path is a Priority.First PREFIX marker on IntroCutscene.OnDestroy,
 * which by definition runs before anything else on that method can throw, and a 10 second
 * fallback covers even that being lost.
 *
 * IDENTITY ON CUSTOM SERVERS
 * This lobby plays on a custom server (useDtls false in the log), and servers without account
 * auth hand out EMPTY friend codes. An empty code used to mean "never shielded", silently. Now
 * an empty code falls back to a name-based key ("name:" + PlayerName). A name can be copied, a
 * friend code cannot - but the shield is a courtesy worth one round, not a security boundary,
 * and the fallback is what makes the feature exist at all on such servers. The assignment logs
 * how many players sat on each identity kind, so the next playtest answers which case we are in.
 *
 * WHAT THE SHIELD DELIBERATELY DOES NOT COVER
 * A shielded newcomer who is a LOVER still dies when their partner dies. TOR's lover cascade calls
 * MurderPlayer directly (PlayerControlPatch.cs:1226) and bypasses every check, and that is left
 * alone on purpose: the shield protects against being killed, not against the
 * bond itself. Suppressing it would mean a lover pair cannot die together in the first round, which
 * quietly guts the lovers' own rule for everybody else at the table.
 *
 * Options 1380-1381, module byte 242 on UTSRpc.CallId = 240. See ID-Registry.md.
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

    public static class NewcomerShield {

        // ---- Options (1380-1381) ----
        public static CustomOption Enabled;
        public static CustomOption NotifyKiller;

        // ---- Host state: survives rounds, lobbies, and now short restarts ----
        // Friend codes that have already started a round with this host. Originally memory-only;
        // since 2026-08-15 the set is persisted with a heartbeat and RESTORED when the game comes
        // back within ten minutes: a crash or a quick reboot must not hand the
        // whole lobby a fresh shield round. Only after ten minutes of the game being gone does the
        // session truly start over.
        private static readonly HashSet<string> seenFriendCodes = new HashSet<string>();

        // THE FIRST-LOBBY GRACE: the very first lobby of a session is
        // the regular group assembling, not newcomers arriving - so nobody who joins before the
        // session's FIRST round starts gets the automatic shield. Only people who show up once a
        // round has already been played this session count as new. A restored session (crash +
        // relaunch within the window) had its first round already, so the grace does not re-apply.
        // The host's MANUAL mark still wins even in the first lobby: an explicit click is intent.
        private static bool sessionHadRound;
        // Friend codes the host marked by hand in the lobby; they get the same one-round shield.
        private static readonly HashSet<string> manualNewcomers = new HashSet<string>();
        // ...and the opposite: people the host took the shield away from. Without this second set the
        // lobby button could not undo an AUTOMATIC detection at all - toggling a player the rule
        // already found would just add them to the manual set and leave them protected either way.
        private static readonly HashSet<string> manualExcluded = new HashSet<string>();

        // ---- Everyone: who is shielded in THIS round (player ids, valid until the first meeting) ----
        private static readonly HashSet<byte> shielded = new HashSet<byte>();

        private const byte RpcId = UsefulTORStuffPlugin.NewcomerShieldRpcId;
        private const byte SubSetShields = 0;   // count, ids...
        private const byte SubClear      = 1;

        public static bool Active => shielded.Count > 0;
        public static bool IsShielded(byte playerId) => shielded.Contains(playerId);
        public static IReadOnlyCollection<byte> Shielded => shielded;

        // The outline colour. Deliberately NOT one of TOR's: the Medic shield is cyan
        // (Color32(0,221,255), TheOtherRoles.cs:442) and "shield first kill" is plain blue
        // (PlayerControlPatch.cs). Gold is unmistakable next to both, and reads as "protected"
        // rather than "healed".
        public static readonly Color ShieldColor = new Color32(255, 205, 40, byte.MaxValue);

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1380, Types.General,
                    "Protect Players New To This Session", false, null, true);
                NotifyKiller = CustomOption.Create(1381, Types.General,
                    "Tell The Killer Why The Kill Failed", true, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.newcomershield.option_name");
                UTSLocalization.BindOptionTitle(NotifyKiller, "uts.newcomershield.option_notify");
                UsefulTORStuffPlugin.Logger?.LogInfo("[NewcomerShield] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() {
            UTSRpc.Register(RpcId, HandleModuleRpc);
            RestoreState();
        }

        // ---- persistence: the ten minute window ----
        // The state file holds a heartbeat timestamp, the first-round flag and the seen set, one
        // code per line (player names cannot contain newlines, so no escaping is needed). The
        // heartbeat is refreshed every 30 seconds while the set is non-empty, so even a hard crash
        // leaves a timestamp within half a minute of the real exit. On startup the state is
        // restored only when that heartbeat is younger than ten minutes; otherwise it is ignored
        // and the session starts fresh, first-lobby grace included.
        private static readonly TimeSpan PersistWindow = TimeSpan.FromMinutes(10);
        private const float HeartbeatSeconds = 30f;
        private static float nextHeartbeat;

        private static string StatePath =>
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "UTSNewcomerShield.state");

        private static void RestoreState() {
            try {
                if (!System.IO.File.Exists(StatePath)) return;
                var lines = System.IO.File.ReadAllLines(StatePath);
                if (lines.Length < 2) return;
                if (!DateTime.TryParse(lines[0], System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.RoundtripKind, out var stamp)) return;
                var age = DateTime.UtcNow - stamp.ToUniversalTime();
                if (age > PersistWindow || age < TimeSpan.FromMinutes(-5)) {
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[NewcomerShield] stored session is {age.TotalMinutes:F1} min old - starting fresh.");
                    return;
                }
                sessionHadRound = lines[1] == "1";
                int n = 0;
                for (int i = 2; i < lines.Length; i++) {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    seenFriendCodes.Add(lines[i]);
                    n++;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[NewcomerShield] session restored ({n} known player(s), "
                    + $"heartbeat {age.TotalSeconds:F0}s old) - a crash or quick restart does not re-shield anyone.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[NewcomerShield] state restore failed: {e.Message}");
            }
        }

        private static void SaveState() {
            try {
                var lines = new List<string>(seenFriendCodes.Count + 2) {
                    DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    sessionHadRound ? "1" : "0"
                };
                lines.AddRange(seenFriendCodes);
                System.IO.File.WriteAllLines(StatePath, lines);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[NewcomerShield] state save failed: {e.Message}");
            }
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        // The identifier. Friend code when the server hands one out; on auth-less custom servers
        // (this lobby's feinis.deb.at, and every plain Impostor server) the code is EMPTY for
        // everybody, and treating that as "not identifiable, never shielded" made the feature
        // silently dead there. So an empty code falls back to a name-based key. A name can be
        // copied and a friend code cannot, but the shield is one free round, not a security
        // boundary, and without the fallback it does not exist at all on the server this group
        // actually plays on. The "name:" prefix keeps the two identity kinds from ever colliding.
        public static string CodeOf(PlayerControl p) {
            try {
                string code = p?.Data?.FriendCode;
                if (!string.IsNullOrEmpty(code)) return code;
                string name = p?.Data?.PlayerName;
                return string.IsNullOrEmpty(name) ? "" : "name:" + name;
            } catch { return ""; }
        }

        // True when the player has a REAL friend code (not the name fallback). Only used for the
        // diagnostic count in the round-start log, so the next playtest can tell which identity
        // kind this server actually delivers.
        private static bool HasRealCode(PlayerControl p) {
            try { return !string.IsNullOrEmpty(p?.Data?.FriendCode); } catch { return false; }
        }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // Host-side view for the lobby panel: is this player currently going to be shielded next round?
        // The host's word beats the rule in both directions: an explicit exclusion wins over
        // everything, an explicit mark wins over "already seen".
        public static bool WouldShield(PlayerControl p) {
            if (Enabled == null || !Enabled.getBool()) return false;
            string code = CodeOf(p);
            if (string.IsNullOrEmpty(code)) return false;
            if (manualExcluded.Contains(code)) return false;
            if (manualNewcomers.Contains(code)) return true;
            // First-lobby grace: before the session's first round, the automatic rule is off -
            // everyone assembling in lobby one is the regular group, not a newcomer. Mirrored
            // here so the lobby preview never promises a shield the round start will not grant.
            if (!sessionHadRound) return false;
            return !seenFriendCodes.Contains(code);
        }

        public static bool IsManual(PlayerControl p) {
            string code = CodeOf(p);
            return !string.IsNullOrEmpty(code) && manualNewcomers.Contains(code);
        }

        // Toggled from the lobby panel (host only).
        // Flips whatever the player's CURRENT state is, however it came about. Protected becomes
        // excluded, unprotected becomes marked - so the button always does what its label says.
        public static void ToggleManual(PlayerControl p) {
            string code = CodeOf(p);
            if (string.IsNullOrEmpty(code)) return;
            if (WouldShield(p)) {
                manualNewcomers.Remove(code);
                manualExcluded.Add(code);
            } else {
                manualExcluded.Remove(code);
                manualNewcomers.Add(code);
            }
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
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] send failed: {e}");
            }
        }

        private static void SendClear() {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] clear failed: {e}");
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
                        // Host-authoritative: a forged message would let any client shield anyone.
                        if (UTSRpc.RequireHost("NewcomerShield.SetShields")) ApplySetShields(ids);
                        break;
                    }
                    case SubClear:
                        if (UTSRpc.RequireHost("NewcomerShield.Clear")) ApplyClear();
                        break;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] rpc failed: {e}");
            }
        }

        private static void ApplySetShields(List<byte> ids) {
            shielded.Clear();
            foreach (byte id in ids) shielded.Add(id);
            if (shielded.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[NewcomerShield] {shielded.Count} player(s) shielded until the first meeting.");
        }

        private static void ApplyClear() => shielded.Clear();

        // ====================================================================
        // The driver: one tick, owned by this feature, driven by NewcomerShieldUI.Update
        //
        // NOT a Harmony postfix on anything. The first build died because it shared patched
        // methods with TOR: TOR's GameStartManagerUpdatePatch.Postfix throws every frame on this
        // install, and Harmony skips every later postfix of the same method once an earlier one
        // has thrown (a finalizer only swallows the exception at the method boundary, the skipped
        // postfixes stay skipped). A MonoBehaviour Update on the plugin's own GameObject cannot be
        // taken down by anyone else's patch. See the header for the full autopsy.
        // ====================================================================
        private static float nextTick;
        private static bool roundSeen;          // a round transition was observed by this tick
        private static float roundSeenAt;
        private static bool assignedThisRound;  // the one-shot latch for the round-start decision
        // Set by the Priority.First prefix marker below; the timeout exists for the day even that
        // marker is lost (an intro skipped or destroyed abnormally).
        private static bool introOverSeen;
        private const float IntroFallbackSeconds = 10f;

        public static void Tick() {
            try {
                if (Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 0.5f;

                var client = AmongUsClient.Instance;
                if (client == null) return;

                // Heartbeat for the ten minute persistence window: while there is state worth
                // keeping, its timestamp stays within 30 seconds of "now" - so even a hard crash
                // reads as "the game was here a moment ago" and a relaunch restores the session.
                if (seenFriendCodes.Count > 0 && Time.realtimeSinceStartup >= nextHeartbeat) {
                    nextHeartbeat = Time.realtimeSinceStartup + HeartbeatSeconds;
                    SaveState();
                }

                bool inRound = ShipStatus.Instance != null && client.IsGameStarted;
                if (!inRound) {
                    roundSeen = false;
                    introOverSeen = false;
                    assignedThisRound = false;
                    PhantomWatchdog();
                    // Lobby: the host recomputes and broadcasts the preview. LobbyScreen.Exists,
                    // never GameStartManager.Instance - the getter CONSTRUCTS a broken instance
                    // when none exists (see LobbyScreen in LobbyLeakGuard.cs), and polling it from
                    // this always-on tick is exactly how v1.3.3.15 planted a phantom
                    // GameStartManager at boot and degraded every session since.
                    if (client.AmHost && LobbyScreen.Exists) LobbyPreviewTick();
                    return;
                }

                if (!roundSeen) { roundSeen = true; roundSeenAt = Time.realtimeSinceStartup; }

                // The shield ends AFTER the first meeting, not at its start (changed 2026-08-23).
                //
                // It used to be cleared the moment MeetingHud.Start ran, and that made the meeting
                // the one place the shield could not protect anybody: the Guesser only exists in a
                // meeting, and so does the vote. A newcomer was safe from every kill in the opening
                // minutes and then perfectly shootable and votable the second the first meeting
                // opened, which is the half of the game a newcomer is least able to defend.
                //
                // So the lifetime now spans the first meeting: seen at its start, dropped once it
                // is genuinely over. "Over" is checked here rather than hooked onto one controller,
                // because a meeting can end through MeetingHud.Close, a normal ExileController, the
                // Airship one, or a disconnect that takes the whole thing down. Waiting for all
                // three instances to be gone covers every one of those without a patch per path,
                // and it keeps this file's original principle intact: the clear lives in the tick,
                // outside any shared patch chain.
                if (firstMeetingSeen && shielded.Count > 0
                    && MeetingHud.Instance == null && ExileController.Instance == null) {
                    if (client.AmHost) SendClear();
                    else shielded.Clear();
                }

                if (assignedThisRound || !client.AmHost) return;
                if (Enabled == null || !Enabled.getBool()) { assignedThisRound = true; return; }

                // Wait for the intro to be over before deciding. Two reasons: the shield list must
                // be built AFTER TOR's resetVariables (which clears it and runs during round setup,
                // well before the intro ends - see the resetVariables memory note), and the intro
                // end is also when the old IntroCutscene.OnDestroy hook fired, so the round
                // semantics stay exactly what they were.
                if (!introOverSeen
                    && Time.realtimeSinceStartup - roundSeenAt < IntroFallbackSeconds) return;

                assignedThisRound = true;
                AssignShields();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] tick failed: {e}");
            }
        }

        // The proof-of-fix probe for the phantom GameStartManager (see LobbyScreen in
        // LobbyLeakGuard.cs). A GameStartManager outside a joined lobby is ALWAYS wrong; before
        // this build one appeared at boot in every session and is the prime suspect behind the
        // degraded rounds. Logged once per sighting streak so the next playtest log answers
        // whether the getter hygiene actually removed it. FindObjectOfType constructs nothing.
        private static bool phantomLogged;
        private static void PhantomWatchdog() {
            try {
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.NotJoined) {
                    phantomLogged = false;
                    return;   // joined or in a lobby: a GameStartManager is legitimate
                }
                var gsm = UnityEngine.Object.FindObjectOfType<GameStartManager>();
                if (gsm == null) { phantomLogged = false; return; }
                if (phantomLogged) return;
                phantomLogged = true;
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "[NewcomerShield] phantom GameStartManager detected OUTSIDE any lobby "
                    + $"(object '{gsm.gameObject.name}') - some code constructed one via the "
                    + "DestroyableSingleton getter. This is the degraded-session trigger.");
            } catch { }
        }

        // The only Harmony hook left in the decision path, and deliberately the safest kind there
        // is: a Priority.First PREFIX that sets one flag and can neither throw nor be skipped -
        // prefixes run in priority order BEFORE the original, so nothing that throws later in the
        // chain (TOR's own OnDestroy patches included) can reach back and un-run it.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)]
        static class IntroOverMarkerPatch {
            public static void Prefix() => introOverSeen = true;
        }

        // ====================================================================
        // Host: decide at the start of the round
        // ====================================================================
        private static void AssignShields() {
            try {
                int realCodes = 0, nameFallbacks = 0;
                // The session's first round: the automatic rule stands down (see sessionHadRound),
                // only an explicit host mark shields. Everyone present is registered as known.
                bool grace = !sessionHadRound;
                var ids = new List<byte>();
                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (!IsAlive(p)) continue;
                    string code = CodeOf(p);
                    if (string.IsNullOrEmpty(code)) continue;
                    if (HasRealCode(p)) realCodes++; else nameFallbacks++;

                    bool isNew = manualNewcomers.Contains(code)
                                 || (!grace && !seenFriendCodes.Contains(code));
                    if (isNew) ids.Add(p.PlayerId);

                    // Playing this round counts as having been here, whether shielded or not.
                    // The manual mark is one-shot for the same reason: the host ticked a box for
                    // THIS round, not forever.
                    seenFriendCodes.Add(code);
                    manualNewcomers.Remove(code);
                    manualExcluded.Remove(code);
                }
                sessionHadRound = true;
                SaveState();

                // Always logged, even for zero shields: this line is the proof the decision ran at
                // all, and the identity counts answer whether this server hands out friend codes.
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[NewcomerShield] round start{(grace ? " (first round, grace - automatic rule off)" : "")}: "
                    + $"{realCodes} player(s) with a friend code, "
                    + $"{nameFallbacks} on the name fallback, {ids.Count} to shield.");

                if (ids.Count > 0) SendSetShields(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] round start failed: {e}");
            }
        }

        // Set the moment the first meeting opens; the tick uses it to know that the shield's one
        // meeting has begun and may be dropped as soon as that meeting is over. Purely local: every
        // client sees its own MeetingHud.Start, so no RPC is needed to agree on it.
        private static bool firstMeetingSeen;

        // The shield covers the opening minutes AND the first meeting, then it is gone. Note what
        // that now includes: inside that meeting the newcomer cannot be guessed and cannot be voted
        // for (see NewcomerMeetingProtection.cs). That is a deliberate widening of what this feature
        // does - it can now keep a player in the game through one vote, where before it only kept
        // them alive between votes.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() => firstMeetingSeen = true;
        }

        // ====================================================================
        // Enforcement 1: vanilla kills, host-authoritative
        //
        // CheckMurder runs on the HOST (the killer only sends CmdCheckMurder). Returning false here
        // stops the kill for a client that has never heard of this feature - the hard guarantee.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
        static class CheckMurderPatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (shielded.Count == 0 || target == null) return true;
                    if (!shielded.Contains(target.PlayerId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[NewcomerShield] blocked a vanilla kill on {target.Data?.PlayerName} (shielded).");
                    return false;
                } catch { return true; }
            }
        }

        // ====================================================================
        // Enforcement 2: every TOR/UC role kill, on the killer's own client
        //
        // POSTFIX, not prefix: HarmonyX runs every prefix regardless of what the others return, so a
        // prefix that skips the original would still let another mod's prefix act on a kill we have
        // already refused. Overriding the RESULT afterwards is the pattern this codebase settled on
        // for overrides against TOR-patched methods. The original has no side effects on this path -
        // it only reaches its "PerformKill" default - so letting it run costs nothing.
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class CheckMurderAttemptPatch {
            public static void Postfix(PlayerControl killer, PlayerControl target,
                                       ref MurderAttemptResult __result) {
                try {
                    if (shielded.Count == 0 || target == null) return;
                    if (__result == MurderAttemptResult.SuppressKill) return;   // already refused
                    if (!shielded.Contains(target.PlayerId)) return;

                    __result = MurderAttemptResult.SuppressKill;

                    // Feedback for the killer, on his own client only. Without it the button simply
                    // eats the cooldown and the round looks broken; with it he stops wasting attempts
                    // on someone he cannot kill anyway.
                    if (NotifyKiller != null && NotifyKiller.getBool()
                        && killer != null && PlayerControl.LocalPlayer != null
                        && killer.PlayerId == PlayerControl.LocalPlayer.PlayerId) {
                        var hud = HudManager.Instance;
                        if (hud != null && hud.Chat != null)
                            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                                UTSLocalization.Tr("uts.newcomershield.kill_blocked"));
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 0: a shielded player cannot even be TARGETED
        //
        // Refusing the kill afterwards is not enough for every role, and the Thief is the proof: TOR
        // runs his suicide BEFORE it looks at the result (Buttons.cs, thiefKillButton - only
        // BlankKill returns early), so a Thief clicking a shielded newcomer would kill himself for a
        // kill that never happens. The same hole would cost an impostor his cooldown for nothing.
        //
        // TOR's own targeting helper takes an "untargetable" list, so the cleanest fix is to put
        // shielded players on it: no highlight, no click, for every role at once. The checks further
        // down stay as the safety net for roles that kill without targeting.
        //
        // The Guesser is NOT one of them, contrary to what this comment claimed until
        // AUDIT-2026-08-23 (H-6): his shot runs through RPCProcedure.guesserShoot -> Exiled(),
        // which is neither a murder nor a targeted attack, so none of the checks below ever saw it.
        // That gap is closed in NewcomerMeetingProtection.cs, together with the vote block.
        // ====================================================================
        // The helper lives in PlayerControlFixedUpdatePatch, NOT PlayerControlPatch - looking for the
        // latter is why the first attempt logged "setTarget not found" and left shielded players
        // targetable. The class is public, so this is a plain attribute patch; the reflection
        // fallback below stays as a diagnostic for future TOR renames.
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch),
                      nameof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch.setTarget))]
        static class SetTargetPatch {
            public static void Prefix(ref List<PlayerControl> untargetablePlayers) =>
                SetTargetPrefix(ref untargetablePlayers);
        }

        private static void SetTargetPrefix(ref List<PlayerControl> untargetablePlayers) {
            try {
                if (shielded.Count == 0) return;
                // A peaceful ability is asking (Medic, Shifter, Morphling, Tracker, Deputy, Eraser,
                // Arsonist, Pursuer, Silencer): the shield stops kills, not the rest of the game.
                // See ShieldPeaceGate.cs for how the caller is identified.
                if (ShieldPeaceGate.Peaceful) return;
                // Being recruited is not being killed: a shielded newcomer may become the Sidekick.
                // TOR sets the Jackal's kill target and his sidekick target through this one helper,
                // so the gate cannot be lifted for the recruit alone - it is lifted for the Jackal
                // while he can still create a sidekick at all. His KILL on that same player is
                // refused anyway, twice over and independently of this list: CheckMurder on the host
                // (enforcement 1) and the checkMuderAttempt postfix on his own client (enforcement 2,
                // which also tells him why). Once the sidekick exists TOR clears canCreateSidekick
                // (RPC.cs:719) and the gate closes again by itself.
                if (JackalCanRecruitNow()) return;
                var list = untargetablePlayers != null
                    ? new List<PlayerControl>(untargetablePlayers) : new List<PlayerControl>();
                foreach (byte id in shielded) {
                    var p = Helpers.playerById(id);
                    if (p != null && !list.Contains(p)) list.Add(p);
                }
                untargetablePlayers = list;
            } catch { }
        }

        // Mirrors TOR's own condition for showing the sidekick button (Buttons.cs:1036) rather than
        // inventing a second rule: same flag, same owner check, same alive check. A Sidekick is
        // deliberately NOT covered - he cannot recruit, so nothing about him is peaceful here.
        private static bool JackalCanRecruitNow() {
            try {
                var local = PlayerControl.LocalPlayer;
                return Jackal.canCreateSidekick
                       && Jackal.jackal != null && local != null
                       && Jackal.jackal.PlayerId == local.PlayerId
                       && local.Data != null && !local.Data.IsDead;
            } catch {
                return false; // unknown state keeps the shield closed
            }
        }

        // ====================================================================
        // The shield you can SEE - painted by UTSShieldOutlines, the shared painter, which reads
        // this feature's Shielded list. Shared because a player can hold several shields at once
        // (Medic, first kill, newcomer, spawn protection) and the single outline slot then CYCLES
        // through the colours; the painter file carries the whole story, including the
        // HudManager.Update-not-FixedUpdate autopsy that used to live here.
        // ====================================================================

        // ====================================================================
        // Lobby preview
        //
        // The shield is decided at the start of the round, but seeing it only then is too late: the
        // point is that everybody - the newcomer included - knows about it BEFORE the round starts.
        // So the host broadcasts the same list already in the lobby and refreshes it whenever it
        // changes (someone joins or leaves, the host ticks a box, the option is switched).
        //
        // Called from Tick(), which has already established: we are the host, no round is running
        // (ShipStatus is null, so a leaked lobby screen can never make this recompute mid-game and
        // clear a live shield), and GameStartManager.Instance exists. Formerly a Priority.Low
        // postfix on GameStartManager.Update - the method whose TOR postfix throws every frame on
        // this install, which is why the preview never once ran. See the header.
        // ====================================================================
        private static string lastPreviewKey = "";

        private static void LobbyPreviewTick() {
            try {
                var ids = new List<byte>();
                if (Enabled != null && Enabled.getBool()) {
                    foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                        if (p == null || p.Data == null || p.Data.Disconnected) continue;
                        if (WouldShield(p)) ids.Add(p.PlayerId);
                    }
                }

                // Only send when something actually changed - this runs twice a second.
                ids.Sort();
                string key = string.Join(",", ids);
                if (key == lastPreviewKey) return;
                lastPreviewKey = key;

                if (ids.Count > 0) SendSetShields(ids);
                else SendClear();
            } catch { }
        }

        // ====================================================================
        // Resets
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            // Only the per-round shield: the seen set is the whole point and must survive.
            public static void Postfix() {
                shielded.Clear();
                firstMeetingSeen = false;
                lastPreviewKey = "";
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            // Player ids are per connection, so the shield never carries into another lobby.
            // seenFriendCodes deliberately does: the same person in a new lobby is not new again.
            public static void Postfix() {
                shielded.Clear();
                firstMeetingSeen = false;
                manualNewcomers.Clear();   // a hand-picked mark belongs to the lobby it was made in
                lastPreviewKey = "";
            }
        }
    }
}
