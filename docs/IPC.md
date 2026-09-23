# XivWayfinder IPC

Other plugins can point the player somewhere without referencing XivWayfinder's assembly, through Dalamud's IPC.
The names and types below are the contract; they live in [`src/Shared/IpcContract.cs`](../src/Shared/IpcContract.cs)
and a host test checks that every one of them is written in this file.
They follow the family's `<Plugin>.v1.<Verb>` naming (`XivArcade.v1.Search`, `XivDesktop.v1.Launch`,
`Linkpearl.v1.GetStatus`); a breaking change would come as `v2` names beside these.

> **Status:** the functions are registered and their argument checks and JSON are covered by host tests. No
> other plugin has called them in the game yet.

| Name | Arguments → result | What it does |
| --- | --- | --- |
| `XivWayfinder.v1.SetTarget` | `uint territoryId, float x, float z, string label` → `bool` | Point at world position (`x`, `z`) in that territory. |
| `XivWayfinder.v1.SetMapTarget` | `uint territoryId, float mapX, float mapY, string label` → `bool` | The same, in the map coordinates the game prints. |
| `XivWayfinder.v1.Clear` | → `bool` | Forget the target set by IPC or `/wayfinder`. |
| `XivWayfinder.v1.GetState` | → `string` | What XivWayfinder is pointing at, as JSON. |

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
* The functions can be called from any thread. The pointer picks the target up on the next frame.
* XivWayfinder never moves the character, whatever is asked of it.

## Calling it

```csharp
// once, e.g. in your plugin's constructor
var setTarget = pluginInterface.GetIpcSubscriber<uint, float, float, string, bool>("XivWayfinder.v1.SetTarget");
var setMapTarget = pluginInterface.GetIpcSubscriber<uint, float, float, string, bool>("XivWayfinder.v1.SetMapTarget");
var clear = pluginInterface.GetIpcSubscriber<bool>("XivWayfinder.v1.Clear");
var getState = pluginInterface.GetIpcSubscriber<string>("XivWayfinder.v1.GetState");

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

### For Ghostty's `/ask` coordinate links

A link that carries the zone and the map coordinates the answer printed maps directly onto
`XivWayfinder.v1.SetMapTarget(territoryId, mapX, mapY, label)`. When only a zone name is known, resolve it to a
`TerritoryType` row first; the player can also type `/wayfinder 11.2 14.5 Central Shroud`, which does that
lookup itself.

### For XivMcp

`XivWayfinder.v1.GetState` answers "where is the player being pointed, and how far is it" without any game reads of
its own; `XivWayfinder.v1.SetTarget` and `XivWayfinder.v1.Clear` are the write side.

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
| `style` | `bead`, `glove` or `both`. |
| `playerTerritoryId` | The player's territory. |
| `guide` | `none`, `walk` (same zone, pointing), `arrived`, `teleport` (another zone, an aetheryte is named) or `elsewhere` (another zone without an aetheryte). |
| `navmesh` | The pointer follows a vnavmesh path rather than a straight line. |
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
