// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * AntiStartKill - the spawn area is a safe zone: nobody kills (or sidekicks) until BOTH sides of
 * the attempt have left the start zone at least once.
 *
 * The classic start kill - somebody camps the Dropship/Cafeteria spawn and stabs a player who is
 * still reading their role card - decides rounds before they begin. This rule removes exactly that
 * window and nothing else: one flag per player, "has left the spawn area once". A kill or a
 * sidekick needs BOTH flags (killer and victim). The elegant consequence: the kill check needs no
 * geometry at all, because a player who never left the zone is necessarily still standing in it.
 *
 * THE ZONE, WITHOUT PER-MAP HARDCODING
 * At round start (after the intro, when players stand on their spawn) the HOST records each
 * player's ship room via ShipStatus.FastRooms - Dropship on Polus, Cafeteria on Skeld, Launchpad
 * on Mira - and their exact position as a fallback for positions outside any room collider.
 * "Left" = seen in a DIFFERENT room, or (when no room resolves) further than a few units from the
 * recorded spawn point. Teleport-style jumps in the opening seconds (Airship spawn select, a vent
 * hop) RE-RECORD the spawn instead of counting as leaving - walking can't jump, so a real exit on
 * foot is never misread, and a vent exit merely delays the impostor's own flag. Every "left" event
 * is a one-shot host RPC, so all modded clients agree on the flags.
 *
 * WHEN IT ENDS
 * A meeting ends ALL remaining protection (explicitly requested: "nach Meetings wird der Schild
 * automatisch beendet") - the feature covers the round opening, never the mid-game. Death,
 * disconnect and round end clear it too. It does NOT re-arm after meetings.
 *
 * ENFORCEMENT - the same two-and-a-half layers as NewcomerShield, for the same reasons:
 *  1. PlayerControl.CheckMurder PREFIX on the host: vanilla kills are host-authoritative, so this
 *     blocks even a killer whose client never heard of the feature.
 *  2. Helpers.checkMuderAttempt POSTFIX on every modded client: the single funnel for every
 *     TOR/UC role kill - impostor button, Sheriff, Vampire bite (the bite itself is refused, so no
 *     delayed death sneaks past), Warlock's curse kill (killer = the Warlock, wherever his cursed
 *     proxy stands), Witch, Ninja, Thief. Only PerformKill/DelayVampireKill are overridden -
 *     a BlankKill already kills nobody and the Pursuer's blank was consumed inside the original,
 *     so rewriting it would only distort the button feedback.
 *  3. RPCProcedure.jackalCreatesSidekick PREFIX: sidekicking is not a murder and bypasses funnel 2,
 *     so it is refused at the RPC procedure both send- and receive-side. Prefix-skip is safe here:
 *     neither TOR nor any of our mods patch this procedure (checked 2026-08-15), so there is no
 *     foreign prefix whose side effects could run anyway (the HarmonyX all-prefixes pitfall).
 *
 * Deliberately NOT touched: setTarget's untargetable list. NewcomerShield uses it, but there it
 * protects a HANDFUL of players - here EVERYBODY is protected at round start, and untargetable-
 * everyone would break every benign start-zone targeting there is (the Medic shielding somebody in
 * the Dropship being the canonical case). Cost of that trade: a Thief who attacks a valid target
 * in the spawn zone keeps his suicide wart (TOR sets suicideFlag before the result is read), and a
 * blocked killer eats his cooldown - the notify option below exists so he at least learns why.
 *
 * EDGE CASES THIS FILE DEFENDS AGAINST (each at its own code site):
 *  - Host migration mid-round: the promoted host must neither RE-assign protection (sawAssignment
 *    latch, RPC-set on every client) nor guess "has left" without the host-only spawn snapshot
 *    (protection ends with a clean Clear instead).
 *  - A meeting racing the assignment (emergency cooldown 0 + lost intro marker): meetingSeen
 *    stands the assignment down - a shield granted after a meeting is exactly what this feature
 *    promises not to create. Late assignment in general is capped (AssignWindowSeconds).
 *  - Hide'n'Seek / PropHunt: both MODES are built on hunting from the first second; no assignment
 *    there (TOR gates its own first-kill shield the same way, IntroPatch.cs:89).
 *  - Thief suicide: TOR raises Thief.suicideFlag inside checkMuderAttempt, before any postfix can
 *    veto the kill - the flag is cleared for spawn-protected attempts, so the zone's "nobody dies
 *    here" also covers the Thief's own mistake. (His Armored armor may still break first,
 *    checkArmored fires earlier in the original - accepted, vanishingly rare.)
 *  - Overlapping/jittery room colliders: a room CHANGE only counts as leaving together with >1
 *    unit of distance from the recorded spawn point.
 *  - Bomber: bomb detonation kills run through checkMurderAttemptAndKill on every victim's own
 *    client (Objects/Bomb.cs:86), so funnel 2 covers them - a protected player survives the
 *    blast, a still-protected Bomber's bomb kills nobody.
 *  - Sheriff misfire counts as a kill like any other: a spawn-protected exchange suppresses it,
 *    and TOR's button treats SuppressKill as a full no-op (no cooldown, Buttons.cs:396).
 *
 * Options 1390-1391, module byte 241 on UTSRpc.CallId = 240 (new feature - channel only, no
 * legacy dual-send). See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.CustomGameModes;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class AntiStartKill {

        // ---- Options (1390-1391) ----
        public static CustomOption Enabled;
        public static CustomOption NotifyKiller;

        // ---- Everyone: who has NOT yet left the spawn area (player ids, host-authoritative) ----
        private static readonly HashSet<byte> protectedIds = new HashSet<byte>();

        public static bool Active => protectedIds.Count > 0;
        public static bool IsProtected(byte playerId) => protectedIds.Contains(playerId);

        // The outline colour, painted by UTSShieldOutlines. Green: reads as "safe zone", and it is
        // unmistakable next to the other three shield colours (Medic cyan Color32(0,221,255),
        // TOR first-kill plain blue, NewcomerShield gold 255/205/40).
        public static readonly Color ShieldColor = new Color32(80, 240, 100, byte.MaxValue);

        private const byte RpcId = UsefulTORStuffPlugin.AntiStartKillRpcId;
        private const byte SubSetProtected = 0;   // count, ids...
        private const byte SubLeft         = 1;   // playerId
        private const byte SubClear        = 2;

        // ---- Host state: the recorded spawn of every protected player ----
        private struct SpawnInfo {
            public bool hasRoom;
            public SystemTypes room;
            public Vector2 pos;
        }
        private static readonly Dictionary<byte, SpawnInfo> spawns = new Dictionary<byte, SpawnInfo>();
        // Previous tick's position, to tell a teleport (re-record) from walking out (left).
        private static readonly Dictionary<byte, Vector2> lastPos = new Dictionary<byte, Vector2>();

        // Walking covers well under one unit per tick at any legal speed; vent hops, Airship spawn
        // select and Transporter swaps cover several units instantly.
        private const float TeleportJump = 3f;
        // Teleport tolerance only in the opening seconds - later a jump IS a movement tool in use,
        // and whoever plays movement tools has long stopped being a spawn camper's victim.
        private const float TeleportGraceSeconds = 20f;
        // Fallback when no ship room resolves for a position (corridor gaps): how far from the
        // recorded spawn point counts as "left".
        private const float LeaveDistance = 5f;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1390, Types.General,
                    "Anti Start Kill (Spawn Is A Safe Zone)", false, null, true);
                NotifyKiller = CustomOption.Create(1391, Types.General,
                    "Tell The Killer Why The Kill Failed", true, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.antistartkill.option_name");
                UTSLocalization.BindOptionTitle(NotifyKiller, "uts.antistartkill.option_notify");
                UsefulTORStuffPlugin.Logger?.LogInfo("[AntiStartKill] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(RpcId, HandleModuleRpc);

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // TOR's PropHunt class is INTERNAL (HideNSeek is public), so its gamemode flag comes via
        // reflection - resolved once, and a missing field simply reads as "not prop hunt".
        private static System.Reflection.FieldInfo fiPropHunt;
        private static bool propHuntResolved;

        private static bool IsPropHuntGM() {
            try {
                if (!propHuntResolved) {
                    propHuntResolved = true;
                    fiPropHunt = typeof(Helpers).Assembly
                        .GetType("TheOtherRoles.CustomGameModes.PropHunt")
                        ?.GetField("isPropHuntGM",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (fiPropHunt == null)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[AntiStartKill] PropHunt.isPropHuntGM not found - prop hunt gate inactive.");
                }
                return fiPropHunt != null && (bool)fiPropHunt.GetValue(null);
            } catch { return false; }
        }

        // Ship room containing `pos`, via the same FastRooms/OverlapPoint lookup the vanilla
        // UsablesPatch and CollectorRelics use. Null when no room collider contains the point.
        private static SystemTypes? RoomAt(Vector2 pos) {
            try {
                var ship = ShipStatus.Instance;
                if (ship == null || ship.FastRooms == null) return null;
                foreach (var room in ship.FastRooms.Values)
                    if (room != null && room.roomArea != null && room.roomArea.OverlapPoint(pos))
                        return room.RoomId;
            } catch { }
            return null;
        }

        // ---- RPC ----
        private static void SendSetProtected(List<byte> ids) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubSetProtected);
                w.Write((byte)Math.Min(ids.Count, 255));
                for (int i = 0; i < ids.Count && i < 255; i++) w.Write(ids[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetProtected(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] send failed: {e}");
            }
        }

        private static void SendLeft(byte playerId) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubLeft);
                w.Write(playerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyLeft(playerId);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] left send failed: {e}");
            }
        }

        private static void SendClear() {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] clear failed: {e}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSetProtected: {
                        int n = reader.ReadByte();
                        var ids = new List<byte>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadByte());
                        // Host-authoritative: a forged message would let any client freeze the round.
                        if (UTSRpc.RequireHost("AntiStartKill.SetProtected")) ApplySetProtected(ids);
                        break;
                    }
                    case SubLeft:
                        byte id = reader.ReadByte();
                        if (UTSRpc.RequireHost("AntiStartKill.Left")) ApplyLeft(id);
                        break;
                    case SubClear:
                        if (UTSRpc.RequireHost("AntiStartKill.Clear")) ApplyClear();
                        break;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] rpc failed: {e}");
            }
        }

        private static void ApplySetProtected(List<byte> ids) {
            sawAssignment = true;   // migration guard - see the latch comment above
            protectedIds.Clear();
            foreach (byte id in ids) protectedIds.Add(id);
            if (protectedIds.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[AntiStartKill] {protectedIds.Count} player(s) protected until they leave the spawn area.");
        }

        private static void ApplyLeft(byte playerId) => protectedIds.Remove(playerId);

        private static void ApplyClear() => protectedIds.Clear();

        // ====================================================================
        // The driver: a HudManager.Update postfix - HudManager only exists during rounds, which is
        // the only time this feature has anything to do. The painter (UTSShieldOutlines) already
        // proved this hook reliable on this install; the per-round flag resets live in the
        // resetVariables patch below, NOT here, because this tick never runs in the lobby.
        // ====================================================================
        private static float nextTick;
        private static bool roundSeen;
        private static float roundSeenAt;
        private static bool assignedThisRound;
        private static float assignedAt;
        // Set by the Priority.First prefix marker below (the safest hook there is - see
        // NewcomerShield's autopsy); the timeout covers even that marker being lost.
        private static bool introOverSeen;
        private const float IntroFallbackSeconds = 10f;

        // ---- edge-case latches ----
        // True once ANY assignment was applied this round - set on the host AND on every client
        // (via the SetProtected RPC). This is the host-MIGRATION guard: a client promoted to host
        // mid-round still has assignedThisRound == false, and without this latch its next tick
        // would run AssignProtection and re-protect the whole lobby wherever they happen to stand.
        private static bool sawAssignment;
        // True once a meeting started this round. A meeting must END all protection for good -
        // including protection that was never granted: with the emergency cooldown at zero a
        // meeting can beat the 10 s intro FALLBACK (the marker being lost), and assigning right
        // after it would be exactly the post-meeting shield this feature promises not to be.
        private static bool meetingSeen;
        // The assignment window. Normally the assignment runs seconds after the intro; anything
        // arriving this late is a migrated host or a horribly stalled start, and protecting
        // players mid-round who long left the spawn would be wrong either way.
        private const float AssignWindowSeconds = 40f;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class DriverPatch {
            public static void Postfix() => Tick();
        }

        private static void Tick() {
            try {
                if (Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 0.2f;

                var client = AmongUsClient.Instance;
                if (client == null) return;
                if (ShipStatus.Instance == null || !client.IsGameStarted) return;

                if (!roundSeen) { roundSeen = true; roundSeenAt = Time.realtimeSinceStartup; }

                // A meeting ends every remaining shield - by design, and permanently for the round.
                // The MeetingHud.Start postfix below says the same; the tick repeats it from outside
                // any shared patch chain, so the shield can never outlive the meeting.
                if (MeetingHud.Instance != null) {
                    meetingSeen = true;
                    if (protectedIds.Count > 0) {
                        if (client.AmHost) SendClear();
                        else ApplyClear();
                    }
                    return;
                }

                if (!client.AmHost) return;

                if (!assignedThisRound) {
                    // One assignment per round, ever - even across a host migration (sawAssignment
                    // arrives via RPC on every client, so the promoted host knows). A meeting kills
                    // the round's protection before it is even granted, a stale round gets none,
                    // and the chase gamemodes are built ON start kills - protection would break
                    // them (TOR gates its own first-kill shield there too, IntroPatch.cs:89).
                    if (sawAssignment || meetingSeen
                        || Time.realtimeSinceStartup - roundSeenAt > AssignWindowSeconds
                        || HideNSeek.isHideNSeekGM || IsPropHuntGM()) {
                        assignedThisRound = true;
                        return;
                    }
                    if (Enabled == null || !UTSGate.Bool(Enabled)) { assignedThisRound = true; return; }
                    // Wait for the intro: the spawn snapshot must be taken where players actually
                    // STAND, and TOR's resetVariables (which clears our list) must be long over.
                    if (!introOverSeen
                        && Time.realtimeSinceStartup - roundSeenAt < IntroFallbackSeconds) return;
                    assignedThisRound = true;
                    AssignProtection();
                    return;
                }

                // Host migration while protection is live: the spawn snapshot was host-only state
                // and did not migrate. Without it "has left" is undecidable, so the protection ends
                // cleanly instead of guessing - fail open toward normal gameplay.
                if (protectedIds.Count > 0 && spawns.Count == 0) {
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[AntiStartKill] host changed mid-round - spawn snapshot lost, protection ends.");
                    SendClear();
                    return;
                }

                LeaveTick();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] tick failed: {e}");
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)]
        static class IntroOverMarkerPatch {
            public static void Prefix() => introOverSeen = true;
        }

        // ====================================================================
        // Host: snapshot the spawn, protect everyone
        // ====================================================================
        private static void AssignProtection() {
            try {
                spawns.Clear();
                lastPos.Clear();
                var ids = new List<byte>();
                var roomLog = new Dictionary<string, int>();
                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (!IsAlive(p)) continue;
                    Vector2 pos = p.GetTruePosition();
                    SystemTypes? room = RoomAt(pos);
                    spawns[p.PlayerId] = new SpawnInfo {
                        hasRoom = room.HasValue,
                        room = room ?? default,
                        pos = pos
                    };
                    lastPos[p.PlayerId] = pos;
                    ids.Add(p.PlayerId);
                    string key = room.HasValue ? room.Value.ToString() : "none";
                    roomLog[key] = roomLog.TryGetValue(key, out int c) ? c + 1 : 1;
                }
                assignedAt = Time.realtimeSinceStartup;

                // Always logged: this line is the proof the assignment ran, and the room histogram
                // answers whether the zone lookup found the actual spawn room on this map.
                var parts = new List<string>();
                foreach (var kv in roomLog) parts.Add($"{kv.Value}x {kv.Key}");
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[AntiStartKill] round start: {ids.Count} player(s) protected in the spawn area "
                    + $"({string.Join(", ", parts)}).");

                if (ids.Count > 0) SendSetProtected(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] assign failed: {e}");
            }
        }

        // ====================================================================
        // Host: watch the protected players until each has left the zone once
        // ====================================================================
        private static void LeaveTick() {
            if (protectedIds.Count == 0) return;
            try {
                List<byte> left = null;
                foreach (byte id in protectedIds) {
                    var p = Helpers.playerById(id);
                    // Dead, gone, or never snapshotted: the flag has no job left to do.
                    if (!IsAlive(p) || !spawns.TryGetValue(id, out SpawnInfo spawn)) {
                        (left ??= new List<byte>()).Add(id);
                        continue;
                    }

                    Vector2 pos = p.GetTruePosition();
                    SystemTypes? room = RoomAt(pos);
                    // A room change also needs a real distance from the spawn POINT: overlapping
                    // room colliders or lookup jitter right on the spawn tile must never flag a
                    // player who has not actually moved (one unit is far below any doorway).
                    bool hasLeft = (room.HasValue && spawn.hasRoom)
                        ? room.Value != spawn.room && (pos - spawn.pos).magnitude > 1f
                        : (pos - spawn.pos).magnitude > LeaveDistance;

                    if (hasLeft
                        && Time.realtimeSinceStartup - assignedAt < TeleportGraceSeconds
                        && lastPos.TryGetValue(id, out Vector2 prev)
                        && (pos - prev).magnitude > TeleportJump) {
                        // A jump, not a walk (Airship spawn select, a vent hop): this is still the
                        // player ARRIVING somewhere, so the arrival becomes the new spawn.
                        spawns[id] = new SpawnInfo {
                            hasRoom = room.HasValue, room = room ?? default, pos = pos };
                        lastPos[id] = pos;
                        continue;
                    }

                    if (hasLeft) (left ??= new List<byte>()).Add(id);
                    else lastPos[id] = pos;
                }

                if (left != null)
                    foreach (byte id in left) {
                        SendLeft(id);
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[AntiStartKill] player {id} left the spawn area - no longer protected "
                            + $"({protectedIds.Count} remaining).");
                    }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[AntiStartKill] leave tick failed: {e}");
            }
        }

        // The requested rule: any meeting ends all remaining protection - and marks the round so a
        // not-yet-run assignment stands down for good (see meetingSeen).
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    meetingSeen = true;
                    if (protectedIds.Count == 0) return;
                    if (AmHost()) SendClear();
                    else ApplyClear();   // clients drop it locally too, in case the RPC is lost
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 1: vanilla kills, host-authoritative
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
        static class CheckMurderPatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (protectedIds.Count == 0 || __instance == null || target == null) return true;
                    if (!protectedIds.Contains(__instance.PlayerId)
                        && !protectedIds.Contains(target.PlayerId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[AntiStartKill] blocked a vanilla kill on {target.Data?.PlayerName} (spawn protection).");
                    return false;
                } catch { return true; }
            }
        }

        // ====================================================================
        // Enforcement 2: every TOR/UC role kill, on the killer's own client
        //
        // POSTFIX, not prefix - the override-as-postfix pattern this codebase settled on for
        // TOR-patched methods (HarmonyX runs every prefix regardless of skip flags). Only
        // PerformKill and DelayVampireKill are rewritten: SuppressKill is already refused, and a
        // BlankKill kills nobody while its side effects (the Pursuer's consumed blank) already
        // happened inside the original.
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class CheckMurderAttemptPatch {
            public static void Postfix(PlayerControl killer, PlayerControl target,
                                       ref MurderAttemptResult __result) {
                try {
                    if (protectedIds.Count == 0 || killer == null || target == null) return;
                    if (!protectedIds.Contains(killer.PlayerId)
                        && !protectedIds.Contains(target.PlayerId)) return;

                    // Nobody dies at spawn - the Thief included. TOR raises his suicide flag INSIDE
                    // the original (a failed thief kill returns SuppressKill with suicideFlag set,
                    // Helpers.cs:518) and the thief button reads it right after this call returns
                    // (Buttons.cs:1885). Clearing it here voids the suicide for a spawn-protected
                    // attempt. No role probing opens up: refused kill and spared suicide look
                    // identical from the thief's screen - nothing happens either way.
                    if (Thief.thief != null && killer.PlayerId == Thief.thief.PlayerId
                        && Thief.suicideFlag)
                        Thief.suicideFlag = false;

                    if (__result != MurderAttemptResult.PerformKill
                        && __result != MurderAttemptResult.DelayVampireKill) return;

                    __result = MurderAttemptResult.SuppressKill;
                    NotifyLocal(killer, "uts.antistartkill.kill_blocked");
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 3: the Jackal's sidekick, explicitly part of the rule
        //
        // Not a murder, so funnel 2 never sees it. The procedure runs on the sender AND on every
        // receiver of the RPC, so all modded clients refuse it in the same tick. Prefix-skip is
        // safe: nobody else patches this procedure (see the header).
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.jackalCreatesSidekick))]
        static class JackalCreatesSidekickPatch {
            public static bool Prefix([HarmonyArgument(0)] byte targetId) {
                try {
                    if (protectedIds.Count == 0) return true;
                    var jackal = Jackal.jackal;
                    if ((jackal == null || !protectedIds.Contains(jackal.PlayerId))
                        && !protectedIds.Contains(targetId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[AntiStartKill] blocked a sidekick creation (spawn protection).");
                    NotifyLocal(jackal, "uts.antistartkill.sidekick_blocked");
                    return false;
                } catch { return true; }
            }
        }

        // Feedback for the blocked actor, on his own client only. Without it the button just eats
        // the cooldown and the round looks broken. Throttled: a Vampire hammering his bite button
        // (its couldUse stays true) must not flood his own chat.
        private static float lastNotifyAt = -10f;

        private static void NotifyLocal(PlayerControl actor, string key) {
            try {
                if (NotifyKiller == null || !UTSGate.Bool(NotifyKiller)) return;
                if (actor == null || PlayerControl.LocalPlayer == null
                    || actor.PlayerId != PlayerControl.LocalPlayer.PlayerId) return;
                if (Time.realtimeSinceStartup - lastNotifyAt < 1.5f) return;
                lastNotifyAt = Time.realtimeSinceStartup;
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null)
                    hud.Chat.AddChat(PlayerControl.LocalPlayer, UTSLocalization.Tr(key));
            } catch { }
        }

        // ====================================================================
        // Resets. The per-round flags reset HERE (resetVariables runs during every round setup,
        // long before the post-intro assignment) because the HudManager driver never ticks in the
        // lobby. PlayerId lists additionally clear on OnGameJoined - resetVariables-only resets
        // leak into foreign lobbies (the Fake-Mini bug, UTS 1.3.3.4).
        // ====================================================================
        private static void ResetRoundState() {
            protectedIds.Clear();
            spawns.Clear();
            lastPos.Clear();
            roundSeen = false;
            introOverSeen = false;
            assignedThisRound = false;
            sawAssignment = false;
            meetingSeen = false;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => ResetRoundState();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => ResetRoundState();
        }
    }
}
