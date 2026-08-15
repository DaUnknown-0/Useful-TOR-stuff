# TOR - Forgotten Fixes

A companion plugin for [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR)
4.8.0 that bundles several quality-of-life fixes, a pile of new role options, and a cross-mod Mod
Manager. It resolves TOR types via reflection, so every patch degrades to a no-op (with a log
warning) rather than crashing if TOR's internals change. Most win-checks and meeting overrides are
host-authoritative — they apply regardless of who has the mod.

> Formerly *Useful TOR Stuff*. The DLL is still `UsefulTORStuff.dll` and the repository is still
> `Useful-TOR-stuff`; only the in-game name changed to **TOR - Forgotten Fixes**.

📖 **Full documentation:** <https://daunknown-0.github.io/tor-mods-wiki/useful.html> (searchable, EN/DE).

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
- **Grouped option sync** — TOR's option receiver resolves each incoming id with `First(...)`, which
  throws on an unknown id and aborts the rest of that 200-option block (RPC.cs:203). A client without
  one of the host's mods therefore lost every TOR setting that came after the first mod option and
  played the round with its own stored values for them. The host now sends TOR's options and each
  mod's options in separate blocks, so an abort can only ever drop options the receiver doesn't have
  anyway. Incoming unknown ids are skipped instead of aborting, too.
- **`/role` no longer freezes the client** — TOR's `GetRoleDescription` spins on
  `while (ReadmePage == "") { }` with no yield and no timeout. If the README download fails or has
  not finished, the first `/role` locks the Unity main thread for good. The description is now
  produced without spinning, the download is retried in the background, and a role missing from the
  README no longer throws (TOR's `Substring(-1)`).
- **Delayed Bait report no longer throws** — `RPCProcedure.uncheckedCmdReportDeadBody` dereferences
  `Helpers.playerById(targetId).Data` unchecked. When a Bait victim leaves the lobby before the
  delayed report fires, that RPC throws on every client processing it. The report is dropped instead.
- **`stopStart` no longer throws on an unknown id** — same missing null check on the host's chat
  line; everything the original does still happens, only the unresolvable name is replaced.
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
| Jester Quantity (max 3) | 1 | Extra Jesters, assigned host-side to players who ended up as plain crewmates, and only when TOR spawned a Jester at all. Selectable up to the same limit in the **role draft** (spawn chance unchanged). **Each Jester wins alone**: whoever is voted out is the sole winner, the others lose. Every Jester sees his own role card; nobody else does. Requires **all** players to have the mod (otherwise it falls back to 1, with a host warning). Raises the neutral count above TOR's "Neutral Roles" limits by design |
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
| Delay Lover Death (Revenger) | Off | Needs **Both Lovers Die** on. A *killed* Lover no longer kills the partner instantly; at the end of the next meeting the survivor may awaken as a **Revenger** (a delayed Lover suicide happens otherwise). Sub-options only show while the Lovers modifier can actually spawn |
| ↳ Chance Surviving Lover Becomes Revenger | 0% | Roll deciding the Revenger path vs. the delayed suicide |
| ↳ Revenger Mode | Targeted Justice | *Targeted Justice* — a non-killer Revenger may only kill the Lover's killer (a wrong target misfires and kills the Revenger); *Blind Rage* — may kill anyone, but a wrong kill still dooms them next meeting. A correct kill ends the game as a **Lovers win** |
| ↳ Revenger Kill Cooldown | 30 s | 10–60 s; only for a non-killer Revenger's Sheriff-style kill button (**Q**). A Revenger who already has their own kill (Impostor/Jackal/Sheriff…) uses that instead — no second button |

The Revenger is client-side (a live kill button, local kills, chat), so it is **gated on every player
having this mod** (version handshake) — the host gets a lobby warning otherwise, exactly like the
Snitch fix. State syncs via RPC 247; the win uses a separate game-over reason so the two Lovers (the
fallen one + the Revenger) are the winners.

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

Sabotage tuning (master toggle defaults off → vanilla):

| Option | Default | Notes |
|---|---|---|
| Sabotage Tuning | Off | Master toggle. Replaces the shared sabotage cooldown with an independent timer per type; all timers reset to their max when a sabotage ends |
| ↳ Minimum Cooldown (Reduction Floor) | 10 s | 0–30 s; global floor the per-use reduction can't go below |
| ↳ Reactor/Meltdown · Oxygen · Communications · Lights · Airship Crash — Cooldown | 30 s | 10–60 s; independent cooldown per type |
| ↳ ↳ …Cooldown Reduction per Use | 0 s | 0–15 s; each use lowers that type's cooldown by X (floored at the minimum, reset every meeting; counted globally for all impostors) |
| ↳ Reactor/Meltdown Duration | 30 s | 10–90 s; reactor/Polus-laboratory fix time (deadly only) |
| ↳ Oxygen Duration | 30 s | 10–90 s; oxygen depletion time (deadly only) |
| ↳ Airship Crash Duration | 30 s | 10–120 s; airship crash countdown (deadly only) |

Map-aware (each option only applies where the sabotage exists; Reactor and the Polus laboratory are
one type). Cooldowns are enforced client-side (need every impostor to have the mod); durations are
host-authoritative (stamped onto the system's `Countdown` on the host and synced to all clients).
Mutually exclusive with the Chance modifier's sabotage-cooldown override — while Sabotage Tuning is
on, the Chance override stands down (Sabotage Tuning takes precedence). New file `SabotageTuning.cs`;
options 1330–1344.

Cross-mod integration: while **Unknown's Collection**'s *Siphoner* is draining a nearby impostor it
publishes a sabotage block over shared `AppDomain` state (no hard dependency). Sabotage Tuning honours
it — every sabotage is refused, and on the host the shared timer is kept positive so host validation
rejects it too — regardless of whether the tuning toggle itself is on.

## Custom icons & audio

Several buttons now carry their own icon (the borrowed TOR sprite is only a fallback) and play a short
sound cue when used: Bomber **cancel bomb**, Medic **unshield/reshield**, Trapper **self-limp** toggle,
Trickster **avatar mixup**, and the **Lover Revenger** awakening sting. Assets load once via
`UTSAssets` and are cached.

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

## Settings gate: host without the mod

TOR's option sync is host-driven, so an option the host does not have is never broadcast and every
client keeps its own locally saved value for it. To stop that from turning into a one-sided rule
change, **every settings-driven feature of this plugin stands down when the host does not run it**:
options fall back to their defaults, which is exactly TOR's behaviour without this plugin.

- Open (everything works) when you are the host, or when the host runs this mod in any version.
- Closed (settings-driven features off) only for a client whose host is missing from the mod
  handshake. Such a client gets an orange lobby notice and one chat line when the round starts.
- Unaffected either way: the option-less bugfixes (Bloody throttle and killer map, Trapper shift
  charges, positional sounds, Snitch client fix) and the local tools (Mod Manager, WebConfig, lobby
  password gate, map language toggle).
- Also exempt, by decision: options that cannot hand anyone an advantage the rest of the lobby
  doesn't have. Today those are the **meeting map ping** (a communication tool) and the **Drunk
  rename** (a local display name). Everything that changes a rule, a cooldown, a modifier count or
  what one player can see stays gated.

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
| 1.2.0 | 4.8.0 | Steam build matching TOR 4.8.0 |

## License

This project is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

It is a derivative work of [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles), which
is also GPL-3.0. As required by the GPL, the full source of this modification is available in this
repository, and any redistribution or modified version must remain under GPL-3.0.
