# XivWayfinder IPC

Other plugins can point the player somewhere without referencing XivWayfinder's assembly, through Dalamud's IPC.
The names and types below are the contract; they live in [`src/Shared/IpcContract.cs`](../src/Shared/IpcContract.cs)
and a host test checks that every one of them is written in this file.
They follow the family's `<Plugin>.v1.<Verb>` naming (`XivArcade.v1.Search`, `XivDesktop.v1.Launch`,
`Linkpearl.v1.GetStatus`); a breaking change would come as `v2` names beside these.

> **Status:** target/state argument checks and JSON have host coverage; external live calls to those gates
> remain unverified. XivRug’s optional shared-depth calls have been verified live in both plugin load orders
> and through Wayfinder unload/reload on 2026-10-03 (details below).

| Name | Arguments → result | What it does |
| --- | --- | --- |
| `XivWayfinder.v1.SetTarget` | `uint territoryId, float x, float z, string label` → `bool` | Point at world position (`x`, `z`) in that territory. |
| `XivWayfinder.v1.SetMapTarget` | `uint territoryId, float mapX, float mapY, string label` → `bool` | The same, in the map coordinates the game prints. |
| `XivWayfinder.v1.Clear` | → `bool` | Forget the target set by IPC or `/wayfinder`. |
| `XivWayfinder.v1.GetState` | → `string` | What XivWayfinder is pointing at, as JSON. |
| `XivWayfinder.v1.AcquireWorldDepth` | `nint device, uint width, uint height` → `nint` | Optional COM depth-view lease for compatible native scene renderers; render-stage restrictions below. |
| `XivWayfinder.v1.Arrived` | event: `string label, uint territoryId` | Raised once after the player remains inside a target's arrival radius. |
| `XivWayfinder.v1.StateChanged` | event: `string stateJson` | Raised when the target or guide kind changes, with the `GetState` JSON. |

## Rules

* **`territoryId`** is a `TerritoryType` row id (what `IClientState.TerritoryType` returns). `0` means the zone the
  player is in now.
* **`x`, `z`** are world coordinates (a `GameObject.Position`'s X and Z; Y is height and is not needed). They must
  be finite and within ±5000, or the call returns `false` and nothing changes.
* **`mapX`, `mapY`** are map coordinates as the game shows them, `X: 11.2 Y: 14.5` → `11.2f, 14.5f`, above 0 and
  below 44. They are converted with the territory's own map (`TerritoryType.Map`: `SizeFactor`, `OffsetX`,
  `OffsetY`). `false` for a territory without a map.
* **`label`** is shown beside the target and returned by `GetState`. It may be empty. Control and formatting
  characters become spaces, runs of spaces collapse, and it is cut at 120 characters.
* A new `SetTarget` or `SetMapTarget` replaces the previous IPC or `/wayfinder` target. The player's map flag and
  quest are separate sources, and by default the IPC/command target comes first (see *Priority* in
  [WAYFINDER.md](WAYFINDER.md)). If the player has chosen to follow only the flag or only quests, or has switched
  targets from other plugins off, an IPC target is accepted but not shown: `GetState` says what is shown.
* Once the player has stood within the arrival radius (4 yalms by default) for a moment, the target is cleared,
  unless the player has turned that off.
* Target/state functions can be called from any thread. The pointer picks the target up on the next frame.
  `AcquireWorldDepth` has the stricter render-stage contract below.
* XivWayfinder never walks or turns the character. However, when the user has
  explicitly enabled automatic Teleport, changing the followed target or journey
  can start a Teleport cast and spend gil. `GetState` includes
  `autoTeleport: { enabled, maxGil, status }`; `enabled` reports the persisted
  opt-in even while guidance itself is disabled. IPC cannot opt into automatic
  Teleport or change the cost limit. Consumers should disclose this consequence
  before changing guidance, not promise that setting a target never moves the player.
  `StateChanged` also publishes changes to the teleport setting, limit and status.

## Calling it

