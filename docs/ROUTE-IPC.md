# Route snapshot IPC

`XivWayfinder.v1.GetRoute`: no arguments, returns a JSON string. Optional
consumers do not reference the Wayfinder assembly. No movement APIs are involved.

Fields: `version` (1), `territoryId`, `generatedAt` (UTC Unix milliseconds),
`walkable`, `action`, `next`, `points` (arrays of world X,Y,Z, up to 512 corners).
New publishers also include optional `visual` for a walkable path:

- `progress`, `guideArc`, `lookAheadArc`: ground-plane distances from the route's start;
- `guide`, `lookAhead`: corresponding world X,Y,Z positions, with navmesh height;
- `color`: the Wayfinder trail's rendered RGBA tint, components in 0..1.

Progress is the same continuous navigation progress used by the guide, not a
second nearest-segment projection. The independently moving companion's actual
arc may temporarily be behind the player's while it returns; its light pool
must not be snapped to a static ahead offset. Guide and look-ahead distances
retain their distinct placement/aim meanings. Consumers must validate arcs against the path,
positions against those arcs, and colors for finite bounds. Older v1 publishers
without `visual` remain compatible; malformed supplied metadata must be rejected.
Refresh these small values without rebuilding a cached route raster when its
geometry has not changed.
Consumers must reject unsupported versions, malformed/non-finite coordinates,
wrong territory and timestamps older than two seconds or in the future. On IPC
failure/unload, discard the previous route. This is an ephemeral snapshot, not
persistable navmesh geometry or proof of floor classification.

Only visible, enabled, same-zone walking guidance with a valid navmesh path
publishes points. Teleports, arrival, missing paths and hidden guidance publish
empty points but retain the current action/next-step cue. Oversized paths are
rejected, never silently truncated. The snapshot refreshes at most every 250 ms
in steady state; target and validity changes invalidate it immediately.

The floor-map consumer polls at 4 Hz, renders the route into its depth-tested rug
material, and displays the action/next-step cues in settings. Its route tint and
guide/look-ahead light pools use the optional visual metadata; passed portions
fade behind shared progress. This is source integration, not certification of
every terrain or composition mode. Quest completion and teleport execution are
not implied by receiving this snapshot.
