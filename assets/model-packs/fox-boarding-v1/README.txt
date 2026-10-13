FOX GROUND-BOARDING v1 — OPTIONAL, experimental MODEL PACK

This is an opt-in model pack, not a Dalamud plugin installation or configuration.
It includes the untouched original Fox and a separate animation derivative.
Opening this ZIP never replaces the original file or auto-selects a guide.

EXPLICIT OPT-IN
1. In a compatible XivWayfinder build, open /wayfinder localmodels and choose
   this ZIP, or use /wayfinder preview PATH-TO-ZIP.
2. Explicitly choose Fox.boarding-prototype.glb in the model list. Previewing
   it leaves your currently chosen guide intact. Closing the window restores it.
3. For the experimental ground-hop behavior, preview grounded, animated,
   in-place, automatic animation (not a pinned clip), at scale 0.1 through 1.5.
4. Only choose Use this guide if you want to save this derivative as your guide.
   You can instead choose Original/Fox.glb to keep the original animations.
   Existing saved models remain separate choices. No cast is needed to preview.

WHAT THIS ADDS / DOES NOT CLAIM
New rig-specific procedural clips cover crouch/leap/settle, held, return/land
and recovery from a captured pose; original Survey/Walk/Run remain intact.
A compatible runtime can synchronize a small ground-rendezvous hop to an
actually observed player Teleport cast. This is not arms carrying and is not
attachment to a measured teleport VFX. It does not cast Teleport, move the player,
spend gil, download other assets or change your chosen quest.

The runtime still requires a complete walkable approach, fresh supported floor,
avatar/body clearance and actual GPU submission. Missing evidence declines or
holds the visual. Host tests and synthetic-scene checks are not live verification
of every terrain/collision/rendering case. Native scene latency and visual
boarding behavior remain experimental; do not describe them as fully live-tested.

PRESERVATION AND CREDIT
Original/Fox.glb is byte-for-byte unchanged. The derivative preserves original
mesh, skin, textures, materials and gait clips. Keep ATTRIBUTION.txt and
LICENSES.txt with both files; those notices distinguish the CC0 base mesh from
the CC-BY 4.0 rig, animations, conversion and new animation work.

REPRODUCIBLE PACK BUILD (from the source repository, output directory exists)
python3 tools/pack_model.py --manifest assets/model-packs/fox-boarding-v1/pack.json --output /chosen/new/FoxBoarding-v1.zip
The output must not already exist. Every source file is SHA256-pinned in pack.json.
The separate ZIP uses sorted entries, fixed 1980 timestamps, fixed regular-file
modes and ZIP_STORED; it is not included automatically in the plugin package.
