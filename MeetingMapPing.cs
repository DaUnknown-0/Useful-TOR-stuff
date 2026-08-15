// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MeetingMapPing - click the minimap during a MEETING to drop a marker everyone can see.
 *
 * The marker is a clone of the vanilla map "HerePoint" (the little crewmate icon), tinted
 * in the sender's player color - exactly the icon players already know from the map. One
 * ping per player: a new click moves that player's marker (2s cooldown per player,
 * markers expire after 10 seconds). Placement is visualized with a
 * small effect (marker pops oversized + a growing/fading pulse ring clone) that fires the
 * moment a viewer first SEES the new/moved ping - so it also plays for players who only
 * open the map afterwards. (The pulse fade multiplies sr.color; if the PlayerMaterial
 * shader ignores vertex alpha the ring still reads as a growing echo and then disappears.)
 * Only ALIVE players can send (a dead player's ping would leak ghost knowledge to the
 * living); everyone (incl. dead) sees the markers. Markers also clear when the meeting ends.
 *
 * Sync: RPC 254 carrying two floats - the MAP-LOCAL click position. Map-local coordinates
 * are identical on every client (same map prefab layout), so no world/MapScale conversion
 * is needed on either side (LawyerLoverTracker needs it because it starts from a WORLD
 * position; a click already happens in map space). Clients without the mod simply ignore
 * the unknown RPC id and see nothing.
 *
 * Rendering piggybacks on MapBehaviour.FixedUpdate (only runs while the map is shown, the
 * map object is inactive otherwise - same trick as LawyerLoverTracker). Markers are
 * parented under HerePoint's parent, so closing the map hides them with it. The sync pass
 * also clears everything once no meeting is running, covering meeting end, round start
 * and disconnects without extra patches.
 *
 * Host toggle: CustomOption 1360 (TOR "General" tab, host-synced) gates send AND display.
 */

