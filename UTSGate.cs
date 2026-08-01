// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UTSGate - "the host does not have this mod" gate for every settings-driven feature.
 *
 * THE PROBLEM
 * TOR's option sync is host-driven: CustomOption.ShareOptionSelections() walks the host's
 * CustomOption.options list and broadcasts (id, selection) pairs; every client writes those onto
 * the option with the matching id (CustomOptions.cs, ShareOptions RPC). An option the host does
 * not HAVE is never in that list, so it is never sent, so the client keeps whatever value its own
 * BepInEx config last stored (CustomOption ctor: selection = entry.Value).
 *
 * Consequence: a client running Forgotten Fixes in a lobby whose host does NOT run it keeps every
 * one of our options at its own locally saved value, while nobody else in the lobby plays by those
 * values. That is not a cosmetic desync, it is a one-sided rule change: shorter sabotage cooldowns,
 * extra Mini/Armored/Tiebreaker holders, the Medic's unshield button, the Lawyer/Lover map tracker,
 * a Trickster mixup sabotage that only exists on one screen, ... all of it silently in that one
 * player's favour, and all of it invisible to everyone else.
 *
 * THE RULE
 * A settings-driven feature may only act when the settings it reads are the ones the whole lobby
 * agreed on, i.e. when the HOST has this mod (then the host's values arrive via TOR's normal sync
 * and everyone reads the same numbers). We are always allowed to act when we ARE the host, since
 * then our values are what gets shared.
 *
 * HOW
 * Not by rewriting CustomOption.selection: the host's own sync writes into that same field, so
 * blanking it would race the sync and (via TOR's preset/config paths) risk persisting the blanked
 * value. Instead every feature reads its options THROUGH this class, which returns the option's
 * defaultSelection while the gate is closed. Every option in this mod defaults to "the behaviour
 * TOR has without it" (toggles default Off, quantity options default to 1), so a closed gate is
 * exactly vanilla TOR behaviour, with no state touched and nothing to restore afterwards.
 *
 * WHICH OPTIONS
 * Only the ones this mod created. Features also read TOR's own options (CustomOptionHolder.*) and
 * those come from the host over the normal sync, so they must keep their real value. The owned set
 * is captured automatically in UsefulTORStuffPlugin.Load(): snapshot CustomOption.options before
 * our CreateOptions() calls, diff afterwards. That way a future feature is gated the day it is
 * written, with no registration list to forget to update.
 *
 * WHAT IS NOT GATED
 * Bugfixes without an option (Bloody throttle, Bloody killer map, Trapper shift charges, sound at
 * position, Snitch client fix - which has its own, stricter "everyone has the mod" gate) stay
 * active: they repair TOR behaviour instead of changing the agreed rules, and they cost nobody
 * anything. Local tools (Mod Manager, WebConfig, lobby password gate, map language toggle) are
 * likewise untouched; WebConfig deliberately reads CustomOption.selection directly, so the host's
 * settings editor always shows the real stored values.
 */

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

        // Latched in the lobby (GameStartManager.Update stops running once the game starts), so the
        // value a round plays under is the one that was true when it started. Optimistic default:
        // "open" until a lobby frame proves otherwise, so main menu / freeplay / any code path that
        // never reaches a lobby behaves normally.
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

        // Recomputed every lobby frame from the mod-presence handshake (UsefulVersionHandshake).
        // The host's client id is AmongUsClient.Instance.HostId - the same lookup TOR itself uses to
        // detect a host without TOR (GameStartManagerPatch.cs:147). Version differences are NOT
        // checked here: a host with any build of this mod still shares OUR option ids, which is all
        // the gate needs. Mismatched versions are surfaced separately by the Mod-Check board.
        public static void EvaluateInLobby() {
            try {
                var client = AmongUsClient.Instance;
                if (client == null) return;
                SetState(client.AmHost
                         || UsefulVersionHandshake.playerVersions.ContainsKey(client.HostId));
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Gate] evaluation failed: {ex.Message}");
            }
        }

        // Joining a lobby resets the latch to "open": the handshake dictionary was just cleared, so
        // an immediate false would only mean "nobody has answered yet". The first lobby frames after
        // the host's broadcast settle it, long before a round can start.
        public static void ResetOnGameJoined() => SetState(true);

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
            if (SettingsActive || !ownOptionIds.Contains(option.id) || alwaysActiveIds.Contains(option.id))
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
