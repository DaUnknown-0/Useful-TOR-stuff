// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DraftTimerReset - restarts the role draft's pick timer when the host asks for it.
 *
 * HostFix (host only) takes a disconnected player out of TOR's draft pick order and resends the
 * order. TOR's receivePickOrder only swaps the list; the pick timer on every client keeps running
 * and is reset by nothing but a pick, so the next picker started with the time the leaver had
 * already used, and at timer >= maxTimer his client picked a random role at once (audit
 * 2026-10-04). HostFix has no code on the clients, so it sends this module (237 on UTSRpc's
 * channel 240) after the resend, and every client with this plugin sets RoleDraft.timer back to 0.
 * Clients without UTS keep TOR's behaviour.
 */

using System;
using System.Reflection;
using HarmonyLib;
using Hazel;

namespace UsefulTORStuff {
    public static class DraftTimerReset {
        public const byte DraftTimerRpcId = 237;

        private static FieldInfo timerField;
        private static bool resolved;

        public static void RegisterRpc() => UTSRpc.Register(DraftTimerRpcId, Handle);

        private static void Handle(MessageReader reader) {
            if (!UTSRpc.RequireHost("draft timer reset")) return;
            Reset();
        }

        /// RoleDraft.timer = 0 (TOR's RoleDraft is internal: reflection, resolved once).
        public static void Reset() {
            try {
                if (!resolved) {
                    resolved = true;
                    var t = AccessTools.TypeByName("TheOtherRoles.Modules.RoleDraft");
                    timerField = t != null ? AccessTools.Field(t, "timer") : null;
                    if (timerField == null) UsefulTORStuffPlugin.Logger?.LogWarning("[DraftTimerReset] RoleDraft.timer not found.");
                }
                if (timerField == null) return;
                timerField.SetValue(null, 0f);
                UsefulTORStuffPlugin.Logger?.LogInfo("[DraftTimerReset] pick timer restarted (picker left).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DraftTimerReset] {e.Message}");
            }
        }
    }
}
