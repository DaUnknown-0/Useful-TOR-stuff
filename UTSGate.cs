// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

// Audit 16.11. UTS Teil 3: Regeloptionen sind nur nach Empfang vom aktuellen Host aktiv.
// Nicht übertragene Optionen behalten ihre Config, werden aber mit defaultSelection gelesen.
// TOR-Optionen und ausdrücklich lokale Anzeigeoptionen bleiben davon ausgenommen.

using System;
using System.Collections.Generic;
using System.Linq;
using TheOtherRoles;

namespace UsefulTORStuff {
    public static class UTSGate {

        // ── Ownership: which CustomOptions belong to this mod ──────────────────────────────────

        private static readonly HashSet<int> ownOptionIds = new HashSet<int>();
        private static HashSet<int> preExistingIds;

        // Called immediately before the first CreateOptions() in Load().
        public static void BeginOptionCapture() {
            try {
                preExistingIds = new HashSet<int>(CustomOption.options.Select(o => o.id));
            } catch (Exception ex) {
                preExistingIds = null;
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Gate] option capture start failed: {ex.Message}");
            }
        }

        // Called after the last CreateOptions() in Load(). Everything that appeared in between is
        // ours. If the snapshot failed we end up with an EMPTY owned set, which means the gate has
        // no effect at all: features keep working exactly as they did before this class existed.
        // That is the deliberate failure direction. The alternative (assume everything is ours)
        // would gate TOR's own options and break the mod in every lobby.
        public static void EndOptionCapture() {
            try {
                if (preExistingIds == null) return;
                foreach (var o in CustomOption.options)
                    if (o != null && o.id != 0 && !preExistingIds.Contains(o.id)) ownOptionIds.Add(o.id);
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[Gate] {ownOptionIds.Count} own option(s) registered "
                    + $"(ids {string.Join(", ", ownOptionIds.OrderBy(i => i))}).");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Gate] option capture end failed: {ex.Message}");
            } finally {
                preExistingIds = null;
            }
        }

        // The option ids this mod created. Read by OptionSyncFix, which has to know which options a
        // client without this mod cannot possibly resolve.
        public static IEnumerable<int> OwnOptionIds => ownOptionIds;

        // ── Exemptions: our options that are NOT a rule change ────────────────────────────────
        //
        // The gate exists to stop ONE player from playing by numbers nobody else agreed to. An
        // option that cannot hand its owner an advantage over the others does not need it, and
        // switching such a thing off would only make the mod feel broken for no gain. Registered by
        // the feature itself (MarkAlwaysActive in its CreateOptions), so the reason lives next to
        // the option instead of in a list here that nobody reads.
        //
        // Exempt today: the meeting map ping (a communication tool - everyone with the mod sees it,
        // and it grants no information the pinger didn't already have) and the Drunk rename (a
        // display name for a TOR modifier, purely local cosmetics). The option-less features are
        // unaffected by the gate anyway: the Bloody drop throttle (a BepInEx config value, pure
        // performance), the map language toggle, and the plain bug fixes.
        private static readonly HashSet<int> alwaysActiveIds = new HashSet<int>();

        public static void MarkAlwaysActive(CustomOption option) {
            if (option == null) return;
            alwaysActiveIds.Add(option.id);
        }

        // ── The gate itself ───────────────────────────────────────────────────────────────────

        // Mod-Anwesenheit und empfangene IDs werden getrennt geprüft.
        private static bool hostHasMod = true;

        // Remembers what we last logged/announced, so the state change is reported once, not per frame.
        private static bool? lastAnnounced;

        public static bool SettingsActive {
            get {
                var client = AmongUsClient.Instance;
                if (client == null) return true;
                // As host our own values ARE the shared ones (we are the sender of the sync), so the
                // gate is open regardless of what any client is missing. Answered live rather than
                // from the latch so freeplay and host-side testing are never gated by a stale value
                // left over from a previous lobby.
                if (client.AmHost) return true;
                return hostHasMod;
            }
        }

        // Audit 16.11. UTS Teil 3: Anwesenheit allein sagt nichts über einzelne Options-IDs.
        // Nur in dieser Lobby vom aktuellen Host empfangene Optionen dürfen Regeln aktivieren.
        private static readonly HashSet<int> receivedHostOptions = new();
        private static int receivedHost = int.MinValue, receivedGame = int.MinValue;

        // Review 20.11. A1: Ein Hostwechsel in derselben Lobby (alter Host geht) leert die Freigaben
        // NICHT mehr. Die empfangenen Werte stammen vom bisher vereinbarten Host und sind auf allen
        // Clients gleich; der neue Host liest dieselben Werte über seine selection. Vorher fielen alle
        // Gäste auf Standardwerte zurück, der neue Host nicht (Desync bis zum nächsten Vollsync).
        // Nur ein Lobbywechsel (GameId) leert. Wird dieser Client selbst neuer Host, schickt er in der
        // Lobby einmal einen Vollsync (EvaluateInLobby), damit alle wieder denselben Stand bestätigt haben.
        private static bool resyncOwed;

        private static void CheckHost() {
            var client = AmongUsClient.Instance;
            if (client == null) return;
            if (receivedHost == client.HostId && receivedGame == client.GameId) return;
            if (receivedGame != client.GameId) {
                receivedHostOptions.Clear();
                resyncOwed = false;
            } else if (receivedHost != int.MinValue && client.AmHost) {
                resyncOwed = true;
            }
            receivedHost = client.HostId;
            receivedGame = client.GameId;
        }

        internal static void RecordHostOption(int id) {
            CheckHost();
            receivedHostOptions.Add(id);
        }

        public static void EvaluateInLobby() {
            try {
                var client = AmongUsClient.Instance;
                if (client == null) return;
                CheckHost();
                SetState(client.AmHost
                         || UsefulVersionHandshake.playerVersions.ContainsKey(client.HostId));
                if (resyncOwed) {
                    resyncOwed = false;
                    if (client.AmHost) {
                        UsefulTORStuffPlugin.Logger?.LogInfo("[Gate] became host in this lobby: sending a full option sync.");
                        PacedOptionSync.QueueFullSync();
                    }
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Gate] evaluation failed: {ex.Message}");
            }
        }

        public static void ResetOnGameJoined() {
            receivedHostOptions.Clear();
            receivedHost = receivedGame = int.MinValue;
            resyncOwed = false;
            SetState(AmongUsClient.Instance?.AmHost == true);
        }

        private static void SetState(bool active) {
            hostHasMod = active;
            if (lastAnnounced == active) return;
            lastAnnounced = active;

            UsefulTORStuffPlugin.Logger?.LogInfo(active
                ? "[Gate] settings-driven features are active."
                : "[Gate] host is missing TOR - Forgotten Fixes: settings-driven features are disabled "
                  + "locally (options fall back to their defaults).");
        }

        // ── Option readers ────────────────────────────────────────────────────────────────────
        // Drop-in replacements for CustomOption's getters. Non-owned options (TOR's own) always
        // read their real value; ours read defaultSelection while the gate is closed.

        private static int Index(CustomOption option) {
            if (option == null) return 0;
            CheckHost();
            bool localAuthority = AmongUsClient.Instance == null || AmongUsClient.Instance.AmHost;
            if (!ownOptionIds.Contains(option.id) || alwaysActiveIds.Contains(option.id)
                || localAuthority || (SettingsActive && receivedHostOptions.Contains(option.id)))
                return option.selection;
            return option.defaultSelection;
        }

        public static bool Bool(CustomOption option) => option != null && Index(option) > 0;

        public static int Sel(CustomOption option) => Index(option);

        public static int Qty(CustomOption option) => option == null ? 1 : Index(option) + 1;

        public static float Num(CustomOption option) {
            if (option == null) return 0f;
            try {
                return (float)option.selections[Index(option)];
            } catch {
                // Not a numeric option (or a selections array shorter than defaultSelection, which
                // cannot happen through CustomOption.Create). Fall back to TOR's own getter so a
                // mis-typed call site behaves exactly as it did before.
                try { return option.getFloat(); } catch { return 0f; }
            }
        }
    }
}
