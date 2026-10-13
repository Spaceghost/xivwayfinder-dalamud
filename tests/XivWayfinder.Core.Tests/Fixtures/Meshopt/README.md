# Meshopt import fixtures

## Khronos MeshoptCubeTest

Source: [Khronos glTF Sample Assets](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/edc7c9e67c639d230715049ee31f9a96a6babbbe/Models/MeshoptCubeTest), revision `edc7c9e67c639d230715049ee31f9a96a6babbbe`.
Author: Arseny Kapoulkine. License: [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/legalcode), as recorded in the upstream model README.

- `MeshoptCubeTest.gltf` and `MeshoptCubeTest.bin` are the unchanged files from upstream `glTF-Meshopt/`.
- That upstream variant declares `KHR_meshopt_compression` as required and omits the fallback buffer's URI. These changes are already present upstream; no local geometry or material edits were made. The importer must decode compressed views to load this variant.
- `MeshoptCubeTestFallback.gltf` is the unchanged contents of upstream `glTF/MeshoptCubeTest.gltf`, renamed locally to coexist with the required variant. `MeshoptCubeTestFallback.bin` is the upstream uncompressed fallback. Comparisons with decoded data allow cyclic triangle corner rotation and filter rounding.
- The ten PNG label images are unchanged upstream assets. The required and fallback variants share them and `MeshoptCubeTest.bin`.

`provenance.json` records the exact source revision and SHA256 of each original file. Tests create a temporary uncompressed reference by removing meshopt extension objects from the optional variant's buffers and views; binary geometry, animation, materials and images remain unchanged. Another test replaces the optional fallback URI with a missing filename to verify that supported compression does not read it.

The required model's geometry, normals, colors and 31 animation samples are compared with that uncompressed reference. Triangle comparisons allow cyclic corner rotation while preserving winding. A separate live check on 2026-10-03 rendered the required KHR variant's colored cube grid and textured labels at size 1.5; selecting its authored `RotateCubes` clip changed visible cube orientations between frames. The selector covered part of the grid, so this does not verify every cube or broader occlusion behavior. Stopping the preview restored the saved Fox.

## Upstream decoder vectors

`upstream-vectors.json` contains 17 vectors from [meshoptimizer's decoder tests](https://github.com/zeux/meshoptimizer/blob/4c203430ca565cb59a468a91922c76c208169536/js/meshopt_decoder.test.js), revision `4c203430ca565cb59a468a91922c76c208169536`, under the MIT license. Encoded bytes are unchanged; expected uint16/uint32 arrays are serialized as little-endian bytes. Legacy triangle version 0 vectors are excluded because neither glTF meshopt extension specifies that version.

The full meshoptimizer notice is retained at `src/XivWayfinder.Core/ThirdParty/meshoptimizer.LICENSE.md` and embedded in the distributed Core assembly.
