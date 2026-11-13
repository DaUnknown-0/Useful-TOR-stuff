// Copyright (C) 2026 DaUnknown-0. Licensed under GPL-3.0-or-later.
//
// HarmonyBatch - faster start: patched methods are built once per Harmony.PatchAll instead of once per patch, and
// patches on in-game classes are built after the main menu is on screen (2026-11-13, User: start plus login under
// 20 seconds).
//
// Measured on a cold start (StartTrace, 13.11.): 1813 Harmony patch operations hit only 475 methods, and every
// operation rebuilds the whole wrapper of its method plus the native detour (~11-16 ms each). Most repeats come from
// one mod patching the same method from many patch classes (Unknown's Collection: 589 operations on 108 methods).
//
// 1. While a Harmony.PatchAll runs, a patch only records itself (Harmony keeps its patch info as always; the wrapper
//    is not built: prefix on HarmonyX's internal PatchFunctions.UpdateWrapper). When the outermost PatchAll returns,
//    each recorded method is built once with all its patches. A mod's patches are active when its PatchAll returns,
//    as before. (A first version collected until all plugins had loaded: a plugin that read the game settings in its
//    Load got them before Forgotten Fixes' redirect was built. Scoped to PatchAll that cannot happen.)
// 2. Patches on classes that only exist in a game or lobby (PlayerControl, MeetingHud, ShipStatus, HudManager, the
//    minigames, TOR's RPCProcedure and FixedUpdate logic, ...) are not built at load at all. From the main menu on
//    they are built a few per frame (~15 ms) once the login is done, while the player looks at the menu. Before any
//    scene loads (SceneManager.LoadScene*), a connection starts or a game is joined, whatever is left is built at once,
//    so none of those classes ever runs without its patches. The main menu itself never touches these classes.
//
// Duplicated in Unknown's Collection and Forgotten Fixes (namespace aside identical); whichever loads first installs
// it (AppDomain key), the other one does nothing. Switch: [Performance] Batch Harmony Patches At Start in the
// config of the mod that loads first; off there means off for all.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace UsefulTORStuff {

    internal static class HarmonyBatch {
        private const string Key = "TORMods.HarmonyBatch";
        private const long SliceMs = 15;
        private static int depth;
        private static bool lateOpen;                  // in-game classes are still being collected
        private static readonly List<MethodBase> pending = new List<MethodBase>();
        private static readonly HashSet<MethodBase> pendingSet = new HashSet<MethodBase>();
        private static readonly List<MethodBase> late = new List<MethodBase>();
        private static readonly HashSet<MethodBase> lateSet = new HashSet<MethodBase>();
        private static MethodInfo updateWrapper, addReplacement, toPatchInfo;
        private static Action<string> log;
        private static int batches, recorded, built, lateBuilt;
        private static long lateMs;
        private static bool sliceRunning;
        private static bool building;                  // our own builds go straight through the prefix
        private static bool menuSeen;                  // the start-up scene loads must not end the late phase

        // classes that exist only in a game or a lobby (OnlineGame / Tutorial scene), never in the main menu
        private static readonly HashSet<string> InGame = new HashSet<string>(StringComparer.Ordinal) {
            "PlayerControl", "PlayerPhysics", "CustomNetworkTransform", "MeetingHud", "PlayerVoteArea", "ShipStatus",
            "HudManager", "Vent", "VentButton", "IntroCutscene", "ExileController", "AirshipExileController",
            "MapBehaviour", "MapRoom", "MapConsole", "Console", "SystemConsole", "KillButton", "ReportButton", "UseButton",
            "SabotageButton", "AbilityButton", "PetButton", "EndGameManager", "GameStartManager", "LobbyBehaviour",
            "LobbyViewSettingsPane", "ChatController", "ChatBubble", "KeyboardJoystick", "ScreenJoystick", "DeadBody",
            "SpawnInMinigame", "TaskPanelBehaviour", "ProgressTracker", "PlayerTask", "NormalPlayerTask", "SabotageTask",
            "ReactorSystemType", "SwitchSystem", "HudOverrideSystemType", "LifeSuppSystemType", "SabotageSystemType",
            "DoorsSystemType", "MovingPlatformBehaviour", "Ladder", "ZiplineBehaviour", "PlainDoor", "MushroomWallDoor",
            "LogicGameFlowNormal", "LogicGameFlowHnS", "ShapeshifterPanel", "OverlayKillAnimation", "KillOverlay",
            // TOR's own in-game logic (managed)
            "RPCProcedure", "PlayerControlFixedUpdatePatch", "RoleManagerSelectRolesPatch", "CustomButton",
            "MeetingCalculateVotesPatch", "HudManagerUpdatePatch", "MeetingHudPatch", "ExileControllerPatch",
            "IntroCutsceneOnDestroyPatch", "EndGamePatch", "OnGameEndPatch",
        };

        internal static void Install(bool enabled, Action<string> logger) {
            var d = AppDomain.CurrentDomain;
            if (d.GetData(Key) != null) return;            // another mod installed it (or switched it off)
            d.SetData(Key, enabled ? "on" : "off");
            if (!enabled) return;
            log = logger;
            try {
                var asm = typeof(Harmony).Assembly;
                updateWrapper = AccessTools.Method(asm.GetType("HarmonyLib.PatchFunctions"), "UpdateWrapper");
                var pm = asm.GetType("HarmonyLib.Public.Patching.PatchManager");
                addReplacement = AccessTools.Method(pm, "AddReplacementOriginal");
                toPatchInfo = AccessTools.Method(pm, "ToPatchInfo", new[] { typeof(MethodBase) });
                var patchAll1 = AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), new[] { typeof(Assembly) });
                if (updateWrapper == null || addReplacement == null || toPatchInfo == null || patchAll1 == null) {
                    log?.Invoke("HarmonyX internals not found, patches apply one by one.");
                    return;
                }
                var self = new Harmony("tormods.harmonybatch");
                self.Patch(updateWrapper, prefix: new HarmonyMethod(typeof(HarmonyBatch), nameof(UpdateWrapperPrefix)) { priority = Priority.First });
                self.Patch(addReplacement, prefix: new HarmonyMethod(typeof(HarmonyBatch), nameof(AddReplacementPrefix)) { priority = Priority.First });
                var enter = new HarmonyMethod(typeof(HarmonyBatch), nameof(Enter)) { priority = Priority.First };
                var leave = new HarmonyMethod(typeof(HarmonyBatch), nameof(Leave)) { priority = Priority.Last };
                // only PatchAll(Assembly): PatchAll() calls it, and PatchAll() itself finds its caller on the stack
                self.Patch(patchAll1, prefix: enter, finalizer: leave);

                // the late part: start building at the main menu, finish before any scene loads
                var force = new HarmonyMethod(typeof(HarmonyBatch), nameof(ForceLate)) { priority = Priority.First };
                int hooks = 0;
                foreach (var m in typeof(UnityEngine.SceneManagement.SceneManager).GetMethods(BindingFlags.Public | BindingFlags.Static)) {
                    if (m.Name != "LoadScene" && m.Name != "LoadSceneAsync") continue;
                    try { self.Patch(m, prefix: force); hooks++; } catch { }
                }
                // and when a connection starts or a game is joined (both happen in the main menu scene, before the load)
                foreach (var m in typeof(InnerNet.InnerNetClient).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    if (m.Name != "Connect" && m.Name != "CoConnect") continue;
                    try { self.Patch(m, prefix: force); hooks++; } catch { }
                }
                var joined = AccessTools.Method(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined));
                if (joined != null) { try { self.Patch(joined, prefix: force); } catch { } }
                var menuStart = AccessTools.Method(typeof(MainMenuManager), nameof(MainMenuManager.Start));
                if (hooks > 0 && menuStart != null) {
                    self.Patch(menuStart, postfix: new HarmonyMethod(typeof(HarmonyBatch), nameof(MenuPostfix)));
                    lateOpen = true;
                }
                log?.Invoke($"patched methods are built once per PatchAll{(lateOpen ? ", in-game classes from the main menu on" : "")}.");
            } catch (Exception e) {
                depth = 0;
                lateOpen = false;
                log?.Invoke("install failed, patches apply one by one: " + e.Message);
            }
        }

        private static bool IsInGame(MethodBase m) {
            var t = m.DeclaringType;
            while (t != null && t.DeclaringType != null) t = t.DeclaringType;   // nested patch classes count as their outer class
            if (t == null) return false;
            return InGame.Contains(t.Name) || t.Name.EndsWith("Minigame", StringComparison.Ordinal);
        }

        private static void Enter() => depth++;

        // a finalizer: runs also when PatchAll throws, so a broken patch class cannot leave the batch open
        private static Exception Leave(Exception __exception) {
            if (depth > 0 && --depth == 0) Flush();
            return __exception;
        }

        // HarmonyX: PatchFunctions.UpdateWrapper(MethodBase original, PatchInfo patchInfo) builds the wrapper and the detour
        private static bool UpdateWrapperPrefix(MethodBase original, ref MethodInfo __result) {
            if (original == null || building) return true;
            if (lateOpen && IsInGame(original)) {
                if (lateSet.Add(original)) late.Add(original);
                pendingSet.Remove(original);
                recorded++;
                __result = null;
                return false;
            }
            if (depth == 0) return true;
            if (pendingSet.Add(original)) pending.Add(original);
            recorded++;
            __result = null;
            return false;
        }

        // PatchProcessor.Patch hands the (now null) replacement on to PatchManager.AddReplacementOriginal
        private static bool AddReplacementPrefix(MethodInfo replacement) => replacement != null;

        private static bool Build(MethodBase m) {
            building = true;
            try {
                var info = toPatchInfo.Invoke(null, new object[] { m });
                var rep = updateWrapper.Invoke(null, new object[] { m, info }) as MethodInfo;
                if (rep != null) addReplacement.Invoke(null, new object[] { m, rep });
                return true;
            } catch (Exception e) {
                log?.Invoke($"building {m.DeclaringType?.Name}.{m.Name} failed: {(e.InnerException ?? e).Message}");
                return false;
            } finally {
                building = false;
            }
        }

        private static void Flush() {
            if (pending.Count == 0) return;
            var sw = Stopwatch.StartNew();
            var list = pending.ToArray();
            pending.Clear();
            pendingSet.Clear();
            foreach (var m in list) if (!lateSet.Contains(m) && Build(m)) built++;
            batches++;
            log?.Invoke($"PatchAll #{batches}: {list.Length} method(s) built once ({sw.ElapsedMilliseconds} ms); so far {recorded} patch operations -> {built} builds, {late.Count} in-game method(s) waiting.");
        }

        // ---- the in-game part ----
        private static void MenuPostfix(MainMenuManager __instance) {
            menuSeen = true;
            if (!lateOpen || sliceRunning || late.Count == 0) return;
            sliceRunning = true;
            log?.Invoke($"main menu: building {late.Count} in-game method(s) a few per frame.");
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Slices());
        }

        // the login runs on the main thread's frames as well: let it finish first (at most 20 s), it cannot be joined
        // online before it anyway, and a connection that starts earlier builds the rest at once (ForceLate)
        private static bool LoginDone() {
            try { return EOSManager.InstanceExists && EOSManager.Instance.HasFinishedLoginFlow(); } catch { return true; }
        }

        private static IEnumerator Slices() {
            var wait = Stopwatch.StartNew();
            while (lateOpen && !LoginDone() && wait.ElapsedMilliseconds < 20000) yield return null;
            while (lateOpen && late.Count > 0) {
                var sw = Stopwatch.StartNew();
                while (late.Count > 0 && sw.ElapsedMilliseconds < SliceMs) BuildOneLate();
                yield return null;
            }
            if (lateOpen) Close("main menu");
        }

        private static void BuildOneLate() {
            var m = late[late.Count - 1];
            late.RemoveAt(late.Count - 1);
            lateSet.Remove(m);
            var sw = Stopwatch.StartNew();
            if (Build(m)) lateBuilt++;
            lateMs += sw.ElapsedMilliseconds;
        }

        // before any scene loads: everything that is left, now
        private static void ForceLate() {
            // the game itself loads the main menu scene at start: only scene loads and connections after it count
            if (!lateOpen || !menuSeen) return;
            int left = late.Count;
            var sw = Stopwatch.StartNew();
            while (late.Count > 0) BuildOneLate();
            Close(left > 0 ? $"scene load ({left} built at once in {sw.ElapsedMilliseconds} ms)" : "scene load");
        }

        private static void Close(string why) {
            lateOpen = false;
            log?.Invoke($"in-game methods done ({why}): {lateBuilt} built after the main menu, {lateMs} ms in all.");
        }
    }
}
