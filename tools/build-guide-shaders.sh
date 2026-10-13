#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
shader_source="$repo_root/src/XivWayfinder.Plugin/Shaders"
shader_tmp=$(mktemp -d "$repo_root/.guide-shader-build.XXXXXX")
cleanup() {
  rm -f -- "$shader_tmp/GuideVertex.dxbc" "$shader_tmp/GuidePixel.dxbc"
  rmdir -- "$shader_tmp"
}
trap cleanup EXIT

# Match XivRug's pinned compiler, producing D3D11 SM5 bytecode rather than DXIL.
podman run --rm \
  -v "$shader_source:/source:ro,Z" \
  -v "$shader_tmp:/output:Z" \
  registry.fedoraproject.org/fedora:44 sh -ec '
    dnf -y -q install vkd3d-compiler-1.17-2.fc44 >/dev/null
    vkd3d-compiler -x hlsl -b dxbc-tpf -p vs_5_0 \
      -o /output/GuideVertex.dxbc /source/GuideVertex.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p ps_5_0 \
      -o /output/GuidePixel.dxbc /source/GuidePixel.hlsl
  '

# Validate both outputs before replacing either shipped binary.
for shader_name in GuideVertex GuidePixel; do
  test "$(od -An -tx1 -N4 "$shader_tmp/$shader_name.dxbc" | tr -d ' \n')" = 44584243
done
for shader_name in GuideVertex GuidePixel; do
  mv -- "$shader_tmp/$shader_name.dxbc" "$shader_source/$shader_name.dxbc"
done
(
  cd -- "$shader_source"
  sha256sum GuideVertex.hlsl GuidePixel.hlsl GuideVertex.dxbc GuidePixel.dxbc > GuideShaders.sha256
  cat GuideShaders.sha256
)
