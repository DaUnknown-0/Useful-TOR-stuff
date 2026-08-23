// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SabotageTuning - per-sabotage cooldowns and per-sabotage durations ("Sabotage Tuning" section
 * on the TOR Settings tab). Master toggle defaults OFF, so without it everything stays vanilla.
 *
 * Vanilla Among Us has a SINGLE shared sabotage cooldown (SabotageSystemType.Timer) that gates
 * every sabotage at once. We replace that with an INDEPENDENT cooldown timer per logical sabotage
 * type. While no sabotage is active each type's timer ticks down independently; when ANY sabotage
 * ends, ALL timers are reset to their respective maximum (this is the requested behaviour).
 *
 * Three features:
 *   1. Per-type cooldown (configurable, applies to all impostors) - for every menu sabotage.
 *   2. Per-type cooldown reduction: each use of a type lowers THAT type's cooldown by X seconds
 *      (X per type), floored at the configurable Minimum Cooldown (default 10s), reset every meeting.
 *   3. Per-type duration for the deadly sabotages only (Reactor/Meltdown, Oxygen, Airship Crash);
 *      the non-deadly ones (Comms, Lights) run until fixed and have no duration.
 *
 * How it hooks (all member names verified against AmongUs.GameLibs.Steam 2024.10.29):
 *   - Gating: prefix on MapRoom.SabotageReactor/SabotageOxygen/SabotageComms/SabotageLights/
 *     SabotageHeli (the per-room sabotage entry points fired by the menu buttons). Returning false
 *     blocks the sabotage on the clicking impostor while that type's per-type timer is still running.
 *   - Menu usability: postfix on InfectedOverlay.CanUseSabotage (getter). Vanilla gates the whole
 *     menu on the shared SabotageSystemType.Timer; we force it usable whenever no sabotage is active
 *     (and doors aren't preventing it), so our per-type prefixes alone decide what is allowed.
 *   - Cooldown ticking / reset-on-end / one-time init / global use-counting / host shared-timer
 *     neutralisation: postfix on ShipStatus.Update. Counting happens on the active-edge by probing
 *     each system's IsActive (synced, identical on every client), so the reduction is GLOBAL across
 *     impostors rather than tied to whoever clicked. The host's shared SabotageSystemType.Timer is
 *     forced to 0 while idle because the host validates incoming sabotages against it.
 *   - Visuals: postfix on InfectedOverlay.Update greys each room's special icon per its per-type timer
 *     via MapRoom.SetSpecialActive(perc).  [perc semantics: cooldown fraction; verify look in-game]
 *     Optional per-icon seconds readout ("Show Sabotage Cooldown Seconds", off by default): the
 *     proportional greying alone doesn't say how many seconds a given type still needs once several
 *     independent timers are running at once, so a small TMP label is created once per room (child of
 *     MapRoom.special) and shown/hidden/updated straight off the same timer[] array the tick postfix
 *     already maintains.
 *   - Reductions reset: postfix on MeetingHud.Start.
 *   - Game reset: postfix on AmongUsClient.OnGameEnd.
 *
 * Chance modifier compatibility: the TOR Chance modifier has its own sabotage-cooldown override that
 * also drives the shared timer. The two are mutually exclusive - while Sabotage Tuning is enabled we
 * disable the Chance sabotage override (reflection, no build dependency; Sabotage Tuning takes
 * precedence). When Sabotage Tuning is off, the Chance override behaves exactly as before.
 *
 * Durations (feature 3) are HOST-AUTHORITATIVE: on the sabotage active-edge the host stamps the
 * configured duration onto the just-activated deadly sabotage's Countdown and serialises it to every
 * client. LifeSupp.Countdown is a public field; Reactor/Heli expose only a non-public Countdown
 * setter (HeliSabotageSystem.CharlesDuration is a const), so those are set through that setter via
 * reflection. Durations apply to all clients as long as the host has the mod.
 *
 * Cooldowns (features 1+2) are enforced CLIENT-SIDE per impostor (the sabotage menu is local), so
 * they only affect impostors who run the mod - same gating reality as the Snitch fix. A vanilla
 * impostor keeps the shared vanilla cooldown.
 *
 * Reactor and Laboratory (Polus) are the same logical type (one ReactorSystemType, different map).
 * Map differences are automatic: ShipStatus.Systems only contains the systems present on the map,
 * and the menu only shows rooms that exist, so unused types simply never fire.
 *
 * IDs 1330-1345 used here (1320-1323 are SpyExtras). Keep plugin-wide unique.
 */

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SabotageTuning {
        public enum SabType { Reactor = 0, Oxygen = 1, Comms = 2, Lights = 3, Heli = 4 }
        private const int N = 5;
        private const float MIN_COOLDOWN = 10f;

        // Master toggle (header) + per-type cooldown / reduction options. Durations only for the
        // three deadly types.
        public static CustomOption Enabled;
        private static CustomOption minCooldownOpt;                           // reduction floor (global)
        private static CustomOption showSecondsOpt;                           // per-icon cooldown seconds readout
        private static readonly CustomOption[] cdOpt = new CustomOption[N];   // base cooldown
        private static readonly CustomOption[] redOpt = new CustomOption[N];  // reduction per use
        private static CustomOption reactorDur, oxygenDur, heliDur;           // deadly durations

        // Runtime state (per logical type). usage drives the reduction; timer is the live countdown.
        private static readonly float[] timer = new float[N];
        private static readonly int[] usage = new int[N];
        private static bool prevActive;
        private static bool gameInit;

        // Cooldown-seconds labels (one per logical type, lazily created next to MapRoom.special).
        // See OverlayUpdatePatch / UpdateCooldownText.
        private static readonly TMPro.TextMeshPro[] cdText = new TMPro.TextMeshPro[N];

        // AUDIT-2026-08-16: last Mathf.CeilToInt(remaining) actually written to cdText[i], per type.
        // -1 means "nothing shown yet / currently hidden", which never collides with a real second
        // count and so always forces a fresh text push the next time that type's label is shown.
        private static readonly int[] lastShownSecs = InitLastShownSecs();
        private static int[] InitLastShownSecs() {
            var a = new int[N];
            for (int i = 0; i < N; i++) a[i] = -1;
            return a;
        }

        // Non-public Countdown setters for the deadly sabotages whose duration we override (resolved
        // once via reflection; LifeSupp.Countdown is a public field and needs none).
        private static bool settersResolved;
        private static MethodInfo reactorCountdownSetter;
        private static MethodInfo heliCountdownSetter;

        // Chance modifier interop (reflection, no hard dependency). When our Sabotage Tuning is on we
        // disable the Chance modifier's sabotage-cooldown override (they both drive the shared timer and
        // would fight); Sabotage Tuning takes precedence. See SuppressChanceSabotage.
        private static bool chanceResolved;
        private static FieldInfo chanceSabEnabledField;
        private static bool warnedConflict;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(
                    1330, Types.General, "Sabotage Tuning", false, null, true);
                UTSLocalization.BindOptionTitle(Enabled, "uts.sabotagetuning.enabled");

                minCooldownOpt = CustomOption.Create(1344, Types.General, "Minimum Cooldown (Reduction Floor)", 10f, 0f, 30f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(minCooldownOpt, "uts.sabotagetuning.min_cooldown");

                showSecondsOpt = CustomOption.Create(1345, Types.General, "Show Sabotage Cooldown Seconds", false, Enabled);
                UTSLocalization.BindOptionTitle(showSecondsOpt, "uts.sabotagetuning.show_cooldown_seconds");

                cdOpt[(int)SabType.Reactor] = CustomOption.Create(1331, Types.General, "Reactor/Meltdown Cooldown", 30f, 10f, 60f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(cdOpt[(int)SabType.Reactor], "uts.sabotagetuning.reactor_cooldown");
                redOpt[(int)SabType.Reactor] = CustomOption.Create(1332, Types.General, "Reactor/Meltdown Cooldown Reduction per Use", 0f, 0f, 15f, 0.5f, Enabled);
                UTSLocalization.BindOptionTitle(redOpt[(int)SabType.Reactor], "uts.sabotagetuning.reactor_cooldown_reduction");
                reactorDur = CustomOption.Create(1333, Types.General, "Reactor/Meltdown Duration", 30f, 10f, 90f, 5f, Enabled);
                UTSLocalization.BindOptionTitle(reactorDur, "uts.sabotagetuning.reactor_duration");

                cdOpt[(int)SabType.Oxygen] = CustomOption.Create(1334, Types.General, "Oxygen Cooldown", 30f, 10f, 60f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(cdOpt[(int)SabType.Oxygen], "uts.sabotagetuning.oxygen_cooldown");
                redOpt[(int)SabType.Oxygen] = CustomOption.Create(1335, Types.General, "Oxygen Cooldown Reduction per Use", 0f, 0f, 15f, 0.5f, Enabled);
                UTSLocalization.BindOptionTitle(redOpt[(int)SabType.Oxygen], "uts.sabotagetuning.oxygen_cooldown_reduction");
                oxygenDur = CustomOption.Create(1336, Types.General, "Oxygen Duration", 30f, 10f, 90f, 5f, Enabled);
                UTSLocalization.BindOptionTitle(oxygenDur, "uts.sabotagetuning.oxygen_duration");

                cdOpt[(int)SabType.Comms] = CustomOption.Create(1337, Types.General, "Communications Cooldown", 30f, 10f, 60f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(cdOpt[(int)SabType.Comms], "uts.sabotagetuning.comms_cooldown");
                redOpt[(int)SabType.Comms] = CustomOption.Create(1338, Types.General, "Communications Cooldown Reduction per Use", 0f, 0f, 15f, 0.5f, Enabled);
                UTSLocalization.BindOptionTitle(redOpt[(int)SabType.Comms], "uts.sabotagetuning.comms_cooldown_reduction");

                cdOpt[(int)SabType.Lights] = CustomOption.Create(1339, Types.General, "Lights Cooldown", 30f, 10f, 60f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(cdOpt[(int)SabType.Lights], "uts.sabotagetuning.lights_cooldown");
                redOpt[(int)SabType.Lights] = CustomOption.Create(1340, Types.General, "Lights Cooldown Reduction per Use", 0f, 0f, 15f, 0.5f, Enabled);
                UTSLocalization.BindOptionTitle(redOpt[(int)SabType.Lights], "uts.sabotagetuning.lights_cooldown_reduction");

                cdOpt[(int)SabType.Heli] = CustomOption.Create(1341, Types.General, "Airship Crash Cooldown", 30f, 10f, 60f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(cdOpt[(int)SabType.Heli], "uts.sabotagetuning.heli_cooldown");
                redOpt[(int)SabType.Heli] = CustomOption.Create(1342, Types.General, "Airship Crash Cooldown Reduction per Use", 0f, 0f, 15f, 0.5f, Enabled);
                UTSLocalization.BindOptionTitle(redOpt[(int)SabType.Heli], "uts.sabotagetuning.heli_cooldown_reduction");
                heliDur = CustomOption.Create(1343, Types.General, "Airship Crash Duration", 30f, 10f, 120f, 5f, Enabled);
                UTSLocalization.BindOptionTitle(heliDur, "uts.sabotagetuning.heli_duration");

                UsefulTORStuffPlugin.Logger?.LogInfo("[SabotageTuning] Options created under TOR Settings.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SabotageTuning] CreateOptions failed: {e}");
            }
        }

        private static bool Active => Enabled != null && UTSGate.Bool(Enabled);

        // Cross-plugin sabotage block from Unknown's Collection's Siphoner (AppDomain shared data, no hard
        // dependency). While the Siphoner is draining a nearby impostor it publishes an absolute Time.time
        // until which sabotage must be suppressed; we honour it here instead of letting our idle branch pin
        // the shared timer back to ready. Mirrors how we suppress the Chance modifier above.
        private const string SiphonerBlockKey = "TORMods.SiphonerSabotageBlockUntil";
        private static bool SiphonerBlockActive() {
            try { return AppDomain.CurrentDomain.GetData(SiphonerBlockKey) is float until && Time.time < until; }
            catch { return false; }
        }
        private static float SiphonerBlockRemaining() {
            try {
                return AppDomain.CurrentDomain.GetData(SiphonerBlockKey) is float until
                    ? Mathf.Max(0.1f, until - Time.time) : 0.1f;
            } catch { return 0.1f; }
        }

        // Cooldown maximum for a type after applying its accumulated reduction, floored at the
        // configurable minimum cooldown. The floor is clamped to the base so it can never raise a
        // cooldown above its configured value (only stop the reduction from going lower).
        private static float CurrentMax(SabType t) {
            int i = (int)t;
            float baseMax = cdOpt[i] != null ? UTSGate.Num(cdOpt[i]) : 30f;
            float red = redOpt[i] != null ? UTSGate.Num(redOpt[i]) : 0f;
            float floor = Mathf.Min(minCooldownOpt != null ? UTSGate.Num(minCooldownOpt) : MIN_COOLDOWN, baseMax);
            return Mathf.Max(floor, baseMax - usage[i] * red);
        }

        private static void ResetTimersToMax() {
            for (int i = 0; i < N; i++) timer[i] = CurrentMax((SabType)i);
        }

        // Fresh game / fresh meeting: clear reduction counters and put every type on full cooldown.
        private static void ResetAll() {
            for (int i = 0; i < N; i++) usage[i] = 0;
            ResetTimersToMax();
        }

        private static bool TryMap(SystemTypes st, out SabType t) {
            switch (st) {
                case SystemTypes.Reactor:
                case SystemTypes.Laboratory: t = SabType.Reactor; return true;
                case SystemTypes.LifeSupp:   t = SabType.Oxygen;  return true;
                case SystemTypes.Comms:      t = SabType.Comms;   return true;
                case SystemTypes.Electrical: t = SabType.Lights;  return true;
                case SystemTypes.HeliSabotage: t = SabType.Heli;  return true;
                default: t = SabType.Reactor; return false;
            }
        }

        private static SabotageSystemType GetSab(ShipStatus ship) {
            if (ship == null || ship.Systems == null) return null;
            ISystemType sys;
            if (!ship.Systems.TryGetValue(SystemTypes.Sabotage, out sys) || sys == null) return null;
            return sys.TryCast<SabotageSystemType>();
        }

        private static ISystemType GetRaw(ShipStatus ship, SystemTypes st) {
            if (ship == null || ship.Systems == null) return null;
            ISystemType sys;
            if (!ship.Systems.TryGetValue(st, out sys)) return null;
            return sys;
        }

        // Host-only, called on the sabotage active-edge: set the just-activated deadly sabotage's
        // Countdown to the configured duration. The host serialises Countdown to every client, so the
        // duration applies for all. LifeSupp.Countdown is a public field; Reactor/Heli expose a
        // non-public Countdown setter (HeliSabotageSystem.CharlesDuration is a const and cannot be
        // changed), so those are driven through the setter via reflection.
        private static void ResolveCountdownSetters() {
            if (settersResolved) return;
            settersResolved = true;
            try { reactorCountdownSetter = typeof(ReactorSystemType).GetProperty("Countdown")?.GetSetMethod(true); } catch { }
            try { heliCountdownSetter = typeof(HeliSabotageSystem).GetProperty("Countdown")?.GetSetMethod(true); } catch { }
        }

        private static void ApplyDeadlyDurations(ShipStatus ship) {
            ResolveCountdownSetters();

            var reactorRaw = GetRaw(ship, SystemTypes.Reactor) ?? GetRaw(ship, SystemTypes.Laboratory);
            var reactor = reactorRaw != null ? reactorRaw.TryCast<ReactorSystemType>() : null;
            if (reactor != null && reactor.IsActive && reactorDur != null && reactorCountdownSetter != null) {
                try { reactorCountdownSetter.Invoke(reactor, new object[] { UTSGate.Num(reactorDur) }); } catch { }
            }

            var o2 = GetRaw(ship, SystemTypes.LifeSupp)?.TryCast<LifeSuppSystemType>();
            if (o2 != null && o2.IsActive && oxygenDur != null) o2.Countdown = UTSGate.Num(oxygenDur);

            var heli = GetRaw(ship, SystemTypes.HeliSabotage)?.TryCast<HeliSabotageSystem>();
            if (heli != null && heli.IsActive && heliDur != null && heliCountdownSetter != null) {
                try { heliCountdownSetter.Invoke(heli, new object[] { UTSGate.Num(heliDur) }); } catch { }
            }
        }

        // Called from the SabotageX prefixes: block if this type is still on cooldown (client-side gate
        // for the clicking impostor). Counting is done globally on activation (CountActivation) so the
        // reduction applies for all impostors, not just whoever clicked.
        private static bool TryTrigger(SabType t) {
            if (SiphonerBlockActive()) return false; // Siphoner drain blocks every sabotage (even if our tuning is off)
            if (!Active) return true;             // feature off -> vanilla
            return timer[(int)t] <= 0f;           // on cooldown -> block (false), else allow (true)
        }

        // ---- system "is this sabotage currently active" probes (synced state, same on every client) ----
        private static bool ReactorActive(ShipStatus s) {
            var raw = GetRaw(s, SystemTypes.Reactor) ?? GetRaw(s, SystemTypes.Laboratory);
            var sys = raw != null ? raw.TryCast<ReactorSystemType>() : null;
            return sys != null && sys.IsActive;
        }
        private static bool OxygenActive(ShipStatus s) {
            var raw = GetRaw(s, SystemTypes.LifeSupp);
            var sys = raw != null ? raw.TryCast<LifeSuppSystemType>() : null;
            return sys != null && sys.IsActive;
        }
        private static bool CommsActive(ShipStatus s) {
            var raw = GetRaw(s, SystemTypes.Comms);
            var sys = raw != null ? raw.TryCast<HudOverrideSystemType>() : null;
            return sys != null && sys.IsActive; // note: Mira HQ comms uses a different system (not counted)
        }
        private static bool LightsActive(ShipStatus s) {
            var raw = GetRaw(s, SystemTypes.Electrical);
            var sys = raw != null ? raw.TryCast<SwitchSystem>() : null;
            return sys != null && sys.IsActive;
        }
        private static bool HeliActive(ShipStatus s) {
            var raw = GetRaw(s, SystemTypes.HeliSabotage);
            var sys = raw != null ? raw.TryCast<HeliSabotageSystem>() : null;
            return sys != null && sys.IsActive;
        }

        // A sabotage just started: count the use of whichever type became active (observed identically
        // on every client), lowering that type's next cooldown via CurrentMax.
        private static void CountActivation(ShipStatus s) {
            if (ReactorActive(s)) usage[(int)SabType.Reactor]++;
            if (OxygenActive(s))  usage[(int)SabType.Oxygen]++;
            if (CommsActive(s))   usage[(int)SabType.Comms]++;
            if (LightsActive(s))  usage[(int)SabType.Lights]++;
            if (HeliActive(s))    usage[(int)SabType.Heli]++;
        }

        // Mutual exclusion with the Chance modifier's sabotage-cooldown override: while Sabotage Tuning
        // is active, force TOR_ChanceModifier.Chance.sabotageEnabled = false so its HudManager.Update
        // patch backs off (both otherwise drive the shared SabotageSystemType.Timer). Resolved via
        // reflection so there is no build dependency and it is a no-op if the Chance mod is absent.
        private static void SuppressChanceSabotage() {
            if (!chanceResolved) {
                chanceResolved = true;
                try {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                        Type t = null;
                        try { t = asm.GetType("TOR_ChanceModifier.Chance", false); } catch { }
                        if (t != null) {
                            chanceSabEnabledField = t.GetField("sabotageEnabled", BindingFlags.Public | BindingFlags.Static);
                            break;
                        }
                    }
                } catch { }
            }
            if (chanceSabEnabledField == null) return;
            try {
                if ((bool)chanceSabEnabledField.GetValue(null)) {
                    chanceSabEnabledField.SetValue(null, false);
                    if (!warnedConflict) {
                        warnedConflict = true;
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[SabotageTuning] Sabotage Tuning is enabled -> disabling the Chance modifier's sabotage cooldown override (mutually exclusive; Sabotage Tuning takes precedence).");
                    }
                }
            } catch { }
        }

        // Optional numeric readout next to a room's icon (Show Sabotage Cooldown Seconds). Created
        // lazily, once per SabType, as a child of that type's MapRoom.special icon so it inherits the
        // icon's position/layer/sorting automatically; reused afterwards (only text/visibility change
        // per frame). Unity's overloaded == null also catches a destroyed object, which is what makes
        // the cached reference safe across a HudManager/scene change - a stale entry is simply
        // rebuilt under the new icon instead of being dereferenced.
        private static void UpdateCooldownText(SabType t, MapRoom r, bool show) {
            int i = (int)t;
            float remaining = timer[i];
            if (!show || remaining <= 0f) {
                if (cdText[i] != null) cdText[i].gameObject.SetActive(false);
                // AUDIT-2026-08-16: hidden -> next time this type is shown it must repaint unconditionally,
                // even if the displayed second count happens to match whatever was last drawn.
                lastShownSecs[i] = -1;
                return;
            }

            if (cdText[i] == null) {
                var icon = r.special;
                var go = new GameObject("UTSSabCooldownText") { layer = icon.gameObject.layer };
                go.transform.SetParent(icon.transform, false);
                go.transform.localPosition = new Vector3(0f, -0.5f, -0.02f); // just under the icon
                var txt = go.AddComponent<TMPro.TextMeshPro>();
                txt.fontSize = 2.2f;
                txt.alignment = TMPro.TextAlignmentOptions.Center;
                txt.enableWordWrapping = false;
                txt.color = Color.white;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) { mr.sortingLayerID = icon.sortingLayerID; mr.sortingOrder = icon.sortingOrder + 1; }
                cdText[i] = txt;
                lastShownSecs[i] = -1; // freshly (re)built label, force the first text push below
            }

            cdText[i].gameObject.SetActive(true);

            // AUDIT-2026-08-16 (perf): the displayed, rounded-up second count only changes once per
            // second, so only touch .text (string concat + TMP rebuild) when it actually changed.
            int secs = Mathf.CeilToInt(remaining);
            if (secs != lastShownSecs[i]) {
                // ASCII only: digits plus "s" - the HUD TMP font has no glyphs beyond that (see CLAUDE.md).
                cdText[i].text = secs.ToString() + "s";
                lastShownSecs[i] = secs;
            }
        }

        // ---- Patches (attribute-based; picked up by harmony.PatchAll in UsefulTORStuffPlugin) ----

        [HarmonyPatch(typeof(MapRoom), nameof(MapRoom.SabotageReactor))]
        private static class SabReactorPatch { private static bool Prefix() => TryTrigger(SabType.Reactor); }

        [HarmonyPatch(typeof(MapRoom), nameof(MapRoom.SabotageOxygen))]
        private static class SabOxygenPatch { private static bool Prefix() => TryTrigger(SabType.Oxygen); }

        [HarmonyPatch(typeof(MapRoom), nameof(MapRoom.SabotageComms))]
        private static class SabCommsPatch { private static bool Prefix() => TryTrigger(SabType.Comms); }

        [HarmonyPatch(typeof(MapRoom), nameof(MapRoom.SabotageLights))]
        private static class SabLightsPatch { private static bool Prefix() => TryTrigger(SabType.Lights); }

        [HarmonyPatch(typeof(MapRoom), nameof(MapRoom.SabotageHeli))]
        private static class SabHeliPatch { private static bool Prefix() => TryTrigger(SabType.Heli); }

        // Force the menu usable whenever no sabotage is active, ignoring the shared vanilla timer; the
        // per-type prefixes do the real gating.
        [HarmonyPatch(typeof(InfectedOverlay), nameof(InfectedOverlay.CanUseSabotage), MethodType.Getter)]
        private static class CanUseSabotagePatch {
            private static void Postfix(InfectedOverlay __instance, ref bool __result) {
                if (!Active) return;
                try {
                    var sab = __instance.sabSystem;
                    if (sab == null) return;
                    __result = !sab.AnyActive && !__instance.DoorsPreventingSabotage && !SiphonerBlockActive();
                } catch { }
            }
        }

        // Cooldown ticking + reset-on-sabotage-end + one-time per-game init.
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FixedUpdate))]
        private static class ShipStatusTickPatch {
            private static void Postfix(ShipStatus __instance) {
                if (!Active) return;
                try {
                    var sab = GetSab(__instance);
                    if (sab == null) return;

                    if (!gameInit) { ResetAll(); gameInit = true; }

                    bool active = sab.AnyActive;
                    if (active && !prevActive) {
                        // A sabotage just started -> count the use of the activated type (global) and,
                        // on the host, stamp the configured duration onto the deadly sabotage.
                        CountActivation(__instance);
                        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
                            ApplyDeadlyDurations(__instance);
                    }
                    if (active) {
                        // Sabotage running: timers frozen; leave the shared timer alone.
                    } else {
                        // Neutralise the shared vanilla cooldown on the host so its UpdateSystem never
                        // rejects a sabotage that our per-type gate allows (the host validates against
                        // sab.Timer, which only needs to be <= 0). Our per-type prefixes are the only gate.
                        //
                        // Use a small NEGATIVE sentinel rather than exactly 0f: vanilla lets this shared
                        // timer run negative while idle, and TOR's Jackal/Sidekick lights-sabotage button
                        // (Buttons.cs, jackalAndSidekickSabotageLightsButton) re-arms itself every frame via
                        //   if (Helpers.sabotageTimer() > Timer) Timer = Helpers.sabotageTimer() + 5f;
                        // Pinning the shared timer to exactly 0 made "0 > (slightly negative button timer)"
                        // true forever, so that button looped 5->0 and never became usable on the host.
                        // A negative value keeps host validation happy while never tripping that comparison.
                        // While a Siphoner block is active keep the shared timer positive so host
                        // validation also rejects sabotage; otherwise pin it idle as before.
                        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
                            sab.Timer = SiphonerBlockActive() ? SiphonerBlockRemaining() : -1f;

                        if (prevActive) {
                            // A sabotage just ended -> every type's timer back to its maximum.
                            ResetTimersToMax();
                        } else {
                            float dt = Time.fixedDeltaTime;
                            for (int i = 0; i < N; i++)
                                if (timer[i] > 0f) timer[i] = Mathf.Max(0f, timer[i] - dt);
                        }
                    }
                    prevActive = active;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SabotageTuning] Update failed: {e}");
                }
            }
        }

        // Visual: grey each room's special icon by its per-type cooldown. Runs after vanilla's own
        // Update (which thinks everything is ready because we forced CanUseSabotage), so this wins.
        [HarmonyPatch(typeof(InfectedOverlay), nameof(InfectedOverlay.Update))]
        private static class OverlayUpdatePatch {
            private static void Postfix(InfectedOverlay __instance) {
                if (!Active) return;
                try {
                    var rooms = __instance.rooms;
                    if (rooms == null) return;
                    bool showSeconds = UTSGate.Bool(showSecondsOpt);
                    for (int i = 0; i < rooms.Length; i++) {
                        var r = rooms[i];
                        if (r == null || r.special == null) continue;
                        if (!TryMap(r.room, out SabType t)) continue;
                        float cm = CurrentMax(t);
                        // perc = remaining cooldown fraction (0 = ready, 1 = full cooldown).
                        float perc = cm <= 0f ? 0f : Mathf.Clamp01(timer[(int)t] / cm);
                        r.SetSpecialActive(perc);
                        UpdateCooldownText(t, r, showSeconds);
                    }
                } catch { }
            }
        }

        // Reductions + cooldowns reset on every meeting.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        private static class MeetingResetPatch {
            private static void Postfix() {
                if (!Active) return;
                ResetAll();
            }
        }

        // Keep the Chance modifier's sabotage override disabled while Sabotage Tuning is active. A
        // prefix on HudManager.Update runs before Chance's own HudManager.Update postfix, so Chance
        // sees sabotageEnabled == false that frame and backs off.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.First)]
        private static class SuppressChancePatch {
            private static void Prefix() {
                if (!Active) return;
                SuppressChanceSabotage();
            }
        }

        // Per-game state reset so the next game starts clean.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        private static class GameEndPatch {
            private static void Postfix() {
                gameInit = false;
                prevActive = false;
                warnedConflict = false;
                // AUDIT-2026-08-16: clear the remembered cooldown-seconds cache so the first frame of
                // the next round always repaints the label instead of trusting a stale prior value.
                for (int i = 0; i < N; i++) lastShownSecs[i] = -1;
            }
        }

        // AUDIT-2026-08-16: lobby change (rejoin/new lobby) also needs the cooldown-seconds cache
        // cleared - OnGameEnd covers "round just ended" but not every path back into a fresh lobby.
        //
        // AUDIT-2026-08-23 (L-13): the same is true of everything else this file remembers. Only
        // the label cache was being cleared here, so a lobby left abnormally (crash, kick, straight
        // into another lobby) carried its per-type reduction counters into the next game: the first
        // sabotage of the fresh round came back with a cooldown already shortened by the PREVIOUS
        // round's usage. gameInit is cleared too, so the first tick of the next round runs its own
        // ResetAll instead of trusting a leftover "already initialised".
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class GameJoinedPatch {
            private static void Postfix() {
                for (int i = 0; i < N; i++) lastShownSecs[i] = -1;
                for (int i = 0; i < N; i++) usage[i] = 0;
                gameInit = false;
                prevActive = false;
                warnedConflict = false;
            }
        }
    }
}
