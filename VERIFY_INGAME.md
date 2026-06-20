# In-game verification checklist — TOR - Forgotten Fixes 1.1.0

## F1 — Combined lobby Mod-Check (needs Chance installed + a second client)
- [ ] With Chance + Useful installed: in a lobby with another player, the host sees **one**
      "Mod-Check:" block listing each player's Chance/Useful status (not two separate red lists).
- [ ] A player missing one mod shows "— missing" (gray) for that column; a different version shows
      red; all-matching collapses to "Mod-Check: all players match ✓".
- [ ] With Chance NOT installed: Forgotten Fixes shows its original standalone "missing TOR - Forgotten Fixes"
      list (unchanged single-mod behaviour).
- [ ] HostFix is never listed in the combined block.
- [ ] Block is host-only by default and does not stack per frame.

## F2 — Update All + release notes (needs a published newer release)
- [ ] When ≥1 mod has an update, the header **UPDATE ALL** button is enabled; with none, it is
      greyed out.
- [ ] Clicking it downloads each updatable mod one after another (never in parallel), then shows
      "N updated — restart required" (or "N updated, M failed — …").
- [ ] An expanded mod entry with an available update shows "What's new:" notes (≤10 lines / ~600
      chars, markdown stripped, "…" when truncated) with no extra network calls.
- [ ] A mod whose update check failed (rate limit) is skipped/counted as failed, not a crash.



## P1.1 — Bloody throttle reset across games
- [ ] Play a game with a Bloody-modifier player who drops a trail. Start a **new** game; confirm a
      bloody player near the previous game's last drop position drops blood normally from the start
      (no silently-skipped first drops). Log shows
      `Patched resetVariables() — Bloody throttle drop map cleared each round.` at load.

## P1.2 — ModManagerUI asset reuse
- [ ] Open the Mod Manager repeatedly and toggle several mods on/off many times. Memory should not
      climb per open/toggle (textures/sprites/material are now shared/cached, not recreated).
- [ ] Visuals unchanged: panel, entry backgrounds, toggle button colours (green/red/orange), and the
      overlay dim still render correctly.

## P1.5 — Handshake cache reset
- [ ] Leave and re-join lobbies; the mod-presence list reflects only current-lobby players.

## P2.3 — PingTracker line
- [ ] "TOR - Forgotten Fixes vX" appears once in the top-corner version block, never stacking per frame.

## Regression sweep (unchanged behaviour to confirm still works)
- [ ] Bloody lag throttle still thins drops while moving.
- [ ] Client-side Snitch reveal still gated on all-players-have-mod; HostFix stands down accordingly.
- [ ] Sheriff parity-win option + host warning still shown.
