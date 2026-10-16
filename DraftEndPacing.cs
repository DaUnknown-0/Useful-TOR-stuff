// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DraftEndPacing - the host's three assignment calls at the end of the Role Draft go out one after
 * another instead of in the same frame.
 *
 * When the last pick is in, TOR's draft coroutine (RoleDraft.cs:306-310) calls, on the host and in one
 * frame, assignRoleTargets, assignGuesserGamemode and assignModifiers, each of which sends its own
 * reliable RPCs to everybody; a few frames later the intro ends and every mod's round-start messages
 * follow. A player with a weak connection was dropped by the server at exactly this step four times
 * (2026-10-16), while the same player stayed with the draft switched off.
 *
 * The three calls are held back and replayed 0.3 / 0.6 / 0.9 s later, still inside the 3-second fade
 * TOR plays right after them (RoleDraft.cs:313-319), so every role, target and modifier is in place
 * before the round starts, just not in one burst. If the draft ends early (lobby, game end), anything
 * still held back runs at once. Only when the draft is running and only on the host; the normal role
 * assignment without draft is untouched. Switch: [Network] PacedDraftEnd.
 *
 * Postfixes on these methods run on the skipped call too (HarmonyX runs postfixes after a false
 * prefix); TiebreakerMultiple.TopUp checks Skipping for that reason.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class DraftEndPacing {
        private const float Step = 0.3f;
        private static ConfigEntry<bool> Enabled;
        private static readonly List<(MethodBase Method, object[] Args, float At)> held = new();
        private static bool replaying;
        internal static bool Skipping { get; private set; }

        internal static void Bind(ConfigFile config) {
            Enabled = config.Bind("Network", "PacedDraftEnd", true,
                "As host, send the role targets, guesser and modifier assignment at the end of the Role Draft a moment apart instead of in "
                + "one burst (helps players with weak connections; only the host's setting counts).");
        }

        internal static void Install(Harmony harmony) {
            try {
                if (Enabled == null || !Enabled.Value) return;
                var t = TorType.Get("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                int n = 0;
                foreach (var name in new[] { "assignRoleTargets", "assignGuesserGamemode", "assignModifiers" }) {
                    var m = t?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (m == null) continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(DraftEndPacing), nameof(Prefix)) { priority = Priority.First });
                    n++;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[DraftEndPacing] {n} of 3 draft-end assignment(s) paced.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DraftEndPacing] install failed: {e.Message}");
            }
        }

        public static bool Prefix(MethodBase __originalMethod, object[] __args) {
            Skipping = false;
            try {
                if (replaying) return true;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !DraftMusic.DraftRunning()) return true;
                float at = Time.realtimeSinceStartup + Step * (held.Count + 1);
                held.Add((__originalMethod, __args == null ? null : (object[])__args.Clone(), at));
                Skipping = true;
                return false;
            } catch { Skipping = false; return true; }
        }

        private static void Run(int i) {
            var (m, args, _) = held[i];
            held.RemoveAt(i);
            replaying = true;
            try { m.Invoke(null, args); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[DraftEndPacing] {m.Name} failed: {e.InnerException?.Message ?? e.Message}"); }
            finally { replaying = false; Skipping = false; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (held.Count == 0) return;
                try {
                    Skipping = false;
                    var c = AmongUsClient.Instance;
                    if (c == null || !c.AmHost || c.GameState != InnerNet.InnerNetClient.GameStates.Started) {
                        held.Clear();   // no longer host or no game: nothing left to assign
                        return;
                    }
                    float now = Time.realtimeSinceStartup;
                    if (held[0].At <= now || !DraftMusic.DraftRunning()) Run(0);
                } catch { held.Clear(); }
            }
        }

        // The intro ending with calls still held back (should not happen inside the 3 s fade): run them now.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndFlush {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                try { while (held.Count > 0) Run(0); } catch { held.Clear(); }
            }
        }
    }
}
