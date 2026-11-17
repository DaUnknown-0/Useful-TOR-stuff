// Useful TOR Stuff, GPL-3.0-or-later.
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    internal static class AuditFixC_MeetingState {
        // Audit 16.11. TOR-017: Vor dem Meeting stoppen, ohne Bewegung im Meeting freizugeben.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
        private static class StopRewind {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => TimeMaster.isRewinding = false;
        }

        // Audit 16.11. TOR-019: Bereits empfangene Raummeldungen ueber TORs Feldtausch retten.
        [HarmonyPatch]
        private static class SnitchRooms {
            private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get(
                "TheOtherRoles.Patches.MeetingHudPatch+StartMeetingPatch"), "Prefix");
            private static void Prefix(out Dictionary<byte, byte> __state) {
                __state = Snitch.playerRoomMap;
                var local = PlayerControl.LocalPlayer;
                var tracker = HudManager.Instance != null ? HudManager.Instance.roomTracker : null;
                if (local != null && tracker != null && __state != null)
                    __state[local.PlayerId] = tracker.LastRoom != null ? (byte)tracker.LastRoom.RoomId : byte.MinValue;
            }
            private static void Postfix(Dictionary<byte, byte> __state) {
                if (__state == null) return;
                Snitch.playerRoomMap ??= new Dictionary<byte, byte>();
                foreach (var pair in __state) Snitch.playerRoomMap.TryAdd(pair.Key, pair.Value);
            }
        }
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.MeetingEndedUpdate))]
        private static class ClearSnitchRooms {
            private static void Postfix() => Snitch.playerRoomMap?.Clear();
        }

        // Audit 16.11. TOR-055: Besitzer und Vollstaendigkeit auch bei nichtleerem Cache pruefen.
        [HarmonyPatch]
        private static class MapSystems {
            private static readonly FieldInfo systems = AccessTools.Field(typeof(TheOtherRoles.Utilities.MapUtilities), "_systems");
            private static ShipStatus owner;
            private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(TheOtherRoles.Utilities.MapUtilities), "Systems");
            private static void Prefix() {
                var current = ShipStatus.Instance;
                var cache = (Dictionary<SystemTypes, Il2CppSystem.Object>)systems.GetValue(null);
                if (current == null || owner != current || TheOtherRoles.Utilities.MapUtilities.CachedShipStatus != current ||
                    cache.Count != current.Systems.Count) cache.Clear();
                owner = current;
                TheOtherRoles.Utilities.MapUtilities.CachedShipStatus = current;
            }
        }
        [HarmonyPatch(typeof(TheOtherRoles.Utilities.MapUtilities), nameof(TheOtherRoles.Utilities.MapUtilities.MapDestroyed))]
        private static class MapDestroyed {
            private static void Postfix() => TheOtherRoles.Utilities.MapUtilities.CachedShipStatus = null;
        }
    }
}
