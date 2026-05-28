// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UsefulTORStuffPlugin - External fixes for TOR 4.8.0.
 *
 * 1) Bloody modifier lag: TOR's PlayerControlPatch.bloodyUpdate() spawns a brand-new
 *    Bloodytrail (GameObject + SpriteRenderer + 10s coroutine) for every bloody player
 *    on EVERY FixedUpdate (~50 Hz). With a 10s lifetime that's up to ~500 live blood
 *    GameObjects per player, which tanks the framerate. Fix: patch the Bloodytrail
 *    constructor and skip it unless the player moved at least MinDropDistance since their
 *    last accepted blood drop.
 *
 * 2) Snitch logic reimplementation (SnitchLogic): a gated, client-side replacement for TOR's
 *    Snitch room/chat/map/HUD paths. It records ShareRoom RPCs into a persistent room map, then
 *    suppresses TOR's buggy Snitch surfaces only when the required reflection handles are present;
 *    otherwise TOR's original behavior stays active. HostFixPlugin's host-only fallback (Fix 4)
 *    still covers a Snitch WITHOUT this mod by having the host re-broadcast its room; if both run
 *    they write the same host entry (idempotent). The version handshake (UsefulVersionHandshake,
 *    RPC 253) now only feeds the lobby display and lets HostFix stand down its redundant re-send
 *    when every player already has this mod (SnitchClientFixActive).
 *
 * Strategy: minimal, defensive patches via reflection so no compile-time TOR reference
 * is needed; if TOR changes its internals the patches simply become no-ops.
 */

global using Il2CppInterop.Runtime;
global using Il2CppInterop.Runtime.Attributes;
global using Il2CppInterop.Runtime.InteropTypes;
global using Il2CppInterop.Runtime.InteropTypes.Arrays;
global using Il2CppInterop.Runtime.Injection;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UsefulTORStuff;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("Among Us.exe")]
[BepInDependency("me.eisbison.theotherroles", BepInDependency.DependencyFlags.HardDependency)]
public class UsefulTORStuffPlugin : BasePlugin
{
    public const string PluginGuid = "com.tormod.usefultorstuff";
    public const string PluginName = "Useful TOR Stuff";
    public const string PluginVersion = "1.0.0";
    public static readonly System.Version Version = System.Version.Parse(PluginVersion);

    // Custom RPC for the mod-presence handshake (see UsefulVersionHandshake).
    // 253 is free: TOR's CustomRPC enum runs 100–~180, the Chance mod uses 200/201/250/251.
    public const byte VersionHandshakeRpcId = 253;

    public static ManualLogSource Logger { get; private set; }

    internal static Assembly TORAssembly;
    internal static ConfigEntry<float> MinDropDistance;

    // True only when the version handshake confirms every connected player runs the same
    // Useful TOR Stuff build. Gates the client-side Snitch fix and is read by HostFixPlugin
    // (via cross-assembly reflection) so its host-only fallback can stand down.
    public static bool SnitchClientFixActive;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

        // Check if this mod is enabled. Early return wenn deaktiviert.
        var enabled = Config.Bind("General", "Enabled", true, "Enable this mod");
        if (!enabled.Value) {
            Logger.LogInfo($"{PluginName} is disabled in config — skipping load.");
            return;
        }

        // Mod-Manager Config: Wenn aktiviert, werden Update-Buttons in die Mod-Manager-UI verschoben.
        var modManagerEnabled = Config.Bind("ModManager", "Enabled", false,
            "When enabled, update buttons move into the Mod Manager UI. When disabled, update buttons " +
            "appear at their original positions.");
        ModManagerRegistry.SetModManagerEnabled(modManagerEnabled.Value);

        MinDropDistance = Config.Bind(
            "Bloody", "MinDropDistance", 0.35f,
            "Minimum distance (in world units) a bloody player must travel before a new blood " +
            "trail drop is spawned. Higher = fewer blood objects = less lag. 0 disables throttling.");

        var harmony = new Harmony(PluginGuid);

        // Manual reflection patches (TOR types are internal): Bloody throttle plus SnitchLogic's
        // reflection-gated room recorder and surface reimplementation.
        PatchBloodyThrottle(harmony);
        SnitchLogic.Initialize(harmony);

        // All attribute-based [HarmonyPatch] classes in this assembly: VersionDisplayPatch,
        // the UsefulVersionHandshake patches (RPC 253 + lobby messages), and the gated Snitch
        // surface patches. Assembly-wide so nested patch classes are picked up too.
        harmony.PatchAll(typeof(UsefulTORStuffPlugin).Assembly);

        // Self-updater: checks GitHub releases and offers an in-game update button.
        AddComponent<UsefulTORStuffUpdater>();

        // Mod-Manager UI Components: Button im Hauptmenü + Popup-UI.
        AddComponent<ModManagerButton>();
        AddComponent<ModManagerUI>();

        // Registriere diese Mod in der Mod-Manager-Registry via AppDomain.
        try {
            var modData = new System.Collections.Generic.Dictionary<string, object> {
                { "Guid", PluginGuid },
                { "Name", PluginName },
                { "Version", Version },
                { "RepositoryOwner", UsefulTORStuffUpdater.RepositoryOwner },
                { "RepositoryName", UsefulTORStuffUpdater.RepositoryName },
                { "ButtonColor", Color.green },
                { "Enabled", enabled }
            };
            AppDomain.CurrentDomain.SetData($"ModManager.RegisteredMod.{PluginGuid}", modData);
            Logger.LogInfo($"Registered {PluginName} in Mod Manager registry.");
        } catch (Exception ex) {
            Logger.LogError($"Failed to register {PluginName}: {ex}");
        }

        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    // ========================================================================
    // Bloody throttle: patch the Bloodytrail constructor (resolved via reflection,
    // since the type is internal to TOR) and skip construction when the player hasn't
    // moved far enough since their last drop.
    // ========================================================================

