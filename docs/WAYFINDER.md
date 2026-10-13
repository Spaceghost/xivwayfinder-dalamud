# XivWayfinder: the full guide

> **Development status:** the native pointer, moogle and black chocobo have been observed rendering in game, and clicking native library entries previews models without closing the selector. Import and animation fixtures pass host tests. Local Fox and CesiumMan previews have also rendered and animated in game; closing the preview restored the chosen guide. Choosing Fox and reopening its saved collection entry also passed a live check. The complete model catalog and all movement/terrain combinations have not been verified. See the dated check and limitations below.

## Guide library and appearance

Open `/wayfinder models` to browse Minions, Mounts, Creatures, Characters or Model IDs. Add a search, such as `/wayfinder models delivery moogle`, to open matching results directly. Search by name, model ID, NPC appearance ID or model kind; multiple search words narrow the results together. Pinned Pointer, Moogle and Chocobo choices stay available. Selecting a result starts a temporary world preview. Adjust size, rotation, grounded/floating placement and movement pace before **Use this guide**, which becomes available when that selected native model is drawn. Closing the selector or stopping its preview restores the saved guide without changing your destination. Per-model profiles remember appearance and placement preferences.

Native guide loading has a bounded retry policy: an allocated actor must become visible or finish loading its draw resources within ten seconds. Allocation alone does not clear previous failures. Failed attempts wait two seconds, then four seconds, and stop after the third failure until an explicit retry or model change. Each timeout is counted once. Once resources have loaded, a temporarily hidden or culled actor is kept; visibility still determines whether the selector can use that preview. Eight host tests pass, including three never-ready allocations, loaded-but-hidden actors and repeated timeout polling. The combined build passed 1,261 host tests and loaded on 2026-10-03 at 10:30 UTC; no warnings were observed for the next two minutes. Gairhard reached the ready state, but the three-failure stop itself remains host-tested only.

Characters use named NPC appearance data, including their clothing and dyes. Raw human Model IDs use your character's appearance instead. Creatures include named NPC models and hunt creatures whose names are explicitly associated with their models in the game data; this is not a complete name index for every enemy. Other native model types are marked experimental, so check the preview before choosing one.

Named demihumans retain their NPC equipment, which can determine which body parts appear. For a raw demihuman Model ID, **Authored appearance** lets you explicitly choose a named character using that body. An installed-data audit found 3,450 named demihuman appearances with equipment. Examples for preview checks are delivery moogle (NPC appearance 1000063, model 15), Qiqirn trader (1000902, model 14) and Sweetnix Rosycheeks (1001209, model 6). These data checks do not verify every appearance in game. If one source sheet fails, other collections remain available; expand the loading issues and choose **Retry reading model library** to try that source again.

Named hunt creatures also retain their authored battle-NPC outfits. Battle-NPC appearance IDs and saved profiles are separate from event-NPC appearances, even when they share a model. For example, Slipkinx Steeljoints (battle NPC 4543) and Sweetnix Rosycheeks (event NPC 1001209) both use model 6. A smoke check against installed data found 37,051 catalog entries, including the 3,450 named event-NPC demihuman appearances, and verified all 15 named demihuman hunts against their authored equipment by applying each outfit to an isolated actor buffer. A local live check on 2026-10-03 showed Dark Helmet (battle NPC 3187, model 722) ready in the open selector at size 1.000, with its full metal helmet and crab legs visible beside the player. The remaining demihuman hunt appearances and their movement still need live checks.

Human hunts with authored customization now also appear under Characters. Search `/wayfinder models Albin the Ashen` for battle NPC 3182, model 0, or `/wayfinder models Omni` for battle NPC 4538, model 1283. Albin's model-zero row is valid because his battle-NPC customization supplies the human appearance. An installed-data check verified both identities, all 26 customization bytes and their outfits in isolated actor buffers, bringing the catalog to 37,053 entries. The build passed with no warnings or errors.

**Human hunt previews — 2026-10-03:** Albin rendered at fitted size 0.702 in his dark chainmail outfit, first at rest and then with a visible walking stride. Omni rendered at fitted size 0.698 in gray and black armor with red accents, first at rest and then with a visible running stride. Both checks kept the selector open with grounded placement selected. Albin's long back-carried weapon and Omni's blue weapon were also visible. These checks verify those appearances and preview gaits; they do not establish autonomous route movement or compatibility across the full catalog.

While a temporary native or local-model preview is active with guidance visible, the legacy flat cursor and paired bead are hidden. The saved pointer style, destination, route trail and travel cue are preserved; stopping the preview restores the selected style. A local before/after check on 2026-10-03 confirmed that the white glove and paired bead visible in **Both** mode disappear during the native Dark Helmet preview. A separate imported-model check that day showed both visible before preview, hidden while Fox loaded, and still hidden when Fox rendered Survey at size 1.5 with scene rendering active. Closing the imported preview restored **Both** mode's flat cursor and bead.

A local development check on 2026-10-02 confirmed that delivery moogle appearance 1000063 renders with its
authored hat and mailbag. The selector stayed open and kept **Use this guide** disabled while its draw object
was loading. Stopping the preview restored the chosen imported Fox without changing the destination.
The large 13th Child model (4161) also rendered after automatic fitting at scale 0.048. Preview fitting
uses the model's scaled bounds, including native size factors, and only shrinks a new selection; saved sizes
and manual adjustments are retained. The size control remains available for unusual proportions.

