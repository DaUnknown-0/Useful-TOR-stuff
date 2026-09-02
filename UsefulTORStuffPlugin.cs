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
    public const string PluginName = "TOR - Forgotten Fixes";
    public const string PluginVersion = "1.4.3.8";
    public static readonly System.Version Version = System.Version.Parse(PluginVersion);

    // Module byte for the mod-presence handshake (see UsefulVersionHandshake). Since the RPC
    // consolidation this is BOTH the module byte on UTSRpc.CallId = 240 and - for as long as the
    // legacy dual-send exists - the standalone callId older builds still listen on. See UTSRpc.cs.
    public const byte VersionHandshakeRpcId = 253;

    // Module byte for the mod inventory broadcast (UTSModInventory). 255 is the only free byte left
    // in the UTS block (244-254 are taken). New feature, so it exists ONLY on channel 240 - no
    // legacy callId, and older builds ignore it by design (UTSRpc.HandleRpcPatch).
    public const byte ModInventoryRpcId = 255;

    // Module byte for the newcomer kill shield (NewcomerShield). 242 sits just below MultiJester's
    // 243; like the inventory above it is a new feature and exists ONLY on channel 240.
    public const byte NewcomerShieldRpcId = 242;

    // Module byte for the spawn-area kill protection (AntiStartKill). 241 was the last free byte
    // below the newcomer shield's 242; new feature, so it exists ONLY on channel 240.
    public const byte AntiStartKillRpcId = 241;

    public static ManualLogSource Logger { get; private set; }

    internal static Assembly TORAssembly;
    // Shared "show test versions" display toggle, surfaced in the Mod Manager (top-right). Persisted
    // here; the live state lives in a process-wide AppDomain flag (VersionDisplay) read by every mod.
    public static ConfigEntry<bool> ShowTestVersionsConfig;
    internal static ConfigEntry<float> MinDropDistance;
    internal static ConfigEntry<bool> WebConfigEnabled;
    internal static ConfigEntry<int> WebConfigPort;
    internal static ConfigEntry<float> ModManagerButtonX;
    internal static ConfigEntry<float> ModManagerButtonY;
    // Mod sync (UTSModInventory/UTSModSync/UTSModDownloader). Client-side by nature - which mods
    // THIS installation is missing is nobody else's business, so it is a config entry and
    // deliberately not a host-synced CustomOption.
    public static ConfigEntry<bool> ModSyncEnabled;

    // True only when the version handshake confirms every connected player runs the same
    // Useful TOR Stuff build. Gates the client-side Snitch fix and is read by HostFixPlugin
    // (via cross-assembly reflection) so its host-only fallback can stand down.
    public static bool SnitchClientFixActive;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

        // Check if this mod is enabled. Early return wenn deaktiviert - ABER der Mod Manager läuft
        // trotzdem (siehe LoadModManagerOnly): dieser Mod BESITZT den Manager, und der Schalter zum
        // Wiedereinschalten liegt genau darin. Ohne das wäre "aus" eine Einbahnstraße, die sich nur
        // noch per Hand in der .cfg umkehren lässt.
        var enabled = Config.Bind("General", "Enabled", true, "Enable this mod");
        if (!enabled.Value) {
            Logger.LogInfo($"{PluginName} is disabled in config — loading the Mod Manager only.");
            LoadModManagerOnly(enabled);
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

        // Shared display toggle for the 4th version component (.W on test builds). Initialise the
        // process-wide flag from the saved value so every mod's version line agrees from the first frame.
        ShowTestVersionsConfig = Config.Bind("Version", "ShowTestVersions", false,
            "Show the 4th version component (the test-version number, e.g. v1.2.3.4) in mod version " +
            "lines. Stable builds (vX.Y.Z) are unaffected. Toggleable in the Mod Manager.");
        VersionDisplay.SetShowTestVersions(ShowTestVersionsConfig.Value);

        // Mod sync: compare this client's mod set against the host's and offer the missing pieces.
        // Downloads always take an explicit click; this switch only decides whether the comparison
        // happens and the lobby button appears at all.
        ModSyncEnabled = Config.Bind("ModSync", "Enabled", true,
            "In a lobby, compare your installed mods with the host's and offer to download what is "
            + "missing or mismatched. Download links come from a catalog compiled into this mod - the "
            + "host only sends which mod it runs, never where to get it. Nothing is ever downloaded "
            + "without an explicit click.");
        UTSRejoin.Bind(Config);

        MinDropDistance = Config.Bind(
            "Bloody", "MinDropDistance", 0.35f,
            "Minimum distance (in world units) a bloody player must travel before a new blood " +
            "trail drop is spawned. Higher = fewer blood objects = less lag. 0 disables throttling.");

        // Local host-only web settings editor (WebConfig): serves a browser page on 127.0.0.1
        // that edits every mod option + the vanilla Among Us options. Loopback-only; writes are
        // gated on AmHost. Disabled or re-ported here.
        WebConfigEnabled = Config.Bind("WebConfig", "Enabled", true,
            "Serve a local browser page (http://127.0.0.1:<port>/) for editing all lobby settings. "
            + "Loopback-only (never exposed to the network); only the host can change values.");
        WebConfigPort = Config.Bind("WebConfig", "Port", 32200,
            "TCP port for the local settings web page. If busy, the next few ports are tried.");

        // Read-only tracer for the decon-door / emergency-meeting reports; see DeconDiag.cs for
        // what each log line proves. The patch is attribute-based (PatchAll below).
        DeconDiag.Bind(Config);

        // Repair path for Harmony patches that stop executing mid-session (see DetourWatchdog.cs for
        // the measurements). Only the config entries are bound here; arming happens after PatchAll,
        // because its canary has to be registered in the same wrapper as everything else first.
        DetourWatchdog.Bind(Config);

        // Crash forensics for THIS machine: keep the BepInEx log across sessions, register WER
        // minidumps, log memory. Bound here, applied right after (nothing below depends on it, and
        // if the plugin dies later in Load the settings are already in place for the next launch).
        CrashDiagnostics.Bind(Config);
        CrashDiagnostics.Install();

        // TOR's 1032-file hat pack decoded to ~600 MB of ARGB32 on every client; this shrinks it
        // to DXT5 without a CPU copy (see HatTextureDiet.cs). Attribute patch, applied by PatchAll.
        HatTextureDiet.Bind(Config);

        var harmony = new Harmony(PluginGuid);

        // Collision watchdog for our consolidated custom callId (see UTSRpc.cs). TOR's CustomRPC enum
        // grows with every release; if it ever reaches our channel (or Unknown's Collection's), the
        // mods would silently mis-parse each other. Reflection-only, log-only, once per start.
        WarnOnRpcIdCollisions();

        // Mod-presence handshake receiver on the consolidated channel. It has no other load-time
        // entry point (all its patches are attribute-based), so it is registered here.
        UsefulVersionHandshake.RegisterRpc();

        // Mod inventory receiver (module byte 255). Like the handshake above it has no other
        // load-time entry point - all its patches are attribute-based.
        UTSModInventory.RegisterRpc();

        // Newcomer kill shield receiver (module byte 242). Same reason as the two above: its patches
        // are attribute-based, so the RPC registration has no other home.
        NewcomerShield.RegisterRpc();

        // Spawn-area kill protection receiver (module byte 241). Same pattern.
        AntiStartKill.RegisterRpc();

        // Manual reflection patches (TOR types are internal): Bloody throttle, the Bloody
        // killer-map color fix, plus SnitchLogic's reflection-gated room recorder and surface
        // reimplementation.
        PatchBloodyThrottle(harmony);
        PatchBloodyKillerMap(harmony);
        PatchBloodyResetVariables(harmony);
        SnitchLogic.Initialize(harmony);

        // Snapshot of TOR's option list BEFORE any of our own options exist. Everything added
        // between here and EndOptionCapture() below belongs to this mod and is therefore subject to
        // the "host does not have this mod" gate (UTSGate). Keep every CreateOptions() call inside
        // this bracket - an option created outside it silently stays ungated.
        UTSGate.BeginOptionCapture();

        // Sheriff "prevents killer parity win" option + win-check patches. CreateOptions must run
        // after TOR's CustomOptionHolder.Load() (guaranteed by the hard dependency on TOR).
        SheriffParityWin.CreateOptions();
        SheriffParityWin.TryPatch(harmony);

        // Vulture "counts guessed players as eaten" option + guesserShoot postfix. Same host-
        // authoritative win pattern as SheriffParityWin; CreateOptions runs after TOR's
        // CustomOptionHolder.Load() (guaranteed by the hard dependency on TOR).
        VultureGuessEat.CreateOptions();
        VultureGuessEat.TryPatch(harmony);

        // Meeting-duration override option (TOR Settings tab). Its MeetingHud.Start / OnGameEnd
        // patches are attribute-based and picked up by PatchAll below; only the options need
        // explicit creation, after TOR's CustomOptionHolder.Load().
        MeetingDurationOverride.CreateOptions();

        // Bomber "can cancel bomb" option. Its HudManager.Start / HandleRpc patches are
        // attribute-based (picked up by PatchAll); only the option needs explicit creation here.
        BomberCancel.CreateOptions();

        // Swapper "can fix lights/comms" options. All patches (Console.CanUse, minigame Begin/Close)
        // are attribute-based and picked up by PatchAll below; only the options need creation here.
        SwapperLightsFix.CreateOptions();

        // Medic "can reshield" option. HudManager.Start / HandleRpc patches are attribute-based.
        MedicReshield.CreateOptions();

        // Sidekick "can kill Jackal" option (reflection postfix on the private sidekickSetTarget).
        SidekickKillJackal.CreateOptions();
        SidekickKillJackal.TryPatch(harmony);

        // Spy "can fully vent" option. Spy.clearAndReload / Vent.Use / Vent.SetButtons patches are
        // attribute-based (PatchAll); only the option needs explicit creation here.
        SpyFullVent.CreateOptions();

        // Spy "Evil Flash on Death" + "Shifter Dies When Targeting Spy" options. Both patches are
        // attribute-based ([HarmonyPatch]) and picked up by PatchAll below.
        SpyExtras.CreateOptions();

        // Time Master "unguessable after shield saved a kill" option. RPCProcedure patches are
        // attribute-based (PatchAll); the guesser-list hide is a reflection patch (TryPatch).
        TimeMasterUnguessable.CreateOptions();
        TimeMasterUnguessable.TryPatch(harmony);

        // What the newcomer shield does inside the first meeting: not guessable, not votable.
        // TryPatch adds the reflection prefix on TOR's private guesserOnClick; the vote block and
        // the guesserShoot prefix are attribute-based (PatchAll). See NewcomerMeetingProtection.cs.
        NewcomerMeetingProtection.TryPatch(harmony);

        // Trapper "trapped players limp / self-limp" options. Trap.triggerTrap / physics / HandleRpc /
        // HudManager.Start patches are attribute-based (PatchAll); only the options need creation.
        TrapperLimp.CreateOptions();

        // Invert "Inverted Vision" option. HudManager.Update patch is attribute-based (PatchAll).
        InvertVision.CreateOptions();

        // Invert modifier "Rename to Drunk" option. Mutates CustomOption and RoleInfo strings
        // at runtime; no patches needed.
        DrunkRename.CreateOptions();

        // Trickster "Avatar Mixup Sabotage" option (Fungle). HudManager.Start patch is attribute-based;
        // TryPatch resolves TOR's private lightsOutButton for the shared cooldown.
        TricksterAvatarSabotage.CreateOptions();
        TricksterAvatarSabotage.TryPatch();

        // Trickster "Box Count" option (1-5, default 3): how many placed Jack-in-the-Boxes convert
        // into a connected vent network, replacing TOR's hardcoded 3. The clearAndReload postfix is
        // attribute-based (PatchAll); only the option needs explicit creation here. Gated on
        // "everyone has the mod" (falls back to TOR's own 3, with a host warning otherwise).
        TricksterBoxCount.CreateOptions();
        TrapperExtras.CreateOptions();

        // Lawyer/Lover "knows target/partner position on map" options. MapBehaviour / HudManager
        // patches are attribute-based (PatchAll); only the options need explicit creation.
        LawyerLoverTracker.CreateOptions();

        // Lover "Delay Lover Death" + Revenger path. Most patches are attribute-based (PatchAll);
        // TryPatch adds the reflection patch on TOR's internal CheckAndEndGameForLoverWin (instant
        // Revenger win) and resolves the UncheckedMurderPlayer RPC id. CreateOptions runs after TOR's
        // CustomOptionHolder.Load(). This feature is client-side and gated on "everyone has the mod".
        // NOTE: Revenger is NOT in any Role Draft — it only activates mid-game when a Lover dies.
        LoverRevenger.CreateOptions();
        LoverRevenger.TryPatch(harmony);

        // Tiebreaker quantity (max 3): multiple Tiebreakers, all shown as such, majority tie-break.
        // setModifier/resetVariables tracking + the RoleInfo.getRoleInfoForPlayer display postfix are
        // attribute-based (PatchAll); TryPatch adds the reflection/manual patches (getSelectionForRoleId
        // multiply, assignModifiers top-up, and the full CheckForEndVoting resolution reimplementation
        // that reuses TOR's CalculateVotes).
        TiebreakerMultiple.CreateOptions();
        TiebreakerMultiple.TryPatch(harmony);

        // MultiModifiers: quantity options for the single-holder modifiers Mini + Armored. Extra
        // holders live in own lists and get scale/kill-protection/armor-block re-supplied by
        // attribute patches (PatchAll); TryPatch adds the reflection patches (getSelectionForRoleId
        // multiply, assignModifiers top-up, miniUpdate age suffix). Gated on "everyone has the mod".
        MultiModifiers.CreateOptions();
        MultiModifiers.TryPatch(harmony);

        // Sabotage Tuning: per-sabotage cooldowns (+ per-use reduction) for all menu sabotages and
        // per-sabotage durations for the deadly ones (TOR Settings tab). All MapRoom / InfectedOverlay /
        // ShipStatus / MeetingHud / AmongUsClient patches are attribute-based (PatchAll); only the
        // options need explicit creation here, after TOR's CustomOptionHolder.Load().
        SabotageTuning.CreateOptions();

        // Meeting map ping: click the minimap during a meeting -> everyone sees a HerePoint
        // marker in the clicker's color (RPC 254). MapBehaviour/HandleRpc patches are
        // attribute-based (PatchAll); only the host option (1360) needs creation here.
        // The map language toggle (MapLanguageToggle) is patch-only - nothing to create.
        MeetingMapPing.CreateOptions();

        // Random impostor count (host rolls min..max at game start, stays secret), Spy unlock
        // at max >= 2, and the Jackal sidekick gating (refill / per-game chance, RPC 244).
        // SelectRoles / GetAdjustedNumImpostors / intro / HandleRpc patches are attribute-based
        // (PatchAll); TryPatch adds the reflection postfix on getRoleAssignmentData (Spy unlock).
        ImpostorCountRange.CreateOptions();
        ImpostorCountRange.TryPatch(harmony);

        // True Modifier Chances (option 1375, Modifier tab, default OFF): the modifier percentages
        // become real independent spawn chances instead of TOR's lottery tickets. TryPatch adds the
        // reflection patches on assignModifiers (roll + result log) and getSelectionForRoleId
        // (winner -> ensured, loser -> 0); setModifier/resetVariables/OnGameJoined are
        // attribute-based (PatchAll). Must be created AFTER TiebreakerMultiple/MultiModifiers -
        // it reads their quantities and switches their own multiply postfixes off while active.
        TrueModifierChances.CreateOptions();
        TrueModifierChances.TryPatch(harmony);

        // Multi-Jester (option 1376, Neutral tab): up to three Jesters, each of whom wins alone.
        // The role-identity/win patches are attribute-based (PatchAll); TryPatch adds the manual
        // postfix on TOR's internal ExileControllerWrapUpPatch.WrapUpPostfix (the win trigger).
        MultiJester.CreateOptions();
        MultiJester.TryPatch(harmony);

        // Newcomer kill shield (options 1380-1381, General tab): somebody playing with this host for
        // the first time cannot be killed before the first meeting of their first round. The host
        // decides who counts as new and hands the ids out over RPC 242; enforcement runs both
        // host-side (vanilla CheckMurder) and on each client (TOR's checkMuderAttempt). All patches
        // are attribute-based and picked up by PatchAll below.
        NewcomerShield.CreateOptions();

        // Anti Start Kill (options 1390-1391, General tab): the spawn area is a safe zone - no
        // kills or sidekicks until killer AND victim have each left it once; any meeting ends all
        // remaining protection. Host records spawns and broadcasts "left" events over module 241;
        // enforcement mirrors the newcomer shield (host CheckMurder + client checkMuderAttempt,
        // plus the jackalCreatesSidekick procedure). The shared painter UTSShieldOutlines shows
        // the green outline and cycles when a player stacks several shields. All patches are
        // attribute-based and picked up by PatchAll below.
        AntiStartKill.CreateOptions();

        // Both kill shields above gate TOR's targeting helper, which knows nothing about WHY a player
        // is being targeted. This frees the peaceful abilities (Medic, Shifter, Morphling, Tracker,
        // Deputy, Eraser, Arsonist, Pursuer) from both gates by marking their targeting methods, and
        // publishes the AppDomain contract the sibling mods use for the same purpose.
        ShieldPeaceGate.TryPatch(harmony);
        ShieldPeaceGate.RegisterContract();

        // Trapper log crash: a trap holding the id of a player who has left freezes the meeting for
        // the Trapper and for every ghost (TOR's StartMeeting prefix dereferences a null player).
        // TryPatch only resolves the reflection handles; both patches are attribute-based.
        TrapperMeetingCrash.TryPatch(harmony);

        // Close the bracket opened above: every option this mod owns is now known to UTSGate.
        UTSGate.EndOptionCapture();

        // Settings overlay (F1) and the lobby settings list: roles and modifiers in their own
        // colour, values in a column, 0% roles collapsed. SnapshotColors must run BEFORE the
        // localization below - the translated option names carry no <color> markup, so this is the
        // last moment TOR's own role colours can still be read off them.
        SettingsOverlayView.Bind(Config);
        SettingsOverlayView.SnapshotColors();
        SettingsOverlayView.TryPatch(harmony);

        // Localization engine: loads the string tables and mutates TOR's role/option strings
        // in place (LocalizationTOR). Must run AFTER every CreateOptions above so first-pass
        // originals are complete; the SetLanguage/GetString patches are attribute-based
        // (PatchAll below) and re-apply on every language switch.
        UTSLocalization.Initialize(Config);

        // All attribute-based [HarmonyPatch] classes in this assembly: VersionDisplayPatch,
        // the UsefulVersionHandshake patches (RPC 253 + lobby messages), and the gated Snitch
        // surface patches. Assembly-wide so nested patch classes are picked up too.
        harmony.PatchAll(typeof(UsefulTORStuffPlugin).Assembly);

        // Must follow PatchAll: the watchdog's canary postfix is attribute-based, so it only exists
        // once the line above ran, and the surface scan it logs wants the full patch set in place.
        DetourWatchdog.Initialize();

        // Local host-only web settings editor. Started after PatchAll so its HudManager.Update
        // pump patch is already installed. Loopback-only listener; host-gated writes.
        if (WebConfigEnabled.Value) {
            try { WebConfig.Start(WebConfigPort.Value); }
            catch (Exception ex) { Logger.LogError($"WebConfig start failed: {ex}"); }
        }

        // Self-updater: checks GitHub releases and offers an in-game update button.
        AddComponent<UsefulTORStuffUpdater>();

        // Der automatische Start-Check der Updater läuft jetzt los — als Drossel-Basis merken,
        // damit ein Öffnen des Mod Managers direkt nach dem Start keinen redundanten Re-Check
        // auslöst (zählt als „in der letzten Minute bereits geprüft“).
        ModManagerRegistry.MarkUpdateCheckNow();

        // Lobby-Passwortsperre: blockiert den Spielstart bis der Host das korrekte Passwort eingibt.
        AddComponent<LobbyPasswordGate>();

        // Mod-Manager UI Components: Button im Hauptmenü + Popup-UI.
        AddComponent<ModManagerButton>();
        AddComponent<ModManagerUI>();

        // Mod sync: the download queue and the "back to the lobby" main-menu button. The lobby
        // panel itself is created on demand (UTSModSyncUI), the inventory patches are
        // attribute-based and were picked up by PatchAll above.
        AddComponent<UTSModDownloader>();
        AddComponent<UTSRejoinButton>();
        AddComponent<UTSModSyncUI>();

        // Host-only lobby panel for the newcomer kill shield (who gets a free first round).
        AddComponent<NewcomerShieldUI>();

        // Memory heartbeat for the crash log (see CrashDiagnostics.cs).
        AddComponent<CrashDiagnosticsTicker>();

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
                { "RuntimeEnabled", true },
                // Live toggle in the Mod Manager: the mod sync only ever reads this at lobby time,
                // so flipping it takes effect immediately - no restart needed.
                { "ExtraToggle", ModSyncEnabled },
                { "ExtraToggleLabel", UTSLocalization.Tr("uts.modsync.title") }
            };
            ModManagerRegistry.RegisterMod(PluginGuid, modData);
        } catch (Exception ex) {
            Logger.LogError($"Failed to register {PluginName}: {ex}");
        }

        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    // ========================================================================
    // Disabled mode: the Mod Manager, and nothing else.
    //
    // This plugin owns the Mod Manager - the main-menu button, the UI, the registry. Switching this
    // mod off used to take all of that with it, and since the manager is also where every mod's
    // on/off switch lives, the only way back was editing the .cfg by hand. Unknown's Collection
    // already registers itself while disabled for the same reason; here it has to go one step
    // further and actually run the manager.
    //
    // What deliberately does NOT happen here: no Harmony.PatchAll, no CreateOptions, no RPC
    // registration, no localisation applied to TOR's strings. Nothing this branch does can change
    // anything in a round - it draws a menu and lets you flip switches that take effect after a
    // restart.
    // ========================================================================
    private void LoadModManagerOnly(ConfigEntry<bool> enabled)
    {
        try {
            var modManagerEnabled = Config.Bind("ModManager", "Enabled", true,
                "When enabled, update buttons move into the Mod Manager UI. When disabled, update buttons "
                + "appear at their original positions.");
            ModManagerRegistry.SetModManagerEnabled(modManagerEnabled.Value);
            ModManagerButtonX = Config.Bind("ModManager", "ButtonPositionX", 0.8f,
                "X position of the Mod Manager button (anchor point 0-1)");
            ModManagerButtonY = Config.Bind("ModManager", "ButtonPositionY", 0.21f,
                "Y position of the Mod Manager button (anchor point 0-1)");

            // The manager shows every mod's version line, so the shared display flag has to be set
            // even here.
            ShowTestVersionsConfig = Config.Bind("Version", "ShowTestVersions", false,
                "Show the 4th version component (the test-version number, e.g. v1.2.3.4) in mod version "
                + "lines. Stable builds (vX.Y.Z) are unaffected. Toggleable in the Mod Manager.");
            VersionDisplay.SetShowTestVersions(ShowTestVersionsConfig.Value);

            // Tables only - the full Initialize() would mutate TOR's role and option strings.
            UTSLocalization.InitializeDisplayOnly(Config);

            // The updater keeps working: a disabled mod must still be updatable, and the manager
            // reads its state to show what needs updating.
            AddComponent<UsefulTORStuffUpdater>();
            ModManagerRegistry.MarkUpdateCheckNow();

            AddComponent<ModManagerButton>();
            AddComponent<ModManagerUI>();

            // Register ourselves so the entry (and its switch) is in the list. RuntimeEnabled is
            // false: our patches never ran this session, so turning it back on needs a restart, and
            // the manager says so instead of pretending the mod is live.
            var modData = new System.Collections.Generic.Dictionary<string, object> {
                { "Guid", PluginGuid },
                { "Name", PluginName },
                { "Version", Version },
                { "RepositoryOwner", UsefulTORStuffUpdater.RepositoryOwner },
                { "RepositoryName", UsefulTORStuffUpdater.RepositoryName },
                { "ButtonColor", Color.green },
                { "Enabled", enabled },
                { "RuntimeEnabled", false }
            };
            ModManagerRegistry.RegisterMod(PluginGuid, modData);

            Logger.LogInfo($"{PluginName} v{PluginVersion}: Mod Manager loaded, all game features off.");
        } catch (Exception ex) {
            Logger.LogError($"Mod-Manager-only load failed: {ex}");
        }
    }

    // ========================================================================
    // RPC collision watchdog: reads TOR's internal CustomRPC enum via reflection and warns if TOR
    // ever grew into the byte range the DaUnknown mods reserved for themselves. Purely diagnostic -
    // nothing is changed, the log line just tells us to move a channel BEFORE players hit the
    // mis-parse in a live round. Runs before TORAssembly is set (PatchBloodyThrottle), so it
    // resolves the assembly itself.
    // ========================================================================
    private void WarnOnRpcIdCollisions()
    {
        try {
            var tor = TORAssembly ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
            var rpcEnum = tor?.GetType("TheOtherRoles.CustomRPC");
            if (rpcEnum == null || !rpcEnum.IsEnum) {
                Logger.LogWarning("[UTSRpc][DIAG] TOR's CustomRPC enum not found — RPC collision watchdog skipped.");
                return;
            }

            int highest = -1;
            var collisions = new List<string>();
            foreach (var name in Enum.GetNames(rpcEnum)) {
                int value = Convert.ToInt32(Enum.Parse(rpcEnum, name));
                if (value > highest) highest = value;
                // >= 200: TOR has entered the block the DaUnknown mods reserved for themselves.
                // == 240 / == 230: a direct hit on our channel / Unknown's Collection's channel.
                if (value >= 200 || value == UTSRpc.CallId || value == 230)
                    collisions.Add($"{name}={value}");
            }

            if (collisions.Count > 0)
                Logger.LogWarning(
                    "[UTSRpc][DIAG] TOR's CustomRPC now uses ids in the range reserved by the DaUnknown mods: "
                    + string.Join(", ", collisions)
                    + $". Our channel is {UTSRpc.CallId} (Unknown's Collection uses 230) — move the affected "
                    + "channel before the next release or RPC payloads will be mis-parsed.");

            Logger.LogInfo($"[UTSRpc][DIAG] channel {UTSRpc.CallId}; highest TOR CustomRPC id is {highest}.");
        } catch (Exception ex) {
            Logger.LogWarning($"[UTSRpc][DIAG] RPC collision watchdog failed: {ex.Message}");
        }
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

        // P1.1: Bei Runden-Reset leeren. Sonst überlebt die Karte über Spiele hinweg, und da
        // PlayerIds wiederverwendet werden, würde ein blutender Spieler nahe der letzten
        // Drop-Position des Vorspiels seine ersten Drops fälschlich überspringen. Aufgerufen aus
        // dem resetVariables-Patch unten.
        public static void ClearLastDropPositions() => _lastDropPos.Clear();

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

    // P1.1: Postfix auf TORs RPCProcedure.resetVariables (per Runde, auf allen Clients), um die
    // Drop-Positions-Karte des Bloody-Throttle zu leeren. Per Reflection aufgelöst wie die
    // anderen TOR-Patches; degradiert zum No-op (Log-Warnung), falls die Methode fehlt.
    public static class BloodyResetVariablesPatch
    {
        public static void Postfix() => BloodyThrottlePatch.ClearLastDropPositions();
    }

    // ========================================================================
    // Bloody killer-map fix: TOR's RPCProcedure.bloody records the bloody victim with
    //   if (Bloody.active.ContainsKey(killer)) return;
    //   Bloody.active.Add(killer, duration);
    //   Bloody.bloodyKillerMap.Add(killer, victim);
    // bloodyKillerMap is only ever cleared at game start, and uses Add (never an
    // overwrite). So once a killer has bled one bloody victim, bloodyKillerMap[killer]
    // stays pinned to that FIRST victim for the rest of the game — every later blood
    // trail (whose color comes from that mapped victim) renders in the first victim's
    // color. The early ContainsKey return also drops a second victim entirely while the
    // first trail is still active.
    //
    // Fix: replace the body via prefix — refresh the timer and OVERWRITE the victim with
    // the indexer, so each kill makes the trail track the latest victim. RPCProcedure.bloody
    // is the only writer of these maps and runs on every client (local kill + RPC handler),
    // so patching it here fixes our own view (host) and any client running this mod.
    // ========================================================================

    // P1.1: Leert die Bloody-Throttle-Drop-Karte bei jedem Runden-Reset, damit sie nicht über
    // Spiele hinweg leakt (siehe BloodyThrottlePatch.ClearLastDropPositions).
    private void PatchBloodyResetVariables(Harmony harmony)
    {
        try
        {
            if (TORAssembly == null)
            {
                Logger.LogWarning("TheOtherRoles assembly not found — Bloody throttle reset disabled.");
                return;
            }

            var rpcProcedureType = TORAssembly.GetType("TheOtherRoles.RPCProcedure")
                ?? TORAssembly.GetTypes().FirstOrDefault(t => t.Name == "RPCProcedure");
            var resetMethod = rpcProcedureType?.GetMethod("resetVariables",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (resetMethod == null)
            {
                Logger.LogWarning("RPCProcedure.resetVariables not found — Bloody throttle reset disabled.");
                return;
            }

            harmony.Patch(resetMethod,
                postfix: new HarmonyMethod(typeof(BloodyResetVariablesPatch), nameof(BloodyResetVariablesPatch.Postfix)));
            Logger.LogInfo("Patched resetVariables() — Bloody throttle drop map cleared each round.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to patch resetVariables for Bloody throttle: {ex}");
        }
    }

    private void PatchBloodyKillerMap(Harmony harmony)
    {
        try
        {
            if (TORAssembly == null)
            {
                Logger.LogError("TheOtherRoles assembly not found — Bloody killer-map fix disabled.");
                return;
            }

            var rpcType = TORAssembly.GetType("TheOtherRoles.RPCProcedure")
                ?? TORAssembly.GetTypes().FirstOrDefault(t => t.Name == "RPCProcedure");
            var bloodyType = TORAssembly.GetType("TheOtherRoles.Bloody")
                ?? TORAssembly.GetTypes().FirstOrDefault(t => t.Name == "Bloody");
            if (rpcType == null || bloodyType == null)
            {
                Logger.LogWarning("RPCProcedure or Bloody type not found — Bloody killer-map fix disabled.");
                return;
            }

            var method = rpcType.GetMethod("bloody",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(byte), typeof(byte) }, null);
            if (method == null)
            {
                Logger.LogWarning("RPCProcedure.bloody(byte, byte) not found — Bloody killer-map fix disabled.");
                return;
            }

            const BindingFlags staticField = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            BloodyKillerMapPatch.ActiveField = bloodyType.GetField("active", staticField);
            BloodyKillerMapPatch.MapField = bloodyType.GetField("bloodyKillerMap", staticField);
            BloodyKillerMapPatch.DurationField = bloodyType.GetField("duration", staticField);
            if (BloodyKillerMapPatch.ActiveField == null || BloodyKillerMapPatch.MapField == null
                || BloodyKillerMapPatch.DurationField == null)
            {
                Logger.LogWarning("Bloody.active/bloodyKillerMap/duration field(s) not found — Bloody killer-map fix disabled.");
                return;
            }

            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(BloodyKillerMapPatch), nameof(BloodyKillerMapPatch.Prefix)));
            Logger.LogInfo("Patched RPCProcedure.bloody — bloody trail now tracks the latest victim.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to patch RPCProcedure.bloody: {ex}");
        }
    }

    public static class BloodyKillerMapPatch
    {
        public static FieldInfo ActiveField;       // Dictionary<byte, float>
        public static FieldInfo MapField;          // Dictionary<byte, byte>
        public static FieldInfo DurationField;     // float

        // __0 = killerPlayerId, __1 = bloodyPlayerId (the killed bloody-modifier victim).
        // Returning false replaces TOR's buggy Add/early-return logic.
        public static bool Prefix(byte __0, byte __1)
        {
            try
            {
                var active = ActiveField.GetValue(null) as Dictionary<byte, float>;
                var map = MapField.GetValue(null) as Dictionary<byte, byte>;
                if (active == null || map == null) return true; // let the original run

                float duration = Convert.ToSingle(DurationField.GetValue(null));
                active[__0] = duration; // set/refresh timer (indexer, not Add)
                map[__0] = __1;         // overwrite victim with the latest kill (indexer, not Add)
                return false;           // skip the buggy original
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Bloody killer-map fix failed: {ex}");
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
        private static string cachedLine;
        private static string cachedTemplate;
        private static bool cachedShowTest;

        public static void Postfix(PingTracker __instance)
        {
            if (__instance == null || __instance.text == null) return;
            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text)) return;

            // PERF: the line only changes at a language switch or when the test-version toggle
            // flips. The previous build re-formatted it every frame (Version.ToString + a
            // string.Format) just to compare the result against the cache - three strings per
            // frame for nothing. The translated TEMPLATE is a stable dictionary instance until the
            // tables reload, so a reference compare on it (plus the toggle) is the whole change
            // detector, and it stays self-healing without a LanguageApplied subscription.
            string template = UTSLocalization.Tr("uts.plugin.version_line");
            bool showTest = VersionDisplay.ShowTestVersions();
            if (cachedLine == null || !ReferenceEquals(template, cachedTemplate) || showTest != cachedShowTest)
            {
                cachedTemplate = template;
                cachedShowTest = showTest;
                // The localized string still carries this mod's own old <link> wrapper (its click
                // used to toggle the credit line by itself); UnknownsCollective.Render() now
                // supplies its own wrapper and click handling, so strip the old one here rather
                // than touching every locale file's uts.plugin.version_line entry.
                string rawLine = UTSLocalization.Tr("uts.plugin.version_line", VersionDisplay.Format(Version));
                const string linkOpen = "<link=\"usefulTORStuffCredits\">";
                const string linkClose = "</link>";
                string line = rawLine;
                if (line.StartsWith(linkOpen) && line.EndsWith(linkClose))
                    line = line.Substring(linkOpen.Length, line.Length - linkOpen.Length - linkClose.Length);
                cachedLine = line;
            }

            UnknownsCollective.Contribute(PluginGuid, cachedLine);
            text = UnknownsCollective.Render(__instance.text, text);

            // PERF: TextMeshPro rebuilds its mesh on EVERY assignment to .text, even when the
            // string is identical - the setter marks the text dirty without comparing. Six of our
            // mods write this same field one after another each frame and
            // UnknownsCollective.Render is idempotent within a frame, so at most the first of
            // those writes carries a change. See the same guard in the other five plugins.
            if (!string.Equals(__instance.text.text, text, StringComparison.Ordinal))
                __instance.text.text = text;
        }
    }
}
