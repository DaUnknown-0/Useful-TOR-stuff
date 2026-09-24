// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * SubmergedSelfTest - autotest only (Diagnostics / Submerged Self Test, default OFF).
 *
 * Starts a Freeplay on Submerged (map 6) straight from the main menu and walks the map's systems
 * one by one, logging "[SubTest] PASS|FAIL|INFO <step>: <detail>" and photographing into
 * UTSShots\. Built 24.09. for the "O2 sabotage does nothing on Submerged" report and the request to
 * check the whole map with TOR + our mods:
 *   env        ship type, systems, rooms, vents, consoles, task pool
 *   sabmap     sabotage map: which MapRoom method each special/door button really calls
 *   sab:*      every sabotage button clicked like a player, system/task must go active, then repaired
 *   o2:*       Submerged's O2 over the three entry paths (button, MapRoom.SabotageOxygen, raw RPC),
 *              TOR's engineer mask fix
 *   door:*     door buttons close a door
 *   console:*  every SystemConsole / MapConsole opens its minigame
 *   task:*     every task of the pool (all assigned) opens its minigame at its console
 *   vent:*     every vent: enter, move to each neighbour, exit
 *   floor      floor change both ways (TOR's SubmergedCompatibility.ChangeFloor)
 *   kill/meeting/exile/spawn  kill a dummy, report, force the vote, watch exile + spawn-in
 *   o2death    O2 runs out: the local player must die (Submerged's oxygen death)
 * Submerged keeps the floor per player (FloorHandler, cutoff y = -6.19): a snap onto the lower deck
 * without a floor change is shifted back up by 48.119, so every snap here changes the floor first.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.GameOptions;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheOtherRoles;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {
    internal static class SubmergedSelfTest {
        private const string P = "[SubTest]";
        private const float FloorCutoff = -6.19f;
        private static ConfigEntry<bool> Enabled;
        private static bool _autoFired, _runFired;
        private static int _pass, _fail, _shot;

        internal static void Bind(ConfigFile cfg) {
            Enabled = cfg.Bind("Diagnostics", "Submerged Self Test", false,
                "Autotest only: starts a Freeplay on Submerged from the main menu and tests every map system "
                + "(sabotages, doors, consoles, tasks, vents, floors, meeting, O2 death), logging [SubTest] lines "
                + "and screenshots into UTSShots. Needs Submerged installed. false = off (default).");
        }

        private static void Log(string kind, string step, string detail) {
            if (kind == "PASS") _pass++;
            else if (kind == "FAIL") _fail++;
            var line = $"{P} {kind} {step}: {detail}";
            if (kind == "FAIL") UsefulTORStuffPlugin.Logger?.LogWarning(line);
            else UsefulTORStuffPlugin.Logger?.LogInfo(line);
        }

        private static void Shot(string tag) {
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"sub_{_shot++:D3}_{tag.Replace(':', '_').Replace('/', '_')}_{DateTime.Now:HHmmss}.png");
                ScreenCapture.CaptureScreenshot(file);
                UsefulTORStuffPlugin.Logger?.LogInfo($"{P} shot {tag} -> {file}");
            } catch (Exception e) { Log("INFO", "shot", e.Message); }
        }

        // ---------------------------------------------------------------- autostart

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class AutoStartPatch {
            private static void Postfix(MainMenuManager __instance) {
                if (_autoFired || Enabled == null || !Enabled.Value) return;
                _autoFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, AutoStart());
            }
        }

        private static IEnumerator AutoStart() {
            float t0 = Time.time;
            while (!LoginDone()) {
                if (Time.time - t0 > 45f) { Log("FAIL", "autostart", "EOS login timeout"); yield break; }
                yield return null;
            }
            StartFreeplay();
        }

        private static bool LoginDone() {
            try { return EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { return false; }
        }

        private static void StartFreeplay() {
            try {
                if (!SubmergedCompatibility.Loaded) { Log("FAIL", "autostart", "Submerged is not loaded"); return; }
                var popover = Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Log("FAIL", "autostart", "no FreeplayPopover"); return; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap((MapNames)6);
                Log("INFO", "autostart", $"freeplay started on map 6, Submerged {SubmergedCompatibility.Version}");
            } catch (Exception e) { Log("FAIL", "autostart", e.ToString()); }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class RunPatch {
            private static void Postfix(HudManager __instance) {
                if (_runFired || Enabled == null || !Enabled.Value) return;
                var ac = AmongUsClient.Instance;
                if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
                _runFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Run());
            }
        }

        // ---------------------------------------------------------------- Submerged reflection

        private static Type _floorType, _o2Type;
        private static MethodInfo _getFloorHandler;
        private static FieldInfo _onUpper, _o2Countdown, _o2Masks;
        private static PropertyInfo _o2Instance, _o2IsActive;

        private static void ResolveSubmerged() {
            if (_floorType != null || SubmergedCompatibility.Types == null) return;
            _floorType = SubmergedCompatibility.Types.FirstOrDefault(t => t.Name == "FloorHandler");
            _getFloorHandler = _floorType != null ? AccessTools.Method(_floorType, "GetFloorHandler", new[] { typeof(PlayerControl) }) : null;
            _onUpper = _floorType != null ? AccessTools.Field(_floorType, "onUpper") : null;
            _o2Type = SubmergedCompatibility.Types.FirstOrDefault(t => t.Name == "SubmarineOxygenSystem" && t.Namespace == "Submerged.Systems.Oxygen");
            if (_o2Type != null) {
                _o2Instance = AccessTools.Property(_o2Type, "Instance");
                _o2IsActive = AccessTools.Property(_o2Type, "IsActive");
                _o2Countdown = AccessTools.Field(_o2Type, "countdown");
                _o2Masks = AccessTools.Field(_o2Type, "playersWithMask");
            }
        }

        private static bool? OnUpper() {
            try {
                ResolveSubmerged();
                var fh = _getFloorHandler?.Invoke(null, new object[] { PlayerControl.LocalPlayer });
                return fh != null && _onUpper != null ? (bool)_onUpper.GetValue(fh) : null;
            } catch { return null; }
        }

        private static object O2() { try { ResolveSubmerged(); return _o2Instance?.GetValue(null); } catch { return null; } }
        private static bool O2Active() { try { var o = O2(); return o != null && (bool)_o2IsActive.GetValue(o); } catch { return false; } }
        private static string O2State() {
            try {
                var o = O2();
                if (o == null) return "no SubmarineOxygenSystem";
                var masks = _o2Masks?.GetValue(o) as HashSet<byte>;
                return $"active {(bool)_o2IsActive.GetValue(o)}, countdown {(float)_o2Countdown.GetValue(o):F1}, masks {masks?.Count ?? -1}";
            } catch (Exception e) { return "o2 state failed: " + e.Message; }
        }

        // ---------------------------------------------------------------- helpers

        private static PlayerControl Me => PlayerControl.LocalPlayer;

        // true when a floor change was requested (the caller then waits before snapping)
        private static bool PrepareFloor(Vector2 target) {
            bool wantUpper = target.y > FloorCutoff;
            var cur = OnUpper();
            if (cur == null || cur.Value == wantUpper) return false;
            try { SubmergedCompatibility.ChangeFloor(wantUpper); } catch (Exception e) { Log("INFO", "floor", "ChangeFloor failed: " + e.Message); }
            return true;
        }

        private static void Snap(Vector2 p) {
            try { Me.NetTransform.RpcSnapTo(p); } catch (Exception e) { Log("INFO", "snap", e.Message); }
        }

        private static void CloseMinigame() {
            try { if (Minigame.Instance != null) Minigame.Instance.ForceClose(); } catch { }
            try { if (MapBehaviour.Instance != null && MapBehaviour.Instance.IsOpen) MapBehaviour.Instance.Close(); } catch { }
        }

        private static string MinigameName() {
            try { return Minigame.Instance != null ? Minigame.Instance.GetIl2CppType().Name + "/" + Minigame.Instance.name : null; } catch { return "?"; }
        }

        private static SabotageSystemType Sab() {
            try {
                return ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Sabotage, out var s) ? s.TryCast<SabotageSystemType>() : null;
            } catch { return null; }
        }

        // every activatable system that reports active, plus the sabotage tasks the local player holds
        private static string ActiveSabotages() {
            var parts = new List<string>();
            try {
                foreach (var kv in ShipStatus.Instance.Systems) {
                    IActivatable a = null;
                    try { a = kv.Value?.TryCast<IActivatable>(); } catch { }
                    if (a != null && a.IsActive) parts.Add(kv.Key.ToString());
                }
            } catch { }
            if (O2Active()) parts.Add("SubmergedO2");
            try {
                foreach (var t in Me.myTasks)
                    if (t != null && t.TryCast<NormalPlayerTask>() == null && t.TryCast<ImportantTextTask>() == null) parts.Add("task:" + t.TaskType);
            } catch { }
            return string.Join(",", parts.Distinct());
        }

        private static bool AnySabotageActive() {
            try { var s = Sab(); if (s != null && s.AnyActive) return true; } catch { }
            return O2Active();
        }

        // Repair everything the host can repair directly (freeplay: the local player is the host).
        private static void RepairAll() {
            var ship = ShipStatus.Instance;
            try { ship.RepairCriticalSabotages(); } catch (Exception e) { Log("INFO", "repair", "RepairCriticalSabotages: " + e.Message); }
            try {
                if (ship.Systems.TryGetValue(SystemTypes.Electrical, out var e)) {
                    var sw = e.TryCast<SwitchSystem>();
                    if (sw != null)
                        for (int i = 0; i < 5; i++)
                            if (((sw.ActualSwitches ^ sw.ExpectedSwitches) & (1 << i)) != 0) ship.UpdateSystem(SystemTypes.Electrical, Me, (byte)i);
                }
            } catch (Exception e) { Log("INFO", "repair", "lights: " + e.Message); }
            try {
                if (ship.Systems.TryGetValue(SystemTypes.Comms, out var c)) {
                    var hud = c.TryCast<HudOverrideSystemType>();
                    if (hud != null && hud.IsActive) ship.UpdateSystem(SystemTypes.Comms, Me, 0);
                    var hq = c.TryCast<HqHudSystemType>();
                    if (hq != null && hq.IsActive) { ship.UpdateSystem(SystemTypes.Comms, Me, 16); ship.UpdateSystem(SystemTypes.Comms, Me, 17); }
                }
            } catch (Exception e) { Log("INFO", "repair", "comms: " + e.Message); }
            // anything else still active: the generic "stop" amounts
            try {
                foreach (var kv in ship.Systems) {
                    IActivatable a = null;
                    try { a = kv.Value?.TryCast<IActivatable>(); } catch { }
                    if (a == null || !a.IsActive || kv.Key == SystemTypes.Doors || kv.Key == SystemTypes.Sabotage) continue;
                    try { ship.UpdateSystem(kv.Key, Me, 16); } catch { }
                    try { if (a.IsActive) ship.UpdateSystem(kv.Key, Me, 0); } catch { }
                }
            } catch { }
            try { var s = Sab(); if (s != null) s.Timer = 0f; } catch { }
            try { SabotageTuning.DiagClearCooldowns(); } catch { }
        }

        private static ButtonBehavior ButtonOf(SpriteRenderer r) {
            if (r == null) return null;
            var b = r.GetComponent<ButtonBehavior>();
            if (b == null) b = r.GetComponentInParent<ButtonBehavior>();
            return b;
        }

        private static string PersistentCalls(ButtonBehavior b) {
            try {
                var ev = b.OnClick;
                var names = new List<string>();
                for (int i = 0; i < ev.GetPersistentEventCount(); i++) {
                    var target = ev.GetPersistentTarget(i);
                    names.Add($"{(target != null ? target.GetIl2CppType().Name : "null")}.{ev.GetPersistentMethodName(i)}");
                }
                return names.Count == 0 ? "(no persistent calls)" : string.Join(" + ", names);
            } catch (Exception e) { return "calls? " + e.Message; }
        }

        private static InfectedOverlay OpenSabotageMap() {
            try {
                CloseMinigame();
                HudManager.Instance.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Sabotage });
                return MapBehaviour.Instance != null ? MapBehaviour.Instance.infectedOverlay : null;
            } catch (Exception e) { Log("FAIL", "sabmap", "open failed: " + e.Message); return null; }
        }

        // ---------------------------------------------------------------- the run

        private static IEnumerator Run() {
            Log("INFO", "run", "ship present, waiting 12 s for the intro / spawn-in");
            float until = Time.time + 12f;
            while (Time.time < until) yield return null;

            // spawn-in selection at round start (Submerged SubmarineSelectSpawn) or other open minigame
            string mg0 = MinigameName();
            if (mg0 != null) {
                Log("INFO", "start", $"minigame open at start: {mg0}");
                Shot("start_minigame");
                until = Time.time + 20f;
                while (Time.time < until && Minigame.Instance != null) yield return null;
                if (Minigame.Instance != null) { Log("INFO", "start", $"still open after 20 s ({MinigameName()}), force closing"); CloseMinigame(); }
            }

            // ---- env
            DoEnv();
            Shot("env");

            Log("INFO", "role", $"start role {Me.Data.Role?.Role} (consoles and tasks run as crew: impostors may not use task consoles)");
            // ---- system and map consoles (cams, admin, vitals, emergency, laptop, ...)
            var consoles = new List<(string name, Vector2 pos, Action use)>();
            try {
                foreach (var c in Object.FindObjectsOfType<SystemConsole>()) if (c != null) { var cc = c; consoles.Add(($"{cc.name}", cc.transform.position, () => cc.Use())); }
                foreach (var c in Object.FindObjectsOfType<MapConsole>()) if (c != null) {
                    var cc = c;
                    consoles.Add(($"{cc.name}(map)", cc.transform.position, () => {
                        float d = cc.CanUse(Me.Data, out bool can, out bool could);
                        Log("INFO", $"console:{cc.name}(map)", $"CanUse {can}, couldUse {could}, dist {d:F2}, usable {cc.UsableDistance:F2}");
                        cc.Use();
                    }));
                }
            } catch (Exception e) { Log("FAIL", "console", e.Message); }
            Log("INFO", "console", $"{consoles.Count} system/map console(s)");
            foreach (var c in consoles) {
                string step = $"console:{c.name}";
                CloseMinigame();
                if (PrepareFloor(c.pos)) { until = Time.time + 0.8f; while (Time.time < until) yield return null; }
                Snap(c.pos);
                until = Time.time + 0.8f; while (Time.time < until) yield return null;
                try { c.use(); } catch (Exception e) { Log("FAIL", step, "Use threw " + e.Message); }
                until = Time.time + 1.5f; while (Time.time < until) yield return null;
                string mg = MinigameName();
                bool mapOpen = MapBehaviour.Instance != null && MapBehaviour.Instance.IsOpen;
                Shot(step);
                Log(mg != null || mapOpen ? "PASS" : "FAIL", step, mg != null ? $"opened {mg}" : mapOpen ? "map opened" : $"nothing opened at ({c.pos.x:F1}, {c.pos.y:F1}), onUpper {OnUpper()}");
                CloseMinigame();
                until = Time.time + 0.6f; while (Time.time < until) yield return null;
            }

            // ---- tasks: assign the whole pool, open each at its console
            var tasks = AssignAllTasks();
            until = Time.time + 1.5f; while (Time.time < until) yield return null;
            var seen = new HashSet<TaskTypes>();
            List<PlayerTask> mine = new();
            try { foreach (var t in Me.myTasks) if (t != null && t.TryCast<NormalPlayerTask>() != null) mine.Add(t); } catch { }
            Log(mine.Count >= tasks ? "PASS" : "FAIL", "task:assign", $"pool {tasks}, local tasks {mine.Count}");
            LogTaskLocations("task:locations");
            foreach (var t in mine) {
                if (t == null || !seen.Add(t.TaskType)) continue;
                string step = $"task:{t.TaskType}";
                var console = FindConsole(t);
                if (console == null) { Log("FAIL", step, "no console found"); continue; }
                Vector2 cp = console.transform.position;
                CloseMinigame();
                if (PrepareFloor(cp)) { until = Time.time + 0.8f; while (Time.time < until) yield return null; }
                Snap(cp);
                until = Time.time + 0.8f; while (Time.time < until) yield return null;
                string why = UseConsole(console);
                until = Time.time + 1.6f; while (Time.time < until) yield return null;
                string mg = MinigameName();
                Shot(step);
                Log(mg != null ? "PASS" : "FAIL", step, mg != null ? $"opened {mg} at {console.name}" : $"nothing opened at {console.name} ({cp.x:F1}, {cp.y:F1}): {why}");
                CloseMinigame();
                until = Time.time + 0.5f; while (Time.time < until) yield return null;
            }

            // ---- become impostor (vanilla role; TOR roles are not assigned in freeplay)
            try { RoleManager.Instance.SetRole(Me, RoleTypes.Impostor); } catch (Exception e) { Log("INFO", "role", e.Message); }
            until = Time.time + 1.5f; while (Time.time < until) yield return null;
            Log(Me.Data.Role != null && Me.Data.Role.IsImpostor ? "PASS" : "FAIL", "role", $"local role {Me.Data.Role?.Role}");

            // ---- sabotage map: wiring of every button
            var ov = OpenSabotageMap();
            until = Time.time + 1.2f; while (Time.time < until) yield return null;
            Shot("sabmap");
            var rooms = new List<(SystemTypes room, bool special, bool door)>();
            if (ov != null) {
                try {
                    Log("INFO", "sabmap", $"CanUseSabotage {ov.CanUseSabotage}, CanUseDoors {ov.CanUseDoors}, rooms {ov.rooms?.Length ?? 0}");
                    foreach (var r in ov.rooms) {
                        if (r == null) continue;
                        var sb = ButtonOf(r.special);
                        var db = ButtonOf(r.door);
                        Log("INFO", "sabmap", $"room {r.room} ({(int)r.room}): special {(sb != null ? PersistentCalls(sb) : "-")} | door {(db != null ? PersistentCalls(db) : "-")}");
                        // UTS Sabotage Tuning must recognise every sabotage button (greying + cooldown)
                        if (sb != null) {
                            bool mapped = SabotageTuning.TryMapRoom(r, out var st);
                            string want = PersistentCalls(sb);
                            bool ok = mapped && want.Contains("Sabotage" + (st == SabotageTuning.SabType.Heli ? "Heli" : st.ToString()));
                            Log(ok ? "PASS" : "FAIL", $"tuning:map:{r.room}", mapped ? $"{st} for {want}" : $"not mapped ({want})");
                        }
                        rooms.Add((r.room, sb != null, db != null));
                    }
                } catch (Exception e) { Log("FAIL", "sabmap", e.ToString()); }
            } else Log("FAIL", "sabmap", "no InfectedOverlay");
            CloseMinigame();
            until = Time.time + 0.8f; while (Time.time < until) yield return null;

            // ---- every sabotage button, clicked like a player
            foreach (var room in rooms.Where(x => x.special)) {
                RepairAll();
                until = Time.time + 2.5f; while (Time.time < until) yield return null;
                string step = $"sab:{room.room}";
                ov = OpenSabotageMap();
                until = Time.time + 1.0f; while (Time.time < until) yield return null;
                bool clicked = ClickRoom(ov, room.room, door: false, step);
                until = Time.time + 2.5f; while (Time.time < until) yield return null;
                string active = ActiveSabotages();
                Shot(step);
                if (!clicked) { }
                else if (active.Length > 0) Log("PASS", step, $"active after click: {active}");
                else Log("FAIL", step, $"nothing became active (O2 {O2State()})");
                CloseMinigame();
                RepairAll();
                until = Time.time + 3f; while (Time.time < until) yield return null;
                string left = ActiveSabotages();
                Log(left.Length == 0 ? "PASS" : "FAIL", step + ":repair", left.Length == 0 ? "repaired" : $"still active: {left}");
            }

            // ---- Submerged O2 over the three entry paths
            for (int path = 0; path < 3; path++) {
                RepairAll();
                until = Time.time + 2.5f; while (Time.time < until) yield return null;
                string step = path switch { 0 => "o2:mapRoom.SabotageOxygen", 1 => "o2:rpc(Sabotage,LifeSupp)", _ => "o2:rpc(130,128)" };
                ov = path == 0 ? OpenSabotageMap() : null;
                if (path == 0) { until = Time.time + 1.0f; while (Time.time < until) yield return null; }
                TriggerO2(path, ov, step);
                until = Time.time + 2.5f; while (Time.time < until) yield return null;
                Log(O2Active() ? "PASS" : "FAIL", step, O2State());
                Log("INFO", step, $"shared AnyActive {Sab()?.AnyActive}, UTS sees O2 {SabotageTuning.SubmergedO2Active()}, map CanUseSabotage now {(MapBehaviour.Instance != null && MapBehaviour.Instance.infectedOverlay != null ? MapBehaviour.Instance.infectedOverlay.CanUseSabotage.ToString() : "map closed")}");
                CloseMinigame();
                if (path == 0 && O2Active()) {
                    // TOR's Engineer remote fix hands out the local player's mask
                    try { SubmergedCompatibility.RepairOxygen(); } catch (Exception e) { Log("INFO", "o2:torfix", e.Message); }
                    until = Time.time + 1.5f; while (Time.time < until) yield return null;
                    Log(O2State().Contains("masks 1") ? "PASS" : "FAIL", "o2:torEngineerFix", O2State());
                    LogTaskLocations("o2:locations");
                    Shot("o2_mask");
                }
            }
            RepairAll();
            until = Time.time + 2.5f; while (Time.time < until) yield return null;

            // ---- doors
            foreach (var room in rooms.Where(x => x.door)) {
                string step = $"door:{room.room}";
                ov = OpenSabotageMap();
                until = Time.time + 1.0f; while (Time.time < until) yield return null;
                int closedBefore = ClosedDoors();
                ClickRoom(ov, room.room, door: true, step);
                until = Time.time + 1.5f; while (Time.time < until) yield return null;
                int closed = ClosedDoors();
                Log(closed > closedBefore ? "PASS" : "FAIL", step, $"closed doors {closedBefore} -> {closed}");
                CloseMinigame();
                until = Time.time + 12f; while (Time.time < until && ClosedDoors() > 0) yield return null;   // doors reopen by themselves
            }

            // ---- floors
            {
                var before = OnUpper();
                try { SubmergedCompatibility.ChangeFloor(!(before ?? true)); } catch (Exception e) { Log("FAIL", "floor", e.Message); }
                until = Time.time + 1.5f; while (Time.time < until) yield return null;
                var after = OnUpper();
                Shot("floor_changed");
                Log(before != null && after != null && after != before ? "PASS" : "FAIL", "floor", $"onUpper {before} -> {after}, y {Me.transform.position.y:F1}");
                try { SubmergedCompatibility.ChangeFloor(true); } catch { }
                until = Time.time + 1.5f; while (Time.time < until) yield return null;
                Log(OnUpper() == true ? "PASS" : "FAIL", "floor:back", $"onUpper {OnUpper()}, y {Me.transform.position.y:F1}");
            }

            // ---- vents
            var vents = ShipStatus.Instance.AllVents?.ToArray() ?? Array.Empty<Vent>();
            Log("INFO", "vent", $"{vents.Length} vent(s)");
            foreach (var v in vents) {
                if (v == null) continue;
                string step = $"vent:{v.Id}:{v.name}";
                Vector2 vp = v.transform.position;
                CloseMinigame();
                if (PrepareFloor(vp)) { until = Time.time + 0.8f; while (Time.time < until) yield return null; }
                Snap(vp);
                until = Time.time + 0.8f; while (Time.time < until) yield return null;
                try { Me.MyPhysics.RpcEnterVent(v.Id); } catch (Exception e) { Log("FAIL", step, "enter threw " + e.Message); continue; }
                until = Time.time + 1.2f; while (Time.time < until) yield return null;
                if (!Me.inVent) { Log("FAIL", step, "not in vent after RpcEnterVent"); continue; }
                var targets = NeighbourVents(v);
                var moved = new List<string>();
                foreach (var n in targets) {
                    string err = null;
                    bool ok = false;
                    try { ok = Vent.currentVent != null && Vent.currentVent.TryMoveToVent(n, out err); } catch (Exception e) { err = e.Message; }
                    until = Time.time + 1.0f; while (Time.time < until) yield return null;
                    bool arrived = Vent.currentVent != null && Vent.currentVent.Id == n.Id;
                    moved.Add($"{n.Id}{(ok && arrived ? "" : $"(FAILED: ok {ok}, now {Vent.currentVent?.Id}, {err})")}");
                    if (!arrived) break;
                    try { Vent.currentVent.TryMoveToVent(v, out err); } catch { }
                    until = Time.time + 1.0f; while (Time.time < until) yield return null;
                }
                bool allMoved = moved.All(m => !m.Contains("FAILED"));
                try { if (Vent.currentVent != null) Me.MyPhysics.RpcExitVent(Vent.currentVent.Id); } catch (Exception e) { Log("FAIL", step, "exit threw " + e.Message); }
                until = Time.time + 1.5f; while (Time.time < until) yield return null;
                Log(allMoved && !Me.inVent ? "PASS" : "FAIL", step, $"neighbours [{string.Join(", ", moved)}], out of vent {!Me.inVent}");
            }

            // ---- kill, report, meeting, exile, spawn-in
            PlayerControl victim = null, exiled = null;
            try {
                foreach (var pc in PlayerControl.AllPlayerControls)
                    if (pc != null && pc != Me && !pc.Data.IsDead) { if (victim == null) victim = pc; else if (exiled == null) exiled = pc; }
            } catch { }
            if (victim != null) {
                if (PrepareFloor(victim.GetTruePosition())) { until = Time.time + 0.8f; while (Time.time < until) yield return null; }
                Snap(victim.GetTruePosition() + new Vector2(0.5f, 0f));
                until = Time.time + 0.8f; while (Time.time < until) yield return null;
                try { Me.RpcMurderPlayer(victim, true); } catch (Exception e) { Log("FAIL", "kill", e.Message); }
                until = Time.time + 1.2f; while (Time.time < until) yield return null;
                Shot("kill");
                until = Time.time + 2.5f; while (Time.time < until) yield return null;
                bool body = Object.FindObjectsOfType<DeadBody>().Any(b => b != null && b.ParentId == victim.PlayerId);
                Log(victim.Data.IsDead && body ? "PASS" : "FAIL", "kill", $"victim dead {victim.Data.IsDead}, body {body}");
                try { Me.CmdReportDeadBody(victim.Data); } catch (Exception e) { Log("FAIL", "meeting", e.Message); }
                until = Time.time + 10f; while (Time.time < until && MeetingHud.Instance == null) yield return null;
                until = Time.time + 4f; while (Time.time < until) yield return null;
                Shot("meeting");
                Log(MeetingHud.Instance != null ? "PASS" : "FAIL", "meeting", MeetingHud.Instance != null ? $"meeting open, state {MeetingHud.Instance.state}" : "no meeting");
                if (MeetingHud.Instance != null) {
                    until = Time.time + 25f;
                    while (Time.time < until && MeetingHud.Instance != null
                           && MeetingHud.Instance.state != MeetingHud.VoteStates.NotVoted && MeetingHud.Instance.state != MeetingHud.VoteStates.Voted) yield return null;
                    Log("INFO", "meeting", $"state before vote end: {MeetingHud.Instance?.state}");
                    until = Time.time + 2f; while (Time.time < until) yield return null;
                    try { MeetingHud.Instance.RpcVotingComplete(new Il2CppStructArray<MeetingHud.VoterState>(0), exiled?.Data, false); }
                    catch (Exception e) { Log("FAIL", "exile", e.Message); }
                    // Freeplay: the results screen waits for the host's Proceed button (online a timer runs on)
                    until = Time.time + 8f; while (Time.time < until) yield return null;
                    Shot("results");
                    ClickProceed();
                    until = Time.time + 12f; while (Time.time < until && ExileController.Instance == null) yield return null;
                    until = Time.time + 1.5f; while (Time.time < until) yield return null;
                    Shot("exile_a");
                    until = Time.time + 2.5f; while (Time.time < until) yield return null;
                    Shot("exile_b");
                    Log(ExileController.Instance != null ? "PASS" : "FAIL", "exile", $"exile screen {(ExileController.Instance != null ? ExileController.Instance.GetIl2CppType().Name : "missing")}, exiled {exiled?.Data?.PlayerName}");
                    until = Time.time + 15f; while (Time.time < until && (ExileController.Instance != null || MeetingHud.Instance != null)) yield return null;
                    until = Time.time + 2f; while (Time.time < until) yield return null;
                    string spawn = MinigameName();
                    Shot("after_exile");
                    Log("INFO", "spawn", spawn != null ? $"after exile: {spawn}" : "after exile: no minigame");
                    // Submerged's spawn-in: pick a deck like a player (freeplay dummies never pick, so the
                    // screen then waits for them; that part is forced closed below)
                    until = Time.time + 12f; while (Time.time < until && Minigame.Instance == null) yield return null;
                    until = Time.time + 3f; while (Time.time < until) yield return null;
                    Shot("spawn_select");
                    ClickSpawnOption();
                    until = Time.time + 4f; while (Time.time < until) yield return null;
                    Shot("spawn_clicked");
                    Log("INFO", "spawn", $"after click: {MinigameName() ?? "closed"}, me at ({Me.transform.position.x:F1}, {Me.transform.position.y:F1}) onUpper {OnUpper()}");
                    until = Time.time + 20f; while (Time.time < until && Minigame.Instance != null) yield return null;
                    if (Minigame.Instance != null) { Log("INFO", "spawn", $"{MinigameName()} still waiting for the freeplay dummies after 20 s, closing"); CloseMinigame(); }
                    until = Time.time + 2f; while (Time.time < until) yield return null;
                    Shot("round2");
                    Log(exiled == null || exiled.Data.IsDead ? "PASS" : "FAIL", "exile:result", $"exiled dead {exiled?.Data?.IsDead}, me at ({Me.transform.position.x:F1}, {Me.transform.position.y:F1}) onUpper {OnUpper()}");
                }
            } else Log("FAIL", "kill", "no dummy to kill");

            // ---- O2 death: last, the local player dies (Submerged ignores O2 while a meeting is up)
            until = Time.time + 20f; while (Time.time < until && (MeetingHud.Instance != null || ExileController.Instance != null)) yield return null;
            CloseMinigame();
            RepairAll();
            until = Time.time + 2.5f; while (Time.time < until) yield return null;
            TriggerO2(1, null, "o2death");
            until = Time.time + 2f; while (Time.time < until) yield return null;
            try { var o = O2(); if (o != null && O2Active()) _o2Countdown.SetValue(o, 3f); } catch (Exception e) { Log("INFO", "o2death", e.Message); }
            until = Time.time + 5f; while (Time.time < until) yield return null;
            Shot("o2death");
            until = Time.time + 3f; while (Time.time < until) yield return null;
            Log(Me.Data.IsDead ? "PASS" : "FAIL", "o2death", $"local player dead {Me.Data.IsDead}, {O2State()}");

            Log("INFO", "done", $"{_pass} PASS, {_fail} FAIL");
        }

        // ---------------------------------------------------------------- step bodies (no yields)

        private static void ClickSpawnOption() {
            try {
                var mg = Minigame.Instance;
                if (mg == null) { Log("INFO", "spawn", "no spawn-in minigame to click"); return; }
                var buttons = mg.GetComponentsInChildren<PassiveButton>(true).Where(b => b != null && b.gameObject.activeInHierarchy).ToList();
                Log("INFO", "spawn", $"{buttons.Count} button(s): {string.Join(", ", buttons.Select(b => b.name))}");
                var pick = buttons.FirstOrDefault(b => b.name.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0);
                if (pick == null) { Log("FAIL", "spawn", "no spawn option button"); return; }
                pick.OnClick.Invoke();
                Log("PASS", "spawn:click", $"clicked {pick.name}");
            } catch (Exception e) { Log("FAIL", "spawn", "click threw " + e.Message); }
        }

        // which task makes MapTaskOverlay.SetIconLocation throw (null/empty Locations)
        private static void LogTaskLocations(string step) {
            var bad = new List<string>();
            try {
                foreach (var t in Me.myTasks) {
                    if (t == null) continue;
                    try {
                        var l = t.Locations;
                        if (l == null) bad.Add($"{t.TaskType}(null)");
                        else if (l.Count == 0) bad.Add($"{t.TaskType}(0)");
                    } catch (Exception e) { bad.Add($"{t.TaskType}(throws {e.GetType().Name})"); }
                }
            } catch { }
            Log("INFO", step, bad.Count == 0 ? "all task locations present" : "tasks without map location: " + string.Join(", ", bad));
        }

        private static void ClickProceed() {
            try {
                var m = MeetingHud.Instance;
                if (m == null) { Log("INFO", "exile", "meeting already closed before Proceed"); return; }
                bool shown = m.ProceedButton != null && m.ProceedButton.gameObject.activeInHierarchy;
                Log("INFO", "exile", $"Proceed button shown {shown}, state {m.state}");
                if (shown) m.ProceedButton.OnClick.Invoke(); else m.HandleProceed();
            } catch (Exception e) { Log("FAIL", "exile", "proceed threw " + e.Message); }
        }

        private static void DoEnv() {
            try {
                var ship = ShipStatus.Instance;
                Log(SubmergedCompatibility.IsSubmerged ? "PASS" : "FAIL", "env",
                    $"ship {ship.GetIl2CppType().Name}, type {(int)ship.Type}, submerged {SubmergedCompatibility.IsSubmerged}, rooms {ship.FastRooms?.Count ?? 0}, vents {ship.AllVents?.Length ?? 0}, doors {ship.AllDoors?.Length ?? 0}");
                var sys = new List<string>();
                foreach (var kv in ship.Systems) sys.Add($"{kv.Key}({(int)kv.Key})={(kv.Value != null ? new Il2CppSystem.Object(kv.Value.Pointer).GetIl2CppType().Name : "null")}");
                Log("INFO", "env", "systems: " + string.Join(", ", sys));
                Log("INFO", "env", $"tasks: common {ship.CommonTasks?.Length}, long {ship.LongTasks?.Length}, short {ship.ShortTasks?.Length}; consoles {Object.FindObjectsOfType<Console>().Length}");
                Log("INFO", "env", $"onUpper {OnUpper()}, me at ({Me.transform.position.x:F1}, {Me.transform.position.y:F1}), players {PlayerControl.AllPlayerControls.Count}");
                try {
                    Log("INFO", "env", $"UTS Sabotage Tuning {(SabotageTuning.Enabled != null ? UTSGate.Bool(SabotageTuning.Enabled).ToString() : "n/a")}, Siphoner block key {AppDomain.CurrentDomain.GetData("TORMods.SiphonerSabotageBlockUntil")}");
                } catch { }
            } catch (Exception e) { Log("FAIL", "env", e.ToString()); }
        }

        private static bool ClickRoom(InfectedOverlay ov, SystemTypes room, bool door, string step) {
            try {
                if (ov == null) { Log("FAIL", step, "no sabotage map"); return false; }
                foreach (var r in ov.rooms) {
                    if (r == null || r.room != room) continue;
                    var b = ButtonOf(door ? r.door : r.special);
                    if (b == null) { Log("FAIL", step, "no button"); return false; }
                    Log("INFO", step, $"click ({PersistentCalls(b)}), CanUseSabotage {ov.CanUseSabotage}, sab timer {Sab()?.Timer:F1}");
                    b.OnClick.Invoke();
                    return true;
                }
                Log("FAIL", step, "room not on the map");
            } catch (Exception e) { Log("FAIL", step, "click threw " + e); }
            return false;
        }

        private static void TriggerO2(int path, InfectedOverlay ov, string step) {
            try {
                var ship = ShipStatus.Instance;
                switch (path) {
                    case 0:
                        MapRoom target = null;
                        if (ov != null) foreach (var r in ov.rooms) if (r != null && ButtonOf(r.special) != null && PersistentCalls(ButtonOf(r.special)).Contains("SabotageOxygen")) target = r;
                        if (target == null) { Log("FAIL", step, "no MapRoom with SabotageOxygen on the sabotage map"); return; }
                        Log("INFO", step, $"MapRoom {target.room}, CanUseSabotage {ov.CanUseSabotage}");
                        target.SabotageOxygen();
                        break;
                    case 1:
                        ship.RpcUpdateSystem(SystemTypes.Sabotage, (byte)SystemTypes.LifeSupp);
                        break;
                    default:
                        ship.RpcUpdateSystem((SystemTypes)130, 128);
                        break;
                }
            } catch (Exception e) { Log("FAIL", step, "trigger threw " + e); }
        }

        private static int ClosedDoors() {
            int n = 0;
            try { foreach (var d in ShipStatus.Instance.AllDoors) if (d != null && !d.IsOpen) n++; } catch { }
            return n;
        }

        private static int AssignAllTasks() {
            try {
                var ship = ShipStatus.Instance;
                var ids = new List<byte>();
                foreach (var arr in new[] { ship.CommonTasks, ship.LongTasks, ship.ShortTasks })
                    if (arr != null) foreach (var t in arr) if (t != null) ids.Add((byte)t.Index);
                Me.Data.RpcSetTasks(new Il2CppStructArray<byte>(ids.ToArray()));
                return ids.Count;
            } catch (Exception e) { Log("FAIL", "task:assign", e.ToString()); return 0; }
        }

        private static Console FindConsole(PlayerTask t) {
            try {
                var nt = t.TryCast<NormalPlayerTask>();
                foreach (var c in Object.FindObjectsOfType<Console>()) {
                    if (c == null || c.TaskTypes == null || !c.TaskTypes.Contains(t.TaskType)) continue;
                    if (nt != null && !nt.ValidConsole(c)) continue;
                    return c;
                }
                // multi-step tasks (download/upload) may validate only a later step's console
                foreach (var c in Object.FindObjectsOfType<Console>())
                    if (c != null && c.TaskTypes != null && c.TaskTypes.Contains(t.TaskType)) {
                        Log("INFO", $"task:{t.TaskType}", $"no ValidConsole match, trying {c.name}");
                        return c;
                    }
            } catch { }
            return null;
        }

        private static string UseConsole(Console c) {
            try {
                float d = c.CanUse(Me.Data, out bool canUse, out bool couldUse);
                c.Use();
                return $"CanUse {canUse}, couldUse {couldUse}, dist {d:F2}, onUpper {OnUpper()}";
            } catch (Exception e) { return "Use threw " + e.Message; }
        }

        private static List<Vent> NeighbourVents(Vent v) {
            var list = new List<Vent>();
            try {
                if (v.NearbyVents != null) foreach (var n in v.NearbyVents) if (n != null && n.Id != v.Id && list.All(x => x.Id != n.Id)) list.Add(n);
                foreach (var n in new[] { v.Left, v.Right, v.Center }) if (n != null && n.Id != v.Id && list.All(x => x.Id != n.Id)) list.Add(n);
            } catch { }
            return list;
        }
    }
}
