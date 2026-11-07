# Integrated journey guidance

Work in progress: one clear route through quests, errands, saved places, map
destinations and teleport legs. Walking and objective completion remain under
the player's control. Automatic Teleport is an explicit, off-by-default opt-in.

## Implemented in source

- Saved, ordered itinerary (up to 64 stops) in the `/wayfinder` window. Add the
  current destination or a saved place; start/resume, pause, confirm done/skip,
  reorder upcoming stops, or clear the itinerary. The current running stop and
  completed stops cannot be reordered accidentally. Completed/invalid persisted
  rows are accounted for when sanitizing saved progress.
- Previous stop undoes an accidental advance without resuming a paused journey.
  Pending stops can be removed; pause before removing the active destination.
  Clearing the itinerary requires confirmation.
- The active stop drives normal navmesh and cross-zone guidance. New manual
  targets, map links, mode changes and IPC targets pause the journey rather than
  silently losing it. Resume returns to the same stop.
- A compact two-line current-action/next-step cue for walking, waiting for a
  route, teleporting, unsupported inter-zone travel and arrival.
- No fake straight-line trail when navmesh is requested but unavailable.
  The directional pointer remains, clearly accompanied by route status.
- Attuned teleport destinations include the destination objective/zone for the
  following leg. Unattuned destinations never instruct the player to teleport.
- Optional automatic Teleport casts the displayed attuned destination through
  the game's normal action, with normal gil costs. `/wayfinder autoteleport on`
  enables it; `off` disables it; `retry` re-arms an interrupted/refused attempt.
  The settings expose the toggle, maximum gil per trip (default 2,000; zero means
  free destinations only), current status and Retry button. Stable guidance and
  two seconds safely stationary are required. Combat, duties, PvP, casts, loading,
  cutscenes, NPC events, crafting, gathering, death, flight, diving, group pose,
  trading and hidden guidance block it. It attempts each destination only once
  per origin zone; missing guidance and toggling cannot trigger retry loops.
  Explicit Retry still respects the 30-second global cooldown. Bounded,
  character-scoped attempt journals survive reloads; a reservation must be
  durably saved before casting. Storage failures and conflicting writers stop
  casts. An already-enabled installation with no saved history holds its first
  valid offer for explicit Retry after this upgrade, because previous in-memory
  attempts cannot be recovered. Source-tested; the automatic cast has not yet
  been verified live.
- `XivWayfinder.v1.GetState` adds `journey: { action, next, hasWalkablePath }`,
  using the same pure guidance builder as the overlay. Existing fields remain.
- Arrival cues explicitly distinguish reaching a location from completing the
  interaction there. Teleport is the only optional automatic game action.

## Companion during a teleport leg (development source)

With a grounded companion and navmesh enabled, an attuned teleport cue requests
a walkable rendezvous ahead of the player. A running player gets renewed
ahead/side requests; these are pathfinding destinations, not positions attached
to the character. Corral targets anticipate up to one second of observed motion
(an eight-yalm query horizon, not an eight-yalm model offset). This mode alone
allows fresh path requests every 0.75 seconds; failed queries retain the normal
two-second retry bound. Pending queries hold the accepted location. A short
query can retain decaying private travel momentum, but no paused time becomes
movement and rejected placements discard that momentum. Stop/join cues stop
following a superseded corral endpoint while the replacement query is pending.
New appearances
start at the route origin, and an already-airborne guide finishes its owned jump
before any rendezvous route can replace it. These temporary approach arcs are
not published as the player's destination route.

At an accepted stop within sight of the player, compatible models face the
player and use their own verified waiting, beckoning or ground-sitting clips.
Ground sitting requires authored entry, seated loop and stand-up phases. A
cancelled sit finishes its entry and stand-up at the held root before walking
can resume. Unsupported rigs keep their supported idle; missing animations are
not relabelled as successful gestures. Automatic-teleport guidance prefers
waiting/beckoning to leave time for the upcoming cast.

