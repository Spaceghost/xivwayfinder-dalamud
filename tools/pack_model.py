#!/usr/bin/env python3
"""Build an optional local-model ZIP from a SHA256-pinned input manifest.

No downloads, installation, configuration changes or model selection. The
manifest directory is the source root. Output must not exist, even as a link.
ZIP_STORED avoids compression-library version differences; metadata is fixed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import unicodedata
import zipfile

MAXIMUM_BYTES = 32 * 1024 * 1024
MAXIMUM_ENTRIES = 512
MAXIMUM_MANIFEST_BYTES = 1024 * 1024
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
ZIP_MODE = (stat.S_IFREG | 0o644) << 16


class PackError(ValueError):
    """An invalid input or unsupported safe publication operation."""


def relative_name(value: object) -> str:
    """The model ZIP importer's portable, non-traversing name subset."""
    if not isinstance(value, str) or not 1 <= len(value) <= 512:
        raise PackError("File names must be nonempty relative paths of at most 512 characters.")
    for part in value.split("/"):
        if not 1 <= len(part) <= 255 or part in (".", "..") or part.endswith((".", " ")):
            raise PackError("File names cannot contain empty, parent, dot or trailing-dot/space components.")
        if any(unicodedata.category(c) == "Cc" or c in '<>:\\"|?*' for c in part):
            raise PackError("File names must use portable path characters and forward slashes.")
        stem = part.split(".")[0].upper()
        if stem in {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"} or (
            len(stem) == 4 and stem[:3] in ("COM", "LPT") and stem[3] in "123456789¹²³"
        ):
            raise PackError("File names cannot use reserved Windows device names.")
    return value


def plain_directory(directory: Path) -> None:
    # Inspect rather than resolve links away: a linked source/output directory
    # must not silently redirect a pack beyond its declared local root.
    for component in reversed((directory, *directory.parents)):
        info = component.lstat()
        junction = getattr(component, "is_junction", lambda: False)()
        if not stat.S_ISDIR(info.st_mode) or junction:
            raise PackError(f"Directory is missing, linked or not ordinary: {component}")


def regular_bytes(path: Path, limit: int) -> bytes:
    plain_directory(path.parent)
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_size > limit:
        raise PackError(f"Input is not an ordinary file or exceeds the size limit: {path.name}")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as source:
        opened = os.fstat(source.fileno())
        if not stat.S_ISREG(opened.st_mode) or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino):
            raise PackError(f"Input changed while opening: {path.name}")
        content = source.read(limit + 1)
    if len(content) > limit:
        raise PackError("Unpacked model-pack inputs must total at most 32 MiB.")
    return content


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    value = {}
    for key, item in pairs:
        if key in value:
            raise PackError("The input manifest contains duplicate JSON keys.")
        value[key] = item
    return value


def payload(manifest_path: Path) -> list[tuple[str, bytes]]:
    try:
        manifest = json.loads(regular_bytes(manifest_path, MAXIMUM_MANIFEST_BYTES).decode("utf-8"), object_pairs_hook=unique_object)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise PackError("The input manifest is not valid UTF-8 JSON.") from error
    if not isinstance(manifest, dict) or set(manifest) != {"format", "files"} or type(manifest["format"]) is not int or manifest["format"] != 1:
        raise PackError("The input manifest must have format 1 and a files array.")
    files = manifest["files"]
    if not isinstance(files, list) or not 1 <= len(files) <= MAXIMUM_ENTRIES:
        raise PackError("A model pack must contain between 1 and 512 file entries.")
    names: set[str] = set()
    sources: set[str] = set()
    entries = []
    for item in files:
        if not isinstance(item, dict) or set(item) != {"source", "path", "sha256"}:
            raise PackError("Each input needs exactly source, path and sha256.")
        source, name = relative_name(item["source"]), relative_name(item["path"])
        digest = item["sha256"]
        if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            raise PackError("Every input must pin a lowercase SHA256 digest.")
        if name.casefold() in names or source.casefold() in sources:
            raise PackError("Duplicate or case-colliding input/output file names are not allowed.")
        names.add(name.casefold()); sources.add(source.casefold())
        entries.append((source, name, digest))
    for name in names:
        pieces = name.split("/")
        if any("/".join(pieces[:i]) in names for i in range(1, len(pieces))):
            raise PackError("A ZIP path cannot be both a file and a parent folder.")
    if not any(Path(name).suffix.lower() in (".glb", ".gltf", ".obj") for _, name, _ in entries):
        raise PackError("An optional model pack needs at least one GLB, glTF or OBJ entry.")
    result = []
    total = 0
    for source, name, digest in sorted(entries, key=lambda e: e[1]):
        # Freeze and hash the same bounded bytes later written into the ZIP.
        content = regular_bytes(manifest_path.parent / source, MAXIMUM_BYTES - total)
        if hashlib.sha256(content).hexdigest() != digest:
            raise PackError(f"Input SHA256 does not match its pin: {source}")
        total += len(content)
        result.append((name, content))
    return result


def build_pack(manifest_path: Path | str, output_path: Path | str) -> tuple[int, int, str]:
    manifest_path = Path(manifest_path).absolute()
    output_path = Path(output_path).absolute()
    if os.path.lexists(output_path):
        raise PackError("Output already exists; choose a new path. Nothing was overwritten.")
    plain_directory(output_path.parent)
    files = payload(manifest_path)  # All validation precedes creating even a staging file.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(prefix="." + output_path.name + ".", suffix=".tmp", dir=output_path.parent, delete=False) as staged:
            temporary = Path(staged.name)
            with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
                archive.comment = b""
                for name, content in files:
                    entry = zipfile.ZipInfo(name, ZIP_TIMESTAMP)
                    entry.create_system = 3  # Unix regular 0644, independent of the producer OS/umask.
                    entry.create_version = entry.extract_version = 20
                    entry.compress_type = zipfile.ZIP_STORED
                    entry.external_attr = ZIP_MODE
                    entry.internal_attr = 0
                    entry.extra = entry.comment = b""
                    archive.writestr(entry, content)
            # The existing selector also bounds the ZIP file itself, not only
            # its expansion. Stored headers can push a near-limit payload over.
            if staged.tell() > MAXIMUM_BYTES:
                raise PackError("The ZIP including metadata must fit within 32 MiB.")
            staged.flush()
            os.fsync(staged.fileno())
        digest = hashlib.sha256()
        with temporary.open("rb") as source:
            for chunk in iter(lambda: source.read(65536), b""):
                digest.update(chunk)
        # Atomic no-clobber publication. A racing creator wins, including a
        # symlink; unsupported hard-link filesystems fail without replacing it.
        os.link(temporary, output_path)
        return len(files), sum(len(content) for _, content in files), digest.hexdigest()
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        entries, expanded, digest = build_pack(args.manifest, args.output)
    except (PackError, OSError) as error:
        parser.exit(1, f"Model pack refused: {error}\n")
    print(f"Wrote {args.output}: {entries} entries, {expanded} unpacked bytes, SHA256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
