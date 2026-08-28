# Changelog — TOR - Forgotten Fixes

## Unreleased

### The settings list reads like a settings list (new, `SettingsOverlayView.cs`)
The F1 overlay and the lobby settings text printed almost everything in white: TOR hard-codes
sub-options to `Color.white`, and it colours a role only by baking a `<color>` tag into the option
name — which sibling mods never did, and which our own localization strips the moment a player
leaves English. The text is now rebuilt from the option list instead of being patched as a string.
- **Every role and modifier in its own colour**, including the roles from Unknown's Collection and
  ChanceMod. Sub-options take a dimmed mix of their role's colour, so a block reads as one unit.
- **Colours survive translation.** They are snapshotted by option ID at load, before the language
  layer rewrites the names, and sibling mods publish theirs through the AppDomain contract
  `UTS.OptionColors` (`"id:1400" -> "FF1919"`), so nothing depends on a display string.
- **Values sit in their own column**: numbers neutral, `On` green, `Off` and `0%` dimmed. Off is
  deliberately not red — that is the impostor colour.
- **Shorter pages**: the redundant role-name prefix is dropped from sub-options ("Tesla Charge
  Countdown" becomes "Charge Countdown"), blocks are sorted by spawn chance, and roles at 0% collapse
  into one dimmed `Off: ...` line.
- **Roles carry their mod's tag** (`[UC]`, `[FF]`, `[Chance]`), read off the option-ID ranges the
  mods already reserve.
- Impostor roles all share `Palette.ImpostorRed` in code, so they get alternating shades of red for
  display only — `Role.color` itself is never touched.
- Page 1 (vanilla settings), Hide N Seek and Prop Hunt are left exactly as TOR built them, and any
  exception falls back to TOR's own text. Five switches in the `SettingsOverlay` config section turn
  the layout, the mod tags, the shades, the value column and the 0% handling on or off.

### Players who leave mid-round keep their line in the end screen (new, `EndScreenLeavers.cs`)
TOR builds the end-of-game role summary by walking `PlayerControl.AllPlayerControls`, and a player
who disconnects is gone from that list — so the one person who crashed, the one everybody asks about
afterwards, left no name, no role and no task count behind.
- Name, role string, task progress and alive state are now written down every two seconds while the
  round runs, because at game end there is nothing left to look up: the PlayerControl is destroyed
  and TOR's role statics are cleared.
- A postfix on TOR's own `OnGameEndPatch.Postfix` appends the missing players to its list before the
  end screen reads it, marked `(left)` and greyed out like a dead player. Kill counts come from
  `GameHistory.deadPlayers`, which survives the disconnect, so they are not guessed.
- If TOR ever renames the internals this hangs off, it logs once and does nothing — the summary is
  then exactly what TOR would have shown on its own. The snapshot is cleared on lobby join and on
  the round reset, so no name can leak into the next round.

### The newcomer shield no longer blocks the Sidekick recruitment
Being recruited is not being killed, but the shield's targeting gate made no distinction: a shielded
newcomer could not be picked as a Sidekick either, because TOR sets the Jackal's kill target and his
sidekick target through the same helper.
- The gate now stands down for the Jackal **while he can still create a sidekick** (TOR's own
  condition from `Buttons.cs`, including the alive check). Once the sidekick exists, TOR clears the
  flag and the gate closes again on its own.
- His kill on that same player is still refused twice and independently of the targeting list:
  `CheckMurder` on the host, and the `checkMuderAttempt` postfix on his own client, which also tells
  him why. A Sidekick is not covered - he cannot recruit, so nothing about him is peaceful here.
- The spawn-zone shield (Anti Start Kill) is unchanged and still blocks sidekicking outright.

### The kill shields stop attacks, not the rest of the game (new, `ShieldPeaceGate.cs`)
Both of our kill shields (newcomer, spawn zone) drop protected players from TOR's shared targeting
helper so no role's bespoke kill path can reach them. That helper is shared with every *peaceful*
ability too, so the Medic could not shield a protected player, the Shifter could not shift, and the
Tracker, Deputy, Eraser, Arsonist, Morphling and Pursuer were equally locked out.
- The peaceful callers now **announce themselves**: a prefix raises a depth counter and a finalizer
  lowers it again (a finalizer survives an exception in the original, so the window cannot stick).
  Both shields skip their gate while it is open.
- Marked is the peaceful side, never the kill side: an unknown or future role stays protected by
  default, and a TOR rename restores the old behaviour instead of opening a hole. Unresolvable method
  names are logged once at load, and that single ability stays gated.
- Attacks are untouched: impostor kill button, Sheriff, Vampire, Jackal, Sidekick, Warlock, Witch,
  Ninja and Thief targeting all stay blocked, as does planting the Maniac's bomb.
- Sibling mods reach the same two facts through an AppDomain contract instead of an assembly
  reference: `UTS.Shield.SetPeaceful` (`Action<bool>`) and `UTS.Shield.IsKillProtected`
  (`Func<byte,bool>`). Unknown's Collection uses both.

### Crash guards for TOR (new, `TorNullGuards.cs` items 7–12)
All six share one root cause: TOR calls `Helpers.playerById(...)` and dereferences the result without
a null check, so any player leaving between the send and the processing of an RPC takes the handler
down.
- **The Bloody trail no longer throws on every tick after its killer leaves.** `bloodyUpdate` reads
  the killer's `PlayerId` *before* the line that would remove the entry, so a disconnected killer is
  never cleaned up and the exception repeats every FixedUpdate for the rest of the round. Because
  `Bloody.active` is broadcast, this hit every client at once. Stale entries are now swept out of
  `Bloody.active` and `Bloody.bloodyKillerMap` before TOR walks them.
- **`overrideDeathReasonAndKiller`**: the null check exists but sits six lines *after* the
  dereference. Reached through `ShareGhostInfo` on Witch exile, Lawyer suicide and Lover suicide.
- **`EventUtility.handleKick`**, **`Portal.startTeleport`**: both dereference a player the caller
  resolved without checking.
- **`guesserShoot`** and **`guesserOnClick`**: guarded with finalizers rather than rebuilt. The crash
  sits mid-loop inside much larger methods, so this contains the damage (the rest of the meeting
  still resolves) without reimplementing TOR's logic. Documented as such in the source.

### Performance patches for TOR (new, `TorPerfFixes.cs`)
- **The F1 settings overlay stops rebuilding every frame.** It rebuilt the entire visible options page
  (LINQ over every registered option, `StringBuilder`, then up to four `TextMeshPro.text` assignments,
  each a full mesh rebuild) on every rendered frame while open, in the lobby *and* in a running round,
  with no change detection. Worse, `buildAllOptions()` triggers a second pass through `ToHudString()`,
  so it ran twice. Now throttled to four rebuilds a second, with an immediate rebuild on page change
  so paging stays instant.
- **`Helpers.MushroomSabotageActive()` is cached per frame.** It copied the local player's whole task
  list on every call, and it is called inside per-player loops, which added up to roughly 2400
  needless allocations a second in a full lobby.
- **`RoleInfo.GetRolesString` gets a 0.25 s cache.** `updatePlayerInfo` rebuilt it twice per player per
  fixed tick, and once the local player is dead it does that for *every* player. Each rebuild
  allocated a list and ran about nine LINQ predicates.
- **The meeting host label** is only rewritten when the host actually changes.
- Deliberately left alone: TOR's lobby client-list copy and `CustomButton.Update()`. Both sit
  inextricably inside larger methods that must keep running; an outside patch would mean rebuilding
  them wholesale. Reasoning is recorded in the file header.

### Performance (this plugin)
- **Medic reshield** no longer runs a LINQ search over every custom button and an unconditional
  `OverrideText` on every frame (audit finding L-4 from 2026-08-11, open until now). The button is
  cached and the text is only pushed when the charge count changes.
- **Sabotage cooldown seconds** only repaint when the displayed second changes.

### New options
- **Trickster Box Count** (1–5, default 3). TOR hardcodes `JackInTheBox.JackInTheBoxLimit = 3`, the
  number of placed boxes needed before they turn into a connected vent network. Every other Trickster
  value is configurable, this one never was, and its impact swings wildly with lobby size: a 3-box net
  covers a large share of a 6-player round and is nearly irrelevant on Airship with 15. Applied via a
  postfix on `Trickster.clearAndReload()`. Requires **all** players to have the mod (otherwise it falls
  back to TOR's 3, with a host warning) — `AllJackInTheBoxes.Count` grows identically everywhere, but
  `hasJackInTheBoxLimitReached()` compares against the local limit, so a mismatched value would let
  some clients see an active vent network while others still wait.
- **Show Sabotage Cooldown Seconds** (default off, sub-option of Sabotage Tuning). Sabotage Tuning
  gives each sabotage type its own independent cooldown, but the icon only shows a proportional fill
  (`SetSpecialActive`), so with five timers running you cannot tell lights from reactor. Adds the
  remaining seconds as a small label per icon. A feedback gap this mod's own feature created: before
  Sabotage Tuning there was only one shared cooldown.

### Trapper: find your own traps, and keep the log (new, `TrapperExtras.cs`)
Two gaps that are both about information the trapper already owns but cannot reach. Each is a host
option, default on.
- **The traps are numbered on the map, with TOR's own trap icon.** TOR's log says "Trap 3:" using `Trap.instanceId`, a counter
  that already runs 1..X, and never says where trap 3 is: the traps are drawn in the world but only
  on the screen the trapper is standing on, so on Airship or Polus the number answers a question
  they cannot ask. The marker is the same artwork the trap is drawn with on the floor
  (`Trapper_Trap_Ingame`, fetched through TOR's own `getTrapSprite`), so the map says what the
  world says, and it uses the same world-to-map transform TOR's trapped-player markers use, so a
  trap marker lands where a player marker for the same spot would. Its size is measured against
  the here-point rather than hard-coded: the sprite is loaded at world scale while map positions
  are divided by `MapScale`, which differs per map, so matching the dot the game already draws
  there is the only sizing that holds everywhere. If the sprite handle ever fails to resolve, the
  marker falls back to a tinted here-point and the numbers still work.
- **The log survives the meeting.** It is written into the meeting chat, and the traps that produced
  it are destroyed a few lines later in the same prefix, so afterwards there is no way back to it -
  and re-posting it into the chat would not help either, because the chat is hidden during a round.
  It is captured in a prefix on `Trap.clearRevealedTraps`, which is the one seam between TOR writing
  the log and destroying the traps, and a button of the trapper's own (L) reopens the chat and writes
  in whatever they have not been shown yet. Capturing at that seam also means the entries carry TOR's
  own shuffle of who walked into which trap, rather than the true trigger order it shuffles precisely
  to hide. Two narrow concessions make the chat usable there: LobbyLeakGuard gains one more exemption
  (alongside meetings, the exile screen, dead players and lovers) for as long as the view is open, and
  SENDING is refused for exactly as long - the trapper reads, and cannot start talking mid-round.

### Fixes
- **The intro-cutscene crash guard actually runs now.** It had been inert since it was written. TOR's
  `SetRoleTexts` is a STATIC method whose parameter happens to be named `__instance`
  (`IntroPatch.cs:216`), and `__instance` is Harmony's reserved name for the target's `this` - a
  static method has none, so the prefix was handed null instead of the cutscene and threw on its
  first line, every single call. Measured off the 2026-08-28 log: 486 "rebuild failed" warnings in
  one session, 54 per round across nine rounds, one per frame of the one-second `Effects.Lerp` TOR
  drives this from. Not once did it succeed, so every intro fell through to TOR's own unguarded
  method - exactly the path the guard exists to avoid. Taking the argument positionally (`__0`) is
  immune to the name collision. The fallback warning is now logged once per intro rather than once
  per frame, and with the full exception instead of just its message: a bare "Object reference not
  set" is what let this hide for as long as it did.
- **A medic who dies with a shield still queued no longer takes the charge with them.** With "Set
  Shield After Meeting" on, pressing the button calls `setFutureShielded` (`RPC.cs:822`), which parks
  the target and sets `Medic.usedShield` true straight away; the shield itself is placed at the exile
  screen, and only while the medic is alive (`ExileControllerPatch.cs:19`). A medic killed in between
  therefore never delivers it, and nothing hands the charge back: `usedShield` is a static that
  belongs to the ROLE and is reset only by `clearAndReload` at round start. `Shifter.shiftRole` moves
  the `Medic.medic` pointer and nothing else, so whoever is shifted into the role inherits the block
  and the shield button refuses every placement, for a shield nobody ever received. The second half
  is quieter: `futureShielded` still points at the dead medic's old target, and at the NEXT exile the
  medic pointer is a living player again, so TOR places that stale shield - one the new medic never
  chose. Both are cleared now. Only the undelivered charge is refunded; a shield that was really
  placed leaves `futureShielded` null and stays spent, so a successor does not get a second one.
- **The settings-change popup names the setting again.** Change a modded option and the notification
  in the lobby's bottom-left corner read just `3` or `On`: the setting it belonged to was missing, so
  the one thing the popup exists to say was the one it did not say, and several changes in a row were
  indistinguishable. TOR calls `Notifier.AddSettingsChangeMessage((StringNames)(this.id + 6000), ...)`
  (`CustomOptions.cs:194`) and the vanilla method builds its line as `GetString(key): value`. That key
  is not a real `StringNames` - the offset exists precisely so it collides with nothing - so the
  lookup finds nothing and only the value survives. The name was never lost, it was never asked for:
  the option is sitting in `CustomOption.options` under that id. Resolved from there and written as
  one line. Keys below 6000 (every vanilla setting) and ids with no option behind them fall through
  untouched.
- **The lobby warnings are readable.** Our top-left lobby messages were drawn at scale 1.0, which is
  smaller than TOR's own resting 1.2 and less than half the 2.0 it uses for a version warning. The
  settings-gate notice is the longest string this mod shows, so on a single unwrapped line it
  stretched the full width of the screen as a hairline. It is now 1.5 and wraps, with the wrap width
  measured off the camera rather than fixed at a character count, so it holds at any resolution and
  in any translation. Everything we change on TOR's shared lobby text is recorded and handed back
  when the countdown takes the element over or the settings overlay covers it.
- **The in-round chat clamp fires once per appearance instead of once per frame**: that is a
  client-crash fix, not a tidiness one. The clamp hides the chat button during a round; it was
  gated on `chat.chatButton.gameObject.activeSelf`, on the assumption that `SetVisible(false)`
  clears that flag. It does not on this install, so the condition never became false and the clamp
  ran on **every frame**: 4662 calls in 78 seconds in the 2026-08-23 log, each one logging "Chat is
  hidden" and walking ControllerManager through QuickChatMenu / BanMenu / ChatUi
  `CloseOverlayMenu`, fourteen log lines a frame. Both hard client crashes in that log
  (19:18:38 and the next day 14:24:54) died mid-write inside exactly that churn, and they are the
  only two in ten days of play. A flag the call does not change cannot be the stop condition, so
  the trigger is now an edge remembered in our own field. An open chat window keeps a budget of its
  own (`ForceClosed` genuinely closes it, so it is self-limiting) capped at eight attempts per
  round, with one warning if it never takes, so a clamp that turns out to be powerless costs one
  call per round rather than sixty a second.
- **A lone surviving Sidekick no longer crashes the end of the round.** "Sidekick Gets Promoted To
  Jackal On Jackal Death" defaults to off, so a dead Jackal leaves `Jackal.jackal` null while
  `Sidekick.sidekick` survives. `PlayerStatistics.GetPlayerCounts` counts the two separately, so the
  lone Sidekick still reaches parity and TeamJackalWin fires — and TOR then reads `Jackal.jackal.Data`
  unconditionally, throwing a NullReferenceException in its own `OnGameEnd` postfix on *every* client.
  Our own "Sidekick Can Kill Jackal" makes the scenario more likely. The Sidekick is now promoted into
  the Jackal slot for the duration of that postfix.
- **BountyHunter no longer throws every tick on an empty target pool.** Its filter (impostors, Spy,
  team-red Sidekick/Jackal, immature Mini, own Lover) can legitimately empty the candidate list, and
  TOR indexes straight into it with no `Count == 0` guard — unlike the structurally identical spot it
  guards itself in `RoleAssignmentPatch.cs`. Because the retry timer resets after every meeting, the
  exception repeated on every tick and took every role update after it in the same sequence (Vulture,
  Medium, Morphling/Camouflager, Lawyer, Pursuer) down with it.
- **A cancelled or meeting-interrupted bomb no longer throws on detonation.** The fuse coroutine keeps
  running on the persistent HudManager after `clearBomb()` has destroyed the bomb's GameObjects, then
  dereferences them anyway. The stale detonation is skipped; its cleanup tail only runs when
  `Bomber.bomb` still points at that exact instance, so a fresh bomb planted in the meantime (the
  cooldown can be shorter than the fuse) is never torn down by a stale coroutine.
- **Sunglasses is now actually lost on Sidekick promotion.** TOR's own README promises it, but
  `jackalCreatesSidekick` erases roles with `ignoreModifier: true`, so the removal branch is never
  reached and the promoted player keeps reduced vision for the rest of the round.
- **TOR's own version handshake no longer leaks between lobbies.** `GameStartManagerPatch.playerVersions`
  is keyed by clientId and never cleared; clientIds are reused across lobbies in one session, so a
  player could briefly inherit a predecessor's matching entry and be counted as compatible before
  their own handshake arrived.
- **The end screen no longer shows disconnected players as alive.** TOR sets `IsAlive` from `IsDead`
  alone, ignoring `Disconnected` even though it separates the two everywhere else.
- **The Lawyer keeps their bonus win when Jester Quantity is above 1.** This plugin's own winner
  override replaced the winner list wholesale, discarding the Lawyer that TOR's postfix had added
  moments earlier — in every round with that outcome, even when no extra Jester was involved.
- **The rejoin button now survives a real crash.** `RememberCurrentLobby()` was only called after a
  successful mod sync, so an Il2Cpp exception or Alt-F4 mid-round left no stored lobby. It now records
  on every `OnGameJoined`, which also covers crashes during the lobby phase.

### Internal
- **All custom RPCs moved onto a single channel (callId 240).** Custom RPCs share one byte-wide
  id space with TOR's own `CustomRPC` enum, which grows with every TOR release (100–183 today).
  This plugin held nine separate bytes (244–249, 252–254), i.e. nine chances for a future TOR
  release to land on one of ours — and such a collision is not a build error but a silent
  mis-parse in a live round. Everything now travels as `[240][module byte][payload]`, where the
  module byte is each feature's historical id, so only one byte has to stay globally free.
  The payload behind the module byte is byte-for-byte unchanged.
  Because this plugin has no gate that stops a round on a version mismatch (the version handshake
  is informational) and is explicitly meant for mixed lobbies, migrated messages are **dual-sent**:
  once on the old callId (understood by older builds) and once on channel 240, with both receive
  paths active. That is only safe for messages whose application is idempotent, so every RPC was
  classified first: `SidekickAllowed` (244), `MultiModifiers` armor-break (245), `SelfLimp` (248),
  `MedicReshield` (249), `CancelBomb` (252), the version handshake (253) and `MeetingMapPing` (254)
  are pure state assignments and were migrated; the Trickster avatar mixup (246, replays a global
  audio cue) and the whole Lover/Revenger protocol (247, kills + chat + win flags) are **not**
  idempotent and deliberately stay on their standalone callId. All legacy paths are marked
  `LEGACY DUAL-SEND` in the source and can be dropped in one go in a future breaking release.
- **RPC collision watchdog.** On load the plugin reads TOR's `CustomRPC` enum via reflection and
  logs a warning if TOR ever uses an id ≥ 200 (or exactly 230/240, the Unknown's Collection and
  Forgotten Fixes channels), plus an info line with the highest TOR id currently in use. Purely
  diagnostic — it surfaces a looming collision before players run into it in a live round.

### Fixes
- **No more phantom Minis/Armored/Tiebreakers in foreign lobbies.** The Mini/Armored/
  Tiebreaker holder lists were only cleared by TOR's `resetVariables` RPC — which only a
  (same-version) TOR host ever sends. Joining a lobby whose host doesn't run the mod kept
  the previous game's entries alive, and since the lists hold bare PlayerIds (reused per
  lobby), whoever now owned those ids was shown and treated as a fake extra Mini
  (shrunk, kill-protected), Armored, or Tiebreaker on this client. The lists are now also
  cleared on every lobby (re-)join (`OnGameJoined`).
- **Positional sounds no longer break the end-game/lobby UI.** TOR's
  `playAtPosition` (bomb fuse + explosion, event sounds) destroyed the AudioSource
  that the vanilla SoundManager owns; every later SoundManager sweep then threw
  (`ShipStatus.OnDestroy → StopAllSound` at round end) and the "Play Again"/"Leave"
  buttons went dead because their click sounds run through the corrupted manager.
  The teardown now uses the managed `StopSound(clip)` instead.
- **Armored Bomber can plant again.** Planting a bomb "attacks" the bomber himself under
  the hood (to consume Pursuer blanks) — with the Armored modifier that probe broke the
  armor, the bomb never spawned, and the plant button locked up (`isPlanted` was set
  anyway). Self-probes now skip the armor check entirely (vanilla Armored AND the
  multi-Armored extras): the bomb plants normally and the armor stays intact.
- **Mini/Armored quantities follow TOR's assignment rules.** Extra Mini/Armored holders
  are now assigned exclusively through TOR's own quantity mechanism (chance tickets):
  every additional copy still rolls the spawn chance, and all modifiers draw from one
  shared player pool — max ONE modifier per person. The old host-side top-up (which
  force-filled the quantity once a single holder spawned, ignoring the chance and
  stacking Mini+Armored on the same player) is gone.

### Features
- **Mod sync: join a lobby, get the mods the host is actually running.** Until now a missing mod was
  invisible: an uninstalled mod sends no handshake, so nothing distinguished "this player doesn't
  have Unknown's Collection" from "Unknown's Collection hasn't spoken up yet". Every client with this
  mod now broadcasts a mod INVENTORY once per lobby (module byte 255 on channel 240): which
  catalogued mods it runs, in which version, and how many mods it runs that the catalog cannot name.
  A client whose set differs from the host's gets a lobby button listing exactly what is missing,
  outdated or switched off, and can fetch it in one click.
  The whole design turns on one rule: **the host sends a catalog ID and nothing else.** Repository,
  asset file name and target path all come from a table compiled into this mod (`UTSModCatalog`), so
  nothing received over the network can steer a URL, a path or a file name - and since mod NAMES are
  looked up locally too, a host cannot inject TMP rich-text into the lobby board either. A mod the
  local catalog does not know can only be counted, never downloaded; the fix for that is to update
  this mod, which is itself in the catalog. Download URLs are additionally parsed and matched against
  the catalog entry's own release path before a byte is fetched, and only the HOST's inventory
  produces suggestions - any other player's is display material.
  Downgrades (host on an older build) and prerelease targets on a client that hides test versions are
  deliberately kept OUT of the bulk button and need their own click: a host on an old build must not
  be able to drag every client back to an arbitrarily old release, and the catalog whitelist does not
  protect against that on its own.
  Because a downloaded DLL only takes effect after a restart, the lobby code, its region and a
  timestamp are persisted before the player quits, and the main menu then offers one button back into
  that lobby (expires after 30 minutes). Client-side throughout, switchable in the Mod Manager.
- **Settings-based features stand down when the HOST doesn't have the mod.** TOR's option sync is
  host-driven: the host broadcasts `(option id, selection)` for every option **it** owns, and an
  option the host does not have is simply never sent. So a client running this mod in a lobby whose
  host does not run it kept every one of our options at whatever its own config last stored, while
  nobody else played by those values: shorter sabotage cooldowns, extra Mini/Armored/Tiebreaker
  holders, the Medic's unshield button, the Lawyer/Lover map tracker, the Trickster mixup sabotage,
  a Revenger, ... one-sided, invisible to everyone else, and not something anyone had agreed to.
  Every settings-driven feature now reads its option through `UTSGate`, which returns the option's
  **default** while the host lacks the mod, i.e. exactly TOR's behaviour without this plugin. The
  gate is open whenever we are the host (our values are the ones being shared) and closes only for
  a client whose host is missing from the mod handshake (`AmongUsClient.HostId`), latched in the
  lobby into the round that follows. The affected options are captured automatically: the option
  list is snapshotted before and after this plugin's `CreateOptions()` calls, so a future feature
  is gated the day it is written and TOR's own options (which DO come from the host) keep their
  real values. Bugfixes without an option (Bloody throttle and killer map, Trapper shift charges,
  positional sounds, the Snitch client fix with its own stricter gate) stay active, as do the local
  tools (Mod Manager, WebConfig, lobby password gate, map language toggle). A client in such a
  lobby is told what happened: an orange lobby notice and one chat line at game start.
  Exempt from the gate are the options that cannot hand anyone an advantage the others don't have:
  the meeting map ping and the Drunk rename. The option-less features were never affected anyway
  (Bloody drop throttle, map language toggle, the plain bug fixes).

- **Up to three Jesters, each winning alone.** New option "Jester Quantity (max 3)" (option 1376,
  Neutral tab, default 1). TOR's Jester is a single `PlayerControl` field, so extra Jesters are kept
  in their own list and the handful of places where TOR asks "is this the Jester?" are extended:
  role display (`RoleInfo.getRoleInfoForPlayer`, which also feeds `Helpers.isNeutral`), fake tasks,
  impostor vision, the killer check, and the emergency-button lock. They are assigned host-side
  after TOR's whole assignment has run, only when TOR actually spawned a Jester (so the spawn chance
  keeps its meaning), and only to players who ended up as plain crewmates. When a Jester is voted
  out he wins ALONE: the winner list becomes exactly the player who was exiled, so the other Jesters
  lose, and on every other ending all extra Jesters are removed from the winners the same way TOR
  removes its own. Each Jester is told he is one (his own intro role card is re-stamped when the
  assignment arrives, so a late message can't leave him looking at "Crewmate"); nobody else's screen
  is touched.
  **Role draft**: works there too, up to the same limit. TOR removes a picked role from everyone
  else's choices via `alreadyPicked.Contains(...)` inside the draft coroutine, so the Jester entry is
  swapped for a neutral placeholder that the draft never offers (`RoleId.Sidekick`) while the
  quantity still has room, which leaves every other count that reads that list intact. The spawn
  chance is untouched: the draft only offers the Jester when TOR's own filters do. The second and
  third pick become extra Jesters instead of overwriting `Jester.jester` (prefix on `setRole`, live
  only while the draft runs), and since `receivePick` runs on every client, no message of our own is
  needed.
  Like the extra Mini/Armored holders, this **only applies when every player has the mod** (the
  extra Jesters exist only inside this code); otherwise the quantity falls back to 1 and the host
  gets a lobby warning. Note that it deliberately exceeds TOR's "Neutral Roles" limits.

### Fixes
- **A mod option no longer costs plain-TOR clients the settings that follow it.** TOR's option
  receiver resolves each incoming id with `CustomOption.options.First(...)`, which THROWS on an
  unknown id, and the try/catch sits outside the loop (RPC.cs:203-211). The host sends the settings
  in blocks of 200, so on a client that lacks one of the host's mods the first unknown id aborted
  the rest of that block: every TOR option after it kept that client's own locally stored value,
  silently, for the whole round. Our options sit right next to the TOR options they belong to, i.e.
  in the middle of the first block, so this was neither rare nor small. The host now sends the same
  data in the same wire format but never mixes owners within a block: TOR's own options first, then
  one group per mod assembly (resolved by reflection: every CustomOption reachable from a static
  field of an assembly that references TOR, with TOR's own always counted as core). An abort can
  then only ever drop the tail of a group the receiver doesn't have anyway. The receiving side is
  fixed too (unknown ids are skipped instead of aborting), which covers the mirror case for us.

- **True Modifier Chances.** New host option "True Modifier Chances" (option 1375, Modifier
  tab, default OFF): the modifier percentages finally behave like actual probabilities.
  TOR never rolls them - 100% modifiers are placed directly and everything else throws
  `percentage x quantity` LOTTERY TICKETS into a pool that is then filled up until the
  modifier count is reached. With enough slots a 10% modifier therefore spawns every single
  round, and with few slots even a 90% one keeps missing. With the option on, the host rolls
  every modifier - and every copy of a quantity modifier - separately against its real
  percentage before the assignment: winners are guaranteed, losers do not appear at all this
  round, and "Minimum/Maximum Modifiers" turns into a pure upper limit instead of a target
  that is always hit exactly (surplus winners are trimmed randomly, which is logged). The
  Lovers pair is untouched - TOR already rolls it correctly. Works with plain-TOR clients
  (host-side only) and switches the Tiebreaker/Mini/Armored quantity multipliers of this
  plugin off while active, so nothing is counted twice.
- **Random Impostor Count (min/max).** New host options "Random Impostor Count" +
  "Minimum/Maximum Impostors" (each 1-3, options 1370-1372, General tab): the host rolls
  the actual impostor count once per game right before role assignment; it stays fixed
  for the whole round and secret from everyone. All visible surfaces (lobby settings,
  intro "There are X Impostors") keep showing the configured maximum: the host-side
  vanilla impostor setting is auto-enforced to the max while the feature is on. With a
  max of 2+ the Spy stays in the role pool even when only 1 impostor was rolled (a Spy
  sighting must not reveal the count), and the intro team lineup is hidden at 1 impostor
  exactly like TOR already hides it at 2+ whenever the Spy is enabled. Limitation: TOR's
  Role Draft keeps its own hardcoded "2+ actual impostors" Spy filter.
- **Jackal sidekick gating (refill / per-game chance).** Two new mutually exclusive
  controls on top of TOR's "Jackal Can Create A Sidekick" (both need it ON):
  "Sidekick Only Fills A Missing Impostor" (option 1373, sub-option of the range
  feature) gives the Jackal the sidekick button exactly when fewer impostors spawned
  than the configured max (guaranteed; no button at full count), so the sidekick refills
  the missing evil role. Otherwise "Chance That The Jackal Can Create A Sidekick"
  (option 1374, 0-100%) is rolled once per game by the host; at 100% TOR's behavior is
  untouched. The verdict is broadcast via RPC 244 and sets Jackal.canCreateSidekick on
  every client; a Sidekick promoted to Jackal keeps TOR's own promotion rules.
- **Meeting Map Ping.** Open the map during a meeting and click anywhere: every player
  (with the mod) sees the vanilla HerePoint crewmate icon in YOUR player color at that
  spot — one marker per player, a new click moves yours, with a red shader outline
  (the vanilla kill-target highlight mechanism) so pings are distinguishable from the
  map's own icons. Markers expire after 10 seconds; placing has a 2-second per-player
  cooldown (enforced on send AND receive). Only alive players can ping (no
  ghost-knowledge leaks); markers also clear when the meeting ends. Synced via RPC 254
  (map-local coordinates, so it is map-agnostic); host toggle "Meeting Map Ping
  (Click On Map)" (option 1360, General tab).
- **On-the-fly language dropdown on the meeting map.** A "[ Language: … v ]" button in
  the bottom-right corner of the MEETING minimap opens a 3-column grid (auto + all 26
  languages, current one highlighted); clicking an entry applies instantly (same live
  re-apply path as the config entry). For languages Among Us itself offers it also
  switches the WHOLE game (CurrentLanguage setter + TranslationController.SetLanguage
  via the Languages TranslatedImageSet — vanilla TextTranslatorTMP labels refresh live);
  extra (tier-B) languages leave the vanilla language untouched. Renders above the map
  background (explicit sorting copied from HerePoint — a plain TMP is drawn behind the
  map and invisible) and hit-tests through the UI Camera, not Camera.main.
- **Ping placement effect.** New/moved map pings pop in oversized and emit a short
  growing pulse ring, fired the moment a viewer first sees the ping (also when opening
  the map only afterwards).
- **UC + HostFix adopted the localization system.** Unknown's Collection (own uc.* tables,
  roles/options translated by pristine-English text match, 86 dynamic call sites on Tr())
  and TOR - Hostfix (hostfix.* tables, updater + credit lines) now follow the shared
  language (AppDomain contract UTS.Loc.ActiveCode/Epoch) — including the previously
  German-only Saboteur scan/Tesla indicator HUD texts, which are now properly localized.
- **Localization engine (UTSLocalization).** All mod texts — TOR's roles, descriptions and the
  complete settings tree (option names, headings, dropdown values) plus every Forgotten-Fixes
  string — are now translated into the 15 vanilla Among Us languages **and 10 extra languages**
  the game itself does not offer (Turkish, Polish, Czech, Hungarian, Romanian, Swedish, Finnish,
  Ukrainian, Indonesian, Vietnamese). Language follows the vanilla game setting automatically;
  the extra languages are selected via the `Localization.ModLanguage` config entry (per client —
  deliberately not a host-synced option). Role NAMES stay English in all languages so mixed
  lobbies keep a common vocabulary. Community fixes/additions: drop a `<code>.json` into
  `BepInEx/config/UTSLocalization/` to override any key without rebuilding. For the extra
  languages the engine can also replace **vanilla** strings (`TranslationController.GetString`
  postfix); the translated vanilla tables are generated from a one-time dump
  (`vanilla_dump_<lang>.json`, written automatically in the main menu) and will ship in a later
  build. TOR's original source stays untouched: roles/options are re-titled by mutating the
  public string fields at runtime and restored on language switch. UC/HostFix pick the language
  up via the shared `UTS.Loc.ActiveCode`/`UTS.Loc.Epoch` AppDomain contract (adoption pending).
  Not yet localized: the WebConfig browser page and a handful of TOR-internal composed UI texts
  (endgame/exile lines built inline in TOR patches).
- **Delay Lover Death + Revenger (Lovers modifier).** New Lover option chain (client-side, gated on
  "everyone has the mod"). With **Delay Lover Death** ON — and only when the first Lover was *killed*
  (not exiled) while "Both Lovers Die" is ON — the surviving Lover's instant suicide is suppressed and
  the decision is deferred to the end of the next meeting. There a configurable %-roll
  (**Chance Surviving Lover Becomes Revenger**, like the Lawyer→Prosecutor chance) turns them into a
  **Revenger** (otherwise they die now as a delayed Lover suicide). From the awakening on it shows as
  "Revenger" (its own `RoleInfo`, keeping the Lovers color) in name tags, the role tab and the end-game
  summary — but the **win counts as a Lovers win for exactly the two Lovers** (the fallen one + the
  Revenger; end screen "Lovers Win"). A **non-killer** Revenger gets a Sheriff-like kill button; a host
  **Revenger Mode** option picks the behaviour: *Targeted Justice* (may only kill the Lover's killer —
  correct kill ends the game instantly as a Lovers win, wrong target is a fatal misfire) or *Blind
  Rage* (may kill anyone; hitting the real killer still wins, otherwise they die at the next meeting
  end with a random rage chat message). A Revenger that already has its own kill button (Impostor,
  neutral killers like Jackal/Sidekick/Thief, or the Sheriff — `Helpers.isKiller` + Sheriff) gets no
  second button: their own normal kill on the Lover's killer triggers the win. Flavor chat: a grief/foreboding line for the surviving
  Lover in the first meeting, a mode-specific awakening line, and the rage-death lines. Suppression
  flips `Lovers.bothDie` off for the triggering `MurderPlayer` so TOR's own suicide+death-reason block
  is skipped cleanly; the win uses TOR's internal `CheckEndCriteriaPatch.CheckAndEndGameForLoverWin`
  (reflection) only as a host entry point but ends with a **separate `CustomGameOverReason` (17)** and
  a dedicated "Revenger Wins" end screen (winner snapshotted so it survives TOR's end-of-game
  `resetVariables`). The win is flagged *before* the killing blow so a kill that removes the last evil
  player can't race a Crew "No Evil Killers Left" end; both end-screen overrides gate on the real
  `gameOverReason == 17` so the text never overlays another team's win. A Lover shot by a **Guesser**
  also arms the Revenger (the Guesser becomes the target; intercepts `RPCProcedure.guesserShoot`, which
  kills via `Exiled()`). While a Revenger is **alive** the Impostors/Jackal cannot claim a numerical
  (parity) win — they must deal with the Revenger first (prefixes on TOR's `CheckAndEndGameFor
  Impostor/JackalWin`). If the **target dies first** (voted out or killed by someone else), the revenge
  is denied and the Revenger dies at the next meeting end with a flavor line. The Revenger is
  **guessable**: while the feature is active its `RoleInfo` is listed in `allRoleInfos`, so the Guesser
  can pick "Revenger" and the (reference-based) correctness check matches. State syncs over a small
  custom RPC (247); kills reuse TOR's `UncheckedMurderPlayer`. New file `LoverRevenger.cs`; options
  1294–1297.
- **Sabotage Tuning (TOR Settings).** Master toggle (default off) that replaces Among Us's single
  shared sabotage cooldown with an independent cooldown timer per sabotage type. While no sabotage is
  active each type's timer ticks down on its own; when any sabotage ends, all timers reset to their
  maximum. Each type (Reactor/Meltdown, Oxygen, Communications, Lights, Airship Crash) has its own
  cooldown and a per-use reduction that lowers that type's cooldown by X seconds, floored at a
  configurable Minimum Cooldown and reset every meeting; the reduction is counted globally for all
  impostors (on the synced active-edge). The deadly sabotages (Reactor/Meltdown, Oxygen, Airship
  Crash) additionally get a configurable duration, stamped host-authoritatively onto the system's
  `Countdown` (LifeSupp via its public field, Reactor/Heli via the non-public Countdown setter since
  `HeliSabotageSystem.CharlesDuration` is a const) and synced to all clients. Cooldowns are enforced
  client-side via `MapRoom.Sabotage*` prefixes plus a forced `InfectedOverlay.CanUseSabotage`; the
  host's shared `SabotageSystemType.Timer` is neutralised while idle so it never rejects a per-type
  allowed sabotage. Mutually exclusive with the Chance modifier's sabotage-cooldown override (Chance
  stands down via reflection while Sabotage Tuning is on; Sabotage Tuning takes precedence). Map-aware
  via `ShipStatus.Systems`. New file `SabotageTuning.cs`; options 1330–1344.
- **Meeting Duration Override (TOR Settings).** New host-authoritative option that overrides the
  meeting timer from the alive/dead player counts at meeting start. A master toggle plus two
  independent formulas — one for the discussion phase, one for the voting phase — each computed as
  `Base + aliveCount × PerAlivePlayer − deadCount × ReductionPerDeadPlayer`, clamped to a hard
  minimum of 0s. On the host, `MeetingHud.Start` writes the results into the vanilla
  `DiscussionTime`/`VotingTime` and `SyncOptions()`s them, so the override applies to every client
  regardless of who has the mod (not mod-gated). The host's configured discussion/voting times are
  snapshotted once per game and restored on `AmongUsClient.OnGameEnd` so the lobby settings don't
  drift. New file `MeetingDurationOverride.cs`; options 1210–1216.

## 1.1.0

Adds the two approved features (F1, F2) on top of the 1.0.1 fix base. No wire-format changes
(RPC 253 handshake unchanged); mixed lobbies keep working.

### Features
- **F1 — Consolidated lobby version handshake (presentation only).** When the Chance mod is also
  loaded, TOR - Forgotten Fixes now owns and renders a single combined **Mod-Check** per-player overview
  (green ok / red mismatch / gray missing), collapsing to "all players match ✓" when nothing is
  wrong, instead of two separate red lists. Each mod publishes its snapshot over the documented
  `TORMods.Handshake.*` AppDomain keys (plain strings / `Dictionary<int,string>` only — no shared
  types); Chance suppresses its own block while Useful is present and falls back to its standalone
  block when alone. HostFix is intentionally excluded (host-only). Host-side by default
  (`ShowToAllPlayers` switch). Wire format (RPCs 251/253) is untouched.
- **F2 — Mod Manager "Update All" + release notes.** A header **UPDATE ALL** button (enabled when
  ≥1 mod has an update) downloads every updatable mod's release **sequentially** — the updaters are
  single-`_busy` state machines — then shows one summary line ("N updated — restart required"),
  resilient to a mod's check/download having failed. Expanded mod entries with an available update
  now render the newest release's notes (crude markdown strip, first ~10 lines / ~600 chars, "…" on
  truncation) from the already-fetched JSON — no extra API calls. Both features probe the new
  reflection hooks and hide gracefully for older installed updaters that lack them.

## 1.0.1

Patch bump. No wire-format changes (RPC 253 handshake unchanged); mixed lobbies keep working.

### P0 — Crash / correctness
- **P0.1** — `CoShowAnnouncement`: `yield return null;` → `yield break;` and a null-guard on
  `MainMenuManager` before `StartCoroutine(...)` (announcement coroutine + `OnSceneLoaded`),
  preventing `Instantiate(null)` and a NRE.
- **P0.2** — `CoCheckForUpdate`: GitHub deserialize + sort wrapped in try/catch/finally so a
  rate-limit/malformed response can no longer kill the coroutine and wedge `_busy`/`_checkCompleted`
  for the session. `Releases == null` treated as "no update"; all exits reset the flags.

### P1 — Functional / leaks
- **P1.1** — `BloodyThrottlePatch._lastDropPos` is cleared each round via a new postfix on TOR's
  `RPCProcedure.resetVariables` (reflection-resolved, degrades to a no-op with a warning if absent),
  so the per-player last-drop map no longer leaks across games / onto reused player ids.
- **P1.2** — `ModManagerUI` no longer leaks `Texture2D`/`Sprite`/`Material` assets. A static
  `GetSolidSprite(Color)` cache (process-wide, `DontDestroyOnLoad`) is reused everywhere and the
  overlay `Material` is created once, instead of fresh assets on every `Show()`/toggle.
- **P1.5** — `playerVersions` cleared on `AmongUsClient.OnGameJoined`, so the handshake cache only
  reflects the current lobby.

### P2 — QoL / hygiene
- **P2.3** — PingTracker version line guarded by its `LinkId` marker against per-frame stacking.
- **P2.4** — Removed dead `ModManagerUI.GenerateModListText()` (never called).
- **P2.5** — `DisableBackgroundUI`: documented the name-substring heuristic for future maintainers
  and added an explicit "never touch anything under `_popup`" guard in the child loop.
- **P2.8** — Updater sends a `User-Agent` header on the GitHub API request.
