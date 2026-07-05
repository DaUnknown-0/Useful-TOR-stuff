# Changelog — TOR - Forgotten Fixes

## Unreleased

### Features
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
