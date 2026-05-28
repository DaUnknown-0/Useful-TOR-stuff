// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace UsefulTORStuff {

    // Mod-presence handshake, modelled on TOR's own VersionHandshake (and the Chance mod's).
    // Every client with Useful TOR Stuff broadcasts its version + assembly GUID at lobby time
    // (RPC 253). Each client can then tell whether EVERY connected player runs the same build —
    // the precondition for the permanent client-side Snitch fix (SnitchRoomPersistFix), which
    // needs all clients to cooperate. The result is published in
    // UsefulTORStuffPlugin.SnitchClientFixActive and surfaced via lobby messages.
    public static class UsefulVersionHandshake {
        public static readonly Dictionary<int, PlayerVersion> playerVersions = new Dictionary<int, PlayerVersion>();
        private static bool versionSent;
        private static bool snitchFixChatShown;

        // Post the "snitch fix active" confirmation to the local chat exactly once per session.
        private static void PostSnitchFixChatOnce() {
            if (snitchFixChatShown) return;
            var hud = HudManager.Instance;
            if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
            snitchFixChatShown = true;
            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                "Snitch fix active — all players have Useful TOR Stuff.");
        }

        public sealed class PlayerVersion {
            public readonly Version version;
            public readonly Guid guid;
            public PlayerVersion(Version version, Guid guid) { this.version = version; this.guid = guid; }
            public bool GuidMatches() =>
                Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.Equals(guid);
        }

        public static void ShareVersion() {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            var v = UsefulTORStuffPlugin.Version;

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, UsefulTORStuffPlugin.VersionHandshakeRpcId, SendOption.Reliable, -1);
            writer.Write((byte)v.Major);
            writer.Write((byte)v.Minor);
            writer.Write((byte)v.Build);
            writer.WritePacked(AmongUsClient.Instance.ClientId);
            writer.Write((byte)(v.Revision < 0 ? 0xFF : v.Revision));
            writer.Write(Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToByteArray());
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            // Apply locally too (the sender never receives its own broadcast).
            Receive(v.Major, v.Minor, v.Build, v.Revision,
                Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId, AmongUsClient.Instance.ClientId);
        }

        // Reads the RPC 253 payload (called from UsefulHandleRpcPatch).
        public static void ReceiveRpc(MessageReader reader) {
            byte major = reader.ReadByte();
            byte minor = reader.ReadByte();
            byte build = reader.ReadByte();
            int clientId = reader.ReadPackedInt32();
            byte revision = 0xFF;
            Guid guid;
            if (reader.Length - reader.Position >= 17) {
                revision = reader.ReadByte();
                byte[] gbytes = reader.ReadBytes(16);
                guid = new Guid(gbytes);
            } else {
                guid = new Guid(new byte[16]);
            }
            Receive(major, minor, build, revision == 0xFF ? -1 : revision, guid, clientId);
        }

        private static void Receive(int major, int minor, int build, int revision, Guid guid, int clientId) {
            Version ver = revision < 0 ? new Version(major, minor, build) : new Version(major, minor, build, revision);
            playerVersions[clientId] = new PlayerVersion(ver, guid);
        }

        // Lists every connected client that lacks this mod or runs a different/modified build.
        // Returns "" when everyone matches. Works on any client (allClients is replicated).
        public static string BuildMismatchMessage() {
            string message = "";
            if (AmongUsClient.Instance == null) return message;
            foreach (InnerNet.ClientData client in AmongUsClient.Instance.allClients.ToArray()) {
                if (client == null || client.Character == null) continue;
                string name = client.Character.Data.PlayerName;

                if (!playerVersions.TryGetValue(client.Id, out PlayerVersion pv)) {
                    message += $"<color=#FF0000FF>{name} is missing Useful TOR Stuff (or has a different version)\n</color>";
                    continue;
                }

                int diff = UsefulTORStuffPlugin.Version.CompareTo(pv.version);
                if (diff > 0)
                    message += $"<color=#FF0000FF>{name} has an older Useful TOR Stuff (v{pv.version})\n</color>";
                else if (diff < 0)
                    message += $"<color=#FF0000FF>{name} has a newer Useful TOR Stuff (v{pv.version})\n</color>";
                else if (!pv.GuidMatches())
                    message += $"<color=#FF0000FF>{name} has a modified Useful TOR Stuff v{pv.version} <size=30%>({pv.guid})</size>\n</color>";
            }
            return message;
        }

        // Re-share whenever someone joins, so late joiners learn everyone's version (and vice versa).
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
        static class OnPlayerJoinedPatch {
            public static void Postfix() {
                if (PlayerControl.LocalPlayer != null) ShareVersion();
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
        static class GameStartManagerStartPatch {
            public static void Postfix() {
                versionSent = false;
            }
        }

        // Runs after TOR's own GameStartManager.Update postfix (Priority.Low). Computes the mod
        // handshake state each lobby frame, confirms the active fix once in chat, and (host-only)
        // draws the mismatch warning on TOR's GameStartText when someone is missing the mod.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class GameStartManagerUpdatePatch {
            public static void Postfix(GameStartManager __instance) {
                if (PlayerControl.LocalPlayer != null && !versionSent) {
                    versionSent = true;
                    ShareVersion();
                }

                if (AmongUsClient.Instance == null) return;

                // Compute on EVERY client. GameStartManager.Update only runs in the lobby, so this
                // value naturally persists (latches) into the game that follows.
                string mismatch = BuildMismatchMessage();
                bool everyone = mismatch == "";
                UsefulTORStuffPlugin.SnitchClientFixActive = everyone;

                var text = __instance.GameStartText;

                // The mismatch warning below draws on TOR's shared GameStartText and moves its pivot
                // to top-left. TOR resets that element's position/scale every frame but NOT its pivot,
                // so a leftover top-left pivot offsets TOR's centred countdown. Restore the centred
                // pivot whenever we are not actively drawing the mismatch warning (incl. during the
                // countdown) so the countdown is never displaced.
                bool drawMismatch = !everyone && AmongUsClient.Instance.AmHost
                                    && __instance.startState != GameStartManager.StartingStates.Countdown;
                if (text != null && !drawMismatch)
                    text.rectTransform.pivot = new Vector2(0.5f, 0.5f);

                if (__instance.startState == GameStartManager.StartingStates.Countdown) return;
                if (text == null) return;

                if (everyone) {
                    // Fix is active — confirm once in chat instead of drawing over GameStartText
                    // (which spammed every frame and corrupted the shared countdown element).
                    PostSnitchFixChatOnce();
                } else {
                    // Someone is missing the mod — only the host needs the heads-up, shown top-left.
                    // The game can still be started; the snitch bug may occur (Host Fix fallback handles it).
                    if (!AmongUsClient.Instance.AmHost) return;
                    string msg = mismatch +
                        "<color=#FFA500FF>The game can still be started, but the snitch bug may " +
                        "still occur (fallback: Host Fix).</color>";
                    if (!__instance.GameStartTextParent.activeSelf || string.IsNullOrEmpty(text.text)) {
                        text.text = msg;
                        var cam = Camera.main;
                        if (cam != null) {
                            // Map the camera's top-left viewport corner to world space, then inset slightly.
                            Vector3 tl = cam.ViewportToWorldPoint(new Vector3(0f, 1f, 10f));
                            tl.z = text.transform.position.z;
                            text.transform.position = tl + new Vector3(0.7f, -0.5f, 0f);
                        }
                        text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                        // Pivot to top-left so the text grows right/down from the corner.
                        text.rectTransform.pivot = new Vector2(0f, 1f);
                        text.transform.localScale = new Vector3(1.0f, 1.0f, 1f);
                        __instance.GameStartTextParent.SetActive(true);
                    } else {
                        text.text += "\n" + msg;
                    }
                }
            }
        }

        // Receive RPC 253 (Prefix with high priority → before the TOR switch handler).
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class UsefulHandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId == UsefulTORStuffPlugin.VersionHandshakeRpcId) {
                    try { ReceiveRpc(reader); } catch { }
                    return false;
                }
                return true;
            }
        }
    }
}
