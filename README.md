# TOR - Forgotten Fixes

A companion plugin for [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR)
4.8.0 that bundles several quality-of-life fixes, a pile of new role options, and a cross-mod Mod
Manager. It resolves TOR types via reflection, so every patch degrades to a no-op (with a log
warning) rather than crashing if TOR's internals change. Most win-checks and meeting overrides are
host-authoritative — they apply regardless of who has the mod.

> Formerly *Useful TOR Stuff*. The DLL is still `UsefulTORStuff.dll` and the repository is still
> `Useful-TOR-stuff`; only the in-game name changed to **TOR - Forgotten Fixes**.

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not
endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are
property of Innersloth LLC. © Innersloth LLC.

## Bugfixes (automatic — no option needed)

- **Bloody lag throttle** — TOR spawns a new `Bloodytrail` GameObject on every FixedUpdate (~50/s),
  up to ~500 per bloody player. A new drop now only spawns once the player has moved at least
  `MinDropDistance` (default `0.35` units) since the last drop. The per-player last-drop map is
  cleared each round.
- **Bloody killer-map fix** — `Bloody.bloodyKillerMap[killer]` used to stay pinned to the first
  victim, so trails after the second kill had the wrong color. The map entry is now overwritten via
  indexer so a trail tracks the *latest* victim.
- **Client-side Snitch reveal** — a reimplementation of the Snitch reveal that runs on every client,
  built on a persistent own room map (recorded from `ShareRoom` RPCs) instead of TOR's
  `playerRoomMap`, which the host loses on reset. Gated on *all players having this mod*
  (`SnitchClientFixActive`); when active, the host-only TOR - Hostfix re-broadcast stands down.
  Surfaced via lobby messages and a one-time chat confirmation.

## New role options

Options appear in TOR's own settings tabs, directly under the relevant role.

### Crewmate

| Option | Default | Notes |
|---|---|---|
| Sheriff Prevents Killer Parity Win | Off | Host-authoritative; suppresses the impostor/Jackal parity win while a Sheriff is alive |
| ↳ Parity Win Block Mode | At Exact Parity Only | or "Always While Sheriff Alive" |
| Swapper Can Fix Lights | Off | Lets the Swapper use the lights panel (TOR blocks it) |
| Swapper Can Fix Comms | Off | Lets the Swapper use the comms panel (TOR blocks it) |
| Medic Can Reshield | Off | Once-per-meeting Unshield button (**G**) to remove and re-assign the shield (RPC 249 to all clients) |
| ↳ Shield Charges | ∞ | Total shield placements per game (∞ or 1–10); shown as `X/Y`. A charge is spent only on placement |
| Time Master Unguessable After Shield Saved A Kill | Off | Time Master leaves the guess list and can't be guessed once his shield has blocked a kill |
| Trapped Players Limp | Off | Trapped players keep limping for `Limp Duration` after the freeze |
| ↳ Trapper Can Self-Limp | Off | Trapper toggle button (**H**) to slow himself |
| ↳ Limp Speed Multiplier | 0.5× | 0.25–0.9× speed while limping |
| ↳ Limp Duration After Freeze | 5 s | 1–20 s |
| Spy Can Fully Vent | Off | Spy can travel through vents like an Engineer, not just enter/exit |
| Evil Flash on Death | Off | When a Spy who also has the VIP modifier is killed, everyone sees a red (impostor-coloured) flash |
| ↳ Seer Sees True Flash | Off | The Seer instead sees the true crewmate-white flash (only when VIP colours are on), revealing the Spy's real alignment |
| Shifter Interaction | Shift Succeeds | What happens when the Shifter targets the Spy: *Shift Succeeds* (vanilla), *Shifter Dies* (Shifter is exiled, shift cancelled), or *Shift Cancelled* (silent, nobody dies) |
| ↳ Shifter Gets Shift Back | Off | In *Shift Cancelled* mode: keep the player as Shifter and return the shift button (instead of consuming the shift) |

### Neutral