using HarmonyLib;
using Hazel;
using System;
using System.Collections.Generic;
using TheOtherRoles;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MeetingMapPing {
        public const byte PingRpcId = 254;

        public static CustomOption Option;

        // playerId -> map-local ping position (authoritative data; markers derive from it)
        private static readonly Dictionary<byte, Vector2> pings = new();
        private static readonly Dictionary<byte, SpriteRenderer> markers = new();
        // placement effect state: where each marker was last seen (to detect moves) and the
        // running pop/pulse animations. Pulses are throwaway marker clones that grow + fade.
        private static readonly Dictionary<byte, Vector2> lastSeenPos = new();
        private static readonly Dictionary<byte, float> popStart = new();
        private static readonly Dictionary<byte, Vector3> baseScale = new();
        private static readonly List<(SpriteRenderer sr, float start, Vector3 baseScale)> pulses = new();
        private const float PopSeconds = 0.35f, PulseSeconds = 0.55f;
        // lifetime/cooldown: markers expire after 10s; one ping per player every 2s. The
        // receive side re-checks the cooldown (1.8s, latency tolerance) so a tampered
        // client cannot spam everyone's map either. placedAt uses each viewer's own clock
        // (set at receive time), so expiry is per client but drift is at most the latency.
        private const float MarkerSeconds = 10f, SendCooldownSeconds = 2f, ReceiveCooldownSeconds = 1.8f;
        private static readonly Dictionary<byte, float> placedAt = new();
        private static float nextSendAllowed;

        public static void CreateOptions() {
            // Receiver registration for the consolidated RPC channel (UTSRpc.CallId = 240).
            // CreateOptions is this feature's only load-time entry point, so it doubles as init.
            UTSRpc.Register(PingRpcId, HandleModuleRpc);

            try {
                Option = CustomOption.Create(1360, Types.General,
                    "Meeting Map Ping (Click On Map)", true);
                UTSLocalization.BindOptionTitle(Option, "uts.mapping.option_name");

                // Exempt from the "host doesn't have this mod" gate (UTSGate): pinging is a
                // communication tool, not a rule. It shows everyone with the mod a marker the pinger
                // could just as well have described out loud, so it hands nobody an advantage.
                UTSGate.MarkAlwaysActive(Option);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MapPing] CreateOptions failed: {e}");
            }
        }

        private static bool Enabled => Option != null && UTSGate.Bool(Option);

        // ---- receive ------------------------------------------------------------------------

        // Shared receive logic. `sender` is the PlayerControl the message arrived on - the legacy
        // patch takes it from __instance, the consolidated channel from UTSRpc.Sender.
        private static void Receive(PlayerControl sender, MessageReader reader) {
            try {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                // AUDIT-2026-08-15: alive-check only existed on the send side (HandleClick);
                // a modified client could call the RPC directly while dead and leak ghost-only
                // knowledge to the living via SyncMarkers. Re-check here on both receivers
                // (module channel + legacy dual-send), after the payload is fully read so the
                // reader cursor stays correct regardless of the outcome.
                if (Enabled && sender != null && sender.Data != null && !sender.Data.IsDead) {
                    byte id = sender.PlayerId;
                    if (!placedAt.TryGetValue(id, out var last)
                        || Time.unscaledTime - last >= ReceiveCooldownSeconds) {
                        pings[id] = new Vector2(x, y);
                        placedAt[id] = Time.unscaledTime;
                    }
                }
            } catch { }
        }

        // Receiver on the consolidated channel (module byte 254). Registered from CreateOptions.
        private static void HandleModuleRpc(MessageReader reader) => Receive(UTSRpc.Sender, reader);

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 254 from pre-240 builds.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        private static class HandleRpcPatch {
            public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader) {
                if (callId != PingRpcId) return true;
                Receive(__instance, reader);
                return false;
            }
        }

        // ---- send + render (map is only active while shown) ---------------------------------

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        private static class MapPatch {
            public static void Postfix(MapBehaviour __instance) {
                try {
                    bool meeting = MeetingHud.Instance != null;
                    if (!meeting || !Enabled) {
                        if (pings.Count > 0 || markers.Count > 0) Clear();
                        return;
                    }
                    HandleClick(__instance);
                    SyncMarkers(__instance);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MapPing] map update failed: {e}");
                }
            }
        }

        private static void HandleClick(MapBehaviour map) {
            if (!Input.GetMouseButtonDown(0)) return;
            if (Time.unscaledTime < nextSendAllowed) return;
            var local = PlayerControl.LocalPlayer;
            if (local == null || local.Data == null || local.Data.IsDead) return;

            // resolve the camera that actually renders the map layer (NOT Camera.main -
            // AU draws the HUD through a separate "UI Camera"; see MapLanguageToggle).
            var cam = MapLanguageToggle.ResolveCamera(map.gameObject.layer);
            if (cam == null) return;
            // Top strip holds the map close button - never ping from there. The language
            // toggle (bottom right) consumes its own clicks first.
            var vp = cam.ScreenToViewportPoint(Input.mousePosition);
            if (vp.y > 0.85f) return;
            if (MapLanguageToggle.IsPointerOverToggle(map, cam)) return;

            var parent = map.HerePoint.transform.parent;
            Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);
            Vector3 mapLocal = parent.InverseTransformPoint(world);

            nextSendAllowed = Time.unscaledTime + SendCooldownSeconds;
            pings[local.PlayerId] = new Vector2(mapLocal.x, mapLocal.y); // local echo
            placedAt[local.PlayerId] = Time.unscaledTime;
            // LEGACY DUAL-SEND (see UTSRpc.cs): legacy callId 254 + consolidated channel 240.
            // Classified IDEMPOTENT: Receive() only assigns pings[id]/placedAt[id] to the transmitted
            // position - the second copy writes the identical Vector2 (and is usually swallowed
            // outright by the 1.8 s receive cooldown, which the first copy just armed). The legacy
            // half exists for pre-240 builds and can be deleted in a future breaking release.
            float px = mapLocal.x, py = mapLocal.y;
            UTSRpc.SendDual(PingRpcId, PingRpcId, w => { w.Write(px); w.Write(py); });
        }

        private static void SyncMarkers(MapBehaviour map) {
            // expire markers after their lifetime (checked while the map is visible; an
            // expired ping received while the map was closed is simply never shown)
            List<byte> expired = null;
            foreach (var kv in placedAt)
                if (Time.unscaledTime - kv.Value > MarkerSeconds)
                    (expired ??= new List<byte>()).Add(kv.Key);
            if (expired != null)
                foreach (var id in expired) RemovePing(id);

            foreach (var kv in pings) {
                bool created = false;
                if (!markers.TryGetValue(kv.Key, out var marker) || marker == null) {
                    marker = UnityEngine.Object.Instantiate(
                        map.HerePoint, map.HerePoint.transform.parent, true);
                    marker.name = $"UsefulMapPing_{kv.Key}";
                    // lift the pair above the map background: HerePoint itself sits at
                    // order 0, and anything below that vanishes BEHIND the map (the first
                    // outline at "marker-1" = -1 was invisible exactly because of this)
                    marker.sortingOrder = map.HerePoint.sortingOrder + 2;
                    markers[kv.Key] = marker;
                    baseScale[kv.Key] = marker.transform.localScale;
                    var pc = PlayerById(kv.Key);
                    if (pc != null) pc.SetPlayerMaterialColors(marker);
                    AddOutline(map, marker);
                    created = true;
                }
                marker.transform.localPosition = new Vector3(kv.Value.x, kv.Value.y, -2.1f);
                if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);

                // placement effect: fires when a viewer first SEES a new/moved ping (a ping
                // received while the map was closed pulses on the next map open instead).
                bool moved = lastSeenPos.TryGetValue(kv.Key, out var seen)
                    && (seen - kv.Value).sqrMagnitude > 0.0001f;
                if (created || moved) {
                    lastSeenPos[kv.Key] = kv.Value;
                    popStart[kv.Key] = Time.unscaledTime;
                    StartPulse(marker);
                } else if (!lastSeenPos.ContainsKey(kv.Key)) {
                    lastSeenPos[kv.Key] = kv.Value;
                }
            }
            AnimateEffects();
        }

        // Red rim so player-set pings are distinguishable from the map's own icons. The
        // player shader (HerePoint uses it - SetPlayerMaterialColors works on the clone)
        // has BUILT-IN outline support: _Outline + _OutlineColor, the same mechanism TOR
        // uses for the kill-target highlight (PlayerControlPatch.cs:59). That yields a
        // proper thin contour; the two hand-rolled attempts (scaled/offset silhouette
        // copies) either vanished behind the map or read as a fat red body.
        private static void AddOutline(MapBehaviour map, SpriteRenderer marker) {
            try {
                marker.material.SetFloat("_Outline", 1f);
                marker.material.SetColor("_OutlineColor", new Color(0.95f, 0.08f, 0.08f));
                UsefulTORStuffPlugin.Logger?.LogInfo("[MapPing] shader outline armed");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[MapPing] outline failed: {e.Message}");
            }
        }

        // ---- placement effect ----------------------------------------------------------------

        private static void StartPulse(SpriteRenderer marker) {
            var pulse = UnityEngine.Object.Instantiate(marker, marker.transform.parent, true);
            pulse.name = marker.name + "_pulse";
            pulse.transform.localPosition = marker.transform.localPosition + new Vector3(0f, 0f, 0.05f);
            pulses.Add((pulse, Time.unscaledTime, marker.transform.localScale));
        }

        private static void AnimateEffects() {
            // marker pop: briefly oversized, easing back to the HerePoint base scale
            foreach (var kv in popStart) {
                if (!markers.TryGetValue(kv.Key, out var marker) || marker == null) continue;
                if (!baseScale.TryGetValue(kv.Key, out var s)) continue;
                float t = Mathf.Clamp01((Time.unscaledTime - kv.Value) / PopSeconds);
                float f = 1f + 0.6f * (1f - t) * (1f - t);
                marker.transform.localScale = s * f;
            }

            // pulse rings: a marker clone growing outwards while fading, then destroyed
            for (int i = pulses.Count - 1; i >= 0; i--) {
                var (sr, start, scale) = pulses[i];
                float t = Mathf.Clamp01((Time.unscaledTime - start) / PulseSeconds);
                if (sr == null || t >= 1f) {
                    if (sr != null) UnityEngine.Object.Destroy(sr.gameObject);
                    pulses.RemoveAt(i);
                    continue;
                }
                sr.transform.localScale = scale * (1f + 2.4f * t);
                var c = sr.color;
                c.a = 0.7f * (1f - t);
                sr.color = c;
            }
        }

        private static PlayerControl PlayerById(byte id) {
            foreach (var pc in PlayerControl.AllPlayerControls)
                if (pc != null && pc.PlayerId == id) return pc;
            return null;
        }

        private static void RemovePing(byte id) {
            pings.Remove(id);
            placedAt.Remove(id);
            lastSeenPos.Remove(id);
            popStart.Remove(id);
            baseScale.Remove(id);
            if (markers.TryGetValue(id, out var m)) {
                if (m != null) UnityEngine.Object.Destroy(m.gameObject);
                markers.Remove(id);
            }
        }

        private static void Clear() {
            pings.Clear();
            foreach (var m in markers.Values)
                if (m != null) UnityEngine.Object.Destroy(m.gameObject);
            markers.Clear();
            foreach (var (sr, _, _) in pulses)
                if (sr != null) UnityEngine.Object.Destroy(sr.gameObject);
            pulses.Clear();
            lastSeenPos.Clear();
            popStart.Clear();
            baseScale.Clear();
            placedAt.Clear();
        }
    }
}
