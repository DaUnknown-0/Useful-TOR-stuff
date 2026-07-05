// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff;

public static class SnitchLogic
{
    private static readonly Dictionary<byte, byte> roomMap = new Dictionary<byte, byte>();

    private static Type snitchType;
    private static Type snitchModeEnumType;
    private static Type snitchTargetsEnumType;
    private static Type helpersType;
    private static Type tasksHandlerType;
    private static Type roleInfoType;
    private static Type mapUtilitiesType;
    private static Type playerControlPatchType;
    private static Type mapBehaviourPatchType;
    private static Type startMeetingPatchType;

    private static FieldInfo snitchPlayerField;
    private static FieldInfo snitchModeField;
    private static FieldInfo snitchTargetsField;
    private static FieldInfo snitchIsRevealedField;
    private static FieldInfo snitchNeedsUpdateField;
    private static FieldInfo snitchTextField;
    private static FieldInfo snitchTaskCountForRevealField;
    private static FieldInfo mapBehaviourHerePointsField;
    private static FieldInfo mapUtilitiesCachedShipStatusField;

    private static MethodInfo shareRoomMethod;
    private static MethodInfo snitchClearAndReloadMethod;
    private static MethodInfo snitchUpdateMethod;
    private static MethodInfo helpersShouldShowGhostInfoMethod;
    private static MethodInfo helpersIsEvilMethod;
    private static MethodInfo helpersIsKillerMethod;
    private static MethodInfo taskInfoMethod;
    private static MethodInfo getRolesStringMethod;
    private static MethodInfo torStartMeetingPrefixMethod;

    private static int snitchModeChatValue;
    private static int snitchModeMapValue;
    private static int snitchModeChatAndMapValue;
    private static int snitchTargetsEvilPlayersValue;
    private static int snitchTargetsKillersValue;

    private static bool chatModeSwapped;
    private static int chatOriginalMode;
    private static bool mapModeSwapped;
    private static int mapOriginalMode;

    // Jede Fähigkeit hat ihr eigenes Readiness-Flag und hängt NUR von den Handles ab, die
    // sie wirklich braucht — so fällt nicht alles aus, nur weil ein einzelnes Handle fehlt.
    internal static bool ShareRoomRecorderReady { get; private set; }
    internal static bool ClearReloadResetReady { get; private set; }
    internal static bool ChatRevealReady { get; private set; }
    internal static bool MapRevealReady { get; private set; }
    internal static bool HudRevealReady { get; private set; }

    // True, wenn TORs eigene StartMeetingPatch.Prefix-Methode aufgelöst werden konnte und der
    // deterministische Wrap (prefix/postfix DIREKT auf diese Methode) installiert ist. Nur dann
    // ist die Unterdrückung von TORs Snitch-Nachricht reihenfolge-unabhängig garantiert. Ist das
    // Flag false, fällt StartMeetingChatPatch (mode-swap auf PlayerControl.StartMeeting) als
    // Best-Effort-Fallback ein (siehe dessen Prepare()).
    internal static bool TorSuppressReady { get; private set; }

    // True, wenn der client-seitige Snitch-Fix lokal überhaupt laufen KÖNNTE (alle nötigen
    // Handles aufgelöst). Kern des Host-Bug-Fix ist die Chat-Reveal-Reimplementierung, die aus
    // der eigenen, NICHT mid-meeting zurückgesetzten roomMap liest — daher Chat-Reveal +
    // Room-Recorder. Das tatsächliche Wirken ist zusätzlich auf SnitchClientFixActive gegated
    // (= alle haben den Mod), das der Handshake aus everyone && ClientFixReady bildet. An genau
    // diesem Flag erkennt HostFix, ob der Client-Fix scharf ist und der Re-Broadcast stillstehen darf.
    internal static bool ClientFixReady { get; private set; }

