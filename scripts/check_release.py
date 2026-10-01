#!/usr/bin/env python3
"""Offline README/release gate. Python 3.11+, standard library only."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import tempfile
from urllib.parse import unquote, urlsplit
import warnings
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
READMES = ("README.md", "README.zh-TW.md", "README.en.md", "README.ja.md")
spec = importlib.util.spec_from_file_location("package_release", ROOT / "artifacts/tools/package_release.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def assets(version):
    return (
        f"GameLibrary-Setup-v{version}.exe",
        f"GameLibrary-Portable-win-x64-v{version}.zip",
        f"GameLibrary-Tools-win-x64-v{version}.zip",
        f"GameLibrary-v{version}-SHA256SUMS.txt",
    )


def anchors(text):
    explicit = set(re.findall(r'(?:id|name)="([^"]+)"', text))
    for heading in re.findall(r"^#+ (.+)$", text, re.M):
        explicit.add(re.sub(r"[^\w\s-]", "", heading.lower()).replace(" ", "-"))
    return explicit


def check_link(source, target, root):
    url = urlsplit(target)
    if url.scheme in ("http", "https", "mailto"):
        return
    require(not url.scheme and not url.netloc, f"Unsupported link in {source}: {target}")
    file = (source.parent / unquote(url.path)).resolve() if url.path else source
    require(file.is_relative_to(root.resolve()) and file.is_file(), f"Missing/outside link: {source.name}: {target}")
    if url.fragment:
        require(unquote(url.fragment) in anchors(file.read_text(encoding="utf-8")),
                f"Missing anchor: {source.name}: {target}")


def check_docs(root=ROOT):
    version = package.read_version(root)
    package.assert_version_sync(version, root)
    public = set()
    for name in (*READMES, "README.zh-CN.md"):
        path = root / name
        text = path.read_text(encoding="utf-8")
        require("\ufffd" not in text, f"Invalid replacement character: {name}")
        for target in re.findall(r'\[[^\]]*\]\(([^)\n]+)\)|(?:href|src)="([^"]+)"', text):
            check_link(path, target[0] or target[1], root)
        require(text.count("<details>") == text.count("</details>"), f"Unclosed details: {name}")
        if name == "README.zh-CN.md":
            continue
        require(all(other in text for other in READMES), f"Missing language switch: {name}")
        require(all(anchor in anchors(text) for anchor in ("download", "demo", "start", "faq")),
                f"Missing navigation: {name}")
        require(f"v{version}" in text, f"Missing current-source version: {name}")
        found = re.findall(r"https://github\.com/sumingwang233/GameLibrary/releases/download/v([0-9.]+)/([^\s)]+)", text)
        versions = {v for v, _ in found}
        require(len(versions) == 1, f"Inconsistent public download version: {name}")
        current = versions.pop()
        require({name for _, name in found} == set(assets(current)), f"Wrong public asset names: {name}")
        public.add(current)
    require(len(public) == 1, "Translated READMEs disagree on public version")
    hero = ET.parse(root / "assets/readme/hero.svg").getroot()
    ns = "{http://www.w3.org/2000/svg}"
    require(hero.get("viewBox") == "0 0 960 300", "Unexpected hero dimensions")
    require(hero.find(ns + "title") is not None and hero.find(ns + "desc") is not None, "Missing SVG accessible description")
    for element in hero.iter():
        require(element.tag not in (ns + "script", ns + "animate", ns + "foreignObject"), "Active SVG content")
        for key, value in element.attrib.items():
            if key.endswith("href"):
                require(value.startswith(("data:image/png;base64,", "#")), "External SVG resource")
    template = (root / "docs/releases/TEMPLATE.md").read_text(encoding="utf-8")
    require(all(section in template for section in ("## 简体中文", "## English", "### 下载", "### Downloads",
                                                  "### 验证与限制", "### Validation and limitations")),
            "Incomplete bilingual release template")
    require((root / "docs/release-policy.md").is_file(), "Missing release policy")
    print(f"README/release gate passed: source v{version}, public v{next(iter(public))}, four languages")
    return version


def manifest(text, expected):
    entries = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([^/\\]+)", line)
        require(match is not None, "Malformed SHA-256 entry")
        digest, name = match.groups()
        require(name not in entries, f"Duplicate checksum: {name}")
        entries[name] = digest
    require(set(entries) == set(expected), "Missing or unexpected checksum entries")
    return entries


def stream_hash(stream):
    result = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        result.update(chunk)
    return result.hexdigest()


def check_dist(directory, version):
    names = assets(version)
    actual = {path.name for path in directory.iterdir()
              if path.is_file() and (path.suffix.lower() in (".exe", ".zip") or path.name.endswith("-SHA256SUMS.txt"))}
    require(actual == set(names), f"Wrong distribution asset set: {sorted(actual)}")
    sums = manifest((directory / names[3]).read_text(encoding="utf-8"), names[:3])
    for name, digest in sums.items():
        with (directory / name).open("rb") as stream:
            require(stream_hash(stream) == digest, f"Asset checksum mismatch: {name}")
    with (directory / names[0]).open("rb") as stream:
        require(stream.read(2) == b"MZ", "Installer is not a Windows executable")
    payloads = (
        ("GameLibrary.Desktop.exe", "GameLibrary.TauriBridge.exe", "GameLibrary.Host.exe"),
        ("GameLibrary.Host.exe", "gamelibrary.exe", "GameLibrary.Mcp.exe"),
    )
    for name, files in zip(names[1:3], payloads):
        with zipfile.ZipFile(directory / name) as archive:
            entries = archive.infolist()
            paths = [entry.filename for entry in entries]
            require(len(paths) == len(set(path.casefold() for path in paths)), f"Duplicate ZIP names: {name}")
            require(set(paths) == {*files, "SHA256SUMS.txt"}, f"Wrong ZIP layout: {name}")
            require(all(not entry.is_dir() for entry in entries), f"ZIP directory entry: {name}")
            sums_inside = manifest(archive.read("SHA256SUMS.txt").decode("utf-8"), files)
            for file, digest in sums_inside.items():
                with archive.open(file) as stream:
                    require(stream_hash(stream) == digest, f"Payload checksum mismatch: {name}/{file}")
    print(f"Distribution integrity passed: v{version}, three packages, inner and outer SHA-256")


def check_stable(directory, version):
    report = json.loads((directory / "release-validation.json").read_text(encoding="utf-8"))
    require(report.get("version") == version, "Validation report version mismatch")
    require(report.get("manualAcceptance") == "passed", "Manual acceptance incomplete")
    checks = report.get("checks", [])
    required = {"dotnet format", "dotnet Release build", "dotnet full tests", "frontend tests",
                "frontend typecheck", "cargo fmt", "Tauri production build", "desktop WebView smoke",
                "final asset integrity"}
    require(required.issubset({check["name"] for check in checks}), "Required stable checks are missing")
    require(all(check.get("status") == "passed" for check in checks), "Stable checks failed or not run")
    print("Stable release gate passed; publication still requires explicit authorization")


def self_test():
    def fails(action):
        try:
            action()
        except (ValueError, RuntimeError, SystemExit, KeyError, zipfile.BadZipFile):
            return
        raise AssertionError("Invalid input was accepted")

    digest = hashlib.sha256(b"payload").hexdigest()
    require(manifest(f"{digest}  file.exe\n", {"file.exe"})["file.exe"] == digest, "Valid manifest failed")
    fails(lambda: manifest(f"{digest}  ../file.exe", {"file.exe"}))
    fails(lambda: manifest(f"{digest}  file.exe\n{digest}  file.exe", {"file.exe"}))
    fails(lambda: manifest(f"{digest}  wrong.exe", {"file.exe"}))
    fails(lambda: manifest("bad  file.exe", {"file.exe"}))
    with tempfile.TemporaryDirectory(prefix="gamelibrary-release-check-") as temp:
        root = Path(temp)
        for relative in ("Directory.Build.props", "src/GameLibrary.Tauri/package.json",
                         "src/GameLibrary.Tauri/package-lock.json", "src/GameLibrary.Tauri/src-tauri/Cargo.toml",
                         "src/GameLibrary.Tauri/src-tauri/Cargo.lock", "src/GameLibrary.Tauri/src-tauri/tauri.conf.json"):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((ROOT / relative).read_bytes())
        version = package.read_version(root)
        package.assert_version_sync(version, root)
        package_json = root / "src/GameLibrary.Tauri/package.json"
        data = json.loads(package_json.read_text(encoding="utf-8"))
        data["version"] = "0.0.0"
        package_json.write_text(json.dumps(data), encoding="utf-8")
        fails(lambda: package.assert_version_sync(version, root))
        names = assets(version)
        dist = root / "dist"
        dist.mkdir()
        (dist / names[0]).write_bytes(b"MZexample")
        for name, files in zip(names[1:3], (
                ("GameLibrary.Desktop.exe", "GameLibrary.TauriBridge.exe", "GameLibrary.Host.exe"),
                ("GameLibrary.Host.exe", "gamelibrary.exe", "GameLibrary.Mcp.exe"))):
            with zipfile.ZipFile(dist / name, "w") as archive:
                payload = b"MZexample"
                for file in files:
                    archive.writestr(file, payload)
                archive.writestr("SHA256SUMS.txt", "".join(
                    f"{hashlib.sha256(payload).hexdigest()}  {file}\n" for file in files))

        def refresh():
            (dist / names[3]).write_text("".join(
                f"{hashlib.sha256((dist / name).read_bytes()).hexdigest()}  {name}\n" for name in names[:3]),
                encoding="utf-8")

        refresh()
        check_dist(dist, version)
        (dist / names[0]).write_bytes(b"MZchanged")
        fails(lambda: check_dist(dist, version))
        refresh()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(dist / names[1], "a") as archive:
                archive.writestr("GameLibrary.Host.exe", b"MZduplicate")
        refresh()
        fails(lambda: check_dist(dist, version))
        (dist / "GameLibrary-Unknown.zip").write_bytes(b"wrong")
        fails(lambda: check_dist(dist, version))
        report = {"version": version, "manualAcceptance": "not-run", "checks": []}
        (dist / "release-validation.json").write_text(json.dumps(report), encoding="utf-8")
        fails(lambda: check_stable(dist, version))
        report["manualAcceptance"] = "passed"
        (dist / "release-validation.json").write_text(json.dumps(report), encoding="utf-8")
        fails(lambda: check_stable(dist, version))
    print("Negative regression checks passed: versions, manifests, hashes, ZIP duplicates and asset names")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", type=Path, help="check the final four distribution assets")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--stable", action="store_true", help="require completed automated and manual acceptance")
    args = parser.parse_args()
    if args.stable and not args.dist:
        parser.error("--stable requires --dist")
    version = check_docs()
    if args.self_test:
        self_test()
    if args.dist:
        check_dist(args.dist.resolve(), version)
        if args.stable:
            check_stable(args.dist.resolve(), version)


if __name__ == "__main__":
    main()
