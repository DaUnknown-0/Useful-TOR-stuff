// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * DeconDiag - a read-only tracer for the "decon door does not open / meeting cannot be called"
 * reports (2026-08-14/15, Polus, host).
 *
 * WHAT IT MUST PROVE
 * ------------------
 * The decon button plays its sound but the door stays shut, and in the same rounds the emergency
 * meeting reportedly cannot be called and the chat misbehaves. Three layers could be broken, and
 * the reports cannot tell them apart:
 *
 *   1. The CLICK layer: DeconControl.Use runs (the sound proves that much) but its OnUse event
 *      never reaches the DeconSystem - then the host system NEVER leaves Idle.
 *   2. The HOST TICK layer: the system receives the command but ShipStatus.FixedUpdate never
 *      drives DeconSystem.Deteriorate in the degraded round - then CurState changes once and the
 *      timer never advances, the doors never cycle.
 *   3. The PRESENTATION layer: the vanilla door actually opens (collider off) and only the
 *      first-person picture keeps showing it closed - that one is Nightfall's, and it has its own
 *      fix (SceneGeometry decon door sync, 0.2.0.6).
 *
 * This file logs the state transitions of every DeconSystem on the ship and of its two doors, so
 * one round of play answers which layer is dead:
 *   - no "CurState ..." line after a button press  -> layer 1 broke.
 *   - "CurState Enter" but the timer never counts and no door line follows -> layer 2 broke.
 *   - door lines say "open" while the player sees a closed door -> layer 3, Nightfall.
 *
 * The same poller watches the meeting path from the outside: it logs every Minigame that opens or
 * closes (the emergency button opens an EmergencyMinigame - if a click produces no such line, the
 * click layer of the CONSOLE is dead) and the appearance of MeetingHud (if the minigame opens but
 * no MeetingHud ever follows a button press, the report RPC layer is dead). It also logs one
 * snapshot of the client state at round start (GameState, IsGameStarted, a surviving
 * GameStartManager), because the running suspicion is that the native GameStartManager.Start
 * failure leaves the whole round half-started.
 *
 * DELIBERATELY NOT A PATCH ON DeconControl.Use OR DeconSystem.UpdateSystem. Both are small Il2Cpp
 * methods, and this project has one recorded case of the Il2Cpp linker's method deduplication
 * turning a detour of a small method into a detour of something else entirely (Minigame.Close
 * took down process-wide HTTP). Everything here is read-only polling from HudManager.Update,
 * which several mods in this family already patch without incident.
 *
 * Config-gated (Diagnostics/DeconTrace); default OFF - it did its job for the 2026-08-14/15 reports
 * and stays in the tree as a read-only tool a host can switch back on if the symptom resurfaces.
 * All logging is state-change-driven: a healthy idle round logs one line.
 */

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public static class DeconDiag {

        internal static ConfigEntry<bool> Enabled;

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("Diagnostics", "DeconTrace", false,
                "Log decontamination system / door state transitions, minigame open/close and "
                + "meeting starts, to pin down why the Polus decon door and the emergency meeting "
                + "misbehave. Read-only; switch on again only if the symptom comes back.");
        }

        // ---- per-round state ----
        private sealed class DeconWatch {
            public DeconSystem System;
            public string Key;              // the SystemTypes slot it sits in
            public int LastState = int.MinValue;
            public bool? UpperOpen;
            public bool? LowerOpen;
        }

        private static readonly List<DeconWatch> watches = new List<DeconWatch>();
        // Fallback-path collider cache for IsOpen() below (unknown door kind, not a ManualDoor):
        // GetComponentsInChildren allocates a fresh array every call, and IsOpen runs twice per
        // watched door every 0.25s poll. Keyed by the door's Il2Cpp pointer, cleared on every fresh
        // ScanRound so a door instance from a previous round can never serve a stale array.
        private static readonly Dictionary<IntPtr, Collider2D[]> fallbackColliderCache = new Dictionary<IntPtr, Collider2D[]>();
        private static bool roundScanned;
        private static float nextPoll;
        private static string lastMinigame = "";
        private static bool meetingSeen;

        private static void Log(string msg) => UsefulTORStuffPlugin.Logger?.LogInfo($"[DeconDiag] {msg}");

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class PollPatch {
            public static void Postfix() {
                try {
                    if (Enabled == null || !Enabled.Value) return;

                    if (ShipStatus.Instance == null || AmongUsClient.Instance == null
                        || !AmongUsClient.Instance.IsGameStarted) {
                        roundScanned = false;
                        watches.Clear();
                        lastMinigame = "";
                        meetingSeen = false;
                        return;
                    }

                    if (Time.realtimeSinceStartup < nextPoll) return;
                    nextPoll = Time.realtimeSinceStartup + 0.25f;

                    if (!roundScanned) { roundScanned = true; ScanRound(); }

                    PollDecon();
                    PollMeetingPath();
                } catch (Exception e) {
                    // One failure disables the tracer for the session rather than spamming.
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[DeconDiag] poll failed, tracer off: {e.Message}");
                    if (Enabled != null) Enabled.Value = false;
                }
            }
        }

        private static void ScanRound() {
            // The round-start snapshot: is this round even properly started? (The native
            // GameStartManager.Start failure is the standing suspicion behind every symptom.)
            var client = AmongUsClient.Instance;
            string gsm = "gone";
            try { gsm = LobbyScreen.Exists ? "ALIVE (leak)" : "gone"; } catch { }
            Log($"round start: GameState={client.GameState}, IsGameStarted={client.IsGameStarted}, "
                + $"AmHost={client.AmHost}, GameStartManager={gsm}, map={ShipStatus.Instance.name}");

            watches.Clear();
            fallbackColliderCache.Clear();
            try {
                foreach (var kv in ShipStatus.Instance.Systems) {
                    DeconSystem ds = null;
                    try { ds = kv.Value?.TryCast<DeconSystem>(); } catch { }
                    if (ds == null) continue;
                    watches.Add(new DeconWatch { System = ds, Key = kv.Key.ToString() });
                }
            } catch (Exception e) {
                Log($"system scan failed: {e.Message}");
            }
            Log($"{watches.Count} decontamination system(s) on this map"
                + (watches.Count > 0 ? " - state transitions will be logged." : "."));
        }

        private static void PollDecon() {
            foreach (var w in watches) {
                var ds = w.System;
                if (ds == null) continue;

                int state;
                float timer;
                try { state = (int)ds.CurState; timer = ds.timer; } catch { continue; }

                if (state != w.LastState) {
                    w.LastState = state;
                    Log($"{w.Key}: CurState={(DeconSystem.States)state}, timer={timer:0.00}, "
                        + $"upperDoor={DoorDesc(ds.UpperDoor)}, lowerDoor={DoorDesc(ds.LowerDoor)}");
                }

                CheckDoor(w, ds.UpperDoor, true);
                CheckDoor(w, ds.LowerDoor, false);
            }
        }

        /// The physical truth of one decon door: its blocking collider. Polus decon doors are
        /// ManualDoor (SomeKindaDoor, NOT OpenableDoor - they have no IsOpen and are never in
        /// ShipStatus.AllDoors), driven by DeconSystem.UpdateDoorsViaState via SetDoorway.
        private static bool? IsOpen(SomeKindaDoor door) {
            try {
                if (door == null) return null;
                var manual = door.TryCast<ManualDoor>();
                if (manual != null && manual.myCollider != null) return !manual.myCollider.enabled;
                // Unknown door kind: fall back to "any enabled solid collider means closed". The
                // CHILD LIST itself (which colliders exist) is cached per door instance - only their
                // .enabled is re-read every poll, so a fresh allocation isn't needed every 0.25s.
                if (!fallbackColliderCache.TryGetValue(door.Pointer, out var colliders)) {
                    colliders = door.GetComponentsInChildren<Collider2D>();
                    fallbackColliderCache[door.Pointer] = colliders;
                }
                foreach (var c in colliders)
                    if (c != null && !c.isTrigger && c.enabled) return false;
                return true;
            } catch { return null; }
        }

        private static string DoorDesc(SomeKindaDoor door) {
            var open = IsOpen(door);
            return open == null ? "?" : (open.Value ? "open" : "closed");
        }

        private static void CheckDoor(DeconWatch w, SomeKindaDoor door, bool upper) {
            var open = IsOpen(door);
            if (open == null) return;
            var prev = upper ? w.UpperOpen : w.LowerOpen;
            if (prev == open) return;
            if (upper) w.UpperOpen = open; else w.LowerOpen = open;
            if (prev != null)   // the very first reading is baseline, not a transition
                Log($"{w.Key}: {(upper ? "upper" : "lower")} door -> {(open.Value ? "OPEN" : "CLOSED")}");
        }

        private static void PollMeetingPath() {
            // Which minigame is up. The emergency button opens an EmergencyMinigame; a click that
            // produces no open/close pair here never reached the console layer at all.
            string current = "";
            try {
                var mg = Minigame.Instance;
                if (mg != null) current = mg.GetIl2CppType()?.Name ?? "Minigame";
            } catch { }
            if (current != lastMinigame) {
                if (lastMinigame != "") Log($"minigame closed: {lastMinigame}");
                if (current != "") Log($"minigame open: {current}");
                lastMinigame = current;
            }

            // And whether a meeting ever materialises.
            bool meeting = false;
            try { meeting = MeetingHud.Instance != null; } catch { }
            if (meeting && !meetingSeen) { meetingSeen = true; Log("MeetingHud appeared."); }
            else if (!meeting && meetingSeen) { meetingSeen = false; Log("MeetingHud gone."); }
        }
    }
}