```csharp
// once, e.g. in your plugin's constructor
var setTarget = pluginInterface.GetIpcSubscriber<uint, float, float, string, bool>("XivWayfinder.v1.SetTarget");
var setMapTarget = pluginInterface.GetIpcSubscriber<uint, float, float, string, bool>("XivWayfinder.v1.SetMapTarget");
var clear = pluginInterface.GetIpcSubscriber<bool>("XivWayfinder.v1.Clear");
var getState = pluginInterface.GetIpcSubscriber<string>("XivWayfinder.v1.GetState");
var arrived = pluginInterface.GetIpcSubscriber<string, uint, object>("XivWayfinder.v1.Arrived");
var stateChanged = pluginInterface.GetIpcSubscriber<string, object>("XivWayfinder.v1.StateChanged");

// Subscribe once, and unsubscribe the same delegates when your plugin unloads.
Action<string, uint> onArrived = (label, territoryId) => { /* queue brief work */ };
Action<string> onStateChanged = stateJson => { /* parse or queue it */ };
arrived.Subscribe(onArrived);
stateChanged.Subscribe(onStateChanged);

// later; IpcNotReadyError means XivWayfinder is not installed or not loaded
try
{
    setMapTarget.InvokeFunc(0, 11.2f, 14.5f, "the ferry dock");   // in the current zone
    setTarget.InvokeFunc(956, -63.4f, 214.9f, "somewhere");         // world X/Z in territory 956 (numbers illustrative)
}
catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError)
{
    // offer to install XivWayfinder, or print the coordinates instead
}
```

Event subscribers run synchronously on Dalamud's framework thread and should return quickly. `StateChanged` is
debounced: changing distance alone does not raise it. Call `GetState` whenever a fresh distance is needed.

### For Ghostty's `/ask` coordinate links

A link that carries the zone and the map coordinates the answer printed maps directly onto
`XivWayfinder.v1.SetMapTarget(territoryId, mapX, mapY, label)`. When only a zone name is known, resolve it to a
`TerritoryType` row first; the player can also type `/wayfinder 11.2 14.5 Central Shroud`, which does that
lookup itself.

### For XivMcp

`XivWayfinder.v1.GetState` answers "where is the player being pointed, and how far is it" without any game reads of
its own; `XivWayfinder.v1.SetTarget` and `XivWayfinder.v1.Clear` are the write side.

## `XivWayfinder.v1.AcquireWorldDepth`

This optional, in-process rendering gate returns an `ID3D11DepthStencilView*` as `nint`. It shares custom-world
occlusion between the imported-guide renderer and XivRug; it does not expose or replace the game’s depth buffer.
Obtain the subscriber with `GetIpcSubscriber<nint, uint, uint, nint>("XivWayfinder.v1.AcquireWorldDepth")`.

* Call synchronously from a verified native **pre-UI** pass, or the validated fallback at `UiBuilder.Draw`
  before plugin UI and the original Present. Both use the D3D11 immediate context and actual backbuffer.
  The fallback may temporarily bind that backbuffer only when all color outputs are empty, preserving and
  restoring the complete binding scope. Do not call from a framework task, an ImGui draw-list callback,
  a deferred context or an offscreen target. The caller owns these checks; the provider’s lock does not
  make arbitrary-thread rendering valid.
* Pass the current Dalamud `UiBuilder.DeviceHandle` and actual full viewport width/height, with origin `(0, 0)`.
  The current renderers require one viewport. Dimensions must each be in `1..16384`; the shared texture is
  single-sampled `D32_FLOAT`. The provider rejects a null/mismatched device, invalid dimensions, an unarmed
  frame or a disposed provider by returning `0`.
* A nonzero result owns **one COM reference**. Bind it only for the current synchronous pass, restore the
  previous render targets and all changed pipeline state in `finally`, then call `Release` exactly once,
  including after an upload or draw failure. Never cache the pointer across passes or clear the shared view.
  The reference keeps that resource alive if the provider unloads or replaces its own resource meanwhile.
* Every participant uses `LESS_EQUAL` and pixel depth `cameraDistance / (1 + cameraDistance)`, where distance
  is from the same active world camera. Opaque and accepted masked fragments write depth. Transparent `BLEND`
  batches retain depth testing without depth writes; this does not provide order-independent transparency.
  Keep game scene depth read-only and separate for world-geometry occlusion.
* The actual swapchain identity and `GetLastPresentCount` identify the render epoch. Native drawing and
  fallback callbacks share that count before the original Present, regardless of plugin callback order.
  The first acquisition clears depth to `1`; later acquisitions preserve earlier participants’ depth.
  This also works when no imported guide is visible, preventing departed models from leaving stale depth.
  A replaced swapchain, size or device resource needs a fresh clear; a failed clear remains pending.
  An unavailable presentation counter returns `0` and emits a throttled warning.
