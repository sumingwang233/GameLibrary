"""Compile the NSIS UI and optionally smoke-test an isolated current-user install.

Uses a unique uninstall key and shortcut folder, dummy payloads, and TEST_BUILD
(no taskkill). No existing GameLibrary install or user library is touched.
"""

import argparse
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "artifacts" / "installer-ui"
SCRIPT = ROOT / "artifacts" / "installer" / "GameLibrary.nsi"
FILES = ("GameLibrary.Desktop.exe", "GameLibrary.TauriBridge.exe", "GameLibrary.Host.exe", "SHA256SUMS.txt")


def run(command, log):
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90)
    log.write_text((result.stdout or "") + (result.stderr or ""), encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}")


def compile_installer(compiler, payload, target, version, product, **defines):
    options = dict(
        VERSION=version, FILE_VERSION=".".join(version.split(".") + ["0"] * (4 - len(version.split(".")))),
        PAYLOAD_DIR=payload, OUTPUT_FILE=target, INSTALLER_ICON=ROOT / "src/GameLibrary.Desktop/App.ico",
        PRODUCT_ID=product, START_MENU_FOLDER=product, TEST_BUILD=1, **defines,
    )
    run([str(compiler), "/V3", "/WX", "/INPUTCHARSET", "UTF8", *(f"/D{k}={v}" for k, v in options.items()), str(SCRIPT)], target.with_suffix(".log"))
    assert target.read_bytes()[:2] == b"MZ", "Installer must be a Windows executable"


def smoke(setup, keep_setup, product, work, language="2052"):
    import winreg

    key = rf"Software\Microsoft\Windows\CurrentVersion\Uninstall\{product}"

    def installed_location():
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, winreg.KEY_READ | winreg.KEY_WOW64_32KEY) as handle:
                return winreg.QueryValueEx(handle, "InstallLocation")[0]
        except FileNotFoundError:
            return None

    assert installed_location() is None, "Refuse to reuse an existing uninstall key"
    data = work / "library.db"
    data.write_bytes(b"isolated library sentinel")
    first, second, third = (work / name for name in ("first install", "upgrade install", "keep install"))
    current = None
    try:
        for installer, folder in ((setup, first), (setup, second), (keep_setup, third)):
            # NSIS requires /D= to be the final UNQUOTED command-line tail,
            # including spaces; list2cmdline would incorrectly quote that option.
            options = [str(installer), "/S"] + ([f"/LANG={language}"] if language != "2052" else [])
            command = subprocess.list2cmdline(options) + f" /D={folder}"
            run(command, folder.with_suffix(".log"))
            current = folder
            assert installed_location() == str(folder)
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, winreg.KEY_READ | winreg.KEY_WOW64_32KEY) as handle:
                assert winreg.QueryValueEx(handle, "InstallerLanguage")[0] == language
            assert all((folder / name).is_file() for name in (*FILES, "Uninstall.exe", ".gamelibrary-install"))
            (folder / "unknown.txt").write_text("keep unknown files", encoding="utf-8")
            assert data.read_bytes() == b"isolated library sentinel"
        assert not (first / FILES[0]).exists(), "Default upgrade must uninstall old program files"
        assert (first / "unknown.txt").is_file(), "Upgrade must retain unknown files"
        assert (second / FILES[0]).is_file(), "IDNO must preserve old files"
        assert (second / "unknown.txt").is_file()
    finally:
        # Only run the uninstaller produced in this workspace's unique test directory.
        if current and (current / "Uninstall.exe").is_file():
            assert current.resolve().is_relative_to(work.resolve())
            run([str(current / "Uninstall.exe"), "/S"], work / "uninstall.log")
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and (installed_location() or (current / FILES[0]).exists()):
                time.sleep(.1)
            assert installed_location() is None, "Test uninstall key must be removed"
            assert not (current / FILES[0]).exists()
            assert (current / "unknown.txt").is_file()
            assert data.read_bytes() == b"isolated library sentinel"
            shortcuts = Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs" / product
            assert not shortcuts.exists(), "Test shortcuts must be removed"
    print("Smoke passed: fresh install, default upgrade, IDNO replay, uninstall, data and unknown-file preservation.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--makensis", type=Path)
    parser.add_argument("--smoke", action="store_true", help="execute only isolated dummy-payload builds")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("NSIS compilation and installer smoke checks require Windows")
    compiler = args.makensis or shutil.which("makensis") or ROOT / "artifacts/toolchain/nsis-3.12/nsis-3.12/makensis.exe"
    if not Path(compiler).is_file():
        parser.error("Pass --makensis or run artifacts/tools/bootstrap_nsis.py first")
    for name, size in (("header.bmp", (150, 57)), ("wizard.bmp", (164, 314))):
        image = (SCRIPT.parent / name).read_bytes()
        assert image[:2] == b"BM" and struct.unpack_from("<ii", image, 18) == size
        assert struct.unpack_from("<H", image, 28)[0] == 24
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    assert version and all(part.isdigit() for part in version.split("."))
    OUTPUT.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex[:12]
    work = OUTPUT / f"check-{token}"
    payload = work / "payload"
    payload.mkdir(parents=True)
    for name in FILES:
        (payload / name).write_bytes(b"dummy installer UI test payload\n")
    product = f"GameLibrary.InstallerCheck.{token}"
    setup, keep = work / "GameLibrary-Check.exe", work / "GameLibrary-Keep-Check.exe"
    compile_installer(compiler, payload, setup, version, product)
    compile_installer(compiler, payload, keep, version, product, OLD_VERSION_SILENT_ANSWER="IDNO")
    previews = []
    preview_product = f"GameLibrary.UiPreview.{token}"
    for name, flags in (
        ("GameLibrary-Installer-Preview.exe", {}),
        ("GameLibrary-Upgrade-Preview.exe", {"PREVIEW_OLD_VERSION": 1}),
    ):
        target = OUTPUT / name
        compile_installer(compiler, payload, target, version, preview_product, UI_PREVIEW=1, **flags)
        previews.append(target)
    print("Compile passed with warnings treated as errors: standard, IDNO, UI preview, upgrade preview.")
    if args.smoke:
        for language in ("2052", "1028", "1033", "1041"):
            language_work = work / f"language-{language}"
            language_work.mkdir()
            smoke(setup, keep, product, language_work, language)
        import winreg
        probe = work / "preview must not install"
        for preview in previews:
            command = subprocess.list2cmdline([str(preview), "/S"]) + f" /D={probe}"
            run(command, work / (preview.stem + ".log"))
            assert not probe.exists()
        try:
            winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"Software\Microsoft\Windows\CurrentVersion\Uninstall\{preview_product}")
        except FileNotFoundError:
            pass
        else:
            raise AssertionError("UI preview must not create an uninstall registration")
        shortcuts = Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs" / preview_product
        assert not shortcuts.exists()
        print("Read-only previews passed: no installation directory, uninstall registration or shortcuts.")
    print(f"Read-only UI previews: {OUTPUT}")


if __name__ == "__main__":
    main()