The guide owns its own position on the walkable route: it rounds corners, eases its speed, leads ahead, and returns along a queried connector when you take another walkable route. It does not lock to your character's shoulder. **Lead by** adjusts how close it travels with the trail on; without the trail it takes point at least six yalms ahead. The legacy `/wayfinder beside` command now selects a closer two-yalm route lead, without shoulder locking. Missing or rejected routes hold the guide; there is no crow-flies fallback.

When you stop and the guide is waiting, it can walk to a clear spot on your preferred side. The saved **Side** preference chooses that side and minimum offset; measured guide and player sizes can increase the distance. It captures your heading when settling starts, so turning in place does not make the guide orbit you. The guide uses a separately queried walking path and checks clearance at its endpoint. An unavailable or too-close endpoint leaves it at its last accepted position. Moving cancels the idle request; returning to the main route also requires a queried connector. Zone, destination and model changes reset the request, and owned jumps and teleport rendezvous take priority.

Spacing retains the largest observed body radius for the current model and scale, with a small outward padding. Imported models measure rendered vertices in the bind and current poses; native models use scaled resource bounds. This improves clearance for broad and offset models but is not a guarantee for every unsampled animation. Thirty spacing tests and 36 idle request/integration tests pass, including rejected endpoints, late query results, placement refusal and route handoff.

**Idle arrival check — 2026-10-03:** the saved Fox guide was fully visible beside the squatting player and faced toward them. The accepted path had three corners and ended approximately 2 yalms from the player, beyond the measured 1.85-yalm minimum clearance. This verifies that idle arrival in the live scene; movement cancellation and rejoining the route still need a live check.

The Guide tab controls placement and movement animation. Grounded guides are pinned to confirmed collision ground. Native animation selection checks the model's installed animation packs for idle, walk and run clips, including separate human and demihuman movement packs and the game's authored shared-pack mappings. A model's authored flight clip can supply its movement; a prop with only an idle keeps that idle. Missing clips are not replaced with unverified walking timelines. Human models use the animation variant of their loaded appearance. Ground/float defaults use skeleton metadata and names and can be overridden. The old flat cursor is only drawn for a selected simple-pointer style while no temporary model preview is active.

Native and local-model previews now search 22 nearby collision-checked spots, including alternate sides, compact positions and both sides of the player relative to the camera. Observed model bounds guide floor, body, overhead and sight-line checks. Candidate and movement positions preserve the guide radius plus a 0.35-yalm player-clearance fallback; the player’s actual skeleton is not measured. Movement previews use the exact checked steps and turn back at obstacles; relocation does not interpolate through unchecked space. All 25 focused preview tests pass, covering missing floors, room walls, centered floating models, exact movement, player clearance and avoiding the selector with the projected body envelope. At 10:52–10:53 UTC on 2026-10-03, Gairhard remained fully visible to the right of the open left-hand selector through rest, walking and return to rest outside a Limsa arch. Momodi was also fully visible beside the selector at size 0.917 while resting, walking and running, with obstacle turnbacks observed. The player had independently moved out of the earlier tight room; compact placement in that room has not been rechecked live. Other collision layouts still need live checks. Imported Fox now remains fully visible over XivRug in both plugin load orders, as detailed below. The latest combined build passes without warnings or errors; the 1,261-test full-suite result predates the selector-avoidance additions.

**Preview slope checks — 2026-10-03:** footprint samples now agree with the measured center plane within 0.02 yalms and a normal-alignment check. Low clearance-ray endpoints stay above that plane so ordinary slopes do not look like walls; a first collision hit is never skipped. A host fixture reproducing the measured 0.1875 slope passes, while raised treads, a farther uphill wall and low ceilings remain rejected. The checks retain the 40-ray per-update cap and do not lift the requested model pose. They account for the possible upper extent of the imported renderer’s existing finite-triangle support correction. The 25 passing preview tests cover these cases; broader live slope and tight-room behavior remain unverified.

