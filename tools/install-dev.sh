#!/usr/bin/env bash
# Build XivWayfinder (Release) and print the path to add under
#   Dalamud Settings -> Experimental -> Dev Plugin Locations
# This script never edits Dalamud's configuration and never copies into ~/.xlcore;
# it stages the build in ../xiv-wayfinder-build/devplugin (XIVWAYFINDER_STAGE overrides).
#
# Environment:
#   DOTNET            dotnet executable (default: ~/.dotnet/dotnet, then dotnet on PATH)
#   XIVWAYFINDER_ARTIFACTS  build output root (default: ../xiv-wayfinder-build/artifacts next to the repo)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$repo/src/XivWayfinder.Plugin/XivWayfinder.Plugin.csproj"

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

artifacts="${XIVWAYFINDER_ARTIFACTS:-$(cd "$repo/.." && pwd -P)/xiv-wayfinder-build/artifacts}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 XIVWAYFINDER_ARTIFACTS="$artifacts"

echo "install-dev: building $project (Release) into $artifacts" >&2
"$dotnet" build "$project" -c Release -nologo -v quiet >&2

dll="$artifacts/bin/XivWayfinder.Plugin/release/XivWayfinder.dll"
if [[ ! -f "$dll" ]]; then
  echo "install-dev: build finished but $dll is missing" >&2
  exit 1
fi

# Stage into a fixed directory so Dalamud (auto-reload) only ever sees complete
# builds, never a half-written bin/ from an in-progress compile.
stage="${XIVWAYFINDER_STAGE:-$(cd "$repo/.." && pwd -P)/xiv-wayfinder-build/devplugin}"
mkdir -p "$stage"
src_dir="$(dirname "$dll")"
for f in XivWayfinder.json XivWayfinder.Core.dll XivWayfinder.Core.pdb XivWayfinder.deps.json XivWayfinder.pdb XivWayfinder.dll; do
  [[ -f "$src_dir/$f" ]] || continue
  cp "$src_dir/$f" "$stage/.$f.tmp" && mv -f "$stage/.$f.tmp" "$stage/$f"
done
dll="$(readlink -f "$stage/XivWayfinder.dll")"

# Wine maps the host root to drive Z:, so /a/b/c becomes Z:\a\b\c.
windows_path="Z:${dll//\//\\}"

cat >&2 <<EOF

Built: $dll

In game, once: /xlsettings -> Experimental -> Dev Plugin Locations, add the path below,
"Save and close". Dev plugins are added disabled: in /xlplugins -> Dev Tools ->
Installed Dev Plugins, enable "XivWayfinder" and tick "Start on boot".
After rebuilding, reload XivWayfinder from /xlplugins (or tick its "Automatic reloading").
EOF
printf '%s\n' "$windows_path"
