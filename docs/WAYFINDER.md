# XivWayfinder: the full guide

> **Status: not yet seen in game.** Everything here is the design as built and host-tested (the maths, the
> priority rules, the timing, the command and IPC parsing); what the pointer looks like in the game, and whether
> the game reads below return what they are expected to, has not been observed. The `/wayfinder test` command
> exists to check it: see [Proving it in game](#proving-it-in-game).

## What it points at

Three sources, in this priority order when *Follow* is **Everything, by priority** (`/wayfinder auto`):

1. **A target you or another plugin set.** `/wayfinder X Y [zone]`, `/wayfinder test`, or
   [IPC](IPC.md) (`XivWayfinder.v1.SetTarget`, `XivWayfinder.v1.SetMapTarget`). One at a time; a new one replaces the old,
   `/wayfinder clear` removes it, and reaching it clears it (after 1.5 s inside the arrival radius, unless
   *Forget a /wayfinder target once reached* is off).
2. **Your map flag.** The first flag in `AgentMap.FlagMapMarkers` while `FlagMarkerCount` is above zero. The flag
   stores world X and Z (`XFloat`, `YFloat`), not a height.
3. **Your quest's next step**, when the game gives it a location (below).

`/wayfinder target`, `/wayfinder flag` and `/wayfinder quest` follow only that source; each source can also be
switched off in the settings. Setting a target by command switches *Follow* back to everything when it was set
to flag or quest only, and says so; an IPC call never changes your settings.

### Where a quest step is

What was looked at in the game's data structures (FFXIVClientStructs as shipped with Dalamud 15.0.3.5, and the
Lumina sheets), and what XivWayfinder uses:

| Source | What it has | Used |
| --- | --- | --- |
| `QuestManager.NormalQuests` | the journal: quest id, current sequence, hidden, priority | which quests are active |
| `QuestManager.TrackedQuests` | the tracked list: `QuestType`, `Index` into `NormalQuests` | which quest comes first; `QuestType == 1` is taken to mean a journal quest (not checked in game) |
| `AgentMap.EventMarkers` | the markers the map draws for quests and events: `ObjectiveId`, `Position` (world, with height), `TerritoryTypeId`, `LevelId` | **first choice**: the live objective, what the game itself shows |
| `Quest.TodoParams` → `Level` | per to-do row: `ToDoCompleteSeq` and up to eight `Level` rows (world X, Y, Z, territory) | **fallback**: also knows steps in other zones |
| `AgentMap.MapQuestLinkContainer` | "continues on another map" links (`QuestId`, `LevelId`, source and target map) | not used yet |

The quest chosen is the first tracked one, then a priority quest, then the journal's order; hidden quests are
skipped. For that quest, the map markers whose `ObjectiveId` names it (by journal id or by `Quest` row, 0x10000 +
id) give the place, the nearest one in your zone first. When there are none, the to-do rows whose
`ToDoCompleteSeq` equals the quest's current sequence (or else the next later one) give `Level` rows, again the
nearest in your zone first, else the first elsewhere. The first quest with any location wins.

Known gaps: event markers depend on what the map has loaded; a step with no `Level` (a duty, a cutscene, an item
to hand in anywhere) has no location and is skipped; the reading of `ToDoCompleteSeq` is the best available
guess, not a documented meaning. Nothing of this has been checked against a real journal yet.

### Another zone

When the target is in another zone, XivWayfinder does not point: a small glass note above your character says
**Teleport to *aetheryte*** (the aetheryte nearest the target in that zone, attuned ones first, *(not attuned)*
otherwise) with the target and its zone underneath. Aetheryte positions come from the `MapMarker` sheet
(`DataType` 3, `DataKey` the aetheryte), converted from map pixels to world coordinates, else the aetheryte's
first `Level` row. A zone without one says *In zone*. Switch *In another zone, name the nearest aetheryte* off to
always get the plain zone note. XivWayfinder never teleports you.

## What it looks like

*Pointer*: **Bead**, **Glove** or **Both** (`/wayfinder bead|glove|both`, default both).

* **The bead** floats 1.5 to 3 yalms ahead of your character (default 2.2) at chest height, in the direction to
  go. It bobs gently, breathes (its size and glow swell and ebb over 2.6 s), and its brightness says how well you
  are headed: dim when you face the right way, brighter as you turn away, full when you face the opposite way.
  It keeps its apparent size readable as the camera zooms. The colour is yours to pick (soft gold by default).
* **The glove** is the game's own white pointing-hand cursor, the classic Final Fantasy finger (see below). While
  the way ahead is on screen it floats near your character, above and just ahead, pointing along the ground
  direction as the camera sees it. When the way lies off screen (behind the camera, or far to one side) it slides
  to the edge of the view where that direction leaves it, fingertip inside the margin, pointing out. It taps
  forward once a second. Pointing left, it is mirrored so the thumb stays on top.
* **Trail** (off by default): a dotted line of small beads along the way, every 2.5 yalms from 2 yalms out, ten of
  them by default, with a soft shimmer travelling outward along them.