| Option | Default | Notes |
|---|---|---|
| Vulture Counts Guessed Players As Eaten | Off | Host-authoritative; a Vulture's own correct guess (Guesser mode) counts +1 body |
| ↳ Play Eat Sound On Counted Guess | Off | Plays the eat sound on a counted guess (audible to everyone) |
| Sidekick Can Kill Jackal | Off | Sidekick can target the Jackal (promotion is governed by TOR's own option) |
| Lawyer Knows Target Position | Off | Lawyer sees their target on the map |
| ↳ ...Last Position Visible In Meeting | Off | Marker stays at the last known position during meetings |
| Lover Knows Partner Position | Off | Lover sees their partner on the map (Modifier → Lover) |
| ↳ ...Last Position Visible In Meeting | Off | Marker stays at the last known position during meetings |

### Impostor

| Option | Default | Notes |
|---|---|---|
| Bomber Can Cancel Bomb | Off | Cancel button (**G**) removes the live bomb at any time (RPC 252 to all clients) |
| Trickster Avatar Mixup Sabotage | Off | Button (**C**) swaps every living player's skin for a configured time; shares the Lights-Out cooldown; works on all maps |
| ↳ Avatar Mixup Sabotage Cooldown | 30 s | 10–60 s |
| ↳ Avatar Mixup Sabotage Duration | 10 s | 3–30 s |

### Modifier

| Option | Default | Notes |
|---|---|---|
| Inverted Vision | Off | Inverts the screen colors (true color negative) while the Invert modifier is active; uses Unity's built-in `Hidden/Internal-Colored` shader |
| Rename to Drunk | Off | Renames the *Invert* modifier (and its intro/end-screen text) to **Drunk** live, no restart needed |
| Tiebreaker Quantity (max 3) | 1 | Allows up to 3 Tiebreakers at once |

### TOR Settings

Meeting-duration options (host-authoritative; synced to everyone when on):

| Option | Default | Notes |
|---|---|---|
| Override Meeting Duration | Off | Master toggle |
| ↳ Discussion Base Time | 15 | seconds |
| ↳ Discussion Per Alive Player | 0 | seconds added per alive player |
| ↳ Discussion Reduction Per Dead Player | 0 | seconds removed per dead player |
| ↳ Voting Base Time | 30 | seconds |
| ↳ Voting Per Alive Player | 0 | seconds added per alive player |
| ↳ Voting Reduction Per Dead Player | 0 | seconds removed per dead player |

On the host, `MeetingHud.Start` writes the results into the vanilla `DiscussionTime`/`VotingTime` and
`SyncOptions()`s them, so the override applies to every client regardless of who has the mod. Formula
per phase: `Base + alive × perAlive − dead × perDead`, clamped to a minimum of 0 s. The host's
configured times are snapshotted once per game and restored on game end so the lobby settings don't
drift.

## Mod Manager & lobby Mod-Check

- **Mod Manager** — an in-game UI listing the installed companion mods, their versions, update
  status, and per-mod enable/disable toggles. Asset-cached, so repeatedly opening it or toggling no
  longer leaks textures, sprites, or materials. Includes an **Update All** header button (sequential,
  with a summary line) and shows each updatable mod's **release notes** in its entry — both from the
  already-fetched GitHub JSON (no extra API calls), and both degrade gracefully for older installed
  updaters that lack the new hooks.
- **Combined lobby Mod-Check** — when the Chance mod is also installed, the lobby shows a single
  per-player version overview (green ok / red mismatch / gray missing) that this mod renders,
  instead of two separate warning lists. TOR - Hostfix is excluded by design (host-only). Host-side
  by default; the RPC wire format is unchanged.
- **Auto-update** — checks this repo's GitHub releases on the main menu and offers an in-game update
  button.
- **Version display** — shows `TOR - Forgotten Fixes vX.Y.Z` in the top-corner version readout.

## Mod version handshake (RPC 253)

Each client broadcasts its version + assembly GUID at lobby time so every client can tell whether
all players share the same build (the precondition for the client-side Snitch fix). The handshake
cache is cleared on joining a lobby so it only reflects the current lobby. Wire format is unchanged
across 1.0.x/1.1.x, so mixed lobbies keep working.

## Configuration (BepInEx config)

`com.tormod.usefultorstuff.cfg`:
- `[General] Enabled` — load the mod (default `true`).
- `[ModManager] Enabled` — show the Mod Manager button/UI (default `true`).
- `[ModManager] ButtonPositionX` / `ButtonPositionY` — main-menu button position (default `0.8` / `0.21`).
- `[Bloody] MinDropDistance` — world-unit distance before a new blood drop spawns (default `0.35`;
  `0` disables throttling).

## Requirements

- [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) 4.8.0 (hard dependency)
- BepInEx IL2CPP 6.0.0-be.697
- Among Us (Steam build matching your TOR version)
- TOR - Hostfix (optional, for Snitch coordination)

## Building

```
dotnet build -c Release
```

The output `UsefulTORStuff.dll` lands in `bin/Release/net6.0/`. Set the `AmongUsLatest` environment
variable to your Among Us folder to auto-copy on build.

## Installation

1. Install The Other Roles into your Among Us BepInEx setup.
2. Copy `UsefulTORStuff.dll` into `<Among Us>/BepInEx/plugins/`.
3. Start the game.

## Compatibility

| TOR - Forgotten Fixes | The Other Roles | Among Us |
|---|---|---|
| 1.1.13 | 4.8.0 | Steam build matching TOR 4.8.0 |

## License

This project is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

It is a derivative work of [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles), which
is also GPL-3.0. As required by the GPL, the full source of this modification is available in this
repository, and any redistribution or modified version must remain under GPL-3.0.
