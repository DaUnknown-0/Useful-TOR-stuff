// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * RoundReplayReports - what only the acting player's own client knows (User 2026-10-02: "the replay
 * really records everything, also morphs, vents, sabotages, kills and all other abilities?").
 *
 * Many abilities never reach the network: the Warlock's curse, the Arsonist's douse, Seer, Hacker,
 * Medium, Detective, Lighter, and the details of Unknown's Collection abilities (the host only sees
 * the module byte). Admin, cameras and vitals are purely local as well. So every client with this mod
 * reports its OWN actions to the host, addressed to the host only (module 235 on UTS' channel 240):
 *  - Sub 0 ability: the button's label and the player its owner had targeted (the outlined one, as
 *    TOR and UC mark their targets). Only clicks TOR's procedures did not already describe: those
 *    reach the host with their target anyway, see RoundReplayHost.
 *  - Sub 1 device: admin, cameras, door log or vitals opened (1) or closed (0).
 * The host checks that the sender reports about himself, keeps at most one report per second per
 * kind, and writes the event; a UC ability's generic line ("uses an ability (Saboteur)") is replaced
 * by the report's detailed one. Clients without this version report nothing - harmless.
 *
 * Host-only, no report needed: completed tasks (PlayerControl.CompleteTask runs on every client
 * through the game's own RPC), who started a sabotage (ShipStatus.UpdateSystem on the host gets the
 * player), and in the meeting the Swapper's swap and every Guesser shot (RoundReplayHost taps).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UsefulTORStuff {

    public static partial class RoundReplay {

        internal const byte ReportRpcId = 235;   // 243 is MultiJester
        private const byte RepAbility = 0, RepDevice = 1;
        private const int EvTask = 8, EvDevice = 9;

        private static readonly string[] DeviceKeys = { "", "uts.replay.dev.admin", "uts.replay.dev.cams", "uts.replay.dev.doorlog", "uts.replay.dev.vitals" };

        // host: an open device event per player (closed with the time it was used)
        private static readonly Dictionary<byte, (Ev E, float From)> deviceOpen = new Dictionary<byte, (Ev, float)>();
        private static readonly Dictionary<int, float> reportLast = new Dictionary<int, float>();
        // host: who asked for which sabotage, and when
        private static readonly Dictionary<SystemTypes, (byte Id, float T)> saboteurOf = new Dictionary<SystemTypes, (byte, float)>();
        // every client: TOR procedures this client ran for its own player (a click they describe is not reported)
        private static int localTaps;

        internal static void RegisterReports() => UTSRpc.Register(ReportRpcId, HandleReport);

        private static void ReportsBegin() {
            deviceOpen.Clear(); reportLast.Clear(); saboteurOf.Clear();
            lastDevice = 0;
        }

        // ---- sending (every client, its own actions) ----
        private static void SendReport(byte sub, Action<MessageWriter> body) {
            try {
                var client = AmongUsClient.Instance;
                var lp = PlayerControl.LocalPlayer;
                if (client == null || lp == null) return;
                var w = client.StartRpcImmediately(lp.NetId, UTSRpc.CallId, SendOption.Reliable, client.HostId);
                w.Write(ReportRpcId);
                w.Write(sub);
                body(w);
                client.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] report {sub} failed: {e.Message}");
            }
        }

        /// <summary>A click on one of the local player's ability buttons (from ButtonPatch).</summary>
        private static void LocalAbility(string label) {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return;
            byte target = OutlinedTarget(lp);
            if (AmHost()) HostAbility(lp, label, target);
            else SendReport(RepAbility, w => { w.Write(label ?? ""); w.Write(target); });
        }

        // TOR and UC mark the current target with an outline on its body: the outlined player nearest
        // to the owner is the one the click was aimed at.
        // The vanilla kill button's target glows too, yet a click on an ability button is never aimed
        // at it: an impostor setting a trap beside his kill target was logged as "uses TRAP on X"
        // (audit 04.10.). That outline is skipped.
        private static byte OutlinedTarget(PlayerControl lp) {
            byte best = 255;
            float bestD = float.MaxValue;
            PlayerControl killTarget = null;
            try { killTarget = HudManager.Instance?.KillButton?.currentTarget; } catch { }
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p == lp || p.Data == null || p.Data.IsDead) continue;
                if (killTarget != null && p == killTarget) continue;
                try {
                    var body = p.cosmetics?.currentBodySprite?.BodySprite;
                    if (body == null || body.material == null || body.material.GetFloat("_Outline") < 0.5f) continue;
                    float d = Vector2.Distance(p.transform.position, lp.transform.position);
                    if (d < bestD && d < 4f) { bestD = d; best = p.PlayerId; }
                } catch { }
            }
            return best;
        }

        // ---- devices: polled on every client ----
        private static int lastDevice;
        private static float nextDevicePoll;

        private static void DeviceTick() {
            if (!recording || Time.realtimeSinceStartup < nextDevicePoll) return;
            nextDevicePoll = Time.realtimeSinceStartup + 0.25f;
            int now = inMeeting ? 0 : CurrentDevice();
            if (now == lastDevice) return;
            int closed = lastDevice;
            lastDevice = now;
            if (closed != 0) DeviceEdge(closed, false);
            if (now != 0) DeviceEdge(now, true);
        }

        private static int CurrentDevice() {
            try {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null || lp.Data == null || lp.Data.IsDead) return 0;
                var mg = Minigame.Instance;
                if (mg != null) {
                    if (mg.TryCast<VitalsMinigame>() != null) return 4;
                    if (mg.TryCast<SecurityLogGame>() != null) return 3;
                    if (mg.TryCast<SurveillanceMinigame>() != null || mg.TryCast<PlanetSurveillanceMinigame>() != null
                        || mg.TryCast<FungleSurveillanceMinigame>() != null) return 2;
                }
                var map = MapBehaviour.Instance;
                if (map != null && map.IsOpen && map.countOverlay != null && map.countOverlay.isActiveAndEnabled) return 1;
            } catch { }
            return 0;
        }

        private static void DeviceEdge(int kind, bool open) {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return;
            if (AmHost()) HostDevice(lp, kind, open);
            else SendReport(RepDevice, w => { w.Write((byte)kind); w.Write(open); });
        }

        // ---- receiving (host) ----
        private static void HandleReport(MessageReader reader) {
            try {
                var sender = UTSRpc.Sender;
                byte sub = reader.ReadByte();
                if (!AmHost() || sender == null || !recording || !hostRec) return;
                if (sub == RepAbility) {
                    string label = reader.ReadString();
                    byte target = reader.ReadByte();
                    if (!Throttle(sender.PlayerId, 0)) return;
                    HostAbility(sender, label, target);
                } else if (sub == RepDevice) {
                    byte kind = reader.ReadByte();
                    bool open = reader.ReadBoolean();
                    if (kind < 1 || kind >= DeviceKeys.Length || !Throttle(sender.PlayerId, 1 + kind * 2 + (open ? 1 : 0))) return;
                    HostDevice(sender, kind, open);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] report failed: {e.Message}");
            }
        }

        private static bool Throttle(byte id, int kind) {
            int key = id << 8 | kind;
            float now = Time.realtimeSinceStartup;
            if (reportLast.TryGetValue(key, out var last) && now - last < 1f) return false;
            reportLast[key] = now;
            return true;
        }

        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        private static void HostAbility(PlayerControl who, string label, byte target) {
            if (!recording || !hostRec || who == null) return;
            label = (label ?? "").Replace("<", "").Replace(">", "").Trim();
            if (label.Length == 0) return;
            if (label.Length > 32) label = label.Substring(0, 32);
            // the detailed report replaces the generic UC line of the same moment
            for (int k = events.Count - 1; k >= 0 && k >= events.Count - 8; k--) {
                var e = events[k];
                if (e.Generic && e.Actor == who.PlayerId && clock - e.T < 2f) { events.RemoveAt(k); break; }
            }
            var tp = target != 255 ? TheOtherRoles.Helpers.playerById(target) : null;
            string text = tp != null
                ? UTSLocalization.Tr("uts.replay.ev.button_target", WhoId(who.PlayerId), label, WhoId(target, false))
                : UTSLocalization.Tr("uts.replay.ev.button_used", WhoId(who.PlayerId), label);
            Note(EvAbility, who.PlayerId, who.transform.position, text, target: tp != null ? target : (byte)255);
        }

        private static void HostDevice(PlayerControl who, int kind, bool open) {
            if (!recording || !hostRec || who == null) return;
            string dev = UTSLocalization.Tr(DeviceKeys[kind]);
            if (open) {
                Note(EvDevice, who.PlayerId, who.transform.position, UTSLocalization.Tr("uts.replay.ev.device_open", WhoId(who.PlayerId), dev));
                if (events.Count > 0) deviceOpen[who.PlayerId] = (events[events.Count - 1], clock);
            } else if (deviceOpen.TryGetValue(who.PlayerId, out var o)) {
                deviceOpen.Remove(who.PlayerId);
                o.E.Text = UTSLocalization.Tr("uts.replay.ev.device_used", WhoId(who.PlayerId), dev, Mathf.Max(1, Mathf.RoundToInt(clock - o.From)));
            }
        }

        // ---- tasks (host: the game's own RPC runs CompleteTask on every client) ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
        static class CompleteTaskPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint idx) {
                try {
                    if (!recording || !hostRec || inMeeting || __instance == null) return;
                    string task = "?", room = "-";
                    try {
                        foreach (var t in __instance.myTasks.ToArray()) {
                            if (t == null || t.Id != idx) continue;
                            task = TranslationController.Instance.GetString(t.TaskType);
                            room = RoomName(t.StartAt);
                            break;
                        }
                    } catch { }
                    Note(EvTask, __instance.PlayerId, __instance.transform.position, UTSLocalization.Tr("uts.replay.ev.task", WhoId(__instance.PlayerId), task, room));
                } catch { }
            }
        }

        // ---- who started a sabotage (host) ----
        private static void NoteSaboteur(PlayerControl player, SystemTypes system, byte amount) {
            try {
                if (!recording || !hostRec || player == null || system != SystemTypes.Sabotage) return;
                saboteurOf[(SystemTypes)amount] = (player.PlayerId, clock);
            } catch { }
        }

        private static byte SaboteurOf(SystemTypes st) {
            if (saboteurOf.TryGetValue(st, out var s) && clock - s.T < 3f) return s.Id;
            // the reactor of Polus is the laboratory, the Airship's the heli sabotage: one request, either key
            foreach (var kv in saboteurOf) if (clock - kv.Value.T < 3f && SabKey(kv.Key) == SabKey(st)) return kv.Value.Id;
            return 255;
        }

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.UpdateSystem), new[] { typeof(SystemTypes), typeof(PlayerControl), typeof(MessageReader) })]
        static class UpdateSystemReaderPatch {
            public static void Prefix(SystemTypes systemType, PlayerControl player, MessageReader msgReader) {
                try {
                    if (systemType != SystemTypes.Sabotage || msgReader == null || msgReader.Length - msgReader.Position < 1) return;
                    int pos = msgReader.Position;
                    byte amount = msgReader.ReadByte();
                    msgReader.Position = pos;
                    NoteSaboteur(player, systemType, amount);
                } catch { }
            }
        }

        // The host's own sabotage never takes that path: RpcUpdateSystem applies it directly on the host.
        // Only the MessageReader overload of UpdateSystem is patched besides: its byte overload is a
        // small wrapper, and small Il2Cpp methods are not detoured (identical code bodies get merged).
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.RpcUpdateSystem), new[] { typeof(SystemTypes), typeof(byte) })]
        static class RpcUpdateSystemPatch {
            public static void Prefix(SystemTypes systemType, byte amount) {
                if (AmHost()) NoteSaboteur(PlayerControl.LocalPlayer, systemType, amount);
            }
        }
    }
}
