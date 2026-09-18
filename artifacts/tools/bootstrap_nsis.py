# -*- coding: utf-8 -*-
"""Download a pinned portable NSIS toolchain into ignored build artifacts."""

from __future__ import annotations

import hashlib
import os
import shutil
import urllib.request
import zipfile


VERSION = "3.12"
ARCHIVE_SIZE = 2_362_938
ARCHIVE_SHA256 = "56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f"
DOWNLOAD_URL = f"https://mirrors.mit.edu/macports/distfiles/nsis/nsis-{VERSION}.zip"
WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TOOLCHAIN_ROOT = os.path.join(WORKSPACE, "artifacts", "toolchain")
ARCHIVE_PATH = os.path.join(TOOLCHAIN_ROOT, f"nsis-{VERSION}.zip")
EXTRACT_ROOT = os.path.join(TOOLCHAIN_ROOT, f"nsis-{VERSION}")
MAKENSIS = os.path.join(EXTRACT_ROOT, f"nsis-{VERSION}", "makensis.exe")


def sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_archive(path: str) -> None:
    if os.path.getsize(path) != ARCHIVE_SIZE:
        raise RuntimeError(f"unexpected NSIS archive size: {os.path.getsize(path)}")
    actual = sha256(path)
    if actual != ARCHIVE_SHA256:
        raise RuntimeError(f"unexpected NSIS archive SHA-256: {actual}")


def safe_extract(archive: zipfile.ZipFile, destination: str) -> None:
    destination = os.path.abspath(destination)
    for entry in archive.infolist():
        target = os.path.abspath(os.path.join(destination, entry.filename))
        if os.path.commonpath((destination, target)) != destination:
            raise RuntimeError(f"unsafe NSIS archive entry: {entry.filename}")
    archive.extractall(destination)


def main() -> None:
    if os.path.isfile(MAKENSIS):
        print(MAKENSIS)
        return

    os.makedirs(TOOLCHAIN_ROOT, exist_ok=True)
    temporary = ARCHIVE_PATH + ".download"
    try:
        with urllib.request.urlopen(DOWNLOAD_URL, timeout=120) as response, open(temporary, "wb") as target:
            shutil.copyfileobj(response, target)
        verify_archive(temporary)
        os.replace(temporary, ARCHIVE_PATH)
        os.makedirs(EXTRACT_ROOT, exist_ok=True)
        with zipfile.ZipFile(ARCHIVE_PATH) as archive:
            safe_extract(archive, EXTRACT_ROOT)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)

    if not os.path.isfile(MAKENSIS):
        raise RuntimeError(f"makensis.exe is missing after extraction: {MAKENSIS}")
    print(MAKENSIS)


if __name__ == "__main__":
    main()
