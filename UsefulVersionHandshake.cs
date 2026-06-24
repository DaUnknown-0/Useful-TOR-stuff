// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace UsefulTORStuff {

    // Mod-presence handshake, modelled on TOR's own VersionHandshake (and the Chance mod's).
    // Every client with Useful TOR Stuff broadcasts its version + assembly GUID at lobby time
    // (RPC 253). Each client can then tell whether EVERY connected player runs the same build —
    // the precondition that gates the client-side Snitch reimplementation (SnitchLogic): it only
    // takes effect when all clients have the mod, and only then does HostFix's host-only
    // re-broadcast fallback stand down. The result is published in
    // UsefulTORStuffPlugin.SnitchClientFixActive and surfaced via lobby messages.
    public static class UsefulVersionHandshake {
        public static readonly Dictionary<int, PlayerVersion> playerVersions = new Dictionary<int, PlayerVersion>();
        private static bool versionSent;
        private static bool snitchFixChatShown;

        // Post the "snitch fix active" confirmation to the local chat once. The guard is re-armed
        // each lobby frame (see GameStartManagerUpdatePatch), so it fires once per started game.
        private static void PostSnitchFixChatOnce() {
            if (snitchFixChatShown) return;
            var hud = HudManager.Instance;
            if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
            snitchFixChatShown = true;

            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                "Snitch client-side fix active — all players have TOR - Forgotten Fixes.");
        }

        // Draw a message anchored to the top-left corner on TOR's shared GameStartText. Guards
        // against per-frame stacking on clients where TOR doesn't rebuild the element (non-host
        // clients: TOR only clears/rewrites GameStartText for the host).
        private static void DrawTopLeftMessage(GameStartManager gsm, TMPro.TMP_Text text, string msg, string marker) {
            if (text.text != null && text.text.Contains(marker)) return;
            text.text = string.IsNullOrEmpty(text.text) ? msg : text.text + "\n" + msg;
            var cam = Camera.main;
            if (cam != null) {
                // Map the camera's top-left viewport corner to world space, then inset slightly.
                Vector3 tl = cam.ViewportToWorldPoint(new Vector3(0f, 1f, 10f));
                tl.z = text.transform.position.z;
                text.transform.position = tl + new Vector3(0.7f, -0.5f, 0f);
            }
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            // Pivot to top-left so the text grows right/down from the corner, never off-screen.
            text.rectTransform.pivot = new Vector2(0f, 1f);
            text.transform.localScale = new Vector3(1.0f, 1.0f, 1f);
            gsm.GameStartTextParent.SetActive(true);
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
                    message += $"<color=#FF0000FF>{name} is missing TOR - Forgotten Fixes (or has a different version)\n</color>";
                    continue;
                }

                int diff = UsefulTORStuffPlugin.Version.CompareTo(pv.version);
                if (diff > 0)
                    message += $"<color=#FF0000FF>{name} has an older TOR - Forgotten Fixes (v{pv.version})\n</color>";
                else if (diff < 0)
                    message += $"<color=#FF0000FF>{name} has a newer TOR - Forgotten Fixes (v{pv.version})\n</color>";
                else if (!pv.GuidMatches())
                    message += $"<color=#FF0000FF>{name} has a modified TOR - Forgotten Fixes v{pv.version} <size=30%>({pv.guid})</size>\n</color>";
            }
            return message;
        }

        // --- F1: Cross-mod lobby handshake board (presentation-layer merge) ---
        // Same documented AppDomain contract as ChanceVersionHandshake (plain strings /
        // Dictionary<int,string> only):
        //   TORMods.Handshake.Registry        → comma-separated guids that have published
        //   TORMods.Handshake.{guid}.name     → short display name
        //   TORMods.Handshake.{guid}.status   → Dictionary<int,string>: clientId → "codeversion"
        //                                       code ∈ ok | old | new | mod ; missing clients omitted
        // UsefulTORStuff is the OWNER of the combined per-player overview when it is loaded. The wire
        // format (RPC 253) is untouched; this is a rendering merge only. Host-only by default.
        private const string HandshakeRegistryKey = "TORMods.Handshake.Registry";
        private const string HandshakeKeyPrefix = "TORMods.Handshake.";
        private const string UsefulGuid = "com.tormod.usefultorstuff";
        private const string ChanceGuid = "com.tormod.chancemodifier";
        private const char StatusSep = '';

        // Single switch for visibility. Default = current behaviour (host-side warnings only).
        private const bool ShowToAllPlayers = false;

        private static void PublishSnapshot() {
            try {
                if (AmongUsClient.Instance == null) return;
                var status = new Dictionary<int, string>();
                foreach (var kv in playerVersions) {
                    PlayerVersion pv = kv.Value;
                    string code;
                    int diff = UsefulTORStuffPlugin.Version.CompareTo(pv.version);
                    if (diff > 0) code = "old";
                    else if (diff < 0) code = "new";
                    else code = pv.GuidMatches() ? "ok" : "mod";
                    status[kv.Key] = code + StatusSep + pv.version;
                }
                AppDomain.CurrentDomain.SetData(HandshakeKeyPrefix + UsefulGuid + ".name", "Useful");
                AppDomain.CurrentDomain.SetData(HandshakeKeyPrefix + UsefulGuid + ".status", status);
                var reg = AppDomain.CurrentDomain.GetData(HandshakeRegistryKey) as string ?? "";
                if (!reg.Split(',').Contains(UsefulGuid))
                    AppDomain.CurrentDomain.SetData(HandshakeRegistryKey, reg == "" ? UsefulGuid : reg + "," + UsefulGuid);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"Handshake snapshot publish failed: {ex.Message}");
            }
        }

        // True when the Chance mod is loaded (so the combined overview must include its column).
        private static bool ChancePresent() =>
            AppDomain.CurrentDomain.GetData("ModManager.RegisteredMod." + ChanceGuid) != null;

        // Builds the combined "Mod-Check" overview from every published handshake snapshot. Sets
        // anyWarn = true if any player is missing/mismatched for any present mod. Returns "" only on
        // error/no data; the all-match case returns a single green confirmation line.
        private static string BuildCombinedModCheck(out bool anyWarn) {
            anyWarn = false;
            if (AmongUsClient.Instance == null) return "";

            var reg = AppDomain.CurrentDomain.GetData(HandshakeRegistryKey) as string ?? "";
            var guids = reg.Split(',').Where(g => g.Length > 0).Distinct().OrderBy(g => g).ToList();
            if (guids.Count == 0) return "";

            // Resolve each mod's name + status dict once.
            var names = new Dictionary<string, string>();
            var stats = new Dictionary<string, Dictionary<int, string>>();
            foreach (var g in guids) {
                names[g] = AppDomain.CurrentDomain.GetData(HandshakeKeyPrefix + g + ".name") as string ?? g;
                stats[g] = AppDomain.CurrentDomain.GetData(HandshakeKeyPrefix + g + ".status") as Dictionary<int, string>
                           ?? new Dictionary<int, string>();
            }

            var sb = new StringBuilder();
            foreach (InnerNet.ClientData client in AmongUsClient.Instance.allClients.ToArray()) {
                if (client == null || client.Character == null) continue;
                string name = client.Character.Data.PlayerName;
                var segments = new List<string>();
                foreach (var g in guids) {
                    string label = names[g];
                    if (stats[g].TryGetValue(client.Id, out string token)) {
                        int sep = token.IndexOf(StatusSep);
                        string code = sep >= 0 ? token.Substring(0, sep) : token;
                        string ver = sep >= 0 ? token.Substring(sep + 1) : "?";
                        if (code == "ok") {
                            segments.Add($"<color=#3FCF4AFF>{label} {ver} ✓</color>");
                        } else if (code == "mod") {
                            anyWarn = true;
                            segments.Add($"<color=#FF0000FF>{label} {ver} (modified)</color>");
                        } else {
                            anyWarn = true;
                            segments.Add($"<color=#FF0000FF>{label} {ver} ✗</color>");
                        }
                    } else {
                        anyWarn = true;
                        segments.Add($"<color=#AAAAAAFF>{label} — missing</color>");
                    }
                }
                sb.Append($"<color=#FFFFFFFF>{name}</color>  ");
                sb.Append(string.Join(" <color=#888888FF>|</color> ", segments));
                sb.Append("\n");
            }

            if (!anyWarn)
                return "<color=#3FCF4AFF>Mod-Check: all players match ✓</color>";

            return "<color=#FFD700FF>Mod-Check:</color>\n" + sb.ToString();
        }

        // P1.5: Beim Betreten einer Lobby den Versions-Cache leeren. ClientIds sind
        // verbindungsskopiert, sodass alte Einträge sonst nur leaken — das Dictionary soll aber
        // ausschließlich die aktuelle Lobby widerspiegeln.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
            public static void Postfix() {
                playerVersions.Clear();
                versionSent = false;
            }
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

                // F1: publish our own snapshot every lobby frame so the combined overview (which we
                // own) and any future renderer can read it uniformly.
                PublishSnapshot();

                // Re-arm the chat post each lobby frame so it fires once per started game (the actual
                // post happens at game start in IntroEndChatPatch, after this stops running).
                snitchFixChatShown = false;

                // Compute on EVERY client. GameStartManager.Update only runs in the lobby, so this
                // value naturally persists (latches) into the game that follows.
                string mismatch = BuildMismatchMessage();
                bool everyone = mismatch == "";
                // "Aktiv" nur, wenn ALLE den Mod haben UND der client-seitige Snitch-Fix hier lokal
                // lauffähig ist (Chat-Reveal + Room-Recorder aufgelöst). Sonst stünde HostFix still,
                // obwohl gar kein funktionierender Client-Fix läuft (alle Clients teilen denselben
                // Build/dieselbe TOR-Version → lokale Readiness = globale). Genau dieses Flag gatet
                // zur Laufzeit die Reveal-Reimplementierungen in SnitchLogic.
                UsefulTORStuffPlugin.SnitchClientFixActive = everyone && SnitchLogic.ClientFixReady;

                var text = __instance.GameStartText;

                // During the countdown TOR shows its own centred "Starting in X" on this shared
                // element. Our lobby messages move the pivot to top-left, and TOR resets position/
                // scale each frame but never the pivot — leaving it would displace the countdown.
                // Restore the centred pivot and stop drawing while the countdown runs.
                if (__instance.startState == GameStartManager.StartingStates.Countdown) {
                    if (text != null) text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    return;
                }
                if (text == null) return;

                // Sheriff parity-win warning: the feature is host-enforced and always applies, but
                // clients without the mod don't see the option. Warn the host (own marker so it
                // coexists with the Snitch messages below; placed before their non-host returns).
                if (AmongUsClient.Instance.AmHost
                    && SheriffParityWin.Option != null && SheriffParityWin.Option.getBool()
                    && !everyone) {
                    DrawTopLeftMessage(__instance, text,
                        "<color=#FFA500FF>'Sheriff Prevents Killer Parity Win' is ON, but not all players have "
                        + "TOR - Forgotten Fixes — they won't see this rule. It still applies (host-enforced).</color>",
                        "Sheriff Prevents Killer Parity Win");
                }

                // F1: when the Chance mod is also present we OWN the combined per-player overview —
                // draw it here (host-only unless ShowToAllPlayers). It replaces BOTH mods' standalone
                // version lists; Chance suppresses its own block while we are loaded.
                bool chancePresent = ChancePresent();
                if (chancePresent && (ShowToAllPlayers || AmongUsClient.Instance.AmHost)) {
                    string combined = BuildCombinedModCheck(out _);
                    if (combined != "")
                        DrawTopLeftMessage(__instance, text, combined, "Mod-Check:");
                }

                if (UsefulTORStuffPlugin.SnitchClientFixActive) {
                    // All players have the mod AND the client-side Snitch fix is locally ready —
                    // no lobby message. The active-fix confirmation is posted once to chat at game
                    // start (IntroEndChatPatch); the top-left lobby text is intentionally omitted so
                    // a Snitch lobby message only ever appears host-only when something is wrong.
                } else if (everyone) {
                    // Everyone has the mod, but the client-side fix is NOT locally ready here (TOR
                    // version drift → missing reflection handles). The client fix is NOT active, so the
                    // Host Fix fallback stays armed — say so instead of falsely claiming the fix is
                    // active. Only the host needs this heads-up.
                    if (!AmongUsClient.Instance.AmHost) return;
                    DrawTopLeftMessage(__instance, text,
                        "<color=#FFA500FF>All players have TOR - Forgotten Fixes, but the client-side Snitch fix " +
                        "is NOT active (TOR mismatch). Falling back to TOR - Hostfix re-broadcast.</color>",
                        "Snitch fix is NOT active");
                } else {
                    // Someone is missing the mod — only the host needs the heads-up, shown top-left.
                    // The game can still be started; the snitch bug may occur (Host Fix fallback handles it).
                    if (!AmongUsClient.Instance.AmHost) return;
                    // F1: when Chance is present the combined Mod-Check block above already lists the
                    // per-player versions, so drop the standalone mismatch prefix and show only the
                    // fallback note. Otherwise keep the full standalone list (single-mod install).
                    string prefix = chancePresent ? "" : mismatch;
                    DrawTopLeftMessage(__instance, text,
                        prefix + "<color=#FFA500FF>The game can still be started, but the snitch bug " +
                        "may still occur (fallback: Host Fix re-broadcast).</color>",
                        "fallback: Host Fix");
                }
            }
        }

        // Post the active-fix confirmation to chat once the game has actually started (intro ended).
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndChatPatch {
            public static void Postfix() {
                if (UsefulTORStuffPlugin.SnitchClientFixActive)
                    PostSnitchFixChatOnce();
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
