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
                    message += $"<color=#FF0000FF>{name} fehlt Useful TOR Stuff (oder hat eine andere Version)\n</color>";
                    continue;
                }

                int diff = UsefulTORStuffPlugin.Version.CompareTo(pv.version);
                if (diff > 0)
                    message += $"<color=#FF0000FF>{name} hat ein aelteres Useful TOR Stuff (v{pv.version})\n</color>";
                else if (diff < 0)
                    message += $"<color=#FF0000FF>{name} hat ein neueres Useful TOR Stuff (v{pv.version})\n</color>";
                else if (!pv.GuidMatches())
                    message += $"<color=#FF0000FF>{name} hat ein modifiziertes Useful TOR Stuff v{pv.version} <size=30%>({pv.guid})</size>\n</color>";
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

        // Runs after TOR's own GameStartManager.Update postfix (Priority.Low) so we append to the
        // GameStartText TOR rebuilds each frame instead of fighting over it (coexists with Chance).
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

                if (__instance.startState == GameStartManager.StartingStates.Countdown) return;

                var text = __instance.GameStartText;
                if (text == null) return;

                if (everyone) {
                    // Patch is active — small, unobtrusive confirmation tucked into the top-left corner.
                    string msg = "<color=#3FCF4AFF>Snitch-Fix aktiv — alle Spieler haben Useful TOR Stuff.</color>";
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
                        // Pivot to top-left so the (now larger) text grows right/down from the
                        // corner instead of widening across its centre and spilling off-screen.
                        text.rectTransform.pivot = new Vector2(0f, 1f);
                        text.transform.localScale = new Vector3(1.0f, 1.0f, 1f);
                        __instance.GameStartTextParent.SetActive(true);
                    } else {
                        text.text += "\n" + msg;
                    }
                } else {
                    // Someone is missing the mod — only the host needs the heads-up, shown centered.
                    // The game can still be started; the snitch bug may occur (Host Fix fallback handles it).
                    if (!AmongUsClient.Instance.AmHost) return;
                    string msg = mismatch +
                        "<color=#FFA500FF>Das Spiel kann gestartet werden, aber der Snitch-Bug kann " +
                        "weiterhin auftreten (Fallback: Host Fix).</color>";
                    if (!__instance.GameStartTextParent.activeSelf || string.IsNullOrEmpty(text.text)) {
                        text.text = msg;
                        // Restore the centred pivot in case the everyone-branch moved it to top-left.
                        text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                        text.transform.localPosition = __instance.StartButton.transform.localPosition + Vector3.up * 5;
                        text.transform.localScale = new Vector3(2f, 2f, 1f);
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
