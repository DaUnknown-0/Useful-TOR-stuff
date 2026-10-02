// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * AirshipWrapUpDefer (review 2026-10-02).
 *
 * On every map but the Airship, ExileController.WrapUp is a plain method: it exiles the voted
 * player, calls ReEnableGameplay (which also resets the local kill timer) and destroys the
 * cutscene. A postfix there sees the finished state.
 *
 * AirshipExileController.WrapUpAndSpawn is a COROUTINE. Calling it only builds the enumerator, so a
 * postfix runs before its body: the exiled player is still alive, the spawn picker has not run, and
 * ReEnableGameplay comes later and overwrites any kill timer set in the postfix. Meeting-end logic
 * hooked there acted on the pre-exile state (Revenger decision with a "living" killer, Mixer swap
 * of a player who is about to be exiled, extra-Mini cooldown wiped by vanilla).
 *
 * Arm() stores the action and runs it once the controller is destroyed, which is the last thing
 * the coroutine does, i.e. the same moment the WrapUp postfix stands for on the other maps. A
 * give-up timer keeps a stalled cutscene from swallowing the action. Same idea as Chance's
 * AirshipExileDefer, which waits only for the death because it does not touch the kill timer.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class AirshipWrapUpDefer {
        private const float GiveUpSeconds = 30f;

        private sealed class Entry {
            public AirshipExileController Controller;
            public float GiveUpAt;
            public string Name;
            public Action Action;
        }

        private static readonly List<Entry> pending = new List<Entry>();

        internal static void Arm(AirshipExileController controller, string name, Action action) {
            if (action == null) return;
            pending.Add(new Entry {
                Controller = controller,
                GiveUpAt = Time.unscaledTime + GiveUpSeconds,
                Name = name,
                Action = action,
            });
        }

        internal static void Clear() => pending.Clear();

        private static bool Finished(Entry e) {
            try {
                if (e.Controller == null) return true;   // Unity null: the coroutine destroyed it
            } catch {
                return true;
            }
            return Time.unscaledTime >= e.GiveUpAt;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (pending.Count == 0) return;
                for (int i = 0; i < pending.Count; ) {
                    var e = pending[i];
                    if (!Finished(e)) { i++; continue; }
                    pending.RemoveAt(i);
                    try {
                        e.Action();
                    } catch (Exception ex) {
                        UsefulTORStuffPlugin.Logger?.LogError($"[AirshipWrapUp] deferred {e.Name} failed: {ex}");
                    }
                }
            }
        }

        // A game that ends during the Airship exile must not carry the action into the next one.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class GameEndPatch {
            public static void Prefix() => Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinedPatch {
            public static void Postfix() => Clear();
        }
    }
}