    public static void Initialize(Harmony harmony)
    {
        try
        {
            var tor = UsefulTORStuffPlugin.TORAssembly
                ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
            if (tor == null)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning("TheOtherRoles assembly not found — Snitch logic disabled.");
                return;
            }

            ResolveHandles(tor);

            // --- Readiness pro Fähigkeit, jeweils nur auf die eigenen Handles bezogen ---

            // Room-Recorder: zwei unabhängige Patches. shareRoom füllt die lokale roomMap,
            // clearAndReload leert sie. Keiner hängt vom anderen ab.
            ShareRoomRecorderReady = shareRoomMethod != null;
            ClearReloadResetReady = snitchClearAndReloadMethod != null;

            // Chat-Reveal braucht NICHT den Room-Recorder: fehlt die roomMap, fällt der Raum
            // pro Spieler auf "open fields" zurück — der böse Spieler erscheint trotzdem.
            ChatRevealReady = snitchPlayerField != null
                && snitchModeField != null
                && snitchTargetsField != null
                && snitchModeEnumType != null
                && snitchTargetsEnumType != null
                && helpersShouldShowGhostInfoMethod != null
                && helpersIsEvilMethod != null
                && helpersIsKillerMethod != null
                && taskInfoMethod != null
                && getRolesStringMethod != null;
            MapRevealReady = snitchPlayerField != null
                && snitchModeField != null
                && snitchTargetsField != null
                && mapBehaviourHerePointsField != null
                && snitchModeEnumType != null
                && snitchTargetsEnumType != null
                && helpersIsEvilMethod != null
                && helpersIsKillerMethod != null
                && taskInfoMethod != null
                && mapUtilitiesCachedShipStatusField != null;
            HudRevealReady = snitchUpdateMethod != null
                && snitchPlayerField != null
                && snitchNeedsUpdateField != null
                && snitchIsRevealedField != null
                && snitchTextField != null
                && snitchTaskCountForRevealField != null
                && snitchTargetsField != null
                && snitchTargetsEnumType != null
                && helpersIsEvilMethod != null
                && helpersIsKillerMethod != null
                && taskInfoMethod != null;

            // Der client-seitige Snitch-Fix kann lokal laufen, sobald Chat-Reveal + Room-Recorder
            // bereit sind. Tatsächlich wirksam wird er aber erst, wenn der Handshake daraus zusammen
            // mit "alle haben den Mod" SnitchClientFixActive bildet (siehe UsefulVersionHandshake).
            ClientFixReady = ChatRevealReady && ShareRoomRecorderReady;

            if (ShareRoomRecorderReady)
            {
                harmony.Patch(shareRoomMethod,
                    postfix: new HarmonyMethod(typeof(SnitchLogic), nameof(ShareRoomPostfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("SnitchLogic: room map recorder enabled.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: room map recorder disabled — missing shareRoom handle.");
            }

            if (ClearReloadResetReady)
            {
                harmony.Patch(snitchClearAndReloadMethod,
                    postfix: new HarmonyMethod(typeof(SnitchLogic), nameof(ClearAndReloadPostfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("SnitchLogic: clearAndReload reset hook enabled.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: clearAndReload reset hook disabled — missing handle (local roomMap still clears at meeting end).");
            }

            if (ChatRevealReady)
                UsefulTORStuffPlugin.Logger?.LogInfo("SnitchLogic: chat reveal reimplementation enabled.");
            else
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: chat reveal stays on TOR's original path — missing Snitch handles.");

            if (MapRevealReady)
                UsefulTORStuffPlugin.Logger?.LogInfo("SnitchLogic: map reveal reimplementation enabled.");
            else
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: map reveal stays on TOR's original path — missing Snitch/MapBehaviour handles.");

            if (HudRevealReady)
            {
                harmony.Patch(snitchUpdateMethod,
                    prefix: new HarmonyMethod(typeof(SnitchLogic), nameof(SnitchHudUpdatePrefix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("SnitchLogic: HUD update reimplementation enabled.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: HUD update stays on TOR's original path — missing Snitch handles.");
            }

            // Deterministische Unterdrückung von TORs eigener Snitch-Nachricht: prefix/postfix
            // DIREKT auf TORs StartMeetingPatch.Prefix. Das läuft garantiert unmittelbar um TORs
            // synchrones Snitch.mode-Lesen herum — anders als der globale-Priorität-abhängige
            // mode-swap auf PlayerControl.StartMeeting, der je nach geladener TOR-Version zu spät
            // greifen kann (TOR sendet dann seine leere Nachricht trotzdem -> Doppelung).
            // Muss VOR harmony.PatchAll (UsefulTORStuffPlugin) feststehen, da StartMeetingChatPatch
            // (Fallback) sein Prepare() an TorSuppressReady koppelt.
            TorSuppressReady = torStartMeetingPrefixMethod != null && ChatRevealReady;
            if (TorSuppressReady)
            {
                harmony.Patch(torStartMeetingPrefixMethod,
                    prefix: new HarmonyMethod(typeof(SnitchLogic), nameof(TorSnitchSuppressPrefix)),
                    postfix: new HarmonyMethod(typeof(SnitchLogic), nameof(TorSnitchSuppressPostfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    "SnitchLogic: deterministische TOR-Snitch-Unterdrückung aktiv (TOR StartMeetingPatch.Prefix umschlossen).");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "SnitchLogic: TOR StartMeetingPatch.Prefix nicht auflösbar — Fallback auf mode-swap-Patch (StartMeetingChatPatch).");
            }

            // Der client-seitige Fix wirkt nur, wenn der Handshake SnitchClientFixActive setzt
            // (alle haben den Mod). Die Reveal-Patches sind angewandt, prüfen das Flag aber zur
            // Laufzeit und fallen sonst auf TORs Originalverhalten zurück.
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"SnitchLogic: client-side Snitch fix {(ClientFixReady ? "ready" : "NOT ready (missing chat-reveal/recorder handles)")} " +
                "— actual effect gated at runtime on SnitchClientFixActive (all players modded).");
        }
        catch (Exception ex)
        {
            UsefulTORStuffPlugin.Logger?.LogError($"Failed to initialize Snitch logic: {ex}");
        }
    }

    // Breite Flags: tolerant gegenüber public/internal/static-Unterschieden zwischen TOR-Releases.
    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static void ResolveHandles(Assembly tor)
    {
        snitchType = ResolveType(tor, "Snitch", "TheOtherRoles.TheOtherRoles+Snitch");
        helpersType = ResolveType(tor, "Helpers", "TheOtherRoles.Helpers");
        tasksHandlerType = ResolveType(tor, "TasksHandler", "TheOtherRoles.TasksHandler");
        roleInfoType = ResolveType(tor, "RoleInfo", "TheOtherRoles.RoleInfo");
        mapUtilitiesType = ResolveType(tor, "MapUtilities", "TheOtherRoles.Utilities.MapUtilities");
        playerControlPatchType = ResolveType(tor, "PlayerControlPatch", "TheOtherRoles.Patches.PlayerControlPatch");
        mapBehaviourPatchType = ResolveType(tor, "MapBehaviourPatch", "TheOtherRoles.Patches.MapBehaviourPatch");
        startMeetingPatchType = ResolveType(tor, "StartMeetingPatch", "TheOtherRoles.Patches.MeetingHudPatch+StartMeetingPatch");

        if (snitchType != null)
        {
            snitchPlayerField = ResolveField(snitchType, "snitch");
            snitchModeField = ResolveField(snitchType, "mode");
            snitchTargetsField = ResolveField(snitchType, "targets");
            snitchIsRevealedField = ResolveField(snitchType, "isRevealed");
            snitchNeedsUpdateField = ResolveField(snitchType, "needsUpdate");
            snitchTextField = ResolveField(snitchType, "text");
            snitchTaskCountForRevealField = ResolveField(snitchType, "taskCountForReveal");
            snitchModeEnumType = snitchType.GetNestedType("Mode", BindingFlags.Public | BindingFlags.NonPublic);
            snitchTargetsEnumType = snitchType.GetNestedType("Targets", BindingFlags.Public | BindingFlags.NonPublic);

            if (snitchModeEnumType != null)
            {
                snitchModeChatValue = GetEnumValue(snitchModeEnumType, "Chat", 0);
                snitchModeMapValue = GetEnumValue(snitchModeEnumType, "Map", 1);
                snitchModeChatAndMapValue = GetEnumValue(snitchModeEnumType, "ChatAndMap", 2);
            }

            if (snitchTargetsEnumType != null)
            {
                snitchTargetsEvilPlayersValue = GetEnumValue(snitchTargetsEnumType, "EvilPlayers", 0);
                snitchTargetsKillersValue = GetEnumValue(snitchTargetsEnumType, "Killers", 1);
            }

            snitchClearAndReloadMethod = ResolveMethod(snitchType, "clearAndReload");
        }

        var rpcProcedureType = ResolveType(tor, "RPCProcedure", "TheOtherRoles.RPCProcedure");
        shareRoomMethod = ResolveMethod(rpcProcedureType, "shareRoom");

        helpersShouldShowGhostInfoMethod = ResolveMethod(helpersType, "shouldShowGhostInfo");
        helpersIsEvilMethod = ResolveMethod(helpersType, "isEvil");
        helpersIsKillerMethod = ResolveMethod(helpersType, "isKiller");

        taskInfoMethod = ResolveMethod(tasksHandlerType, "taskInfo");
        getRolesStringMethod = ResolveMethod(roleInfoType, "GetRolesString");
        mapUtilitiesCachedShipStatusField = ResolveField(mapUtilitiesType, "CachedShipStatus");
        snitchUpdateMethod = ResolveMethod(playerControlPatchType, "snitchUpdate");
        mapBehaviourHerePointsField = ResolveField(mapBehaviourPatchType, "herePoints");

        // TORs eigene StartMeetingPatch.Prefix — Ziel des deterministischen Wraps (prefix/postfix
        // direkt auf diese Methode, damit der Mode-Swap garantiert genau um TORs synchrones
        // Snitch.mode-Lesen herum aktiv ist, unabhängig von globaler Patch-Reihenfolge).
        torStartMeetingPrefixMethod = ResolveMethod(startMeetingPatchType, "Prefix");
    }

    // Auflösung mit mehreren Kandidaten plus Fallback-Suche über alle TOR-Typen.
    // Erster Kandidat ist der einfache Name (für die Fallback-Suche), danach voll qualifizierte
    // Namen für tor.GetType. So fängt ein Runtime-Mismatch (Namespace-/Nesting-Drift) sauber ab.
    private static Type ResolveType(Assembly tor, string simpleName, params string[] fullNames)
    {
        foreach (var name in fullNames)
        {
            var t = tor.GetType(name);
            if (t != null) return t;
        }

        // Fallback: über alle Typen nach dem einfachen Namen suchen.
        var match = SafeGetTypes(tor).FirstOrDefault(t => t.Name == simpleName);
        if (match != null)
        {
            UsefulTORStuffPlugin.Logger?.LogWarning(
                $"SnitchLogic: resolved '{simpleName}' via fallback type scan → {match.FullName}.");
            return match;
        }

        UsefulTORStuffPlugin.Logger?.LogWarning(
            $"SnitchLogic: could not resolve type '{simpleName}' (tried: {string.Join(", ", fullNames)}).");
        return null;
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        catch { return Array.Empty<Type>(); }
    }

    private static FieldInfo ResolveField(Type type, params string[] names)
    {
        if (type == null) return null;
        foreach (var n in names)
        {
            var f = type.GetField(n, AnyMember);
            if (f != null) return f;
        }
        UsefulTORStuffPlugin.Logger?.LogWarning(
            $"SnitchLogic: field '{names.FirstOrDefault()}' not found on {type.FullName}.");
        return null;
    }

    private static MethodInfo ResolveMethod(Type type, params string[] names)
    {
        if (type == null) return null;
        foreach (var n in names)
        {
            var m = type.GetMethod(n, AnyMember);
            if (m != null) return m;
        }
        UsefulTORStuffPlugin.Logger?.LogWarning(
            $"SnitchLogic: method '{names.FirstOrDefault()}' not found on {type.FullName}.");
        return null;
    }

    private static int GetEnumValue(Type enumType, string name, int fallback)
    {
        try { return Convert.ToInt32(Enum.Parse(enumType, name)); }
        catch
        {
            UsefulTORStuffPlugin.Logger?.LogWarning(
                $"SnitchLogic: enum value '{name}' not found on {enumType.FullName} — using fallback {fallback}.");
            return fallback;
        }
    }

    private static PlayerControl GetSnitchPlayer()
    {
        return snitchPlayerField?.GetValue(null) as PlayerControl;
    }

    private static int GetSnitchMode()
    {
        return snitchModeField == null ? -1 : Convert.ToInt32(snitchModeField.GetValue(null));
    }

    private static void SetSnitchMode(int value)
    {
        if (snitchModeField == null || snitchModeEnumType == null) return;
        snitchModeField.SetValue(null, Enum.ToObject(snitchModeEnumType, value));
    }

    private static int GetSnitchTargets()
    {
        return snitchTargetsField == null ? -1 : Convert.ToInt32(snitchTargetsField.GetValue(null));
    }

    private static bool GetSnitchIsRevealed()
    {
        return snitchIsRevealedField != null && Convert.ToBoolean(snitchIsRevealedField.GetValue(null));
    }

    private static void SetSnitchIsRevealed(bool value)
    {
        if (snitchIsRevealedField != null) snitchIsRevealedField.SetValue(null, value);
    }

    private static bool GetSnitchNeedsUpdate()
    {
        return snitchNeedsUpdateField != null && Convert.ToBoolean(snitchNeedsUpdateField.GetValue(null));
    }

    private static void SetSnitchNeedsUpdate(bool value)
    {
        if (snitchNeedsUpdateField != null) snitchNeedsUpdateField.SetValue(null, value);
    }

    private static TMPro.TextMeshPro GetSnitchText()
    {
        return snitchTextField?.GetValue(null) as TMPro.TextMeshPro;
    }

    private static void SetSnitchText(TMPro.TextMeshPro text)
    {
        if (snitchTextField != null) snitchTextField.SetValue(null, text);
    }

    private static int GetSnitchTaskCountForReveal()
    {
        return snitchTaskCountForRevealField == null ? 0 : Convert.ToInt32(snitchTaskCountForRevealField.GetValue(null));
    }

    private static Dictionary<byte, SpriteRenderer> GetHerePoints()
    {
        if (mapBehaviourHerePointsField == null) return null;

        var value = mapBehaviourHerePointsField.GetValue(null) as Dictionary<byte, SpriteRenderer>;
        if (value == null)
        {
            value = new Dictionary<byte, SpriteRenderer>();
            mapBehaviourHerePointsField.SetValue(null, value);
        }

        return value;
    }

    private static bool IsChatRevealMode(int mode)
    {
        return mode == snitchModeChatValue || mode == snitchModeChatAndMapValue;
    }

    private static bool IsMapRevealMode(int mode)
    {
        return mode == snitchModeMapValue || mode == snitchModeChatAndMapValue;
    }

    private static bool IsSnitchTargetMatch(PlayerControl player)
    {
        if (player == null || player.Data == null) return false;

        int targets = GetSnitchTargets();
        if (targets == snitchTargetsEvilPlayersValue) return CallHelpersIsEvil(player);
        if (targets == snitchTargetsKillersValue) return CallHelpersIsKiller(player);
        return false;
    }

    private static bool ShouldRunChatReveal(PlayerControl snitch)
    {
        if (snitch == null || snitch.Data == null || snitch.Data.IsDead) return false;
        if (!IsChatRevealMode(GetSnitchMode())) return false;

        var local = PlayerControl.LocalPlayer;
        return local != null && (local == snitch || CallHelpersShouldShowGhostInfo());
    }

    private static bool ShouldRunMapReveal(PlayerControl snitch)
    {
        if (snitch == null || snitch.Data == null || snitch.Data.IsDead) return false;
        if (!IsMapRevealMode(GetSnitchMode())) return false;

        var local = PlayerControl.LocalPlayer;
        return local != null && local == snitch;
    }

    private static bool CallHelpersShouldShowGhostInfo()
    {
        if (helpersShouldShowGhostInfoMethod == null) return false;
        return Convert.ToBoolean(helpersShouldShowGhostInfoMethod.Invoke(null, Array.Empty<object>()));
    }

    private static bool CallHelpersIsEvil(PlayerControl player)
    {
        if (helpersIsEvilMethod == null || player == null) return false;
        return Convert.ToBoolean(helpersIsEvilMethod.Invoke(null, new object[] { player }));
    }

    private static bool CallHelpersIsKiller(PlayerControl player)
    {
        if (helpersIsKillerMethod == null || player == null) return false;
        return Convert.ToBoolean(helpersIsKillerMethod.Invoke(null, new object[] { player }));
    }

    private static Tuple<int, int> CallTaskInfo(object playerInfo)
    {
        if (taskInfoMethod == null) return Tuple.Create(0, 0);
        return taskInfoMethod.Invoke(null, new[] { playerInfo }) as Tuple<int, int> ?? Tuple.Create(0, 0);
    }

    private static string CallGetRolesString(PlayerControl player, bool useColors, bool showModifier, bool suppressGhostInfo)
    {
        if (getRolesStringMethod == null || player == null) return string.Empty;
        return getRolesStringMethod.Invoke(null, new object[] { player, useColors, showModifier, suppressGhostInfo }) as string ?? string.Empty;
    }

    private static ShipStatus GetCachedShipStatus()
    {
        return mapUtilitiesCachedShipStatusField?.GetValue(null) as ShipStatus;
    }

    private static TMPro.TextMeshPro CreateSnitchText()
    {
        var hud = DestroyableSingleton<HudManager>.Instance;
        if (hud == null || hud.KillButton == null || hud.KillButton.cooldownTimerText == null) return null;

        var text = UnityEngine.Object.Instantiate(hud.KillButton.cooldownTimerText, hud.transform);
        text.enableWordWrapping = false;
        text.transform.localScale = Vector3.one * 0.75f;
        text.transform.localPosition += new Vector3(0f, 1.8f, -69f);
        text.gameObject.SetActive(true);
        return text;
    }

    private static void DestroySnitchText(TMPro.TextMeshPro text)
    {
        if (text == null) return;
        UnityEngine.Object.Destroy(text.gameObject);
        SetSnitchText(null);
    }

    private static void AddChatMessage(PlayerControl speaker, string message)
    {
        var hud = DestroyableSingleton<HudManager>.Instance;
        if (hud?.Chat == null || speaker == null || string.IsNullOrEmpty(message)) return;
        hud.Chat.AddChat(speaker, message);
    }

    private static void ShareRoomPostfix(byte __0, byte __1)
    {
        try
        {
            roomMap[__0] = __1;
        }
        catch (Exception ex)
        {
            UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic shareRoom recorder failed: {ex}");
        }
    }

    private static void ClearAndReloadPostfix()
    {
        try
        {
            roomMap.Clear();
            chatModeSwapped = false;
            mapModeSwapped = false;
        }
        catch (Exception ex)
        {
            UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic clearAndReload reset failed: {ex}");
        }
    }

    private static bool SnitchHudUpdatePrefix()
    {
        if (!HudRevealReady) return true;
        // Nur wirksam, wenn alle den Mod haben — sonst läuft TORs Original-HUD.
        if (!UsefulTORStuffPlugin.SnitchClientFixActive) return true;

        try
        {
            var snitch = GetSnitchPlayer();
            if (snitch == null || snitch.Data == null) return false;
            if (!GetSnitchNeedsUpdate()) return false;

            bool snitchIsDead = snitch.Data.IsDead;
            var taskInfo = CallTaskInfo(snitch.Data);
            int playerCompleted = taskInfo.Item1;
            int playerTotal = taskInfo.Item2;
            if (playerTotal == 0) return false;

            var local = PlayerControl.LocalPlayer;
            int numberOfTasks = playerTotal - playerCompleted;
            int targets = GetSnitchTargets();
            bool localCanSee = local != null &&
                               ((targets == snitchTargetsEvilPlayersValue && CallHelpersIsEvil(local)) ||
                                (targets == snitchTargetsKillersValue && CallHelpersIsKiller(local)));

            var text = GetSnitchText();
            if (GetSnitchIsRevealed() && localCanSee)
            {
                if (text == null)
                {
                    text = CreateSnitchText();
                    SetSnitchText(text);
                }
                else
                {
                    text.text = UTSLocalization.Tr("uts.snitchlogic.snitch_alive_hud", playerCompleted, playerTotal);
                    if (snitchIsDead) text.text = UTSLocalization.Tr("uts.snitchlogic.snitch_dead_hud");
                }
            }
            else if (text != null)
            {
                DestroySnitchText(text);
            }

            if (snitchIsDead)
            {
                if (MeetingHud.Instance == null) SetSnitchNeedsUpdate(false);
                return false;
            }

            if (numberOfTasks <= GetSnitchTaskCountForReveal())
                SetSnitchIsRevealed(true);

            return false;
        }
        catch (Exception ex)
        {
            UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic snitchUpdate replacement failed: {ex}");
            return true;
        }
    }

    // ── Gemeinsame Suppression-/Sende-Logik ────────────────────────────────────────────────
    // Geteilt zwischen dem deterministischen Wrap (TorSnitchSuppress*, prefix/postfix DIREKT auf
    // TORs StartMeetingPatch.Prefix) und dem Best-Effort-Fallback (StartMeetingChatPatch, mode-swap
    // auf PlayerControl.StartMeeting). Beide Pfade sind inhaltsgleich; sie unterscheiden sich nur
    // im Hook-Punkt. Genau einer ist aktiv (Fallback gegated auf !TorSuppressReady).

    // Swappt Snitch.mode auf Map, damit TORs synchroner Reveal-Check (mode != Map) seine eigene
    // (durch die Reset-Race oft leere) Nachricht NICHT sendet. Gibt true zurück, wenn geswappt
    // wurde — dann MUSS anschließend RestoreChatModeAndEmit() laufen.
    private static bool TrySwapChatModeToMap()
    {
        // Nur wirksam, wenn alle den Mod haben. Sonst KEIN Mode-Swap → TOR sendet seine eigene
        // Snitch-Nachricht (und HostFix Fix 4 repariert die Daten per Re-Broadcast).
        if (!UsefulTORStuffPlugin.SnitchClientFixActive) return false;

        var snitch = GetSnitchPlayer();
        if (!ShouldRunChatReveal(snitch)) return false;

        chatOriginalMode = GetSnitchMode();
        if (!IsChatRevealMode(chatOriginalMode)) return false;

        SetSnitchMode(snitchModeMapValue);
        return true;
    }

    // Setzt den gemerkten Originalmodus zurück und sendet SnitchLogics eigene Reveal-Nachricht
    // (die einzige, die der Spieler sehen soll).
    private static void RestoreChatModeAndEmit()
    {
        SetSnitchMode(chatOriginalMode);
        EmitChatReveal(GetSnitchPlayer());
    }

    // Baut nach kurzer Verzögerung die "Bad alive roles in game"-Liste aus der EIGENEN roomMap auf
    // und schickt sie als eine einzige Chat-Nachricht vom Snitch.
    private static void EmitChatReveal(PlayerControl snitch)
    {
        if (!ShouldRunChatReveal(snitch)) return;

        var taskInfo = CallTaskInfo(snitch.Data);
        int playerCompleted = taskInfo.Item1;
        int playerTotal = taskInfo.Item2;
        int numberOfTasks = playerTotal - playerCompleted;
        if (numberOfTasks != 0) return;

        string output = UTSLocalization.Tr("uts.snitchlogic.reveal_header");
        var hud = DestroyableSingleton<HudManager>.Instance;
        if (hud == null) return;

        hud.StartCoroutine(Effects.Lerp(0.4f, new Action<float>(x =>
        {
            if (x != 1f) return;

            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (!IsSnitchTargetMatch(player)) continue;
                if (player == null || player.Data == null || player.Data.IsDead) continue;

                // Robust: einen bösen Spieler IMMER listen, auch wenn sein ShareRoom (noch)
                // nicht in roomMap steht. Der Host sendet sein ShareRoom als Meeting-Autorität
                // vor allen anderen — geht sein Eintrag verloren, fehlte er bisher KOMPLETT.
                // Anwesenheit schlägt korrekten Raum: ohne Eintrag fällt der Raum auf
                // "open fields" (byte.MinValue) zurück, der Spieler erscheint trotzdem.
                if (!roomMap.TryGetValue(player.PlayerId, out byte room)) room = byte.MinValue;

                var roomName = "open fields";
                if (room != byte.MinValue)
                    roomName = DestroyableSingleton<TranslationController>.Instance.GetString((SystemTypes)room);

                output += UTSLocalization.Tr("uts.snitchlogic.reveal_line", CallGetRolesString(player, false, false, true), roomName);
            }

            AddChatMessage(snitch, output);
        })));
    }

    // Deterministischer Wrap auf TORs StartMeetingPatch.Prefix (installiert in Initialize, wenn
    // TorSuppressReady). Prefix swappt VOR TORs synchronem mode-Lesen, Postfix setzt zurück und
    // sendet unsere Nachricht — reihenfolge-unabhängig garantiert.
    private static void TorSnitchSuppressPrefix()
    {
        try { chatModeSwapped = TrySwapChatModeToMap(); }
        catch (Exception ex)
        {
            chatModeSwapped = false;
            UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic TOR-suppress prefix failed: {ex}");
        }
    }

    private static void TorSnitchSuppressPostfix()
    {
        if (!chatModeSwapped) return;

        try { RestoreChatModeAndEmit(); }
        catch (Exception ex)
        {
            UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic TOR-suppress postfix failed: {ex}");
        }
        finally { chatModeSwapped = false; }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
    [HarmonyPriority(Priority.High)]
    private static class StartMeetingChatPatch
    {
        // Best-Effort-Fallback: greift NUR, wenn der deterministische Wrap auf TORs eigene
        // Prefix-Methode NICHT installiert werden konnte (TorSuppressReady == false). Sonst würde
        // hier eine zweite, identische Nachricht entstehen (Doppelung). TorSuppressReady steht in
        // Initialize fest, BEVOR PatchAll dieses Prepare() auswertet.
        public static bool Prepare() => ChatRevealReady && !TorSuppressReady;

        public static void Prefix()
        {
            try { chatModeSwapped = TrySwapChatModeToMap(); }
            catch (Exception ex)
            {
                chatModeSwapped = false;
                UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic chat prefix failed: {ex}");
            }
        }

        [HarmonyPriority(Priority.Low)]
        public static void Postfix()
        {
            if (!chatModeSwapped) return;

            try { RestoreChatModeAndEmit(); }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic chat postfix failed: {ex}");
            }
            finally { chatModeSwapped = false; }
        }
    }

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
    [HarmonyPriority(Priority.High)]
    private static class MapRevealPatch
    {
        public static bool Prepare() => MapRevealReady;

        public static void Prefix()
        {
            // Nur wirksam, wenn alle den Mod haben — sonst läuft TORs Original-Map-Reveal.
            if (!UsefulTORStuffPlugin.SnitchClientFixActive) { mapModeSwapped = false; return; }

            try
            {
                var snitch = GetSnitchPlayer();
                if (!ShouldRunMapReveal(snitch))
                {
                    mapModeSwapped = false;
                    return;
                }

                mapOriginalMode = GetSnitchMode();
                if (!IsMapRevealMode(mapOriginalMode))
                {
                    mapModeSwapped = false;
                    return;
                }

                SetSnitchMode(snitchModeChatValue);
                mapModeSwapped = true;
            }
            catch (Exception ex)
            {
                mapModeSwapped = false;
                UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic map prefix failed: {ex}");
            }
        }

        [HarmonyPriority(Priority.Low)]
        public static void Postfix(MapBehaviour __instance)
        {
            if (!mapModeSwapped) return;

            try
            {
                SetSnitchMode(mapOriginalMode);

                var snitch = GetSnitchPlayer();
                if (!ShouldRunMapReveal(snitch)) return;

                var taskInfo = CallTaskInfo(snitch.Data);
                int playerCompleted = taskInfo.Item1;
                int playerTotal = taskInfo.Item2;
                int numberOfTasks = playerTotal - playerCompleted;
                if (numberOfTasks != 0) return;

                var points = GetHerePoints();
                if (points == null || __instance == null || __instance.HerePoint == null) return;
                var shipStatus = GetCachedShipStatus();
                if (shipStatus == null) return;

                if (MeetingHud.Instance == null)
                {
                    foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                    {
                        if (player == null || player.Data == null || player.Data.IsDead) continue;
                        if (!IsSnitchTargetMatch(player)) continue;

                        Vector3 v = player.transform.position;
                        v /= shipStatus.MapScale;
                        v.x *= Mathf.Sign(shipStatus.transform.localScale.x);
                        v.z = -2.1f;

                        if (points.TryGetValue(player.PlayerId, out SpriteRenderer existing) && existing != null)
                        {
                            existing.transform.localPosition = v;
                            continue;
                        }

                        if (points.ContainsKey(player.PlayerId))
                            points.Remove(player.PlayerId);

                        var herePoint = UnityEngine.Object.Instantiate(__instance.HerePoint, __instance.HerePoint.transform.parent, true);
                        herePoint.transform.localPosition = v;
                        herePoint.enabled = true;

                        int colorId = player.CurrentOutfit.ColorId;
                        player.CurrentOutfit.ColorId = 6;
                        player.SetPlayerMaterialColors(herePoint);
                        player.CurrentOutfit.ColorId = colorId;

                        points.Add(player.PlayerId, herePoint);
                    }
                }
                else
                {
                    foreach (var entry in points.ToList())
                    {
                        if (entry.Value != null) UnityEngine.Object.Destroy(entry.Value.gameObject);
                        points.Remove(entry.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"SnitchLogic map postfix failed: {ex}");
            }
            finally
            {
                mapModeSwapped = false;
            }
        }
    }
}