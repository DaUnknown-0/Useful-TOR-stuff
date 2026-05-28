// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SnitchRoomPersistFix — permanent, timing-independent Snitch reveal fix (client-side).
 *
 * The bug (TOR 4.8.0, MeetingPatch.StartMeetingPatch.Prefix): every client broadcasts its room
 * via the ShareRoom RPC at StartMeeting, then the same prefix resets Snitch.playerRoomMap at its
 * end. The host's ShareRoom (sent immediately) reaches the Snitch BEFORE the host's own batched
 * RpcStartMeeting, so when the Snitch's prefix runs the reset it wipes the host's entry. Non-host
 * players send after receiving RpcStartMeeting, so their entries survive — only the host is lost.
 *
 * The fix (runs on every client, so it needs everyone to have this mod — gated on
 * UsefulTORStuffPlugin.SnitchClientFixActive):
 *   - A postfix on RPCProcedure.shareRoom shadow-records every (playerId -> roomId), independent
 *     of TOR's reset.
 *   - A postfix on PlayerControl.StartMeeting runs AFTER TOR's prefix reset (and ~0.4s before the
 *     reveal lerp reads the map). It writes the host's shadow-recorded room back into
 *     Snitch.playerRoomMap, restoring exactly the entry the race dropped. Late-arriving non-host
 *     ShareRooms land in the map on their own.
 *
 * Reflection only (TOR types are internal): handles become no-ops if TOR changes its internals.
 */

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;

namespace UsefulTORStuff {
    public static class SnitchRoomPersistFix {
        // Shadow copy of the last room broadcast per player id. Never reset by TOR.
        internal static readonly Dictionary<byte, byte> _lastRoom = new Dictionary<byte, byte>();

        internal static FieldInfo SnitchSnitchField;
        internal static FieldInfo SnitchPlayerRoomMapField;
        internal static MethodInfo ShareRoomMethod;

        public static void Initialize(Harmony harmony) {
            try {
                var tor = UsefulTORStuffPlugin.TORAssembly
                    ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
                if (tor == null) {
                    UsefulTORStuffPlugin.Logger.LogWarning("TheOtherRoles assembly not found — Snitch fix disabled.");
                    return;
                }

                var snitchType = tor.GetType("TheOtherRoles.TheOtherRoles+Snitch");
                SnitchSnitchField = snitchType?.GetField("snitch", BindingFlags.Public | BindingFlags.Static);
                SnitchPlayerRoomMapField = snitchType?.GetField("playerRoomMap", BindingFlags.Public | BindingFlags.Static);

                var rpcProcedureType = tor.GetType("TheOtherRoles.RPCProcedure");
                var shareRoomMethod = rpcProcedureType?.GetMethod("shareRoom", BindingFlags.Public | BindingFlags.Static);

                if (SnitchSnitchField == null || SnitchPlayerRoomMapField == null || shareRoomMethod == null) {
                    UsefulTORStuffPlugin.Logger.LogWarning(
                        "Snitch reflection handles incomplete — permanent Snitch fix disabled (HostFix fallback still applies).");
                    return;
                }

                ShareRoomMethod = shareRoomMethod;

                // Shadow-record every shareRoom call (postfix, no Invoke).
                harmony.Patch(shareRoomMethod,
                    postfix: new HarmonyMethod(typeof(ShareRoomRecorder), nameof(ShareRoomRecorder.Postfix)));

                UsefulTORStuffPlugin.Logger.LogInfo("Permanent Snitch fix wired (shareRoom recorder + StartMeeting restore).");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger.LogError($"Failed to initialize Snitch fix: {ex}");
            }
        }

        public static class ShareRoomRecorder {
            // shareRoom(byte playerId, byte roomId)
            public static void Postfix(byte __0, byte __1) {
                try { _lastRoom[__0] = __1; } catch { }
            }
        }

        // Runs after TOR's StartMeeting prefix has reset playerRoomMap, well before the 0.4s reveal lerp.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
        public static class StartMeetingRestorePatch {
            public static void Postfix() {
                try {
                    if (!UsefulTORStuffPlugin.SnitchClientFixActive) return;
                    if (SnitchSnitchField == null || ShareRoomMethod == null) return;
                    if (SnitchSnitchField.GetValue(null) == null) return; // no Snitch in play
                    if (AmongUsClient.Instance == null) return;

                    // Identify the host's PlayerId — the entry the StartMeeting race drops.
                    int hostClientId = AmongUsClient.Instance.HostId;
                    byte hostPlayerId = byte.MaxValue;
                    foreach (var client in AmongUsClient.Instance.allClients.ToArray()) {
                        if (client != null && client.Id == hostClientId && client.Character != null) {
                            hostPlayerId = client.Character.PlayerId;
                            break;
                        }
                    }
                    if (hostPlayerId == byte.MaxValue) return;
                    if (!_lastRoom.TryGetValue(hostPlayerId, out byte room)) return;

                    // Re-insert via TOR's own shareRoom so the host entry lands in the exact
                    // Snitch.playerRoomMap instance the reveal lerp reads. Writing the dictionary
                    // directly is brittle: TOR declares it as a managed System.Dictionary, so casting
                    // the reflected value to an Il2Cpp dictionary returns null and the fix silently
                    // no-ops — which left the Snitch bug unfixed while HostFix's fallback stood down.
                    ShareRoomMethod.Invoke(null, new object[] { hostPlayerId, room });
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger.LogError($"Snitch restore failed: {ex.Message}");
                }
            }
        }
    }
}