**XivRug depth sharing — 2026-10-03:** imported guides and the rug now share an optional plugin-owned depth buffer during native scene rendering, fixing the observed cloth-over-Fox error caused by separate buffers and hook order. The [IPC lease contract](IPC.md#xivwayfinderv1acquireworlddepth) keeps game depth read-only and falls back to each renderer’s private buffer when unavailable. Seven focused frame tests pass, covering both opaque draw orders, stale-depth clearing, nearer folds, transparent batches, resource replacement, provider lifetime and failed clears. Both matched builds pass without warnings or errors.

At 11:41 UTC, with Wayfinder loaded last, the full opaque Fox remained visible over the rug in Survey, Walk and Run, with distinct limb poses. At 11:43, with XivRug loaded last, Survey and Run remained fully visible. Shared-depth submissions were logged by both renderers, and no warning entries appeared during seven minutes of checks. Stopping the preview restored the saved companion. At 11:46, unloading Wayfinder while leaving XivRug loaded kept the rug intact without a stale guide silhouette; reloading Wayfinder restored Fox over the rug alongside the player’s deep squat. Saved guide, overhead fairy-trail and rug settings remained unchanged. This verifies the selected opaque Fox and provider fallback/reload in that scene. Transparent `BLEND` ordering remains approximate; resize/device replacement is host-tested only, and sharp stairs and the earlier tight room remain unverified live.

The Trail tab offers ground or overhead placement, line and sparks, glow, opacity, pulse speed, color presets and reduced motion. Ground marks conform their full footprint to measured collision faces, including neighboring treads and their glow. Unknown floor coverage waits for confirmation. Interface settings position and simplify the instruction text.

### Imported models

Open `/wayfinder localmodels`, or **Your uploaded model collection…** in the Guide tab, to browse a local model file, open a downloaded ZIP model pack, or search saved models. `/wayfinder preview PATH` opens the same temporary world preview; quoted paths are accepted. You can also paste a path into the Guide tab and choose **Preview model…**.

For a ZIP pack, use the file picker, `/wayfinder preview PACK.zip`, or `/wayfinder import PACK.zip`, then explicitly choose a GLB, glTF or OBJ entry from the list, even when the pack contains one model. Click other entries to compare them while the selector stays open. **Use this guide** keeps a local copy with its sidecars for the saved guide. Closing or canceling an uncommitted pack removes its temporary copy after outstanding model reads finish, and restores the chosen guide. Packs may contain up to 512 entries and 32 MB of unpacked files; the normal model, texture and geometry limits still apply.

The pack importer passes 30 focused host tests, including path and link rejection, duplicate names, expansion limits, cancellation, retained copies and nested glTF buffers. Its unchanged public ambientCG `3DApple002_LQ-1K-JPG.zip` fixture loads the original 998-triangle OBJ with its MTL/JPEG base-color material. The fixture and CC0 provenance are in `tests/XivWayfinder.Core.Tests/Fixtures`.

**ZIP live checks — 2026-10-03:** `/wayfinder import` opened that pack with an explicit model entry list and preserved the saved Fox path. Clicking the OBJ rendered the textured apple beside the player at size 1.5; the selector stayed open and reported an active scene renderer. Canceling removed the temporary extracted directory while keeping the source ZIP and saved Fox unchanged. After reopening the pack, an entry path using alternate Windows casing and separators still loaded the textured preview. **Use this guide** saved the canonical extracted path, and closing the selector kept the retained OBJ. Reopening that saved model rendered the textured apple again, with both Apple and Fox available in the saved collection.

The selector stays open while you adjust size, facing, ground/float placement, animation, in-place motion and rest/walk/run preview pace. **Use this guide** becomes available after that candidate has loaded and its renderer has submitted it to the game scene. Choosing it saves the model and its settings in a collection of up to 32 entries and leaves the selector open for further browsing. Closing the selector, **Stop preview**, or `/wayfinder preview stop` restores the chosen guide; previewing does not replace the destination or journey. **Show the pointer** must be enabled and guidance visible for a world preview to appear.

`/wayfinder import PATH` remains a direct selection command for a local OBJ, GLB or glTF 2.0 model; a ZIP path opens the pack selector and preserves the saved guide until you choose **Use this guide**. Choose **Use game model** in the Guide tab, or choose a native model from the library, to return to a game model.

| Format | Supported appearance and motion |
| --- | --- |
| GLB / glTF 2.0 | Embedded or local buffers and PNG/JPEG base-color textures, UVs, vertex colors, node transforms, skeletal skins, morphs, and animation clips. STEP, LINEAR and CUBICSPLINE interpolation are supported. Triangle lists, strips, fans, `KHR_mesh_quantization` attributes, and EXT/KHR meshopt compression are supported. Required KHR meshopt decoding has also been checked live with the official test model. |
| OBJ + MTL | Triangle geometry, UV seams, vertex colors, diffuse color and opacity, and local PNG/JPEG diffuse textures. Texture scale, offset and clamp are supported. OBJ has no skeletal animation. |

Keep the downloaded folder intact: glTF/GLB `.bin` buffers and textures, or an OBJ's MTL files and textures, must remain inside the selected model's folder or its subfolders. GLB and glTF can also use embedded base64 resources. Resource references cannot fetch remote URLs or escape the model folder. Models are limited to 20,000 triangles and 60,000 vertices; the file and its sidecars must total at most 32 MB. Each texture may be at most 4,096 pixels per side, with a combined decoded limit of 32 megapixels. Distinct decoded meshopt buffer views have a separate combined 32 MB limit; shared views are decoded once.

Animation can follow movement using recognizable idle/walk/run names, or use a chosen clip. Floating models prefer hover, flight or swim clips; automatic selection skips transition and directional clips and keeps the authored rest pose when it finds no suitable loop. A single generic clip such as `Animation 1` can play during movement; multiple unknown clips and named actions still require a manual choice. Optional in-place sampling removes horizontal travel on the model's common root while preserving limb animation, vertical bounce and the first pose's authored offset. This prevents a walking loop from repeatedly carrying the guide away from its route position.

Indexed glTF geometry now keeps only vertices referenced by its triangles, with matching UVs, colors, normals, skin weights and morph data remapped together. Unused accessor vertices no longer distort model sizing, ground bounds or in-place root selection, and retained geometry determines the vertex limit. Skins can also use the joint-count prefix of a longer shared inverse-bind matrix accessor. All 139 focused importer tests pass, including eight new cases for these fixes.

**Indexed-geometry live check — 2026-10-03:** a temporary double-sided material version of `IndexedUnusedVertex`, with its geometry unchanged, rendered a large grounded teal triangle at size 1.5 in the open local-model preview. The renderer reported active scene rendering. This verifies that unused vertices no longer shrink that fixture's visible triangle; the inverse-bind matrix-prefix fix remains host-tested only.

Grounded imported models also use the confirmed floor triangle to clear slopes when it covers their entire rendered footprint. The animated, rotated and scaled pose receives only the minimum upward lift needed for 0.03 yalms of vertical clearance above that finite, walkable plane. Existing flat-ground height and authored bounce are preserved. An invalid triangle or incomplete footprint retains the existing center-floor placement; the correction never extrapolates onto adjacent faces, steps or props. Floating models and approved jump arcs are unchanged.

The slope-support helper passes 18 focused host tests, including partial coverage, reversed winding, world offsets and preserved bounce. A separate host check sampled all three official Fox clips at 121 points each: all 363 poses cleared a 25% slope, with at most 0.1432 yalms of added lift. These are geometry and animation-fixture checks; this correction has not yet been verified on slopes in the live game.

The import tests cover skinning, interpolation, morphs, texture/material data, in-place motion and malformed input. Bundle fixtures cover external buffers and textures, embedded data URIs, path confinement, size limits and strip/fan triangulation. Khronos Fox and RiggedSimple models also load and produce finite animated poses in host checks. An earlier quantization check used the official MeshoptCubeTest fixture with ordinary fallback buffers and matched an independently dequantized reference across 22 poses. Optional unsupported compression can still use ordinary buffer or PNG/JPEG fallbacks; unused compressed images do not prevent that fallback from loading. Required Draco and Basis decoding, FBX, raw game model files, advanced PBR materials and arbitrary model formats remain unsupported.

**Meshopt host checks — 2026-10-03:** required `EXT_meshopt_compression` and `KHR_meshopt_compression` now decode directly, including attribute versions 0/1, triangle and index streams, and their defined filters. The managed decoder validates bounds and stream consumption, supports required placeholder buffers, and skips marked optional fallback files. All 37 new meshopt tests and 203 importer regression tests pass. The official required-compression MeshoptCubeTest matches its uncompressed reference for geometry, colors, normals and 31 sampled animation poses. An independent upstream encoder/decoder comparison passes all 645 cases under the specification: 640 match byte-for-byte and five filtered cases differ by at most one component unit, within the permitted one-ULP tolerance; exponential results match exactly. The pinned upstream MIT license is embedded in the Core assembly.

**Required meshopt live check — 2026-10-03:** the official required-`KHR_meshopt_compression` MeshoptCubeTest rendered its colored cube grid and textured red labels at size 1.5 with active scene rendering. Selecting its authored `RotateCubes` clip while preview pace remained Rest changed the visible white cubes’ orientations between frames one second apart. The selector covered part of the grid; this does not verify every cube or broader occlusion behavior. A moving preview reached room scenery and waited for confirmed ground, while Rest was ready; this is not an autonomous route-movement check. `/wayfinder preview stop` restored Fox with its saved path unchanged. No warning entries were observed after the reload.

Fifteen native-animation tests cover clip discovery, model-specific locomotion, bounded animation-pack parsing and shared resident packs; read-only checks against installed game packs found the chocobo's run and the moogle's flight clips. These checks do not establish that every model renders correctly in game.

**Shared human animation packs — 2026-10-03:** resident clip discovery now follows the installed `papLoadTable.plt` mappings for the exact body and animation variant, then verifies each clip in its resolved PAP. The installed table has 357 model/variant records; 2,142 mappings cover the six resident pack names used by guides. Gairhard's male Highlander body (`c0301`) retains its own walk pack and inherits idle and run packs from `c0101`, changing verified idle/walk/run timeline IDs from `0/13/22` to `3/13/22`. Momodi's female Lalafell body (`c1201`) inherits those packs from `c1101`, changing `0/0/0` to `3/13/22`. This prevents the direct-only lookup from missing shared locomotion or freezing a previous walking pose at rest. Nine new tests cover rest → walk → rest, exact variants, sparse mappings, malformed tables, missing clip proof and table-read failures; all 15 animation tests and the combined Release build pass. An optional table-read exception falls back to direct packs without repeatedly faulting the cached lookup; resident PAP read failures retain their normal diagnostics. At 10:52–10:53 UTC, Gairhard’s full body and outfit remained visible beside the open selector; walking changed his position, facing and leg poses, then switching to rest returned him to a balanced idle. Momodi (NPC 1001353) also remained fully visible at fitted size 0.917 in the outdoor scene, with distinct rest, walk and run poses and a return to idle. Her Run preview included obstacle turnbacks and a transient Walking state; another frame explicitly showed **Guide ready · Running** with a running pose. These checks cover observed preview states, without establishing an uninterrupted running orbit. These mappings do not add unverified gestures, jump phases or unsupported travel clips.

**Explicit empty NPC equipment — 2026-10-03:** Gairhard's appearance exposed an equipment sentinel that was being treated as model ID 65535. Following the installed game's `SetupEventNpc` behavior, an event-NPC override of zero keeps its inherited `NpcEquip` item, while `0xFFFFFFFF` explicitly clears the complete equipment slot and dyes. The same 32-bit sentinel in a weapon's 64-bit field clears that weapon. Fresh actor-buffer checks passed for 3,540 installed NPC appearances containing 4,536 explicit-empty fields, with Momodi and Fufucha as ordinary-outfit controls; direct and inherited model IDs, variants and weapon types were retained. The matched-API Release build passes. After the combined reload at 10:30 UTC, Gairhard (NPC 1000125) reached **Guide ready · At rest** within four seconds at fitted size 0.639. The selector initially covered most of him. A later 10:52–10:53 UTC check outside the Limsa arch showed his full outfit at the same size, clear of the selector, walking with distinct strides and returning to balanced rest. No current warnings or preview-search exhaustion diagnostics were observed. This verifies this appearance and preview sequence; the full NPC catalog and autonomous route movement are not established by this check.

**Local live check — 2026-10-02:** `Fox.gltf`, with external `.bin` and PNG files, rendered in the preview and played Walk automatically. Textured `CesiumMan.glb` rendered with smooth normals and played the manually selected `Animation 1`. Closing the local preview restored the previously chosen `Fox.glb`; its saved selection path stayed unchanged throughout previewing. Choosing **Use this guide** for `Fox.glb`, stopping the preview and reopening `/wayfinder localmodels` showed the saved entry and active Survey animation. The saved configuration retained its size, facing, grounded placement, animation, in-place and placement preferences.

After the final reload, the saved collection persisted and `CesiumMan.glb` automatically played its single `Animation 1` while the Walk preview was selected. Ground and overhead classic-line rendering were also observed on a flat Limsa street. Stairs, uneven terrain, broader occlusion cases and the full model catalog remain outside these live checks.

The quantized `MeshoptCubeTest.gltf` also rendered its colored cube grid and image labels in the game scene
on 2026-10-02. This exercises `KHR_mesh_quantization` with the fixture's ordinary fallback buffers; it does
not establish support for required meshopt decoding. Stopping that temporary preview restored Fox.

### External asset repositories

In `/wayfinder models`, choose **Search external model repositories…** to search
Poly Haven, ambientCG and Sketchfab together or separately. Searches are explicit,
cancellable metadata requests, with bounded response sizes and per-provider
failures. Results show the author, license, source page and available animation
count. `/wayfinder assets SEARCH` opens the same window and starts that search.
Click a result's title to view its repository image without leaving the selector. Only the selected image
is fetched; it is replaced or released when another is selected, the search changes, or the window closes.
Repository images show the provider's presentation; preview the downloaded model to check its actual
appearance and animation in your world. The animated-model filter means embedded clips, not universal skeleton
retargeting. No file is downloaded, purchased or imported by a search; Sketchfab
downloads may require the provider's sign-in and authorization. Check the source
license and use a supported local GLB, glTF, OBJ or ZIP model pack when importing. **Preview a local file…**
opens your local-model collection. **Open source page** opens the provider's page in your browser;
**Copy link** is available if the browser does not open. Results and the selected image scroll independently;
**Search help**, **Sources & more results**, and **Compatibility details** hold the longer explanations.

**Direct provider check — 2026-10-03:** the default search service returned 20 Poly Haven chair models, 20 Sketchfab fox models and 20 ambientCG wood materials on their first pages. Selected previews loaded as PNG 193×255 for Poly Haven's `ArmChair_01`, JPEG 448×252 for Sketchfab's `Lowpoly fox`, and PNG 256×256 for ambientCG's `Wood096`. Poly Haven's CDN requires an explicit PNG output request because its supplied PNG URL otherwise returns WebP. These host checks verify provider responses and thumbnail loading; they do not establish that the repository-image UI has rendered in game.

**In-game image check — 2026-10-03:** clicking Sketchfab's `Antique Chair` result displayed its repository image inside the open selector. Closing the selector restored the unobstructed game view and retained the selected local Fox guide. This checks the image loader and UI texture; the chair model itself was not downloaded or imported. A second live check rendered the `Fox` image by Fecalis in the separate preview pane while the results list stayed visible.

The search layer uses the independent `XivVisualAssets` namespace and BCL-only
interfaces: model, material, texture, animation and environment metadata can be
reused by future client-side visual tools without Ghostty or game-server access.

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

`/wayfinder face`, or **Follow the quest I'm facing** in the Journey tab, deliberately selects a known quest
step within 25 degrees ahead of your character in the current zone. Facing alignment wins over proximity;
hidden quests, other-zone coordinates, already-reached steps and malformed positions are ignored. Selection
pauses an active multi-stop journey and temporarily follows that quest, like its Show on Map action. Ordinary
turning never changes the choice. The destination still needs a walkable route before a world guide appears.

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

When the target is in another zone, XivWayfinder has no world direction to point in. A short travel cue near the
top edge says **Teleport to *aetheryte*** when one is attuned, or **Travel to *zone***. It does not cover the
character with a window. Aetheryte positions come from the `MapMarker` sheet
(`DataType` 3, `DataKey` the aetheryte), converted from map pixels to world coordinates, else the aetheryte's
first `Level` row. Switch *In another zone, name the nearest aetheryte* off to always get the plain zone cue.
Automatic Teleport is off by default. Enable it in the settings or with
`/wayfinder autoteleport on` to cast an attuned destination when the cue says to
teleport and you have been safely still for two seconds. Normal gil costs apply,
up to the configured per-trip limit (2,000 gil by default). Interrupted/refused
casts wait for `/wayfinder autoteleport retry`; there is also a Retry button.
Disable with `/wayfinder autoteleport off`. Combat, busy states, duties, PvP,
loading and hidden guidance block automatic casts. Enabling automatic Teleport
keeps aetheryte guidance enabled even if the standalone hint checkbox is off.

## What it looks like

*Pointer*: **Bead**, **Glove**, **Both** or **Minion glove** (`/wayfinder bead|glove|both|minion`, default both).

* **The bead** floats 1.5 to 3 yalms ahead of your character (default 2.2) at chest height, in the direction to
  go. It bobs gently, breathes (its size and glow swell and ebb over 2.6 s), and its brightness says how well you
  are headed: dim when you face the right way, brighter as you turn away, full when you face the opposite way.
  It keeps its apparent size readable as the camera zooms. The colour is yours to pick (soft gold by default).
* **The glove** is the game's own white pointing-hand cursor, the classic Final Fantasy finger (see below). While
  the way ahead is on screen it floats near your character, above and just ahead, pointing along the ground
  direction as the camera sees it. When the way lies off screen (behind the camera, or far to one side) it slides
  to the edge of the view where that direction leaves it, fingertip inside the margin, pointing out. It taps
  forward once a second. Pointing left, it is mirrored so the thumb stays on top.
* **The native model guide** (see [below](#the-minion-glove)): the game's own 3D Wind-up Cursor or another
  model chosen in the library, following its own walkable route. **Show the bead too** adds the simple bead.
  Model mode does not add the old flat cursor.
* **Trail** (on by default): fairy glow and falling, fading dust travel ahead of the companion along the
  confirmed walkable route. Native scene depth hides the particles behind game geometry. **Classic line**
  keeps the line/bead controls as an alternative. Neither mode draws a fallback through walls or unknown
  terrain when vnavmesh is unavailable, pending, disabled or unable to return a route. The settings and
  `/wayfinder status` explain why guidance is waiting.
* **Distance**: `42 y` under the bead (or the glove) on hover (default), always, or never.
* **Arrived**: within 4 yalms (the arrival radius) the pointer gives way to a breathing ring on the spot and a
  small bead with *here* above it.

Simple pointers and labels use ImGui's foreground draw list and `IGameGui.WorldToScreen`;
the native companion, imported guide, every trail style and the arrival ring use world-rendering paths. The direction on screen
comes from projecting two points close to your character (one 1.5 yalms ahead along the ground): both are in front
of the camera even when the target is behind it, so the glove never flips the wrong way. The overlay pushes no
ImGui state and catches every exception: a failed frame is logged (once a minute at most) and skipped.

### Trail terrain and checks

Ground lights and ribbons are split at measured collision-triangle boundaries and placed 0.16 yalms above those
faces. Stable samples across their footprint also look for overlapping raised treads, which a lower floor triangle
alone cannot reveal. Round glow shapes, color gradients and pulses are retained. Overhead marks keep their entire
glow above the configured altitude; all trail styles and the arrival ring use native scene-depth occlusion.

Newly discovered overlapping faces invalidate affected cached lower-floor geometry immediately. A taller surface
covering only part of a mark makes the whole dot or ribbon wait, including its outer glow. The check spans all of
the primitive's triangles, so a tread edge aligned with the spark's radial mesh cannot leave half its glow buried.
A fully covering higher layer can be a separate deck; the trail preserves its intended lower route in that case.

Refreshed downward rays now retire cached support faces contradicted by the confirmed clear part of that ray,
before their normal expiry. A miss clears evidence only along the tested segment; a valid lower hit clears only
above that hit, with a 0.001-yalm endpoint margin. Unrelated faces and probe expiry times are preserved. Retiring
a face also clears retained projections and rebuilds the face index. Discovery still depends on the bounded
sampling cadence, so this does not promise an immediate response to every terrain change.

Sampling has finite resolution: footprint witnesses are spaced 0.125 yalms apart, collision evidence expires after
0.6 seconds, and upper-obstruction discovery searches about 1.5 yalms above the reference floor. Query and
mesh-preparation budgets prioritize the nearby route. A complete mark waits if its coverage is unavailable.
Very small props between samples, unobserved moving collision, ambiguous intersecting floors and surfaces outside
the local height search remain limitations. A broad raised prop can also be indistinguishable from a separate deck;
scene depth remains the final visual occlusion check.

Host tests use real ray/triangle intersections for tessellated slopes, curved ridges, separate and overlapping stair
treads, stacked decks, voids and large world coordinates. On 2026-10-03, all 67 focused trail tests passed, including
0.75- and 1.25-yalm partial tall steps, newly observed faces before cache expiry, and the exact 60-triangle spark
across a tread edge aligned with its mesh. The 20-dot, 127-segment coverage fixtures, both in the open and under a
full upper deck, first completed at frame 15. Every frame from 15 through 179 retained 240/240 dot footprint
triangles and 254/254 line triangles at 60 simulated frames/second, including evidence refreshes. The footprint
tests also cover the native spark's small radii and large world coordinates. The new cache and tall-surface rules
await their own live terrain check. These geometry tests do not establish live stairs or moving-terrain behavior:
live checks covered ground and overhead classic lines on flat Limsa terrain, including the new footprint
projection on 2026-10-02. Ground fairy sparks were also observed on stone beside the rug. The bounded live
diagnostics confirmed that all 40–52 requested spark footprints were prepared without projection failures;
only three centers were inside the current camera view. This does not prove every submitted particle was
visible after depth testing. Stairs and moving terrain still need their live terrain pass.

The seven new cache tests cover removed, raised and lowered platforms plus narrow-floor, off-ray, nonfinite and
out-of-range controls. The three changing-platform cases reproduced stale support at 0.31 seconds, before the
0.6-second expiry, and pass after the correction. The build passed with no warnings or errors and loaded live
on 2026-10-03 at 08:33 UTC. At 08:34 UTC, a follow-up slope check showed the ground ribbon climbing the same
bevel, with the Fox still clear beside the squatting player. Its 39-sample, 4.75-yalm path again prepared 14
segments, two dots and 1,200 vertices with no projection failures or budget stops. This verifies loading and
the gentle-slope regression; changing-platform behavior remains host-tested only.

Development builds provide `/wayfinder terraincheck curb|slope` to find nearby visible collision and preview a short, independently queried trail across it. The check limits collision work per frame and expires after 75 seconds; `/wayfinder terraincheck off`, player movement, hiding guidance or changing zones also stops it. It preserves the destination, journey, saved settings and player control. This is a temporary verification tool, not a new travel target.

**Gentle-slope live check — 2026-10-03:** the slope probe confirmed a nearby beveled curb rising 0.2 yalms, with an upward normal component of 0.8945091. Its three-corner walking path covered 4.75 yalms with 39 collision samples. Ground fairy dots were visible at the curb top, and the glowing ground ribbon visibly climbed the bevel from floor height 16 to 16.2. Ground-line diagnostics prepared 14 segments and both requested dots, submitting 1,200 vertices with no projection failures or budget stops. Both the overhead line and overhead fairy dots were also visible floating with the height setting at 2.5 yalms. The temporary check expired after 75 seconds and ordinary guidance returned. This verifies the sampled gentle slope; vertical stairs and moving terrain still need live checks.

**Thin-curb endpoint check — 2026-10-03, 08:20 UTC:** after the endpoint correction, the curb probe confirmed six candidate endpoints on the narrow curb cap at height 16.2 instead of overshooting it, then reached walking-path validation. Each candidate was rejected because its path did not cross a visible curb meeting the strict step criteria, and the check stopped after six attempts. The nearby curb is beveled. This exercises the corrected endpoint selection and bounded rejection; it does not establish vertical-step or stair rendering.

**Expanded curb search — 2026-10-03, 09:23 UTC:** the enhanced probe completed all 1,220 grid cells from the player's new position `(-250.7845, 16, 46.648163)` in Limsa territory 129. Of those cells, 362 were inside the current view and 347 yielded accepted visible floor hits. The search covers a 0.5-yalm grid between 1.75 and 10 yalms from its starting point, with accepted floors within 1.25 yalms of the player's height. This was a different position and camera view from the earlier bevel checks.

No neighboring floor pair met the curb candidate conditions: both upward normal components at least 0.92 and a height difference of 0.08–0.5 yalms. Consequently `examinedPairs`, bevel rejections, riser misses, invalid risers, unique/duplicate steep risers, selected candidates and route attempts were all zero. The scan ended normally with no qualifying visible curb; it never reached horizontal riser testing or route validation. This records the finite sampled view, not the absence of sharp steps elsewhere or between grid samples. Sharp stairs remain unverified live. The probe was stopped and the fairy trail restored to overhead afterward.

**Quarter-yalm refinement — 2026-10-03, 10:09 UTC:** a curb search now follows an exhausted 1,220-cell coarse pass with 3,660 additional samples at 0.25-yalm spacing. Three host tests cover the bounded grid and thin raised caps aligned along either horizontal axis; the original half-yalm grid missed those caps. The same four-ray, roughly one-millisecond per-frame budget and 75-second expiry remain. A live interior scan from `(-329.23407, 12.899763, 10.80401)` in territory 129 completed all 4,880 distinct cells in about 16 seconds, with 2,002 visible cells and 1,038 accepted floor hits. No neighboring pair met the unchanged curb criteria, so no riser or route was tested. The scan ended normally with the saved overhead fairy trail unchanged. This verifies bounded refinement in game; sharp steps and stairs remain unverified live.


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

### The minion glove

`/wayfinder minion`, or *Minion glove (Wind-up Cursor model)* in the settings, selects the native model style.
The Wind-up Cursor, moogle and black chocobo have been observed in game; the library also offers other native models.

| | |
| --- | --- |
| Which model | The selected `ModelChara` row, with `ENpcBase` appearance data for named event NPCs or `BNpcBase` appearance data for named hunt creatures. The initial Wind-up Cursor is found by its English `Companion` name at run time. Read-only from game version 2026.09.15.0000.0000: Companion 51, ModelChara 469 (Type 3, Model 8044, Base 1, Variant 1: `chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl`). Game model assets are read from your installation and are not shipped with the plugin. |
| How it is shown | A local BattleChara from `ClientObjectManager.CreateBattleCharacter()` (the way Brio and Ktisis make theirs), `ModelContainer.ModelCharaId` set to that row, the `IsTargetable` flag cleared, placed with `SetPosition` and `SetRotation`, and `EnableDraw()` one frame later. Each frame it is moved and turned; with *Tilt it up and down slopes* the draw object's rotation (`DrawObject->Object.Rotation`) is written with a pitch, and its scale with the size setting. |
| Where | Along its own confirmed walkable route, at the configured lead distance. When you stop, it can use a separate queried path to wait on your preferred side with measured body clearance. Grounded models use collision placement; floating models use their configured height. It eases its movement and can return along a queried connector when you move away from the route. |
| Which way | Along its own route segment, with look-ahead limited at the next corner. Turning is gradual; occasional glances follow the guiding light. Missing routes never authorize a direct turn or move through a wall. *Turn the model* corrects its authored forward axis. |
| When it goes | Deleted with `DeleteObjectByIndex` at once when you leave the zone, log out, enter a cutscene or group pose, hide the UI, switch the pointer off or away from the minion, when it is hidden in combat or a duty, and when the plugin unloads; after a second when you arrive or the target is in another zone. If the game removed it first, XivWayfinder only forgets it (it checks the slot still holds its model before deleting). Three failed creations and it gives up until you choose the style again. |

It is not a companion summon: no action is used, nothing is sent to the server, and no other player can see it.
Model rendering has been observed for the three pinned choices. Animation-pack checks establish which clips
are present, but the full catalog's appearance, gait, scale, facing correction and terrain behavior still need
model-by-model live checks. The rotation and size controls remain available for differences between models.

## When it steps aside

Always hidden during cutscenes, zone changes, group pose, when you hide the game UI, and
with no character. Hidden in combat and in duties by default; both are settings.

## Walkable paths with vnavmesh

With [vnavmesh](https://github.com/awgil/ffxiv_navmesh) installed and loaded, and *Follow vnavmesh's walkable
path* on (the default), XivWayfinder asks vnavmesh for the path to the target and points at its next corner instead
of straight at the target; the trail, when on, lays its beads along the path (see *Trail* above). A target without a height (the flag, typed
coordinates) is placed on the navmesh floor under it first. A new path is asked for only when the target moves,
you stray more than 6 yalms from the path, or there is none yet, and never more often than every two seconds.
Until a path arrives, or when none is found, world guidance waits instead of pointing in a straight line.
The settings window shows what vnavmesh is doing. Re-paths use a separate companion-only connector and an
exact route occurrence, preventing self-crossings from snapping the guide onto another leg.

**No automatic walking.** XivWayfinder uses vnavmesh's queries (`Nav.IsReady`, `Nav.PathfindCancelable`,
`Query.Mesh.NearestPoint`) and never its movement gates (`Path.MoveTo`, `SimpleMove.*`). It never
presses keys, walks or turns your character. Automatic Teleport is a separate, optional game action.

## Commands

| Command | What it does |
| --- | --- |
| `/wayfinder` | Opens the settings. |
| `/wayfinder X Y [zone]` | Points at map coordinates, in the zone you are in or the one named. Paste them as the game writes them: `11.2 14.5`, `(11.2, 14.5)`, `X: 11.2 Y: 14.5 Z: 0.3`, or a chat map link's text. |
| `/wayfinder clear` | Forgets that target. |
| `/wayfinder test [yalms]` | A target 20 yalms (3 to 200) straight ahead of your character. |
| `/wayfinder terraincheck curb` · `terraincheck slope` · `terraincheck off` | Development builds only: previews a temporary nearby terrain trail for up to 75 seconds, or stops it. Preserves your target, journey and player control. |
| `/wayfinder save NAME` | Saves the current non-housing world position under a name (or updates that name). |
| `/wayfinder go NAME` | Points at a saved place; names ignore case, accents and punctuation. |
| `/wayfinder forget NAME` · `/wayfinder places` | Removes one saved place, or lists the book. |
| `/wayfinder auto` · `target` · `flag` · `quest` | What to follow. |
| `/wayfinder face` | Selects a known local quest step ahead of your character; does not move the character or automatically switch on ordinary turns. |
| `/wayfinder models [SEARCH]` | Opens the native model library with temporary world previews and an optional search. |
| `/wayfinder assets [SEARCH]` | Opens external model repositories; supplying search text starts a search. Click a result to preview its repository image. |
| `/wayfinder localmodels` | Opens your local-model collection and previews the current imported guide, if selected. |
| `/wayfinder preview PATH` · `preview stop` | Temporarily previews a local OBJ, GLB or glTF model, opens a ZIP pack for explicit model selection, or stops the preview and restores the chosen guide. |
| `/wayfinder import PATH` | Directly selects a local OBJ, GLB or glTF model; ZIP packs open the preview selector for explicit model selection. |
| `/wayfinder bead` · `glove` · `both` · `minion` | What the pointer looks like; `minion` (or `cursor`) is the Wind-up Cursor model. |
| `/wayfinder on` · `off` · `toggle` | Shows or hides it. |
| `/wayfinder status` | One line: destination, distance, confirmed path or reason guidance is waiting, and visibility. |

`/xivwayfinder` is an alias for `/wayfinder`, with every argument the same.

Zone names match case- and accent-insensitively: exactly first, then the start of a name, then anywhere in it;
a territory id works too. Among equal matches the lowest territory id wins.

Named places store territory and world position locally in the plugin configuration. Residential districts and
interiors are refused for now because the same territory and coordinates can mean a different ward, plot or room.

## Proving it in game

Native and imported rendering and selector checks are recorded above. The following is a regression
checklist, not a claim that every item has passed. Local preview rendering, animation and restoration have
been observed, along with saved-entry commit/reopen and ground/overhead classic lines on flat terrain.
Broader terrain and model coverage remain pending. With the plugin loaded as a dev plugin:

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
9. Choose Pointer in `/wayfinder models`, then `/wayfinder test`. Expect the white Wind-up Cursor glove model
   ahead on its walkable route, pointing along that route, and the settings to say the model was found (Companion 51, ModelChara 469 on
   2026.09.15). Check: does its finger point the way (else note the *Turn the model* value that fixes it); is its
   size sensible; is it untargetable (click it); does it go away on `/wayfinder off`, a zone change, a cutscene,
   group pose and unloading the plugin (`/xlplugins`), with no leftover model; does a friend next to you see
   nothing. Walk up and down a slope with vnavmesh: does it tilt, or stay level.
10. With vnavmesh, set a flag behind a building: the trail's beads and line should bend around it along the
    walkable way and stay on the ground as you walk past them. Unload vnavmesh: the trail disappears,
    and `/wayfinder status` explains that it is waiting for a walkable route. No line may point through the building.
11. Keep a destination active, then `/wayfinder localmodels`. Browse a supported animated model, try rest,
    walk/run and a named clip, and check its textures and occlusion. **Use this guide** must remain disabled
    until that candidate renders. Close without choosing: the previous guide and destination should remain.
    Repeat, choose the candidate, then close: the new guide should follow the same destination. Reopen the
    collection to check saved settings, and use `/wayfinder preview stop` to cancel a further preview.

Please report what you see, with `/wayfinder status` and `/xllog` lines for anything that looks wrong.
