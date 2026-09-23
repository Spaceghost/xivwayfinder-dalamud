#!/usr/bin/env bash
# Build Wayfinder (Release) and print the path to add under
#   Dalamud Settings -> Experimental -> Dev Plugin Locations
# This script never edits Dalamud's configuration and never copies into ~/.xlcore;
# it stages the build in ../wayfinder-build/devplugin (WAYFINDER_STAGE overrides).
#
# Environment:
#   DOTNET            dotnet executable (default: ~/.dotnet/dotnet, then dotnet on PATH)
#   WAYFINDER_ARTIFACTS  build output root (default: ../wayfinder-build/artifacts next to the repo)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$repo/src/Wayfinder.Plugin/Wayfinder.Plugin.csproj"

dotnet="${DOTNET:-}"
if [[ -z "$dotnet" ]]; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet="$HOME/.dotnet/dotnet"
  elif command -v dotnet >/dev/null 2>&1; then
    dotnet="$(command -v dotnet)"
  else
    echo "install-dev: dotnet SDK not found (set DOTNET=/path/to/dotnet)" >&2
    exit 1
  fi
fi

artifacts="${WAYFINDER_ARTIFACTS:-$(cd "$repo/.." && pwd -P)/wayfinder-build/artifacts}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 WAYFINDER_ARTIFACTS="$artifacts"

echo "install-dev: building $project (Release) into $artifacts" >&2
"$dotnet" build "$project" -c Release -nologo -v quiet >&2

dll="$artifacts/bin/Wayfinder.Plugin/release/Wayfinder.dll"
if [[ ! -f "$dll" ]]; then
  echo "install-dev: build finished but $dll is missing" >&2
  exit 1
fi

# Stage into a fixed directory so Dalamud (auto-reload) only ever sees complete
# builds, never a half-written bin/ from an in-progress compile.
stage="${WAYFINDER_STAGE:-$(cd "$repo/.." && pwd -P)/wayfinder-build/devplugin}"
mkdir -p "$stage"
src_dir="$(dirname "$dll")"
for f in Wayfinder.json Wayfinder.Core.dll Wayfinder.Core.pdb Wayfinder.deps.json Wayfinder.pdb Wayfinder.dll; do
  [[ -f "$src_dir/$f" ]] || continue
  cp "$src_dir/$f" "$stage/.$f.tmp" && mv -f "$stage/.$f.tmp" "$stage/$f"
done
dll="$(readlink -f "$stage/Wayfinder.dll")"

# Wine maps the host root to drive Z:, so /a/b/c becomes Z:\a\b\c.
windows_path="Z:${dll//\//\\}"

cat >&2 <<EOF

Built: $dll

In game, once: /xlsettings -> Experimental -> Dev Plugin Locations, add the path below,
"Save and close". Dev plugins are added disabled: in /xlplugins -> Dev Tools ->
Installed Dev Plugins, enable "Wayfinder" and tick "Start on boot".
After rebuilding, reload Wayfinder from /xlplugins (or tick its "Automatic reloading").
EOF
printf '%s\n' "$windows_path"
