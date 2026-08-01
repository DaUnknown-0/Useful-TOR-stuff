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
        private static bool gateChatShown;

        // Post the "snitch fix active" confirmation to the local chat once. The guard is re-armed
        // each lobby frame (see GameStartManagerUpdatePatch), so it fires once per started game.
        private static void PostSnitchFixChatOnce() {
            if (snitchFixChatShown) return;
            var hud = HudManager.Instance;
            if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
            snitchFixChatShown = true;

            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                UTSLocalization.Tr("uts.versionhandshake.snitch_fix_active_chat"));
        }

        // Same one-shot mechanism for the settings gate: tell the local player once per round that
        // their own option values are not in play, because the host doesn't run this mod. Without
        // this the effect is invisible - buttons and timers simply behave like vanilla TOR and it
        // looks like the mod is broken rather than deliberately standing down.
        private static void PostGateChatOnce() {
            if (gateChatShown) return;
            var hud = HudManager.Instance;
            if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
            gateChatShown = true;

            hud.Chat.AddChat(PlayerControl.LocalPlayer, UTSLocalization.Tr("uts.gate.chat_disabled"));
        }

        // Draw a message anchored to the top-left corner on TOR's shared GameStartText. Guards
        // against per-frame stacking on clients where TOR doesn't rebuild the element (non-host
        // clients: TOR only clears/rewrites GameStartText for the host). The guard remembers the
        // last text rendered per marker ID and probes for THAT, not for an English substring -
        // display texts are localized (UTSLocalization), so a fixed-literal probe would miss and
        // stack the message every frame in any non-English language.
        private static readonly Dictionary<string, string> lastRenderedByMarker = new();
        private static void DrawTopLeftMessage(GameStartManager gsm, TMPro.TMP_Text text, string msg, string marker) {
            if (text.text != null && lastRenderedByMarker.TryGetValue(marker, out var prev)
                && text.text.Contains(prev)) return;
            lastRenderedByMarker[marker] = msg;
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

        // Receiver registration for the consolidated RPC channel (UTSRpc.CallId = 240). Called once
        // from UsefulTORStuffPlugin.Load() - this module has no other load-time entry point (all its
        // patches are attribute-based and picked up by PatchAll).
        public static void RegisterRpc() {
            UTSRpc.Register(UsefulTORStuffPlugin.VersionHandshakeRpcId, HandleModuleRpc);
        }

        private static void HandleModuleRpc(MessageReader reader) {
            // No subtype byte (there never was one) - the payload starts straight at the version
            // bytes, exactly as it did when 253 was a standalone callId.
            try { ReceiveRpc(reader); } catch { }
        }

        public static void ShareVersion() {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            var v = UsefulTORStuffPlugin.Version;
            int clientId = AmongUsClient.Instance.ClientId;
            byte[] guidBytes = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToByteArray();

            // LEGACY DUAL-SEND (see UTSRpc.cs): legacy callId 253 + consolidated channel 240. This one
            // MUST keep the legacy path for the foreseeable future - the handshake is precisely the
            // message an older build has to be able to read, otherwise a mixed lobby would show
            // "missing mod" for everyone running a pre-240 version. Classified IDEMPOTENT: Receive()
            // only writes playerVersions[clientId] = new PlayerVersion(...), so both copies store the
            // identical entry under the identical key.
            UTSRpc.SendDual(UsefulTORStuffPlugin.VersionHandshakeRpcId,
                            UsefulTORStuffPlugin.VersionHandshakeRpcId,
                            writer => {
                                writer.Write((byte)v.Major);
                                writer.Write((byte)v.Minor);
                                writer.Write((byte)v.Build);
                                writer.WritePacked(clientId);
                                writer.Write((byte)(v.Revision < 0 ? 0xFF : v.Revision));
                                writer.Write(guidBytes);
                            });

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
                    message += UTSLocalization.Tr("uts.versionhandshake.missing_mod", name);
                    continue;
                }

                int diff = UsefulTORStuffPlugin.Version.CompareTo(pv.version);
                if (diff > 0)
                    message += UTSLocalization.Tr("uts.versionhandshake.older_mod", name, pv.version);
                else if (diff < 0)
                    message += UTSLocalization.Tr("uts.versionhandshake.newer_mod", name, pv.version);
                else if (!pv.GuidMatches())
                    message += UTSLocalization.Tr("uts.versionhandshake.modified_mod", name, pv.version, pv.guid);
            }
            return message;
        }

        // True when EVERY connected player runs this same build. The precondition for features that
        // are reimplemented on each client rather than enforced by the host: extra Mini/Armored
        // holders (MultiModifiers) and extra Jesters (MultiJester) only exist in the clients that
        // run this code, so a single player without it would see and play a different game.
        public static bool EveryoneHasMod() {
            try { return BuildMismatchMessage() == ""; }
            catch { return false; }
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

        // True when ANY other mod has published a handshake snapshot besides us (Chance, Unknown's
        // Collection, future mods). The combined Mod-Check overview is drawn whenever there is more
        // than one column to merge - not just when Chance specifically is installed.
        private static bool OtherModsPublished() {
            try {
                var reg = AppDomain.CurrentDomain.GetData(HandshakeRegistryKey) as string ?? "";
                return reg.Split(',').Any(g => g.Length > 0 && g != UsefulGuid);
            } catch { return false; }
        }

        // A client mismatches when, for ANY published mod, it has no handshake entry or a non-"ok"
        // status - the same per-row rule the combined Mod-Check board uses.
        private static bool ClientMismatched(int clientId) {
            try {
                var reg = AppDomain.CurrentDomain.GetData(HandshakeRegistryKey) as string ?? "";
                foreach (var g in reg.Split(',')) {
                    if (g.Length == 0) continue;
                    var stats = AppDomain.CurrentDomain.GetData(HandshakeKeyPrefix + g + ".status")
                                as Dictionary<int, string>;
                    if (stats == null || !stats.TryGetValue(clientId, out string token)) return true;
                    int sep = token.IndexOf(StatusSep);
                    string code = sep >= 0 ? token.Substring(0, sep) : token;
                    if (code != "ok") return true;
                }
                return false;
            } catch { return false; }
        }

        // Lobby name-tag tint: players whose mods don't match get their name ABOVE THEIR HEAD in
        // Impostor red, so the odd one out is visible at a glance without reading the Mod-Check
        // board. Only names WE tinted are ever reset back to white (tintedClients), so this never
        // fights another mod's own lobby name colouring. Lobby-only by construction: called from
        // GameStartManager.Update, and TOR re-manages name colours once the game starts.
        private static readonly HashSet<int> tintedClients = new HashSet<int>();
        private static void TintMismatchedLobbyNames() {
            try {
                if (AmongUsClient.Instance == null) return;
                foreach (InnerNet.ClientData client in AmongUsClient.Instance.allClients.ToArray()) {
                    var nameText = client?.Character?.cosmetics?.nameText;
                    if (nameText == null) continue;
                    if (ClientMismatched(client.Id)) {
                        nameText.color = Palette.ImpostorRed;
                        tintedClients.Add(client.Id);
                    } else if (tintedClients.Remove(client.Id)) {
                        nameText.color = Color.white;
                    }
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"Lobby name tint failed: {ex.Message}");
            }
        }

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
                bool playerMismatch = false;
                foreach (var g in guids) {
                    string label = names[g];
                    if (stats[g].TryGetValue(client.Id, out string token)) {
                        int sep = token.IndexOf(StatusSep);
                        string code = sep >= 0 ? token.Substring(0, sep) : token;
                        string ver = sep >= 0 ? token.Substring(sep + 1) : "?";
                        if (code == "ok") {
                            segments.Add(UTSLocalization.Tr("uts.versionhandshake.modcheck_row_ok", label, ver));
                        } else if (code == "mod") {
                            anyWarn = true; playerMismatch = true;
                            segments.Add(UTSLocalization.Tr("uts.versionhandshake.modcheck_row_modified", label, ver));
                        } else {
                            anyWarn = true; playerMismatch = true;
                            segments.Add(UTSLocalization.Tr("uts.versionhandshake.modcheck_row_bad", label, ver));
                        }
                    } else {
                        anyWarn = true; playerMismatch = true;
                        segments.Add(UTSLocalization.Tr("uts.versionhandshake.modcheck_row_missing", label));
                    }
                }
                // Players whose mods DON'T match the reference build (the snapshots compare against
                // the local = host install, since the board is host-rendered) are named in Impostor
                // red, so the odd ones out are visible at a glance. Matching players stay white.
                string nameColor = playerMismatch
                    ? "#" + UnityEngine.ColorUtility.ToHtmlStringRGBA(Palette.ImpostorRed)
                    : "#FFFFFFFF";
                sb.Append($"<color={nameColor}>{name}</color>  ");
                sb.Append(string.Join(" <color=#888888FF>|</color> ", segments));
                sb.Append("\n");
            }

            // <size=300%> — the per-player board was still hard to read at 130% (playtest feedback:
            // "mindestens doppelt so groß"), so roughly 2.3x that again.
            if (!anyWarn)
                return UTSLocalization.Tr("uts.versionhandshake.modcheck_all_ok");

            return UTSLocalization.Tr("uts.versionhandshake.modcheck_header") + sb.ToString() + "</size>";
        }

        // P1.5: Beim Betreten einer Lobby den Versions-Cache leeren. ClientIds sind
        // verbindungsskopiert, sodass alte Einträge sonst nur leaken — das Dictionary soll aber
        // ausschließlich die aktuelle Lobby widerspiegeln.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
            public static void Postfix() {
                playerVersions.Clear();
                versionSent = false;
                tintedClients.Clear(); // client ids are per-connection; never carry tints across lobbies
                // The handshake is empty again, so "the host has no mod" cannot be concluded from it
                // yet: re-open the settings gate until the first lobby frames have re-evaluated it.
                UTSGate.ResetOnGameJoined();
                gateChatShown = false;
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

                // Settings gate: does the HOST have this mod? Only then do its option values reach
                // everyone through TOR's normal option sync, which is the precondition for any
                // settings-driven feature to be allowed to act (UTSGate). Evaluated every lobby
                // frame and latched into the round that follows, exactly like the mismatch state below.
                UTSGate.EvaluateInLobby();

                // F1: publish our own snapshot every lobby frame so the combined overview (which we
                // own) and any future renderer can read it uniformly.
                PublishSnapshot();

                // Red name tag above mismatched players' heads (every client, every lobby frame).
                TintMismatchedLobbyNames();

                // Re-arm the chat post each lobby frame so it fires once per started game (the actual
                // post happens at game start in IntroEndChatPatch, after this stops running).
                snitchFixChatShown = false;
                gateChatShown = false;

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

                // Settings gate warning. This is the one lobby message aimed at the CLIENT, not the
                // host: our own options are not being synced by this host, so everything settings-
                // driven falls back to its default and the round is played by TOR's rules. Drawn
                // before the host-only blocks below, which all return early for non-hosts.
                if (!UTSGate.SettingsActive) {
                    DrawTopLeftMessage(__instance, text,
                        UTSLocalization.Tr("uts.gate.host_missing_mod"),
                        "settings gate: host without mod");
                }

                // Sheriff parity-win warning: the feature is host-enforced and always applies, but
                // clients without the mod don't see the option. Warn the host (own marker so it
                // coexists with the Snitch messages below; placed before their non-host returns).
                if (AmongUsClient.Instance.AmHost
                    && SheriffParityWin.Option != null && UTSGate.Bool(SheriffParityWin.Option)
                    && !everyone) {
                    DrawTopLeftMessage(__instance, text,
                        UTSLocalization.Tr("uts.versionhandshake.sheriff_parity_warning"),
                        "Sheriff Prevents Killer Parity Win");
                }

                // Multi-Jester warning: the extra Jesters exist only inside this mod (role display,
                // neutral status and win condition are reimplemented per client), so the feature
                // stands down entirely unless everyone has it. Tell the host that his setting is
                // not in effect rather than letting him find out after the round.
                if (AmongUsClient.Instance.AmHost
                    && MultiJester.Quantity != null && MultiJester.ConfiguredQuantity > 1
                    && !everyone) {
                    DrawTopLeftMessage(__instance, text,
                        UTSLocalization.Tr("uts.versionhandshake.multijester_warning"),
                        "Jester Quantity");
                }

                // Lover "Delay Lover Death / Revenger" warning: this feature is client-side and only
                // works when everyone has the mod (unlike the host-enforced Sheriff parity win). Warn
                // the host when it is ON but someone is missing the mod — it will simply not apply.
                if (AmongUsClient.Instance.AmHost
                    && LoverRevenger.DelayOption != null && UTSGate.Bool(LoverRevenger.DelayOption)
                    && !everyone) {
                    DrawTopLeftMessage(__instance, text,
                        UTSLocalization.Tr("uts.versionhandshake.revenger_warning"),
                        "Delay Lover Death (Revenger)");
                }

                // F1: when any other handshake-publishing mod is present (Chance, Unknown's
                // Collection, ...) we OWN the combined per-player overview — draw it here (host-only
                // unless ShowToAllPlayers). It replaces the mods' standalone version lists; Chance
                // suppresses its own block while we are loaded.
                bool combinedShown = OtherModsPublished();
                if (combinedShown && (ShowToAllPlayers || AmongUsClient.Instance.AmHost)) {
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
                        UTSLocalization.Tr("uts.versionhandshake.snitch_fix_not_active"),
                        "Snitch fix is NOT active");
                } else {
                    // Someone is missing the mod — only the host needs the heads-up, shown top-left.
                    // The game can still be started; the snitch bug may occur (Host Fix fallback handles it).
                    if (!AmongUsClient.Instance.AmHost) return;
                    // F1: when the combined Mod-Check block above already lists the per-player
                    // versions, drop the standalone mismatch prefix and show only the fallback note.
                    // Otherwise keep the full standalone list (single-mod install).
                    string prefix = combinedShown ? "" : mismatch;
                    DrawTopLeftMessage(__instance, text,
                        UTSLocalization.Tr("uts.versionhandshake.snitch_fallback_warning", prefix),
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
                if (!UTSGate.SettingsActive)
                    PostGateChatOnce();
            }
        }

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 253 from pre-240 builds
        // (Prefix with high priority → before the TOR switch handler).
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