    private void PatchBloodyThrottle(Harmony harmony)
    {
        try
        {
            TORAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
            if (TORAssembly == null)
            {
                Logger.LogError("TheOtherRoles assembly not found — Bloody throttle disabled.");
                return;
            }

            var bloodytrailType = TORAssembly.GetType("TheOtherRoles.Objects.Bloodytrail");
            if (bloodytrailType == null)
            {
                Logger.LogWarning("Bloodytrail type not found — Bloody throttle disabled.");
                return;
            }

            var ctor = bloodytrailType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(PlayerControl), typeof(PlayerControl) }, null);
            if (ctor == null)
            {
                Logger.LogWarning("Bloodytrail(PlayerControl, PlayerControl) constructor not found — Bloody throttle disabled.");
                return;
            }

            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(BloodyThrottlePatch), nameof(BloodyThrottlePatch.Prefix)));
            Logger.LogInfo("Patched Bloodytrail constructor — blood drops are now distance-throttled.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to patch Bloodytrail: {ex}");
        }
    }

    public static class BloodyThrottlePatch
    {
        // Last accepted blood-drop position per player id.
        private static readonly Dictionary<byte, Vector2> _lastDropPos = new Dictionary<byte, Vector2>();

        // __0 = the "player" argument of Bloodytrail(player, bloodyPlayer). Returning false skips the
        // original constructor body, so no blood GameObject is created this tick.
        public static bool Prefix(PlayerControl __0)
        {
            try
            {
                if (__0 == null) return true;
                float minDist = MinDropDistance != null ? MinDropDistance.Value : 0.35f;
                if (minDist <= 0f) return true; // throttling disabled

                byte id = __0.PlayerId;
                Vector3 p = __0.transform.position;
                Vector2 pos = new Vector2(p.x, p.y);

                if (_lastDropPos.TryGetValue(id, out Vector2 last)
                    && Vector2.Distance(pos, last) < minDist)
                {
                    return false; // not far enough — skip this drop
                }

                _lastDropPos[id] = pos;
                return true;
            }
            catch
            {
                return true; // never block TOR on our account
            }
        }
    }

    // ========================================================================
    // Version display: add a clickable "Useful TOR Stuff vX.Y.Z" line to the top-corner
    // PingTracker. Inserted right after TOR's own "TheOtherRoles vX" line; the Chance and
    // Host Fix lines stack below it.
    //
    // Clicking the name toggles a "Modded by DaUnknown" credit. The toggle state is shared
    // across all three of our mods via a process-wide AppDomain flag (no cross-assembly
    // references), so clicking any mod name flips the same flag — clicking another hides it
    // again. The credit is only inserted if not already present, so it shows at most once.
    // ========================================================================

    [HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
    [HarmonyPriority(Priority.Low)] // run after TOR's own PingTracker postfix
    public static class VersionDisplayPatch
    {
        // Shared with HostFixPlugin and the Chance Modifier — keep this string identical there.
        private const string CreditKey = "TORMods.DaUnknownCreditVisible";
        private const string LinkId = "usefulTORStuffCredits";

        private static bool CreditVisible() =>
            AppDomain.CurrentDomain.GetData(CreditKey) is bool b && b;

        public static void Postfix(PingTracker __instance)
        {
            if (__instance == null || __instance.text == null) return;
            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text)) return;

            // Click the mod name to toggle the shared credit line. PingTracker.text is a
            // world-space TextMeshPro (no canvas), so the link raycast needs the rendering camera.
            if (Input.GetMouseButtonDown(0))
            {
                Camera cam = Camera.main;
                var canvas = __instance.text.canvas;
                if (canvas != null)
                    cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null
                        : (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);
                int link = TMPro.TMP_TextUtilities.FindIntersectingLink(__instance.text, Input.mousePosition, cam);
                if (link != -1 && __instance.text.textInfo.linkInfo[link].GetLinkID() == LinkId)
                    AppDomain.CurrentDomain.SetData(CreditKey, !CreditVisible());
            }

            string line = $"<link=\"{LinkId}\"><color=#3FCF4A>Useful TOR Stuff</color> v{PluginVersion}</link>";
            int nl = text.IndexOf('\n');
            text = nl >= 0
                ? text.Substring(0, nl + 1) + line + "\n" + text.Substring(nl + 1)
                : text + "\n" + line;

            // Insert the shared credit under TOR's "Design by Bavari" line — but only if no other
            // mod already added it this frame, so "Modded by DaUnknown" appears at most once.
            if (CreditVisible() && !text.Contains("DaUnknown"))
            {
                string credit = "\n<size=70%>Modded by <color=#FCCE03FF>DaUnknown</color></size>";
                int anchor = text.IndexOf("Bavari");
                if (anchor >= 0)
                {
                    int lineEnd = text.IndexOf('\n', anchor);
                    text = lineEnd >= 0
                        ? text.Substring(0, lineEnd) + credit + text.Substring(lineEnd)
                        : text + credit;
                }
                else
                {
                    text += credit;
                }
            }

            __instance.text.text = text;
        }
    }
}
