#!/usr/bin/env python3
"""Contracts for the license-free Unity package writer."""

from __future__ import annotations

import gzip
import hashlib
import importlib.util
import json
import subprocess
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "unity" / "create_unitypackage.py"
SPEC = importlib.util.spec_from_file_location("create_unitypackage", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def metadata(guid: str) -> bytes:
    return f"fileFormatVersion: 2\nguid: {guid}\n".encode()


def parse_archive(archive_path: Path) -> dict[str, bytes]:
    archive = gzip.decompress(archive_path.read_bytes())
    entries: dict[str, bytes] = {}
    offset = 0
    while offset + 512 <= len(archive):
        header = archive[offset : offset + 512]
        if not any(header):
            assert not any(archive[offset:])
            return entries
        assert header[257:263] == b"ustar\0"
        checksum_header = bytearray(header)
        checksum_header[148:156] = b" " * 8
        assert int(header[148:156].rstrip(b"\0 "), 8) == sum(checksum_header)
        name = header[:100].split(b"\0", 1)[0].decode()
        size = int(header[124:136].rstrip(b"\0 ") or b"0", 8)
        content_offset = offset + 512
        entries[name] = archive[content_offset : content_offset + size]
        offset = content_offset + ((size + 511) // 512) * 512
    raise AssertionError("archive has no zero-block terminator")


class UnityPackageWriterTests(unittest.TestCase):
    def test_stages_the_reported_tarball_from_npm_11_and_12(self) -> None:
        package_name = "com.wallstop-studios.dxmessaging"
        record = {"name": package_name, "filename": f"{package_name}-4.0.0.tgz"}
        for pack_json in ([record], {package_name: record}):
            with self.subTest(pack_json=pack_json), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                manifest = json.dumps({"name": package_name, "version": "4.0.0"})
                (root / "package.json").write_text(manifest)
                packed = root / "package"
                packed.mkdir()
                for entry in MODULE.REQUIRED_ROOT_ENTRIES:
                    path = packed / entry
                    if entry in {"Editor", "Runtime", "Samples~"}:
                        path.mkdir()
                    else:
                        path.touch()
                (packed / "package.json").write_text(manifest)
                command = subprocess.CompletedProcess([], 0, stdout=json.dumps(pack_json))
                with (
                    patch.object(MODULE.subprocess, "run", return_value=command),
                    patch.object(MODULE, "_extract_pack", return_value=packed) as extract,
                ):
                    staged = MODULE.stage_packed_payload(root, root)
                extract.assert_called_once_with(root / record["filename"], root / "extract")
                self.assertTrue((staged / "Samples").is_dir())

    def test_existing_tarball_supplies_assets_and_must_match_checkout_identity(self) -> None:
        package = {"name": "com.wallstop-studios.dxmessaging", "version": "4.0.0"}
        cases = [package, {**package, "version": "3.3.0"}, {**package, "name": "other"}]
        for archived_package in cases:
            with self.subTest(archived_package=archived_package), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / "package.json").write_text(json.dumps(package))
                packed = root / "package"
                packed.mkdir()
                for entry in MODULE.REQUIRED_ROOT_ENTRIES:
                    path = packed / entry
                    if entry in {"Editor", "Runtime", "Samples~"}:
                        path.mkdir()
                    else:
                        path.touch()
                manifest = json.dumps(archived_package).encode()
                (packed / "package.json").write_bytes(manifest)
                (packed / "Samples~" / "Example.cs").write_text("class Example {}\n")
                tarball = root / "release.tgz"
                with tarfile.open(tarball, "w:gz") as archive:
                    archive.add(packed, arcname="package")
                scratch = root / "scratch"
                scratch.mkdir()
                with patch.object(MODULE.subprocess, "run") as npm:
                    if archived_package == package:
                        staged = MODULE.stage_packed_payload(root, scratch, tarball)
                        self.assertEqual((staged / "package.json").read_bytes(), manifest)
                        self.assertTrue((staged / "Samples" / "Example.cs").is_file())
                    else:
                        with self.assertRaisesRegex(ValueError, "name or version"):
                            MODULE.stage_packed_payload(root, scratch, tarball)
                        self.assertFalse((scratch / "staged").exists())
                    npm.assert_not_called()

    def test_refuses_invalid_npm_pack_json_before_extraction(self) -> None:
        package_name = "com.wallstop-studios.dxmessaging"
        record = {"name": package_name, "filename": "package.tgz"}
        invalid = [
            None,
            [],
            [record, record],
            [None],
            [{**record, "name": "other"}],
            {"other": record},
            {package_name: record, "other": record},
            {package_name: None},
            {package_name: {**record, "name": "other"}},
        ]
        invalid.extend(
            [{**record, "filename": filename}]
            for filename in (None, "../package.tgz", "other\\package.tgz", "A:package.tgz", "package.zip")
        )
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "package.json").write_text(json.dumps({"name": package_name}))
            for pack_json in [*(json.dumps(value) for value in invalid), "{"]:
                with self.subTest(pack_json=pack_json):
                    command = subprocess.CompletedProcess([], 0, stdout=pack_json)
                    with (
                        patch.object(MODULE.subprocess, "run", return_value=command),
                        patch.object(MODULE, "_extract_pack") as extract,
                    ):
                        with self.assertRaisesRegex(ValueError, "npm pack did not"):
                            MODULE.stage_packed_payload(root, root)
                    extract.assert_not_called()

    def test_preserves_metadata_and_writes_deterministic_valid_archive(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dxm-unitypackage-test-") as temporary:
            root = Path(temporary)
            payload = root / "payload"
            sample = payload / "Samples"
            sample.mkdir(parents=True)
            (payload / "Readme.txt").write_text("read me")
            (payload / "Readme.txt.meta").write_bytes(metadata("1" * 32))
            (sample / "Example.cs").write_text("class Example {}\n")
            (sample / "Example.cs.meta").write_bytes(metadata("2" * 32))
            (payload / ".gitkeep").write_text("ignored")
            assets = MODULE.collect_assets(payload)
            self.assertEqual(len(assets), 4)
            self.assertEqual(
                sorted(asset.pathname for asset in assets),
                [
                    MODULE.ASSET_ROOT,
                    f"{MODULE.ASSET_ROOT}/Readme.txt",
                    f"{MODULE.ASSET_ROOT}/Samples",
                    f"{MODULE.ASSET_ROOT}/Samples/Example.cs",
                ],
            )
            self.assertEqual(
                MODULE.generated_folder_metadata(MODULE.ASSET_ROOT)[0],
                "5f5dcf3de2902da12488dc120c1ff555",
            )
            first, second = root / "first.unitypackage", root / "second.unitypackage"
            first_digest = MODULE.write_unitypackage(assets, first)
            second_digest = MODULE.write_unitypackage(assets, second)
            self.assertEqual(first_digest, second_digest)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            self.assertEqual(first_digest, hashlib.sha256(first.read_bytes()).hexdigest())
            self.assertEqual(
                (Path(f"{first}.sha256")).read_text(encoding="ascii"),
                f"{first_digest}  first.unitypackage\n",
            )
            entries = parse_archive(first)
            self.assertEqual(len(entries), 10)
            for asset in assets:
                self.assertEqual(entries[f"{asset.guid}/pathname"].decode(), asset.pathname)
                self.assertEqual(entries[f"{asset.guid}/asset.meta"], asset.metadata)
                self.assertEqual(f"{asset.guid}/asset" in entries, asset.source_path is not None)

    def test_rejects_missing_metadata_duplicate_guids_and_long_archive_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dxm-unitypackage-invalid-") as temporary:
            payload = Path(temporary)
            (payload / "First.txt").write_text("first")
            with self.assertRaisesRegex(ValueError, "has no metadata"):
                MODULE.collect_assets(payload)
            (payload / "First.txt.meta").write_bytes(metadata("1" * 32))
            (payload / "Second.txt").write_text("second")
            (payload / "Second.txt.meta").write_bytes(metadata("1" * 32))
            with self.assertRaisesRegex(ValueError, "duplicate GUIDs"):
                MODULE.collect_assets(payload)
            (payload / "Second.txt").unlink()
            with self.assertRaisesRegex(ValueError, "orphaned metadata"):
                MODULE.collect_assets(payload)
            with self.assertRaisesRegex(ValueError, "too long"):
                MODULE.tar_header("x" * 101, 0)


if __name__ == "__main__":
    unittest.main()