A real observed Teleport spell changes the rendezvous to a nearby joining spot.
Opening the teleport menu, another spell or Return does not count as Teleport;
an interrupted cast returns to the stop request. This observer never starts a
cast or changes player movement. Jumping into the teleport/arms is still a
separate unfinished visual; standing near the cast is not claimed as boarding.
The combined behavior still requires live path, cast-interruption and model
validation before a stable release.

Native jump preflight uses the selected model's resource bounds with the actual
draw scale and both native scale factors. Bounds are relative to the observed
draw origin, which must match the accepted foot root; below-foot geometry is
checked after scaling. Grounded route models establish an upright orientation
before a yaw-only clearance envelope can be used. The additional padding is
not proof that every future animation pose stays inside the resource bounds.
This correction has focused host coverage; live obstacle clearance is still
unverified.

## Sibling-mod itinerary IPC

`XivWayfinder.v1.GetJourney`: `() -> string`, a cached JSON snapshot readable
from any thread: `{version:1,index,running,stops:[{territoryId,x,y,z,label}]}`.
`index` is zero-based; earlier stops have been advanced past, not necessarily
completed. Coordinates are world coordinates, with optional/null height `y`.

`XivWayfinder.v1.AppendJourney`: `(string json) -> bool`. Supply
`{"version":1,"stops":[{"territoryId":134,"x":10,"z":20,"label":"Errand"}]}`.
Appends atomically after validating the entire batch, never replaces existing
stops or starts movement/guidance. Territory must be explicit/nonzero. Maximum
64 total stops and 32768 input characters. False means no change.

`XivWayfinder.v1.ControlJourney`: `(string action) -> bool`, accepts `resume`,
`pause`, `next`, `previous`. Resume enables guidance. If the user opted into
automatic Teleport, any guidance change can initiate a gil-spending cast.
Read `GetState.autoTeleport` before changing destinations or journey progress.
Advance only following explicit player confirmation; proximity is not task
completion. No clear/replace IPC is exposed.

Both write gates must be called on the Dalamud framework update thread;
off-thread writes return false without mutation. Successful writes persist and
refresh GetJourney immediately; GetState/route refresh on the next update.
Consumers must handle absent/unloaded Wayfinder without a runtime dependency.

## Remaining (not claimed complete)

- Refresh quest stops from live objective state (currently stops snapshot their
  destination), add queue editing commands and richer completion history.
- Validated cross-zone transport graph, same-zone teleport shortcuts and cost
  preferences. Current planner selects an aetheryte in the destination zone;
  it cannot yet plan ferries, aethernet or intermediate-zone transfers.
- Route publication and parsing are implemented via `XivWayfinder.v1.GetRoute`;
  XivRug draws the shared route on its map, and Wayfinder has native terrain
  trail rendering. Validate their alignment and cancellation/invalidation live
  across target, zone and navmesh changes; drawing alone is not that proof.
- Shared objective state with XivMcp; explicit completion/confirmation instead
  of silently completing tasks from proximity.
- Complete native-UI/depth/terrain acceptance for route visuals. Flat-terrain
  screenshots and collision fixtures do not establish stairs, moving geometry
  or every overlapping game window.
- Observe the new source changes live. A matching DLL bundle was copied into
  the container's `devPlugins/XivWayfinder` after 211 tests and a clean build;
  the prior bundle is at `XivWayfinder.before-journey-20260930`. Hashes match the
  build output. Hot reload and live navigation are not yet verified.

Runtime audit: Dalamud logged an old missing `StateChangeKey` dependency at
2026-09-30 00:22 UTC, followed by successful loading at 11:40 UTC. Do not report
the old load failure as the current cause without a new matching error.

Historical integration check (2026-09-30): 214 Wayfinder tests and 30 surface tests
pass; both plugin builds have zero warnings/errors. XivMcp's live `list_plugins`
confirms the Wayfinder and FloorMap development plugins are loaded (FloorMap is
now registered). This does not establish that the latest source is loaded, or
that terrain rendering works. The installed Ghostty release reports 0.3.2.0.
No character movement, teleport, game restart or plugin reload was performed
during this check.