* Treat a missing gate, `0`, or an IPC/provider failure as unavailable. Use a private depth buffer for that
  pass; cross-plugin occlusion is then unavailable. The provider can unload between `HasFunction` and
  `InvokeFunc`, so presence checks alone are insufficient. Reacquire on the next pass to discover a reload.

Seven focused host cases cover opaque draw order, subsequent/empty frames, nearer folds, transparent
write policy, resource replacement, unarmed/disposed/reloaded providers and failed-clear retry. These tests
model frame/depth decisions; native COM/device behavior is checked separately in game.

**Live check — 2026-10-03:** with Wayfinder loaded last at 11:41 UTC, the opaque Fox remained fully visible
over XivRug in Survey, Walk and Run; with XivRug loaded last at 11:43, Survey and Run also remained visible.
Both renderers logged shared-depth submissions. XivRug’s private fallback also rendered with an older
Wayfinder that lacked the provider. Unloading Wayfinder at 11:46 while XivRug remained loaded left the rug
intact without a stale guide silhouette; reloading restored Fox over the rug and resumed shared submissions.
Stopping temporary preview restored the saved companion. Resize/device replacement and general transparent
composition were not verified live.

## `XivWayfinder.v1.GetState`

```json
{
  "version": 1,
  "enabled": true,
  "visible": true,
  "reason": "showing",
  "mode": "auto",
  "style": "both",
  "playerTerritoryId": 148,
  "guide": "walk",
  "navmesh": true,
  "routeNote": "",
  "target": {
    "source": "flag",
    "territoryId": 148,
    "zone": "Central Shroud",
    "x": 12.35,
    "z": -6.5,
    "y": null,
    "label": "map flag"
  },
  "distance": 42.19,
  "aetheryte": null
}
```

| Field | Meaning |
| --- | --- |
| `version` | `1`. A breaking change to this shape raises it. |
| `enabled` | The player has the pointer switched on. |
| `visible` | Something is being drawn right now. |
| `reason` | Why not, when not: `switched off`, `no character`, `changing zone`, `cutscene`, `group pose`, `game UI hidden`, `in combat`, `in a duty`, `nothing to point at`. |
| `mode` | `auto`, `target`, `flag` or `quest`: which sources the player follows. |
| `style` | `bead`, `glove`, `both` or `minion`. |
| `playerTerritoryId` | The player's territory. |
| `guide` | `none`, `walk` (same zone, pointing), `arrived`, `teleport` (another zone, an aetheryte is named) or `elsewhere` (another zone without an aetheryte). |
| `navmesh` | The pointer follows a vnavmesh path rather than a straight line. |
| `routeNote` | When `navmesh` is false: why, as vnavmesh's state (`not installed`, `no path found`, `waiting for this zone's mesh`, `asking for a path`, `switched off in the settings`, ...); empty when following a path. |
| `target` | `null`, or the target: `source` is `target` (IPC or `/wayfinder`), `flag` or `quest`; `y` is `null` when the height is unknown. Numbers are rounded to 0.01. |
| `distance` | Ground distance in yalms for `walk` and `arrived`, else `null`. |
| `aetheryte` | For `teleport`: `{"id", "name", "attuned"}`, the aetheryte nearest the target in its zone, attuned ones first. |

## What XivWayfinder calls

When [vnavmesh](https://github.com/awgil/ffxiv_navmesh) is installed and the player has not switched it off,
XivWayfinder asks it for the walkable path and points at the path's next corner. These names come from vnavmesh's
own `vnavmesh/IPCProvider.cs` (checked against commit `6fc8072`, 2026-08-31):

| Gate | Use |
| --- | --- |
| `vnavmesh.Nav.IsReady` | Is a mesh loaded for this zone? |
| `vnavmesh.Nav.PathfindCancelable` | `(Vector3 from, Vector3 to, bool fly, CancellationToken)` → `Task<List<Vector3>>`: the path's corners. At most one request at a time, at most one every two seconds; cancelled when the target changes or the plugin unloads. |
| `vnavmesh.Query.Mesh.NearestPoint` | `(Vector3, float halfExtentXZ, float halfExtentY)` → `Vector3?`: the floor under a target that has no height (a flag, typed coordinates). |

XivWayfinder never calls `vnavmesh.Path.*` or `vnavmesh.SimpleMove.*`, the gates that move the character.
