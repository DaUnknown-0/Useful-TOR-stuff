// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DetourWatchdog - notices when Harmony patches stop executing, and puts them back.
 *
 * THE BUG THIS EXISTS FOR (measured in-game 2026-08-16/17, not theory)
 * --------------------------------------------------------------------
 * Harmony's patch REGISTRY and the code that actually runs can drift apart. On two of TOR's managed
 * methods the self-test measured this state:
 *
 *   - Harmony.GetPatchInfo(RPCProcedure.resetVariables) reports 51 postfixes from three mods
 *     (Unknown's Collection, this mod, HostFix) - exactly matching what the three declare in source.
 *     prefixes=0, transpilers=0, finalizers=0. Exactly ONE 0Harmony assembly (2.10.2.0) is loaded.
 *   - Calling that method clears NONE of the 20 round-list statics its postfixes exist to clear.
 *     No exception propagates, nothing appears in the log. True for a hard-compiled call, for
 *     MethodInfo.Invoke, and for two calls in a row.
 *   - Invoking any one of those postfixes by hand DOES clear its list, so the bodies are fine.
 *   - Adding one trivial no-op postfix under a throwaway Harmony id SUCCEEDS, and afterwards the
 *     very same call clears all 20 fields. Logged as "before forcing a regen: 20 of 20 field(s)
 *     still full" / "after forcing a regen: 0 field(s) still full, sentinel ran: True".
 *   - Same shape on RoleInfo.getRoleInfoForPlayer, in a partial form: within ONE call, Follower's
 *     postfix takes effect while Beacon's and Bug's do not, though all three are registered, have
 *     satisfied guards, and work when applied by hand.
 *
 * So: registered patches are missing from the executing method until a regeneration is forced.
 *
 * WHY (leading explanation, deliberately NOT relied upon below)
 * ------------------------------------------------------------
 * Among Us is a 32-bit process on .NET 6 CoreCLR. On x86 MonoMod patches the code body behind the
 * precode stub, i.e. the tier-0 compilation. When tiered compilation promotes a method to tier 1
 * (default threshold ~30 calls) the runtime compiles fresh code from the ORIGINAL IL - Harmony never
 * rewrites a method's IL, it only installs a jump - and redirects the entry point. The jump in the
 * old tier-0 body is then dead code. Nothing throws, nothing is logged, the registry stays correct.
 * Any new Patch() re-reads the current entry address, which is why one sentinel heals everything at
 * once.
 *
 * A HEAL IS NOT PERMANENT ON A HOT METHOD. This file first claimed it was ("tier 1 is final"), and
 * the next run disproved it: the log reads "repaired (lobby entry)", then two scenarios read a stale
 * role list, then "repaired (canary)". So a repair produces code that is itself subject to being
 * recompiled once it has been called often enough. That is why resetVariables - which TOR calls two
 * or three times per game - stays fixed after one repair, while getRoleInfoForPlayer needs repairing
 * over and over. Polling can only ever bound the damage window, never close it; the actual fix is to
 * stop the recompilation happening at all, by starting the game with DOTNET_TieredCompilation=0.
 *
 * That story fits every measurement, but it is not proven: MonoMod ships a compileMethod JIT hook
 * that should migrate the detour, and the same mechanism would predict far more breakage than we
 * observe (~135 of our patch declarations sit on managed TOR types, including per-frame ones that
 * demonstrably still work). This file therefore does not depend on the explanation being right. It
 * detects the MEASURED state - "a patch that is registered did not run" - and repairs it.
 *
 * WHAT IS DELIBERATELY NOT DONE HERE
 * ----------------------------------
 *   - No load order or plugin priority tweaking. Ruled out: the divergence appears mid-session, and
 *     within one call some postfixes from a single PatchAll run while others do not.
 *   - No one-shot regeneration at the end of loading. Ruled out for the same reason: too early.
 *   - resetVariables is NEVER called as a probe. It wipes real round state.
 *   - No BepInEx/HarmonyX/MonoMod upgrade. TOR is built against be.697.
 *
 * WHY THIS LIVES IN UsefulTORStuff, AND WHY IT IS NOT AN IN-GAME OPTION
 * --------------------------------------------------------------------
 * A forced regeneration rebuilds the wrapper from the WHOLE registry, so healing here also restores
 * Unknown's Collection's and HostFix's patches. The fault is purely client-local: every modded
 * client repairs itself, nothing is host-authoritative and nothing goes over the network. That is
 * also why the switch is a BepInEx Config entry and not a CustomOption: it must keep working when
 * the host does not have this mod, so the UTSGate rule ("settings-based features off without a UTS
 * host") explicitly does not apply.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class DetourWatchdog {
        private const float DefaultInterval = 0.5f;
        private const float LegacyInterval = 5f;   // shipped once, measured too slow - see Bind()

        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<float> checkInterval;

        // Bumped by the canary postfix below. The probe reads it, calls the method, reads it again.
        private static long canaryTicks;

        // A do-nothing postfix. Patching it in is what forces HarmonyX to rebuild the combined
        // wrapper and reinstall the detour at the method's CURRENT entry address - the exact
        // operation the self-test measured as healing all 51 postfixes at once.
        private static class Sentinel {
            public static void Postfix() { }
        }

        private sealed class Watched {
            public string Label;
            public MethodBase Method;

            // Fingerprint of the native code this method entered last time we repaired it. See
            // CaptureFingerprint: this is what lets a drop be detected WITHOUT calling the method,
            // which is the only way resetVariables can ever be watched.
            public IntPtr NativeStart;
            public byte[] Prologue;

            // True once a repair has actually run on this method, which is the ONLY state in which
            // Prologue is worth comparing: it then describes a detour we just installed ourselves.
            // Adopting a method and trusting whatever happened to be at its entry does not work - see
            // the comment on AdoptPatchedTorMethods.
            public bool Baselined;

            // Consecutive failed repairs. NOT a permanent verdict: the first failure is expected and
            // usually clears itself, see MaxHealAttempts.
            public int FailedHeals;
        }

        private static readonly List<Watched> watched = new List<Watched>();
        private static Harmony healer;
        private static MethodInfo sentinelPostfix;
        private static MethodInfo roleInfoProbe;
        private static float nextCheck;
        private static float nextAdopt;
        private static int healCount;
        private static int detectedDrops;   // repairs that followed an actual detected drop
        private static int fingerprintAgreements;
        private static int fingerprintDisagreements;
        private static int prologuesLogged;
        private static int unrepairable;
        private const int MaxHealAttempts = 4;
        // Two, not four. Measured on the first run of the baselining version: arming 68 methods cost
        // ~400ms in total, and at four per tick that is roughly 24ms in a single frame, i.e. over a
        // 60fps budget. Two keeps each arming frame comfortably under it and merely stretches the
        // start-up from about nine seconds to about seventeen, which nobody notices.
        private const int BaselinePerTick = 2;
        private static long totalHealMs;

        // ────────────────────────────────────────────────────────────────────────────────────
        // Load-time setup
        // ────────────────────────────────────────────────────────────────────────────────────
        public static void Bind(ConfigFile config) {
            enabled = config.Bind("DetourWatchdog", "Enabled", true,
                "Detect and repair Harmony patches that stop executing mid-session. Client-local, " +
                "no effect on other players. Turn off only to reproduce the underlying bug.");
            // 0.5s, not the 5s this started with. Measured 2026-08-17: a repair at lobby entry held
            // for less than the gap to the next check, because getRoleInfoForPlayer is called
            // constantly and drops its patches again within seconds of every repair - the log reads
            // "repaired (lobby entry)", then two scenarios fail, then "repaired (canary)". A slow
            // canary loses that race by design. The probe costs one call of a method the game's own
            // nameplate code already makes several times per frame, so twice a second is noise.
            checkInterval = config.Bind("DetourWatchdog", "CheckIntervalSeconds", DefaultInterval,
                "How often to verify that patches still run. The check is one call of a method the " +
                "game already calls constantly, so short intervals are cheap. Hot methods lose their " +
                "patches again within seconds of a repair, so a long interval leaves visible gaps.");

            // Config.Bind reads the value already written to the .cfg, so lowering the default in
            // code does nothing for anyone who has run this build once - the first release shipped
            // 5f, and the log kept saying "checking every 5s" after the default changed. 5s was
            // measured to be too slow (the canary reported the drop AFTER two scenarios had already
            // read a stale role list), so the one stale value is migrated. A value the user actually
            // chose is left alone, unless they happened to pick exactly the old default, which is a
            // trade this makes knowingly: 5s is measured to be too slow to be worth preserving.
            if (Mathf.Approximately(checkInterval.Value, LegacyInterval)) {
                checkInterval.Value = DefaultInterval;
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DetourWatchdog] migrated CheckIntervalSeconds {LegacyInterval} -> {DefaultInterval} " +
                    "(the old value was slower than the fault it is meant to catch).");
            }
        }

        public static void Initialize() {
            try {
                if (enabled != null && !enabled.Value) {
                    UsefulTORStuffPlugin.Logger?.LogInfo("[DetourWatchdog] disabled in config.");
                    return;
                }

                healer = new Harmony(UsefulTORStuffPlugin.PluginGuid + ".detourwatchdog");
                sentinelPostfix = AccessTools.Method(typeof(Sentinel), nameof(Sentinel.Postfix));
                if (sentinelPostfix == null) {
                    UsefulTORStuffPlugin.Logger?.LogError("[DetourWatchdog] sentinel postfix not found - disabled.");
                    return;
                }

                roleInfoProbe = AccessTools.Method(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer));
                Add("RoleInfo.getRoleInfoForPlayer", roleInfoProbe);
                Add("RPCProcedure.resetVariables", AccessTools.Method(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables)));

                // These two are added by hand and stay at the front: index 0 is the one method the
                // canary can call safely, and index 1 is the one whose failure does real damage. Every
                // other patched TOR method is discovered later, on the first tick - see AdoptPatchedTorMethods,
                // which cannot run here because plugins loading after this one (HostFix, Role Control)
                // have not registered their patches yet.
                LogPatchedTorSurface();
                LogRuntimeTiering();

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DetourWatchdog] armed, watching {watched.Count} method(s), " +
                    $"checking every {checkInterval?.Value ?? 5f:0.#}s.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DetourWatchdog] initialize failed: {e}");
            }
        }

        private static void Add(string label, MethodBase m) {
            if (m == null) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] {label} not found - not watched.");
                return;
            }
            watched.Add(new Watched { Label = label, Method = m });
        }

        // Take every managed TOR method anyone has patched under watch, not just the two measured
        // ones. This became defensible only once the passive fingerprint detector was validated
        // (2026-08-17: on the one method both detectors can see, they agreed on the real drop and the
        // 5-byte window produced no false positives all session). That matters because the old
        // objection to a long list was cost: healing everything on a timer would be wasteful and could
        // stutter a round. With per-method detection nothing is repaired that has not actually been
        // replaced, so the list length costs a pointer read and five bytes per method per tick.
        //
        // Runs on ticks rather than at load, because plugins that load after this one have not
        // registered their patches yet, and it repeats occasionally so late or re-registered patches
        // are picked up too. Il2Cpp game types are excluded throughout: those go through a different
        // detour path and were never observed losing their patches.
        private static void AdoptPatchedTorMethods() {
            try {
                var torAssembly = typeof(RPCProcedure).Assembly;
                int added = 0;
                foreach (var m in Harmony.GetAllPatchedMethods()) {
                    if (m?.DeclaringType?.Assembly != torAssembly) continue;
                    bool known = false;
                    for (int i = 0; i < watched.Count; i++)
                        if (watched[i].Method == m) { known = true; break; }
                    if (known) continue;
                    watched.Add(new Watched { Label = $"{m.DeclaringType.Name}.{m.Name}", Method = m });
                    added++;
                }
                if (added > 0)
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[DetourWatchdog] {watched.Count} patched TOR method(s) under watch (+{added}); " +
                        "each gets one repair to establish a baseline before it can be checked.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] adopt failed: {e.GetType().Name}");
            }
        }

        // One line at startup naming every managed TOR method that anyone has patched. Il2Cpp game
        // types are excluded on purpose: those go through a different detour path and were never
        // observed losing their patches.
        private static void LogPatchedTorSurface() {
            try {
                var torAssembly = typeof(RPCProcedure).Assembly;
                int count = 0;
                foreach (var m in Harmony.GetAllPatchedMethods()) {
                    if (m?.DeclaringType?.Assembly == torAssembly) count++;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DetourWatchdog] {count} patched method(s) live in TOR's managed assembly and " +
                    "share the failure mode; all of them are taken under watch on the first tick.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] surface scan failed: {e.GetType().Name}");
            }
        }

        // The leading explanation for the whole fault is .NET tiered compilation replacing the code
        // a detour was installed into. Testing that means starting the game with
        // DOTNET_TieredCompilation=0 - and the ONLY way to know afterwards whether the flag actually
        // reached the process is to have the process say so. Without this line an A/B run is
        // unfalsifiable: a canary hit could mean "the flag does not help" or "the flag never arrived",
        // and those demand opposite conclusions. Reading it costs nothing and is done once.
        private static void LogRuntimeTiering() {
            try {
                string env = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
                string legacy = Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
                string sw = AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool on)
                    ? on.ToString() : "unset";
                bool disabled = env == "0" || legacy == "0" || (sw == "False");
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DetourWatchdog] tiered compilation: {(disabled ? "DISABLED" : "ENABLED (default)")} " +
                    $"[DOTNET_TieredCompilation={env ?? "unset"}, COMPlus_TieredCompilation={legacy ?? "unset"}, " +
                    $"AppContext switch={sw}] on {Environment.Version}, " +
                    $"{(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] tiering probe failed: {e.GetType().Name}");
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────
        // Passive detection: has the native code under a patched method been replaced?
        // ────────────────────────────────────────────────────────────────────────────────────
        // The canary below can only watch ONE method, the one it is safe to call. Everything else -
        // resetVariables above all, which wipes live round state and must never be called as a probe -
        // needs a check that touches nothing. This is it: Harmony's repair writes a jump into the
        // method's native code, so after a repair we record where that code starts and what its first
        // bytes are. If the runtime later compiles a fresh copy of the method, either the entry moves
        // or those bytes are no longer our jump, and both are visible without executing anything.
        //
        // NOT YET PROVEN to work in this process: on 32-bit the entry may be a precode stub whose
        // address stays put while the code behind it is swapped, in which case the pointer alone would
        // never change - hence reading the bytes too, and hence running this ALONGSIDE the canary for
        // now and logging when the two disagree. The canary is the known-good reference; this is the
        // candidate that could cover all 67 methods instead of two. One run with both decides it.
        // Five bytes, not the sixteen this started with. Measured 2026-08-17: a 16-byte window
        // reported "code changed at byte 8" over and over while the canary confirmed the patch was
        // still running fine, i.e. bytes past the jump are simply not part of the detour and change
        // for reasons of their own. The one REAL drop that session showed up at byte 1 - inside the
        // relative offset of an x86 `E9 xx xx xx xx` jump - so the jump itself is exactly the right
        // thing to fingerprint, and everything beyond it is noise that would cause pointless repairs.
        private const int PrologueBytes = 5;

        private static void CaptureFingerprint(Watched w) {
            try {
                w.NativeStart = w.Method.MethodHandle.GetFunctionPointer();
                var buf = new byte[PrologueBytes];
                Marshal.Copy(w.NativeStart, buf, 0, PrologueBytes);
                w.Prologue = buf;
                // Logged once per method so the assumption "the entry starts with a jump" is on the
                // record and can be checked, instead of being taken on faith.
                // Logged for the first few methods so the shape of an entry is on the record rather
                // than assumed. NOTE what these bytes are and are not: MonoMod's own GetFunctionPointer
                // FOLLOWS precode stubs and patches the body behind them, while this reads the raw
                // MethodHandle pointer. So on a method with a precode this is the runtime's jump, not
                // MonoMod's detour. That is fine for the job - a recompile retargets exactly this jump,
                // which is the change we want to notice - but it is not "our detour", and an earlier
                // version of this file justified an E9/FF25 filter on that false premise. There is no
                // such filter any more: the baseline comes from a repair we performed, so whatever the
                // bytes are, a later change to them is meaningful.
                if (prologuesLogged < 3) {
                    prologuesLogged++;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[DetourWatchdog] {w.Label} entry at {w.NativeStart.ToInt64():X} reads " +
                        $"{BitConverter.ToString(buf)} after a repair.");
                }
            } catch (Exception e) {
                w.NativeStart = IntPtr.Zero;
                w.Prologue = null;
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[DetourWatchdog] could not fingerprint {w.Label}: {e.GetType().Name}");
            }
        }

        // Returns null when the fingerprint still matches, otherwise a short description of what moved.
        private static string FingerprintChange(Watched w) {
            if (w.Prologue == null || w.NativeStart == IntPtr.Zero || !w.Baselined) return null;
            try {
                var now = w.Method.MethodHandle.GetFunctionPointer();
                if (now != w.NativeStart)
                    return $"entry moved {w.NativeStart.ToInt64():X} -> {now.ToInt64():X}";
                var buf = new byte[PrologueBytes];
                Marshal.Copy(now, buf, 0, PrologueBytes);
                for (int i = 0; i < PrologueBytes; i++)
                    if (buf[i] != w.Prologue[i]) return $"code changed at byte {i}";
                return null;
            } catch (Exception e) {
                return $"fingerprint read failed: {e.GetType().Name}";
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────
        // Detection
        // ────────────────────────────────────────────────────────────────────────────────────
        // Registered through PatchAll like every other patch in this mod, so it lives in the same
        // wrapper as everything else: once that wrapper stops running, this stops ticking too, which
        // is precisely the signal we want.
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class CanaryPatch {
            public static void Postfix() { canaryTicks++; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class TickPatch {
            public static void Postfix() => Tick();
        }

        private static void Tick() {
            try {
                if (healer == null || watched.Count == 0) return;
                if (Time.time < nextCheck) return;
                nextCheck = Time.time + Mathf.Max(0.1f, checkInterval?.Value ?? DefaultInterval);

                // Late-loading plugins register their patches after this one was armed, so the watch
                // list is topped up here rather than at load. Cheap and idempotent.
                if (Time.time >= nextAdopt) {
                    nextAdopt = Time.time + 30f;
                    AdoptPatchedTorMethods();
                }

                // Give newly adopted methods a baseline: one repair each, a few per tick so ~70 wrapper
                // rebuilds never land in a single frame. Until a method has been through this its entry
                // bytes describe whatever state it happened to be in when adopted - including, possibly,
                // an already-dead detour, which would then never look changed again and never be fixed.
                var sw = Stopwatch.StartNew();
                int baselined = 0;
                for (int i = 0; i < watched.Count && baselined < BaselinePerTick; i++) {
                    if (watched[i].Baselined || watched[i].FailedHeals >= MaxHealAttempts) continue;
                    Heal(watched[i]);
                    baselined++;
                }
                if (baselined > 0) {
                    sw.Stop();
                    NoteRepair(baselined, "baseline", sw.ElapsedMilliseconds);
                    return;   // one job per tick keeps the frame cost predictable
                }

                var local = PlayerControl.LocalPlayer;

                // The passive detector must keep working even if the canary's probe method is gone,
                // so the canary is skipped rather than the whole tick abandoned.
                bool canaryRan = false, canarySaysDropped = false;
                if (local != null && roleInfoProbe != null) { canaryRan = true;

                // Reflection on purpose. A hard-compiled call from this assembly can bind straight to
                // the method's current code, which is exactly the code that may no longer carry the
                // detour - it would check nothing. Going through MethodInfo resolves the entry point.
                long before = canaryTicks;
                try { roleInfoProbe.Invoke(null, new object[] { local, false }); }
                catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] probe call threw: {e.InnerException ?? e}");
                    return;
                }

                canarySaysDropped = canaryTicks == before;
                }

                // Cross-check the passive detector against the canary on the ONE method both can see.
                // This is what earned the passive detector the right to watch the other 66, and it is
                // kept running so a regression in it shows up as a logged mismatch rather than as
                // silently unrepaired methods.
                var probeWatched = watched.Count > 0 ? watched[0] : null;
                string fingerprint = probeWatched == null ? null : FingerprintChange(probeWatched);
                if (canaryRan && probeWatched != null && probeWatched.Baselined
                    && canarySaysDropped != (fingerprint != null) && fingerprintDisagreements++ < 5)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[DetourWatchdog] detector mismatch on {probeWatched?.Label}: canary says " +
                        $"{(canarySaysDropped ? "DROPPED" : "alive")}, native fingerprint says " +
                        $"{(fingerprint != null ? fingerprint : "unchanged")}. Treating it as dropped.");
                else if (canarySaysDropped && fingerprint != null && fingerprintAgreements++ < 5)
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[DetourWatchdog] both detectors agree on {probeWatched?.Label}: {fingerprint}.");

                // Per-method detection means per-method repair: only what actually lost its detour is
                // rebuilt. That is what makes watching everything affordable - the alternative, healing
                // the whole list on a timer, would rebuild dozens of wrappers for nothing and could
                // stutter a round.
                int repaired = 0;
                var names = new List<string>();
                sw.Restart();
                for (int i = 0; i < watched.Count; i++) {
                    bool dropped = (i == 0 && canarySaysDropped) || FingerprintChange(watched[i]) != null;
                    if (!dropped) continue;
                    if (!Heal(watched[i])) continue;
                    repaired++;
                    if (names.Count < 8) names.Add(watched[i].Label);
                }
                sw.Stop();
                // Timed for real. An earlier version called NoteRepair without a stopwatch from this
                // path, so the log claimed "repaired 43 method(s) in 0ms" and the running total was
                // nonsense - which is exactly the number one would want when judging whether watching
                // everything is affordable.
                if (repaired > 0)
                    NoteRepair(repaired, canarySaysDropped ? "canary" : "fingerprint", sw.ElapsedMilliseconds,
                               string.Join(", ", names) + (repaired > names.Count ? ", ..." : ""));
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DetourWatchdog] tick failed: {e}");
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────
        // Preventive repair at round boundaries
        // ────────────────────────────────────────────────────────────────────────────────────
        // resetVariables cannot be probed the way the method above can - calling it wipes live round
        // state - so instead it is refreshed at the two moments that always precede its next use, and
        // that are already dominated by loading, so a wrapper rebuild is invisible there. TOR calls it
        // roughly two to three times per game, so once per lobby entry and once per round start keeps
        // it far below any promotion threshold.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyEntryPatch {
            public static void Postfix() => HealCritical("lobby entry");
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Last)]
        static class RoundStartPatch {
            public static void Postfix() => HealCritical("round start");
        }

        // ────────────────────────────────────────────────────────────────────────────────────
        // Repair
        // ────────────────────────────────────────────────────────────────────────────────────
        // Repairs the two methods whose failure is known to hurt, regardless of detection. Used only
        // at round boundaries, where a rebuild is invisible; everything else is repaired on demand.
        private static void HealCritical(string reason) {
            if (healer == null) return;
            var sw = Stopwatch.StartNew();
            int ok = 0;
            for (int i = 0; i < watched.Count && i < 2; i++)
                if (Heal(watched[i])) ok++;
            sw.Stop();
            NoteRepair(ok, reason, sw.ElapsedMilliseconds);
        }

        private static void NoteRepair(int ok, string reason, long ms = 0, string which = null) {
            healCount++;
            totalHealMs += ms;
            bool isEvidence = reason == "canary" || reason == "fingerprint";
            if (isEvidence) detectedDrops++;

            // Two different things are logged here and they deserve different treatment.
            //
            // A DETECTED repair (canary or fingerprint) is evidence: a patch that was registered had
            // actually stopped running. Those are always logged, lightly rate-limited, because they
            // are how "does this happen in normal play, or only while the test plugin hammers the
            // game?" gets answered - this watchdog lives in the mod, so it keeps reporting with
            // TOR-SelfTest.dll removed, and any such line in that state settles the question.
            //
            // A round-boundary repair is preventive and unconditional, so it proves nothing; it is
            // logged a few times and then merely tallied, to keep the log readable.
            if (isEvidence ? (detectedDrops <= 10 || detectedDrops % 25 == 0)
                           : (healCount <= 3 || healCount % 50 == 0))
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DetourWatchdog] repaired {ok} method(s) ({reason}) in {ms}ms; watching " +
                    $"{watched.Count}, repair #{healCount} this session, of which {detectedDrops} " +
                    $"followed a DETECTED drop; {totalHealMs}ms spent repairing in total." +
                    (which == null ? "" : $" Methods: {which}"));
        }

        private static bool Heal(Watched w) {
            try {
                // Remove-then-add rather than a bare re-add. Only Patch() is MEASURED to rebuild the
                // wrapper and reinstall the detour; whether Unpatch alone does the same was not
                // verified, and adding an already-registered patch a second time is not something to
                // rely on either. Dropping it first keeps exactly one sentinel registered no matter
                // how often this runs, and the Patch() that follows is the operation we know works.
                if (w.FailedHeals >= MaxHealAttempts) return false;
                // Unpatch unconditionally rather than tracking whether the sentinel is installed. If a
                // previous attempt got past Unpatch and then threw inside Patch, HarmonyX does NOT roll
                // the registry back: the sentinel is registered even though the wrapper build failed.
                // A flag would be wrong in exactly that case and the next attempt would register a
                // SECOND sentinel. Unpatch on an absent patch is harmless, so just always do it.
                try { healer.Unpatch(w.Method, sentinelPostfix); } catch { }
                healer.Patch(w.Method, postfix: new HarmonyMethod(sentinelPostfix));
                w.FailedHeals = 0;
                w.Baselined = true;
                // Re-baseline: the repair just installed a fresh detour, so whatever is at the
                // entry NOW is the state we want to notice drifting away from.
                CaptureFingerprint(w);
                return true;
            } catch (Exception e) {
                // A throw here is itself a finding: it would mean the combined wrapper can no longer
                // be compiled at all, and the exception names the patch responsible.
                // Almost always MonoMod's NativeDetour.Undo -> MakeWritable failing with Win32 487
                // "invalid address": the memory the OLD detour was written into is gone, so undoing it
                // fails. This is the bug itself seen from the native side, and it is NOT terminal.
                //
                // An earlier version of this file gave up after one failure and logged "its patches are
                // lost until the game restarts". That was wrong, and the log disproved it: one tick
                // after seven such failures, all seven repaired cleanly. The reason is that
                // Detour.Undo() clears IsApplied BEFORE the throwing MakeWritable call, so on the next
                // attempt the undo is a no-op, the dead detour is dropped from the chain, and the new
                // one is written at the current address. The first failure is therefore what MAKES the
                // retry work. Only a method that keeps failing is given up on, and even then the entry
                // bytes are untouched (the throw happens while disposing the old detour, before
                // anything new is written), so a failed repair never makes things worse.
                w.FailedHeals++;
                bool givingUp = w.FailedHeals >= MaxHealAttempts;
                if (givingUp) unrepairable++;
                if (givingUp || w.FailedHeals == 1)
                    UsefulTORStuffPlugin.Logger?.Log(
                        givingUp ? BepInEx.Logging.LogLevel.Error : BepInEx.Logging.LogLevel.Info,
                        $"[DetourWatchdog] repair of {w.Label} failed (attempt {w.FailedHeals}/{MaxHealAttempts})" +
                        (givingUp ? $"; giving up on it, {unrepairable} method(s) given up this session. "
                                  : "; this is expected once and normally clears on the next attempt. ") +
                        $"Cause: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