* **Distance**: `42 y` under the bead (or the glove) on hover (default), always, or never.
* **Arrived**: within 4 yalms (the arrival radius) the pointer gives way to a breathing ring on the spot and a
  small bead with *here* above it.

All of it is drawn on ImGui's foreground draw list, placed with `IGameGui.WorldToScreen`. The direction on screen
comes from projecting two points close to your character (one 1.5 yalms ahead along the ground): both are in front
of the camera even when the target is behind it, so the glove never flips the wrong way. The overlay pushes no
ImGui state and catches every exception: a failed frame is logged (once a minute at most) and skipped.

### The glove picture

XivWayfinder ships no image of the glove. At run time it reads the cursor from your installed game files, the way
the game draws it, so texture mods apply too:

| | |
| --- | --- |
| ULD | `ui/uld/Cursor.uld`: one texture, one part list with one part |
| Texture | `ui/uld/Cursor.tex` (64×64, BC3), `ui/uld/Cursor_hr1.tex` (128×128) preferred |
| Part | U 0, V 0, W 64, H 64 in the 1× texture: the whole texture |
| Picture | the white gloved hand, index finger pointing right; palm centre at (0.48, 0.53) of the part, fingertip at (0.80, 0.445) |

Measured on game version 2026.09.15.0000.0000 by reading those files read-only from the game's sqpack. At run
time the part is taken from the ULD itself (the first part that uses the cursor texture), so a patch that moves
it is followed; the palm and fingertip fractions above are fixed. If neither texture exists, or *Use the game's
own glove cursor* is off, XivWayfinder draws its own glove from simple shapes (cuff, palm, three curled fingers,
thumb, pointing finger), white with an ink outline: an original drawing in the same spirit, not a copy.

## When it steps aside

Always hidden during cutscenes, zone changes, group pose, when you hide the game UI, and
with no character. Hidden in combat and in duties by default; both are settings.

## Walkable paths with vnavmesh

With [vnavmesh](https://github.com/awgil/ffxiv_navmesh) installed and loaded, and *Follow vnavmesh's walkable
path* on (the default), XivWayfinder asks vnavmesh for the path to the target and points at its next corner instead
of straight at the target; the trail, when on, follows the path. A target without a height (the flag, typed
coordinates) is placed on the navmesh floor under it first. A new path is asked for only when the target moves,
you stray more than 6 yalms from the path, or there is none yet, and never more often than every two seconds.
Until a path arrives, or when none is found, it points in a straight line. The settings window shows what
vnavmesh is doing.

**Pointing only.** XivWayfinder uses vnavmesh's queries (`Nav.IsReady`, `Nav.PathfindCancelable`,
`Query.Mesh.NearestPoint`) and never its movement gates (`Path.MoveTo`, `SimpleMove.*`). Nothing in XivWayfinder
presses a key, moves, turns or teleports your character.

## Commands

| Command | What it does |
| --- | --- |
| `/wayfinder` | Opens the settings. |
| `/wayfinder X Y [zone]` | Points at map coordinates, in the zone you are in or the one named. Paste them as the game writes them: `11.2 14.5`, `(11.2, 14.5)`, `X: 11.2 Y: 14.5 Z: 0.3`, or a chat map link's text. |
| `/wayfinder clear` | Forgets that target. |
| `/wayfinder test [yalms]` | A target 20 yalms (3 to 200) straight ahead of your character. |
| `/wayfinder auto` · `target` · `flag` · `quest` | What to follow. |
| `/wayfinder bead` · `glove` · `both` | What the pointer looks like. |
| `/wayfinder on` · `off` · `toggle` | Shows or hides it. |
| `/wayfinder status` | One line: what it points at, how far, and why it is hidden if it is. |

`/xivwayfinder` is an alias for `/wayfinder`, with every argument the same.

Zone names match case- and accent-insensitively: exactly first, then the start of a name, then anywhere in it;
a territory id works too. Among equal matches the lowest territory id wins.

## Proving it in game

Nothing below has been done yet. With the plugin loaded as a dev plugin:

1. In an open field, `/wayfinder test`. Expect the bead 2.2 yalms ahead at chest height, dim (you face it), and
   the glove above it pointing forward; `/wayfinder status` says about 20 yalms.
2. Turn the character around: the bead brightens, and the glove swings to point back past you; with the camera
   facing forward the glove moves to the bottom edge of the screen.
3. Walk to it: the distance counts down, and within 4 yalms the ring appears; after a moment chat says you have
   arrived and the target is cleared.
4. Place a map flag in the same zone: the pointer follows it. Place one in another zone: the note names an
   aetheryte there.
5. Track a quest with a marker on the map and clear the flag: the pointer follows the quest.
6. Enter a cutscene, hide the UI, start a fight: the pointer steps aside as the settings say.
7. With vnavmesh installed, stand behind a wall from a flag: the pointer aims around it.
8. Switch *Use the game's own glove cursor* off and on: the drawn glove and the game's glove swap.

Please report what you see, with `/wayfinder status` and `/xllog` lines for anything that looks wrong.
