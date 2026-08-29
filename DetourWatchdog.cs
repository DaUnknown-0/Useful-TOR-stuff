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
 * WHAT CHANGED ON 2026-08-29, AND WHY THIS FILE GOT SMALLER
 * ----------------------------------------------------------
 * Seven crash dumps from this machine (AppData\Local\CrashDumps) and a run of hard crashes on
 * other players' clients the same evening ("in the lobby", "walking around in a round") were read
 * together. Three of the dumps are recent and all three are on the main thread: two die INSIDE
 * coreclr.dll at the very same address, one of them in an empty lobby a minute after launch; the
 * third dies at an address inside no module at all - the JIT code region, the same 0x34..0x3A
 * range this file logs for method entries - reading address 0xE. In every one of those sessions
 * tiered compilation was on and this file was busy: 114 methods under watch, two "baseline"
 * repairs per tick, fingerprint repairs whenever the runtime touched an entry, "repair #58 this
 * session" in the host log.
 *
 * That made this file the prime suspect, because it is the ONE component in the whole mod family
 * that rewrites native code while the game runs. And on x86 .NET 6 it does so on ground the
 * runtime is rewriting too: with tiering on, a method's entry is a precode stub the runtime
 * retargets on its own (call-counting stubs in, tier-1 code out). Most of what the fingerprint
 * detector saw as "code changed at byte 1" was exactly that - the runtime moving its own jump, not
 * a patch being lost. Each such sighting triggered Unpatch + Patch, and Unpatch writes MonoMod's
 * SAVED bytes back: the precode as it looked when first detoured, pointing at code and stubs the
 * runtime has since retired. Either the next call goes through that stale jump into recycled stub
 * memory (the no-module crash), or the runtime trips over a precode that no longer holds what it
 * wrote (the identical coreclr address, twice). Not proven from the dumps - there are no symbols -
 * but it is the only account that fits all three signatures and the other players' reports.
 *
 * So the passive detector, the adoption of every patched TOR method and the baseline repairs are
 * gone. What is left is the measured minimum: the two methods that were SEEN to lose their
 * patches, repaired at the two moments they are guaranteed cold (lobby entry, round start), plus
 * the canary - a behavioural probe, not a byte comparison - which repairs getRoleInfoForPlayer
 * only when its postfix demonstrably did not run, and at most once every CanaryHealCooldown. A
 * repair is still a native write and still carries the risk above; it is now rare instead of
 * constant. The real fix remains DOTNET_TieredCompilation=0, which Doorstop cannot set for other
 * players - it reads no runtimeconfig and sets no environment - so it stays a per-player launch
 * option.
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

            // Consecutive failed repairs. NOT a permanent verdict: the first failure is expected and
            // usually clears itself, see MaxHealAttempts.
            public int FailedHeals;
        }

        private static readonly List<Watched> watched = new List<Watched>();
        private static Harmony healer;
        private static MethodInfo sentinelPostfix;
        private static MethodInfo roleInfoProbe;
        private static float nextCheck;
        private static int healCount;
        private static int detectedDrops;   // repairs that followed an actual detected drop
        private static int unrepairable;
        private const int MaxHealAttempts = 4;
        private static long totalHealMs;
        // A canary repair is a native write into a live method. Once every ten seconds bounds how
        // often that can happen even if the probe keeps reporting a drop.
        private static float nextCanaryHeal;
        private const float CanaryHealCooldown = 10f;

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

                // These two, and only these two: index 0 is the one method the canary can call safely,
                // index 1 is the one whose failure does real damage. Every other patched TOR method
                // used to be adopted on the first tick and repaired on a byte comparison; that is what
                // the 2026-08-29 note in the header is about, and it is gone.
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
                    "share the failure mode; only the two measured ones are repaired (see the 2026-08-29 note).");
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

                var local = PlayerControl.LocalPlayer;
                if (local == null || roleInfoProbe == null) return;

                // Reflection on purpose. A hard-compiled call from this assembly can bind straight to
                // the method's current code, which is exactly the code that may no longer carry the
                // detour - it would check nothing. Going through MethodInfo resolves the entry point.
                long before = canaryTicks;
                try { roleInfoProbe.Invoke(null, new object[] { local, false }); }
                catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[DetourWatchdog] probe call threw: {e.InnerException ?? e}");
                    return;
                }
                if (canaryTicks != before) return;   // the postfix ran: nothing is wrong

                // A real drop: the method was called and its registered postfix did not execute. This
                // is the one condition worth a native write mid-session, and even then not more often
                // than the cooldown allows - see the header for what a repair can cost.
                if (Time.time < nextCanaryHeal) return;
                nextCanaryHeal = Time.time + CanaryHealCooldown;
                var sw = Stopwatch.StartNew();
                bool ok = Heal(watched[0]);
                sw.Stop();
                if (ok) NoteRepair(1, "canary", sw.ElapsedMilliseconds, watched[0].Label);
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
