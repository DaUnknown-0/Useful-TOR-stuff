// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UTSRpc - the consolidated custom-RPC channel of Useful TOR Stuff.
 *
 * WHY
 * ---
 * Custom RPCs share one byte-wide id space with TOR's own CustomRPC enum (100-183 today, and it
 * grows with every TOR release). This plugin used to hold nine separate bytes (244-249, 252-254),
 * i.e. nine chances for a future TOR release to land on one of ours. Such a collision is not a
 * build error - it is a silent mis-parse in a live round (TOR reading our payload as one of its
 * own RPCs, which can kill players or desync state). Consolidating onto ONE callId reduces that
 * exposure to a single byte.
 *
 *   [callId 240][moduleId][ ... module's own payload, byte-for-byte unchanged ... ]
 *
 * The module byte keeps each feature's historical id (244 SidekickAllowed, 245 MultiModifiers,
 * 248 SelfLimp, 249 Reshield, 252 CancelBomb, 253 VersionHandshake, 254 MeetingMapPing), so logs
 * and ID-Registry.md stay readable.
 *
 * MIXED VERSIONS - why this is DUAL-SEND, not a hard switch
 * ---------------------------------------------------------
 * Unlike Unknown's Collection, this plugin has NO gate that stops a round when not everyone runs
 * the same build. UsefulVersionHandshake (module 253) is informational only: Useful TOR Stuff is
 * explicitly meant to run in mixed lobbies, next to plain-TOR clients and next to OLDER Useful
 * builds. An older build does not know callId 240 and would silently drop everything sent on it.
 *
 * So a migrated feature sends its payload TWICE: once on the legacy callId (understood by old AND
 * new builds) and once on channel 240 (understood by new builds only). The receiving side accepts
 * BOTH paths, which means a new<->new pair applies the message twice.
 *
 * That is only acceptable for messages whose application is IDEMPOTENT - pure state assignment
 * where applying the same payload twice leaves exactly the same result. Every migrated module was
 * classified before the change; the non-idempotent ones (LoverRevenger 247: kills + chat + win
 * flags; TricksterAvatarSabotage 246: replays an audible global cue) stayed on their legacy callId
 * only, with a comment at the call site saying why. De-duplicating is not possible here: the
 * receiver cannot know whether a legacy message came from a dual-sending new build or from an old
 * build that will never follow up on channel 240.
 *
 * Every legacy path is marked "LEGACY DUAL-SEND" at both the sender and the receiver. They can all
 * be deleted in one go in a future BREAKING release, once mixed-version support for pre-240 builds
 * is dropped - at which point the migrated features become channel-240-only and the last eight
 * legacy bytes are freed.
 *
 * USAGE
 * -----
 *   sender:   UTSRpc.SendDual(ModuleId, LegacyCallId, w => { w.Write(x); w.Write(y); });
 *             // or UTSRpc.Begin(ModuleId) for a channel-240-only message
 *   receiver: UTSRpc.Register(ModuleId, reader => { ... });   // in the feature's init
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;

namespace UsefulTORStuff {
    public static class UTSRpc {
        // The consolidated custom callId of Useful TOR Stuff. 240 sits in the free 211-243 window
        // (TOR <= 183, HostFix 167, ChanceMod 200-202/250-251, Unknown's Collection 230).
        public const byte CallId = 240;

        // moduleId -> handler. Filled once per feature at load time, read by the dispatcher below.
        private static readonly Dictionary<byte, Action<MessageReader>> handlers =
            new Dictionary<byte, Action<MessageReader>>();

        // The PlayerControl the currently dispatched message arrived on (i.e. the sender). Valid
        // only for the duration of a handler call - MeetingMapPing needs it to attribute the ping.
        public static PlayerControl Sender { get; private set; }

        // Start a message on the consolidated channel. The module byte is written for you.
        public static MessageWriter Begin(byte moduleId) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, CallId, SendOption.Reliable, -1);
            w.Write(moduleId);
            return w;
        }

        // LEGACY DUAL-SEND: writes the identical payload twice - first on the legacy callId (so old
        // builds still understand it), then on the consolidated channel. The single writePayload
        // delegate guarantees both copies can never drift apart. Only call this for messages whose
        // application is idempotent (see the file header).
        //
        // Order matters: legacy goes first so a receiver that understands both sees them in the same
        // order on every client (Hazel Reliable preserves ordering per connection).
        //
        // The legacy half can be deleted in a future breaking release - then this becomes Begin().
        public static void SendDual(byte moduleId, byte legacyCallId, Action<MessageWriter> writePayload) {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            try {
                MessageWriter legacy = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, legacyCallId, SendOption.Reliable, -1);
                writePayload?.Invoke(legacy);
                AmongUsClient.Instance.FinishRpcImmediately(legacy);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[UTSRpc] legacy send (callId {legacyCallId}) failed: {e}");
            }
            try {
                MessageWriter w = Begin(moduleId);
                writePayload?.Invoke(w);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[UTSRpc] channel send (module {moduleId}) failed: {e}");
            }
        }

        // Register a feature's receiver for the consolidated channel. A duplicate module byte would
        // mean two features eating each other's payload, so it is logged loudly.
        public static void Register(byte moduleId, Action<MessageReader> handler) {
            if (handler == null) return;
            if (handlers.ContainsKey(moduleId))
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[UTSRpc] module byte {moduleId} registered twice - the later handler wins, " +
                    "one of the two features will never receive its RPCs.");
            handlers[moduleId] = handler;
        }

        public static int RegisteredCount => handlers.Count;

        // Single dispatcher. Runs BEFORE TOR's own HandleRpc handler (Priority.High) and always
        // consumes callId 240 - the channel belongs to us, nobody else may parse it.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader) {
                if (callId != CallId) return true;
                try {
                    byte moduleId = reader.ReadByte();
                    if (handlers.TryGetValue(moduleId, out var handler)) {
                        Sender = __instance;
                        try { handler(reader); }
                        finally { Sender = null; }
                    } else {
                        // A newer build sent a module this one does not have. Harmless (the sender
                        // also broadcast the legacy copy if the feature is dual-sending), but logged.
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            $"[UTSRpc][DIAG] unknown module byte {moduleId} on channel {CallId} - ignored.");
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[UTSRpc] dispatch failed: {e}");
                }
                return false; // channel 240 is ours - never hand it to TOR
            }
        }
    }
}
