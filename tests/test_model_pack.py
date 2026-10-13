"""Standalone packaging checks; no game, network or native compiler needed."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("pack_model", ROOT / "tools/pack_model.py")
PACK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACK)


class ModelPackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="wayfinder-model-pack-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.manifest = self.root / "pack.json"
        self.output = self.root / "pack.zip"
        self.entries = []
        self.add("model.glb", b"synthetic model bytes; not a real GLB")
        self.add("notes.txt", b"attribution fixture\n")

    def add(self, name, content, archive_name=None):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        self.entries.append({"source": name, "path": archive_name or name,
                             "sha256": hashlib.sha256(content).hexdigest()})

    def write_manifest(self):
        self.manifest.write_text(json.dumps({"format": 1, "files": self.entries}), encoding="utf-8")

    def build(self, output=None):
        self.write_manifest()
        return PACK.build_pack(self.manifest, output or self.output)

    def rejected(self):
        with self.assertRaises((PACK.PackError, OSError)):
            self.build()
        self.assertFalse(os.path.lexists(self.output))
        self.assertEqual([], list(self.root.glob(".*.tmp")))

    def test_deterministic_order_metadata_bytes_and_no_compression(self):
        self.entries.reverse()
        self.add("original/source.glb", b"unchanged original", "Original/Fox.glb")
        self.build()
        first = self.output.read_bytes()
        self.entries.reverse()
        for item in self.entries:
            os.utime(self.root / item["source"], (1700000000, 1700000000))
            os.chmod(self.root / item["source"], 0o600)
        count, size, digest = self.build(self.root / "second.zip")
        self.assertEqual(first, (self.root / "second.zip").read_bytes())
        self.assertEqual(hashlib.sha256(first).hexdigest(), digest)
        self.assertEqual(3, count)
        self.assertEqual(sum((self.root / e["source"]).stat().st_size for e in self.entries), size)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(sorted(e["path"] for e in self.entries), archive.namelist())
            self.assertEqual(b"", archive.comment)
            for entry in archive.infolist():
                self.assertEqual(PACK.ZIP_TIMESTAMP, entry.date_time)
                self.assertEqual(zipfile.ZIP_STORED, entry.compress_type)
                self.assertEqual(entry.file_size, entry.compress_size)
                self.assertEqual((stat.S_IFREG | 0o644) << 16, entry.external_attr)
                self.assertEqual(3, entry.create_system)
                self.assertEqual((20, 20), (entry.create_version, entry.extract_version))
                self.assertEqual(b"", entry.extra)
                self.assertEqual(b"", entry.comment)
                self.assertEqual(0, entry.internal_attr)
                source = next(e for e in self.entries if e["path"] == entry.filename)
                self.assertEqual((self.root / source["source"]).read_bytes(), archive.read(entry))

    def test_traversal_absolute_windows_and_unportable_paths_are_rejected(self):
        for field in ("source", "path"):
            old = self.entries[0][field]
            for value in ("../escape.glb", "/absolute.glb", "C:/absolute.glb", "a//b.glb", "a/./b.glb",
                          "a\\b.glb", "x/../b.glb", "CON.glb", "lpt1.txt", "foo./x.glb", "foo /x.glb",
                          "bad\x00.glb", "bad\x85.glb", "", "x" * 513, "../"):
                with self.subTest(field=field, value=value):
                    self.entries[0][field] = value
                    self.rejected()
            self.entries[0][field] = old

    def test_duplicate_case_colliding_sources_and_outputs_are_rejected(self):
        first, second = self.entries
        for field in ("source", "path"):
            for value in (first[field], first[field].upper()):
                with self.subTest(field=field, value=value):
                    old = second[field]; second[field] = value
                    self.rejected(); second[field] = old

    def test_file_cannot_be_ancestor_of_another_archive_entry(self):
        self.entries[1]["path"] = "model.glb/notice.txt"
        self.rejected()

    def test_missing_and_hash_mismatched_inputs_leave_no_archive(self):
        original = (self.root / "model.glb").read_bytes()
        (self.root / "model.glb").write_bytes(b"modified")
        self.rejected()
        (self.root / "model.glb").write_bytes(original)
        (self.root / "notes.txt").unlink()
        self.rejected()

    def test_empty_and_invalid_manifests_and_digest_are_rejected(self):
        self.entries[0]["sha256"] = "ABC" * 22
        self.rejected()
        for value in ({"format": 1, "files": []}, {"format": True, "files": []}, [],
                      {"format": 1, "files": self.entries, "unexpected": True}):
            self.manifest.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaises(PACK.PackError):
                PACK.build_pack(self.manifest, self.output)
            self.assertFalse(self.output.exists())
        self.manifest.write_text('{"format":1,"format":1,"files":[]}', encoding="utf-8")
        with self.assertRaises(PACK.PackError):
            PACK.build_pack(self.manifest, self.output)

    def test_pack_needs_a_model_entry(self):
        self.entries[0]["path"] = "model.txt"
        self.rejected()

    def test_513_entries_are_rejected_before_reading_sources(self):
        self.entries = [{"source": f"{i}.glb", "path": f"{i}.glb", "sha256": "0" * 64}
                        for i in range(513)]
        self.rejected()

    def test_exact_512_entry_boundary_is_supported(self):
        for i in range(510):
            self.add(f"extra/{i}.txt", b"x")
        self.assertEqual(512, self.build()[0])

    def test_non_utf8_manifest_is_rejected(self):
        self.manifest.write_bytes(json.dumps({"format": 1, "files": self.entries}).encode("utf-16"))
        with self.assertRaises(PACK.PackError):
            PACK.build_pack(self.manifest, self.output)
        self.assertFalse(self.output.exists())

    def test_oversized_and_aggregate_expansion_are_rejected(self):
        with (self.root / "model.glb").open("wb") as source:
            source.truncate(PACK.MAXIMUM_BYTES + 1)  # Sparse: metadata validation avoids reading 32 MiB.
        self.rejected()
        (self.root / "model.glb").write_bytes(b"12345")
        (self.root / "notes.txt").write_bytes(b"67890")
        for item in self.entries:
            item["sha256"] = hashlib.sha256((self.root / item["source"]).read_bytes()).hexdigest()
        with mock.patch.object(PACK, "MAXIMUM_BYTES", 9):
            self.rejected()

    def test_stored_header_overhead_cannot_exceed_importer_archive_limit(self):
        (self.root / "model.glb").write_bytes(b"x" * 900)
        self.entries[0]["sha256"] = hashlib.sha256(b"x" * 900).hexdigest()
        with mock.patch.object(PACK, "MAXIMUM_BYTES", 1024):
            self.rejected()  # Payload fits, but ZIP headers do not.

    def test_existing_output_and_dangling_link_are_never_overwritten(self):
        self.output.write_bytes(b"user-owned output")
        with self.assertRaises(PACK.PackError):
            self.build()
        self.assertEqual(b"user-owned output", self.output.read_bytes())
        self.output.unlink()
        self.output.symlink_to(self.root / "absent-target")
        with self.assertRaises(PACK.PackError):
            self.build()
        self.assertTrue(self.output.is_symlink())
        self.assertFalse((self.root / "absent-target").exists())

    def test_input_file_and_parent_symlinks_are_rejected(self):
        (self.root / "model.glb").unlink()
        (self.root / "model.glb").symlink_to(self.root / "notes.txt")
        self.rejected()
        (self.root / "model.glb").unlink()
        (self.root / "model.glb").write_bytes(b"restored")
        self.entries[0]["sha256"] = hashlib.sha256(b"restored").hexdigest()
        (self.root / "linked").symlink_to(self.root, target_is_directory=True)
        self.entries[0]["source"] = "linked/model.glb"
        self.rejected()

    def test_manifest_and_output_directory_links_are_rejected(self):
        self.write_manifest()
        alias = self.root / "linked-manifest.json"; alias.symlink_to(self.manifest)
        with self.assertRaises(PACK.PackError):
            PACK.build_pack(alias, self.output)
        (self.root / "linked-dir").symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(PACK.PackError):
            PACK.build_pack(self.manifest, self.root / "linked-dir/pack.zip")
        self.assertFalse(self.output.exists())

    def test_input_directory_is_not_a_file(self):
        (self.root / "model.glb").unlink(); (self.root / "model.glb").mkdir()
        self.rejected()

    def test_racing_output_creator_wins_and_staging_is_removed(self):
        real_link = os.link
        def competing_link(source, destination):
            Path(destination).write_bytes(b"racing user output")
            real_link(source, destination)
        with mock.patch.object(PACK.os, "link", side_effect=competing_link):
            with self.assertRaises(FileExistsError):
                self.build()
        self.assertEqual(b"racing user output", self.output.read_bytes())
        self.assertEqual([], list(self.root.glob(".*.tmp")))

    def test_failed_publication_leaves_no_partial_output(self):
        with mock.patch.object(PACK.os, "link", side_effect=OSError("unsupported filesystem")):
            self.rejected()

    def test_repo_fox_pack_has_pinned_original_derived_and_attribution(self):
        manifest = ROOT / "assets/model-packs/fox-boarding-v1/pack.json"
        PACK.build_pack(manifest, self.output)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual("48625dfbdd2304ccd7fc6ef090220675d0cdb8c78634564765099c222d304f67",
                             hashlib.sha256(archive.read("Fox.boarding-prototype.glb")).hexdigest())
            self.assertEqual("d97044e701822bac5a62696459b27d7b375aada5de8574ed4362edbba94771f7",
                             hashlib.sha256(archive.read("Original/Fox.glb")).hexdigest())
            attribution = archive.read("ATTRIBUTION.txt").decode("utf-8")
            for credit in ("PixelMannen", "tomkranis", "AsoboStudio", "scurest", "CC0", "CC-BY 4.0"):
                self.assertIn(credit, attribution)
            notice = archive.read("README.txt").decode("utf-8")
            for warning in ("experimental", "Use this guide", "not arms", "does not cast", "original"):
                self.assertIn(warning, notice)
            self.assertNotIn("XivWayfinder.json", archive.namelist())


if __name__ == "__main__":
    unittest.main()
