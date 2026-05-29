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
    internal static ConfigEntry<float> ModManagerButtonX;
    internal static ConfigEntry<float> ModManagerButtonY;

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
        // Default true: der Mod-Manager-Button ist immer im Hauptmenü sichtbar, also gehören die
        // Update-Buttons standardmäßig in den Manager statt zusätzlich ins Hauptmenü.
        var modManagerEnabled = Config.Bind("ModManager", "Enabled", true,
            "When enabled, update buttons move into the Mod Manager UI. When disabled, update buttons " +
            "appear at their original positions.");
        ModManagerRegistry.SetModManagerEnabled(modManagerEnabled.Value);

        ModManagerButtonX = Config.Bind("ModManager", "ButtonPositionX", 0.8f,
            "X position of the Mod Manager button (anchor point 0-1)");
        ModManagerButtonY = Config.Bind("ModManager", "ButtonPositionY", 0.21f,
            "Y position of the Mod Manager button (anchor point 0-1)");

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

        // Registriere diese Mod in der Mod-Manager-Registry.
        try {
            var modData = new System.Collections.Generic.Dictionary<string, object> {
                { "Guid", PluginGuid },
                { "Name", PluginName },
                { "Version", Version },
                { "RepositoryOwner", UsefulTORStuffUpdater.RepositoryOwner },
                { "RepositoryName", UsefulTORStuffUpdater.RepositoryName },
                { "ButtonColor", Color.green },
                { "Enabled", enabled },
                { "RuntimeEnabled", true }
            };
            ModManagerRegistry.RegisterMod(PluginGuid, modData);
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

            // Resolve the private instance fields the color fix needs. If either is missing we
            // still register the throttle prefix; the postfix simply no-ops.
            BloodyColorFixPatch.SpriteRendererField = bloodytrailType.GetField(
                "spriteRenderer", BindingFlags.NonPublic | BindingFlags.Instance);
            BloodyColorFixPatch.ColorField = bloodytrailType.GetField(
                "color", BindingFlags.NonPublic | BindingFlags.Instance);
            if (BloodyColorFixPatch.SpriteRendererField == null)
                Logger.LogWarning("Bloodytrail.spriteRenderer field not found — Bloody color fix disabled.");

            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(BloodyThrottlePatch), nameof(BloodyThrottlePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(BloodyColorFixPatch), nameof(BloodyColorFixPatch.Postfix)));
            Logger.LogInfo("Patched Bloodytrail constructor — blood drops are distance-throttled and color-corrected.");
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
    // Bloody color fix: TOR's Bloodytrail constructor assigns the shared
    // HatManager.PlayerMaterial to the blood SpriteRenderer and tints it via
    // SetPlayerMaterialColors. With that player shader in place the per-drop
    // SpriteRenderer.color the constructor's fade-out coroutine writes every frame
    // is ignored, so every drop renders in whatever the shared material settled on
    // (in practice the first killed player's color) instead of the actual victim's.
    //
    // Fix: right after construction, swap the renderer back to a plain vertex-color
    // sprite material (matching upstream TOR's original behavior). The renderer's
    // own fade coroutine then colors each drop correctly from its per-instance
    // `color` field — including the camouflage/mushroom gray case it already handles.
    // ========================================================================

    public static class BloodyColorFixPatch
    {
        public static FieldInfo SpriteRendererField;
        public static FieldInfo ColorField;

        // Plain sprite material that honors SpriteRenderer.color (RGBA vertex tint),
        // unlike the player shader. Built lazily and kept alive across scene loads.
        private static Material _bloodMaterial;

        private static Material BloodMaterial()
        {
            if (_bloodMaterial != null) return _bloodMaterial;
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;
            _bloodMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_bloodMaterial);
            return _bloodMaterial;
        }

        // __instance = the freshly built Bloodytrail. Skipped construction (throttled
        // prefix returned false) leaves spriteRenderer null, so we simply no-op.
        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null || SpriteRendererField == null) return;

                if (SpriteRendererField.GetValue(__instance) is not SpriteRenderer renderer || renderer == null)
                    return;

                var mat = BloodMaterial();
                if (mat != null) renderer.material = mat;

                // Apply the victim color immediately so the first frame isn't a white
                // flash; the constructor's fade coroutine keeps it updated afterwards.
                if (ColorField != null && ColorField.GetValue(__instance) is Color c)
                    renderer.color = c;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Bloody color fix failed: {ex}");
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
