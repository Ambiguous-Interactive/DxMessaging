#!/usr/bin/env python3
"""Create a deterministic classic Unity package without opening Unity."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import tarfile
import tempfile
import zlib
from pathlib import Path, PurePosixPath
from typing import Iterator, NamedTuple


ASSET_ROOT = "Assets/WallstopStudios/DxMessaging"
BLOCK_SIZE = 512
EXCLUDED_ROOT_ENTRIES = frozenset(("SourceGenerators", "SourceGenerators.meta"))
REQUIRED_ROOT_ENTRIES = (
    "package.json",
    "package.json.meta",
    "README.md",
    "README.md.meta",
    "LICENSE.md",
    "LICENSE.md.meta",
    "CHANGELOG.md",
    "CHANGELOG.md.meta",
    "Runtime",
    "Runtime.meta",
    "Editor",
    "Editor.meta",
    "Samples~",
)


class Asset(NamedTuple):
    guid: str
    pathname: str
    metadata: bytes
    source_path: Path | None = None


def _write_octal(header: bytearray, offset: int, length: int, value: int) -> None:
    encoded = format(value, "o").rjust(length - 1, "0").encode("ascii")
    if len(encoded) != length - 1:
        raise ValueError(f"Tar value {value} does not fit in {length} bytes.")
    header[offset : offset + length - 1] = encoded
    header[offset + length - 1] = 0


def tar_header(name: str, size: int) -> bytes:
    encoded_name = name.encode("utf-8")
    if len(encoded_name) > 100:
        raise ValueError(f"Unity package archive path is too long: {name}")
    header = bytearray(BLOCK_SIZE)
    header[: len(encoded_name)] = encoded_name
    _write_octal(header, 100, 8, 0o644)
    _write_octal(header, 108, 8, 0)
    _write_octal(header, 116, 8, 0)
    _write_octal(header, 124, 12, size)
    _write_octal(header, 136, 12, 0)
    header[148:156] = b" " * 8
    header[156] = ord("0")
    header[257:263] = b"ustar\0"
    header[263:265] = b"00"
    _write_octal(header, 148, 8, sum(header))
    return bytes(header)


def generated_folder_metadata(pathname: str) -> tuple[str, bytes]:
    # Keep the exact GUID algorithm used by New-DeterministicFolderMeta in the
    # licensed manual consumer verifier, so package identities do not churn.
    seed = f"com.wallstop-studios.dxmessaging:folder-meta:v1:{pathname}"
    guid = hashlib.md5(seed.encode("utf-8"), usedforsecurity=False).hexdigest()
    metadata = (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData:\n"
        "  assetBundleName:\n"
        "  assetBundleVariant:\n"
    ).encode("utf-8")
    return guid, metadata


def _read_guid(meta_path: Path) -> tuple[str, bytes]:
    metadata = meta_path.read_bytes()
    for line in metadata.decode("utf-8").splitlines():
        if line.startswith("guid: "):
            guid = line.removeprefix("guid: ")
            if len(guid) == 32 and all(character in "0123456789abcdef" for character in guid):
                return guid, metadata
            break
    raise ValueError(f"Unity metadata has no valid GUID: {meta_path}")


def collect_assets(staged_root: Path) -> list[Asset]:
    root_guid, root_meta = generated_folder_metadata(ASSET_ROOT)
    assets = [Asset(root_guid, ASSET_ROOT, root_meta)]
    for meta_path in staged_root.rglob("*.meta"):
        if not Path(str(meta_path)[: -len(".meta")]).exists():
            raise ValueError(f"Unity package payload contains orphaned metadata: {meta_path}")
    for source_path in sorted(staged_root.rglob("*"), key=lambda item: item.as_posix()):
        if any(part.startswith(".") for part in source_path.relative_to(staged_root).parts):
            continue
        if source_path.name.endswith(".meta"):
            continue
        if not source_path.is_dir() and not source_path.is_file():
            raise ValueError(f"Unity package payload contains an unsupported entry: {source_path}")
        relative = source_path.relative_to(staged_root).as_posix()
        pathname = f"{ASSET_ROOT}/{relative}"
        meta_path = Path(f"{source_path}.meta")
        if source_path.is_file() and not meta_path.is_file():
            raise ValueError(f"Unity package payload entry has no metadata: {source_path}")
        if source_path.is_dir() and not meta_path.is_file() and pathname != f"{ASSET_ROOT}/Samples":
            raise ValueError(f"Unity package payload folder has no metadata: {source_path}")
        guid, metadata = (
            _read_guid(meta_path) if meta_path.is_file() else generated_folder_metadata(pathname)
        )
        assets.append(
            Asset(guid, pathname, metadata, source_path if source_path.is_file() else None)
        )
    assets.sort(key=lambda asset: asset.guid)
    if len({asset.guid for asset in assets}) != len(assets):
        raise ValueError("Unity package payload contains duplicate GUIDs.")
    return assets


def _archive_chunks(assets: list[Asset]) -> Iterator[bytes]:
    for asset in assets:
        entries: list[tuple[str, bytes]] = []
        if asset.source_path is not None:
            entries.append(("asset", asset.source_path.read_bytes()))
        entries.extend((("asset.meta", asset.metadata), ("pathname", asset.pathname.encode())))
        for entry_name, content in entries:
            yield tar_header(f"{asset.guid}/{entry_name}", len(content))
            yield content
            remainder = len(content) % BLOCK_SIZE
            if remainder:
                yield bytes(BLOCK_SIZE - remainder)
    yield bytes(BLOCK_SIZE * 2)


def _write_deterministic_gzip(chunks: Iterator[bytes], output) -> None:
    # Stored DEFLATE blocks avoid compressor-version drift: immutable release
    # reruns must reproduce the same bytes even when the hosted image changes.
    output.write(b"\x1f\x8b\x08\x00\x00\x00\x00\x00\x00\xff")
    checksum = 0
    size = 0
    for chunk in chunks:
        checksum = zlib.crc32(chunk, checksum)
        size = (size + len(chunk)) & 0xFFFFFFFF
        for offset in range(0, len(chunk), 65535):
            block = chunk[offset : offset + 65535]
            output.write(b"\x00")
            output.write(struct.pack("<HH", len(block), len(block) ^ 0xFFFF))
            output.write(block)
    output.write(b"\x01\x00\x00\xff\xff")
    output.write(struct.pack("<II", checksum & 0xFFFFFFFF, size))


def write_unitypackage(assets: list[Asset], output_path: Path) -> str:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_output = Path(f"{output_path}.tmp")
    checksum_path = Path(f"{output_path}.sha256")
    temporary_checksum = Path(f"{checksum_path}.tmp")
    temporary_output.unlink(missing_ok=True)
    temporary_checksum.unlink(missing_ok=True)
    try:
        with temporary_output.open("xb") as raw:
            _write_deterministic_gzip(_archive_chunks(assets), raw)
        os.replace(temporary_output, output_path)
        digest = hashlib.sha256(output_path.read_bytes()).hexdigest()
        temporary_checksum.write_bytes(f"{digest}  {output_path.name}\n".encode("ascii"))
        os.replace(temporary_checksum, checksum_path)
        return digest
    finally:
        temporary_output.unlink(missing_ok=True)
        temporary_checksum.unlink(missing_ok=True)


def _extract_pack(pack_path: Path, destination: Path) -> Path:
    with tarfile.open(pack_path, "r:gz") as archive:
        members = archive.getmembers()
        for member in members:
            pure_path = PurePosixPath(member.name)
            if (
                pure_path.is_absolute()
                or "\\" in member.name
                or not pure_path.parts
                or pure_path.parts[0] != "package"
                or ".." in pure_path.parts
                or not (member.isdir() or member.isfile())
            ):
                raise ValueError(f"npm pack produced an unsafe archive entry: {member.name}")
        archive.extractall(destination, members=members)
    packed_root = destination / "package"
    if not packed_root.is_dir():
        raise ValueError("Extracted npm tarball is missing its package root.")
    return packed_root


def stage_packed_payload(repo_root: Path, scratch: Path) -> Path:
    npm = "npm.cmd" if os.name == "nt" else "npm"
    completed = subprocess.run(
        [npm, "pack", "--ignore-scripts", "--json", "--pack-destination", str(scratch)],
        cwd=repo_root,
        check=True,
        capture_output=True,
        text=True,
    )
    pack = json.loads(completed.stdout)
    if len(pack) != 1 or Path(pack[0].get("filename", "")).name != pack[0].get("filename"):
        raise ValueError("npm pack did not produce exactly one package tarball.")
    packed_root = _extract_pack(scratch / pack[0]["filename"], scratch / "extract")
    for entry in REQUIRED_ROOT_ENTRIES:
        if not (packed_root / entry).exists():
            raise ValueError(f"Packed npm package is missing required Unity export entry: {entry}")
    staged_root = scratch / "staged"
    staged_root.mkdir()
    for source in packed_root.iterdir():
        if source.name in EXCLUDED_ROOT_ENTRIES:
            continue
        destination = staged_root / ("Samples" if source.name == "Samples~" else source.name)
        if source.is_dir():
            shutil.copytree(source, destination)
        else:
            shutil.copy2(source, destination)
    return staged_root


def create_unitypackage(repo_root: Path, output_path: Path) -> tuple[list[Asset], str]:
    with tempfile.TemporaryDirectory(prefix="dxm-unitypackage-create-") as temporary:
        assets = collect_assets(stage_packed_payload(repo_root, Path(temporary)))
        digest = write_unitypackage(assets, output_path)
    print(f"Created {output_path} with {len(assets)} Unity assets.")
    return assets, digest


def main() -> None:
    repo_root = Path(__file__).resolve().parents[2]
    package = json.loads((repo_root / "package.json").read_text(encoding="utf-8"))
    default_output = repo_root / ".artifacts" / "release" / f"{package['name']}-{package['version']}.unitypackage"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=default_output)
    arguments = parser.parse_args()
    create_unitypackage(repo_root, arguments.output.resolve())


if __name__ == "__main__":
    main()
