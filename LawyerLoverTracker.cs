// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LawyerLoverTracker - new options letting the Lawyer/Prosecutor always see their TARGET on the map,
 * and a Lover always see their PARTNER on the map.
 *
 * TOR only shows a name suffix (§ / ♥); there is no map marker. This adds a live map HerePoint at the
 * tracked player's position, using the same map-space mapping TOR uses for the Trapper/Snitch
 * markers (MapBehaviourPatch). A per-role host toggle controls the meeting behaviour ("position in
 * meeting"): when ON, the LAST known position stays visible if the map is opened during the meeting;
 * when OFF, the marker only shows during the round.
 *
 * Meeting note: the minimap isn't normally openable in a meeting, so (per the chosen design) we also
 * keep the map button enabled in meetings for a tracking role with the meeting option ON. This part
 * depends on vanilla map/meeting interlocks and should be verified in-game.
 */

using System;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class LawyerLoverTracker {
        public static CustomOption LawyerRound;     // Lawyer knows target position (round)
        public static CustomOption LawyerMeeting;    // ...also last position in meeting
        public static CustomOption LoverRound;       // Lover knows partner position (round)
        public static CustomOption LoverMeeting;     // ...also last position in meeting

        private static SpriteRenderer marker;        // our single map marker (recreated with the map)
        private static Vector3 cachedPos;             // last known position of the tracked player
        private static bool cachedValid;
        private static PlayerControl cachedColorPlayer;

        public static void CreateOptions() {
            try {
                LawyerRound = CustomOption.Create(1280, Types.Neutral, "Lawyer Knows Target Position", false, CustomOptionHolder.lawyerSpawnRate);
                LawyerMeeting = CustomOption.Create(1281, Types.Neutral, "...Last Position Visible In Meeting", false, LawyerRound);
                LoverRound = CustomOption.Create(1282, Types.Modifier, "Lover Knows Partner Position", false, CustomOptionHolder.modifierLover);
                LoverMeeting = CustomOption.Create(1283, Types.Modifier, "...Last Position Visible In Meeting", false, LoverRound);

                var opts = CustomOption.options;
                foreach (var o in new[] { LawyerRound, LawyerMeeting }) opts.Remove(o);
                int idx = opts.IndexOf(CustomOptionHolder.lawyerCanCallEmergency);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, LawyerRound);
                opts.Insert(idx + 2, LawyerMeeting);

                foreach (var o in new[] { LoverRound, LoverMeeting }) opts.Remove(o);
                int idx2 = opts.IndexOf(CustomOptionHolder.modifierLoverEnableChat);
                if (idx2 < 0) idx2 = opts.Count - 1;
                opts.Insert(idx2 + 1, LoverRound);
                opts.Insert(idx2 + 2, LoverMeeting);

                UsefulTORStuffPlugin.Logger?.LogInfo("[LawyerLoverTracker] Options created under Lawyer and Lovers.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LawyerLoverTracker] CreateOptions failed: {e}");
            }
        }

        // Returns the player to track for the local client (or null), and whether the meeting option
        // for the active role is on.
        private static PlayerControl GetTarget(out bool meetingAllowed) {
            meetingAllowed = false;
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return null;

            if (LawyerRound != null && LawyerRound.getBool()
                && Lawyer.lawyer != null && Lawyer.lawyer == lp && Lawyer.target != null) {
                meetingAllowed = LawyerMeeting != null && LawyerMeeting.getBool();
                return Lawyer.target;
            }

            if (LoverRound != null && LoverRound.getBool()
                && Lovers.lover1 != null && Lovers.lover2 != null
                && (Lovers.lover1 == lp || Lovers.lover2 == lp)) {
                var partner = Lovers.otherLover(lp);
                if (partner != null) {
                    meetingAllowed = LoverMeeting != null && LoverMeeting.getBool();
                    return partner;
                }
            }
            return null;
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { cachedValid = false; cachedColorPlayer = null; }
        }

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        static class MapMarkerPatch {
            public static void Postfix(MapBehaviour __instance) {
                try {
                    var target = GetTarget(out bool meetingAllowed);
                    bool meeting = MeetingHud.Instance != null;

                    bool show;
                    Vector3 worldPos = Vector3.zero;
                    PlayerControl colorPlayer = null;

                    if (target == null) {
                        show = false;
                    } else if (!meeting) {
                        // Round: track live position and remember it for the meeting.
                        if (target.transform != null) {
                            worldPos = target.transform.position;
                            cachedPos = worldPos;
                            cachedValid = true;
                            cachedColorPlayer = target;
                        }
                        show = cachedValid;
                        colorPlayer = target;
                    } else {
                        // Meeting: only if allowed; show the last known position.
                        show = meetingAllowed && cachedValid;
                        worldPos = cachedPos;
                        colorPlayer = cachedColorPlayer;
                    }

                    if (!show) {
                        if (marker != null && marker.gameObject.activeSelf) marker.gameObject.SetActive(false);
                        return;
                    }

                    if (marker == null) {
                        marker = UnityEngine.Object.Instantiate(__instance.HerePoint, __instance.HerePoint.transform.parent, true);
                        marker.name = "UsefulLawyerLoverMarker";
                    }

                    Vector3 v = worldPos;
                    var ship = MapUtilities.CachedShipStatus;
                    if (ship == null) return;
                    v /= ship.MapScale;
                    v.x *= Mathf.Sign(ship.transform.localScale.x);
                    v.z = -2.1f;
                    marker.transform.localPosition = v;
                    marker.enabled = true;
                    if (colorPlayer != null) colorPlayer.SetPlayerMaterialColors(marker);
                    if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LawyerLoverTracker] MapBehaviour postfix failed: {e}");
                }
            }
        }

        // Best-effort: keep the map button usable during a meeting for a tracking role with the
        // meeting option on, so the player can open the minimap and see the last position.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class MapButtonInMeetingPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (MeetingHud.Instance == null || __instance == null || __instance.MapButton == null) return;
                    GetTarget(out bool meetingAllowed);
                    if (meetingAllowed && cachedValid)
                        __instance.MapButton.gameObject.SetActive(true);
                } catch { }
            }
        }
    }
}
