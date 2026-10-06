# -*- coding: utf-8 -*-
"""Build the formal Windows release assets.

Outputs:
- ordinary-user current-user installer EXE (Desktop + Host);
- portable self-contained win-x64 ZIP (Desktop + Host);
- separate advanced-tools ZIP (Host + CLI + MCP);
- SHA-256 manifest for the release assets;
- single-file self-contained executables.
"""

import argparse
import glob
import hashlib
import json
import os
import shutil
import subprocess
import uuid
import zipfile
from datetime import datetime, timezone
from pathlib import Path
import sys
import re
import tomllib
import xml.etree.ElementTree as ET


DOTNET = shutil.which("dotnet") or "dotnet"
NPM = shutil.which("npm.cmd") or shutil.which("npm") or "npm.cmd"
NODE = shutil.which("node.exe") or shutil.which("node") or "node.exe"
WORKSPACE = str(Path(__file__).resolve().parents[2])
DIST = os.path.join(WORKSPACE, "artifacts", "dist")
STAGING_ROOT = os.path.join(DIST, "staging", uuid.uuid4().hex)
USER_PAYLOAD = os.path.join(STAGING_ROOT, "GameLibrary")
TOOLS_PAYLOAD = os.path.join(STAGING_ROOT, "GameLibrary-Tools")
SYMBOLS = os.path.join(STAGING_ROOT, "symbols")
COMPONENT_OUTPUTS = os.path.join(STAGING_ROOT, "component-publish")
INSTALLER_SCRIPT = os.path.join(WORKSPACE, "artifacts", "installer", "GameLibrary.nsi")
INSTALLER_ICON = os.path.join(WORKSPACE, "src", "GameLibrary.Desktop", "App.ico")
PORTABLE_NSIS = os.path.join(
    WORKSPACE,
    "artifacts",
    "toolchain",
    "nsis-3.12",
    "nsis-3.12",
    "makensis.exe",
)
DEFAULT_TIMESTAMP_URL = "http://timestamp.digicert.com"
COMPONENTS = [
    ("GameLibrary.Host", "GameLibrary.Host.exe", True, True),
    ("GameLibrary.TauriBridge", "GameLibrary.TauriBridge.exe", True, False),
    ("GameLibrary.Cli", "gamelibrary.exe", False, True),
    ("GameLibrary.Mcp", "GameLibrary.Mcp.exe", False, True),
]
CHECK_RESULTS = []
SOURCE_COMMIT = None
SOURCE_DIRTY = None


def npm_command():
    cli = Path(NODE).resolve().parent / "node_modules/npm/bin/npm-cli.js"
    return [NODE, str(cli)] if cli.is_file() else [NPM]


def read_version(workspace=None):
    """单一真源：Directory.Build.props 的 <Version>（R38）。缺失即失败，不做静默回退。"""
    props = Path(workspace or WORKSPACE) / "Directory.Build.props"
    version = ET.parse(props).findtext(".//Version", "").strip()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise RuntimeError(f"Invalid or missing product Version: {props}")
    return version


def assert_version_sync(version, workspace=None):
    """Tauri 三件套必须与单一真源一致；tauri.conf.json 不声明版本（继承 Cargo.toml）。"""
    project = Path(workspace or WORKSPACE) / "src/GameLibrary.Tauri"
    load_json = lambda name: json.loads((project / name).read_text(encoding="utf-8"))
    cargo = tomllib.loads((project / "src-tauri/Cargo.toml").read_text(encoding="utf-8"))
    lock = tomllib.loads((project / "src-tauri/Cargo.lock").read_text(encoding="utf-8"))
    products = [entry for entry in lock["package"] if entry["name"] == cargo["package"]["name"]]
    if len(products) != 1:
        raise RuntimeError("Cargo.lock must contain exactly one product entry")
    expectations = [
        ("Cargo.toml", cargo["package"]["version"]),
        ("Cargo.lock", products[0]["version"]),
        ("package.json", load_json("package.json")["version"]),
        ("package-lock.json", load_json("package-lock.json")["version"]),
        ("package-lock.json root", load_json("package-lock.json")["packages"][""]["version"]),
    ]
    if "version" in load_json("src-tauri/tauri.conf.json"):
        raise RuntimeError("tauri.conf.json must inherit its version from Cargo.toml")
    mismatched = [f"{name}={found}" for name, found in expectations if found != version]
    if mismatched:
        raise SystemExit(f"版本声明与单一真源 {version} 不一致：{'; '.join(mismatched)}")


def sh(cmd, cwd=WORKSPACE, timeout=1200, label=None):
    name = label or " ".join(os.path.basename(str(value)) for value in cmd[:2])
    evidence = Path(DIST) / "evidence"
    evidence.mkdir(parents=True, exist_ok=True)
    path = evidence / f"check-{len(CHECK_RESULTS) + 1:02d}.log"
    try:
        proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=timeout)
        output = (proc.stdout or "") + (proc.stderr or "")
        path.write_text(output, encoding="utf-8")
        CHECK_RESULTS.append({"name": name, "status": "passed" if proc.returncode == 0 else "failed",
                              "exitCode": proc.returncode, "evidence": str(path.relative_to(DIST))})
        return proc.returncode, output
    except (OSError, subprocess.TimeoutExpired) as error:
        path.write_text(str(error), encoding="utf-8")
        CHECK_RESULTS.append({"name": name, "status": "failed", "exitCode": None,
                              "evidence": str(path.relative_to(DIST))})
        raise


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_staging(log):
    os.makedirs(USER_PAYLOAD, exist_ok=True)
    os.makedirs(TOOLS_PAYLOAD, exist_ok=True)
    os.makedirs(SYMBOLS, exist_ok=True)
    os.makedirs(COMPONENT_OUTPUTS, exist_ok=True)
    log.append(f"fresh staging: {STAGING_ROOT}")


def publish_all(log, desktop_smoke_skip_reason=None):
    for project, executable, include_in_user, include_in_tools in COMPONENTS:
        component_output = os.path.join(COMPONENT_OUTPUTS, project)
        command = [
            DOTNET,
            "publish",
            os.path.join("src", project, f"{project}.csproj"),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-o",
            component_output,
            "-p:UseAppHost=true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=None",
            f"-p:SourceRevisionId={SOURCE_COMMIT}",
        ]
        rc, output = sh(command)
        log.append(f"publish {project}: rc={rc}")
        if rc != 0:
            log.append(output[-4000:])
            raise RuntimeError(f"publish {project} failed")

        source = os.path.join(component_output, executable)
        if not os.path.isfile(source):
            raise RuntimeError(f"single-file publish output is missing: {source}")
        if include_in_user:
            shutil.copy2(source, os.path.join(USER_PAYLOAD, executable))
        if include_in_tools:
            shutil.copy2(source, os.path.join(TOOLS_PAYLOAD, executable))
    shutil.rmtree(COMPONENT_OUTPUTS)
    publish_tauri_desktop(log, desktop_smoke_skip_reason)
    log.append("single-file payloads: user=3 executables, tools=3 executables")


def publish_tauri_desktop(log, desktop_smoke_skip_reason=None):
    project = os.path.join(WORKSPACE, "src", "GameLibrary.Tauri")
    rc, output = sh([*npm_command(), "run", "tauri", "build", "--", "--no-bundle"],
                    cwd=project, timeout=3600, label="Tauri production build")
    log.append(f"publish GameLibrary.Tauri: rc={rc}")
    if rc != 0:
        log.append(output[-8000:])
        raise RuntimeError("publish GameLibrary.Tauri failed")
    executable = os.path.join(project, "src-tauri", "target", "release", "gamelibrary-desktop.exe")
    if not os.path.isfile(executable):
        raise RuntimeError(f"Tauri executable is missing: {executable}")
    staged_executable = os.path.join(USER_PAYLOAD, "GameLibrary.Desktop.exe")
    shutil.copy2(executable, staged_executable)
    for sidecar in ("GameLibrary.TauriBridge.exe", "GameLibrary.Host.exe"):
        source = os.path.join(project, "src-tauri", "target", "release", sidecar)
        if not os.path.isfile(source):
            raise RuntimeError(f"Tauri sidecar is missing: {source}")
        shutil.copy2(source, os.path.join(USER_PAYLOAD, sidecar))
    if desktop_smoke_skip_reason:
        CHECK_RESULTS.append({"name": "desktop WebView smoke", "status": "not-run",
                              "exitCode": None, "evidence": desktop_smoke_skip_reason})
        log.append(f"desktop smoke not-run: {desktop_smoke_skip_reason}; candidate only")
    else:
        verify_tauri_desktop(staged_executable, log)


def verify_tauri_desktop(executable, log):
    test_id = uuid.uuid4().hex
    test_root = os.path.join(WORKSPACE, "artifacts", "test-runs", test_id)
    data_directory = os.path.join(test_root, "data")
    screenshot = os.path.join(
        WORKSPACE,
        "artifacts",
        "build-reports",
        f"v{read_version()}-release-webview.png",
    )
    os.makedirs(data_directory, exist_ok=True)
    os.makedirs(os.path.dirname(screenshot), exist_ok=True)
    script = os.path.join(
        WORKSPACE,
        "src",
        "GameLibrary.Tauri",
        "scripts",
        "verify_release.mjs",
    )
    try:
        rc, output = sh(
            [NODE, script, executable, data_directory, screenshot],
            timeout=120, label="desktop WebView smoke",
        )
        log.append(f"Tauri release WebView smoke: rc={rc}")
        if output.strip():
            log.append(output.strip()[-4000:])
        if rc != 0:
            raise RuntimeError("Tauri release WebView smoke failed")
    finally:
        shutil.rmtree(test_root, ignore_errors=True)


def separate_pdbs(log):
    count = 0
    for payload in (USER_PAYLOAD, TOOLS_PAYLOAD):
        for root, _, files in os.walk(payload):
            for name in files:
                if not name.lower().endswith(".pdb"):
                    continue
                source = os.path.join(root, name)
                relative = os.path.join(os.path.basename(payload), os.path.relpath(source, payload))
                destination = os.path.join(SYMBOLS, relative)
                os.makedirs(os.path.dirname(destination), exist_ok=True)
                os.replace(source, destination)
                count += 1
    log.append(f"pdb separated: {count}")


def write_payload_checksums(payload, label, log):
    lines = []
    for root, _, files in os.walk(payload):
        for name in sorted(files):
            full = os.path.join(root, name)
            relative = os.path.relpath(full, payload).replace("\\", "/")
            lines.append(f"{sha256(full)}  {relative}")
    with open(os.path.join(payload, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as target:
        target.write("\n".join(lines) + "\n")
    log.append(f"{label} payload checksums: {len(lines)} files")


def find_signtool():
    configured = os.environ.get("GAMELIBRARY_SIGNTOOL")
    if configured:
        return configured
    discovered = shutil.which("signtool")
    if discovered:
        return discovered
    kits_root = os.path.join(
        os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
        "Windows Kits",
        "10",
        "bin",
    )
    candidates = glob.glob(os.path.join(kits_root, "*", "x64", "signtool.exe"))
    return sorted(candidates, reverse=True)[0] if candidates else None


def signing_configuration(require_signing, log):
    signtool = find_signtool()
    pfx = os.environ.get("GAMELIBRARY_SIGNING_PFX")
    password = os.environ.get("GAMELIBRARY_SIGNING_PFX_PASSWORD")
    thumbprint = os.environ.get("GAMELIBRARY_SIGNING_THUMBPRINT")
    timestamp_url = os.environ.get("GAMELIBRARY_TIMESTAMP_URL", DEFAULT_TIMESTAMP_URL)

    if pfx and thumbprint:
        raise RuntimeError("configure either GAMELIBRARY_SIGNING_PFX or GAMELIBRARY_SIGNING_THUMBPRINT, not both")
    if pfx and not os.path.isfile(pfx):
        raise RuntimeError(f"signing PFX does not exist: {pfx}")
    if pfx and password is None:
        raise RuntimeError("GAMELIBRARY_SIGNING_PFX_PASSWORD is required with GAMELIBRARY_SIGNING_PFX")

    configured = bool(signtool and (pfx or thumbprint))
    log.append(f"signing configured: {configured}")
    if require_signing and not configured:
        raise RuntimeError(
            "trusted code signing is required; configure SignTool and a PFX or certificate thumbprint"
        )
    if not configured:
        return None
    return {
        "signtool": signtool,
        "pfx": pfx,
        "password": password,
        "thumbprint": thumbprint,
        "timestamp_url": timestamp_url,
    }


def sign_file(path, signing, log):
    command = [
        signing["signtool"],
        "sign",
        "/fd",
        "SHA256",
        "/tr",
        signing["timestamp_url"],
        "/td",
        "SHA256",
    ]
    if signing["pfx"]:
        command.extend(["/f", signing["pfx"], "/p", signing["password"]])
    else:
        command.extend(["/sha1", signing["thumbprint"]])
    command.append(path)
    rc, output = sh(command, timeout=180)
    if rc != 0:
        log.append(output[-4000:])
        raise RuntimeError(f"Authenticode signing failed: {path}")

    rc, output = sh([signing["signtool"], "verify", "/pa", "/all", path], timeout=60)
    if rc != 0:
        log.append(output[-4000:])
        raise RuntimeError(f"Authenticode verification failed: {path}")
    log.append(f"signed: {os.path.relpath(path, WORKSPACE)}")


def sign_payload(signing, log):
    if signing is None:
        log.append("payload signing skipped")
        return False
    targets = []
    for payload in (USER_PAYLOAD, TOOLS_PAYLOAD):
        for root, _, files in os.walk(payload):
            for name in files:
                if name.lower().endswith(".exe"):
                    targets.append(os.path.join(root, name))
    for target in sorted(targets):
        sign_file(target, signing, log)
    log.append(f"payload signatures: {len(targets)}")
    return True


def find_makensis():
    configured = os.environ.get("GAMELIBRARY_MAKENSIS")
    if configured:
        return configured
    discovered = shutil.which("makensis")
    if discovered:
        return discovered
    candidates = [
        PORTABLE_NSIS,
        os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "NSIS", "makensis.exe"),
        os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "NSIS", "makensis.exe"),
    ]
    return next((candidate for candidate in candidates if os.path.isfile(candidate)), None)


def windows_file_version(version):
    parts = version.split(".")
    if not parts or any(not part.isdigit() for part in parts):
        raise RuntimeError(f"release version must be numeric for Windows metadata: {version}")
    if len(parts) > 4:
        raise RuntimeError(f"release version has more than four components: {version}")
    return ".".join(parts + ["0"] * (4 - len(parts)))


def make_installer(version, log, compiler=None):
    makensis = compiler or find_makensis()
    if not makensis:
        raise RuntimeError(
            "NSIS makensis.exe was not found; set GAMELIBRARY_MAKENSIS or extract "
            "NSIS 3.12 to artifacts/toolchain/nsis-3.12 (run artifacts/tools/bootstrap_nsis.py)"
        )
    for required in (INSTALLER_SCRIPT, INSTALLER_ICON):
        if not os.path.isfile(required):
            raise RuntimeError(f"installer input is missing: {required}")
    target = os.path.join(DIST, f"GameLibrary-Setup-v{version}.exe")
    if os.path.exists(target):
        os.remove(target)
    command = [
        makensis,
        "/V3",
        "/WX",
        "/INPUTCHARSET",
        "UTF8",
        f"/DVERSION={version}",
        f"/DFILE_VERSION={windows_file_version(version)}",
        f"/DPAYLOAD_DIR={USER_PAYLOAD}",
        f"/DOUTPUT_FILE={target}",
        f"/DINSTALLER_ICON={INSTALLER_ICON}",
        INSTALLER_SCRIPT,
    ]
    rc, output = sh(command, timeout=600)
    log.append(f"nsis: rc={rc}, compiler={makensis}")
    if rc != 0 or not os.path.isfile(target):
        log.append(output[-4000:])
        raise RuntimeError("NSIS installer build failed")
    log.append(f"installer: {os.path.getsize(target) / 1048576:.1f} MiB")
    return target


def write_checklist(version, signed, log):
    signing = (
        "程序文件与安装器均已使用受信任 Authenticode 证书签名并完成签名验证。"
        if signed
        else "本次发布未签名，下载者应核对同版本 SHA-256 清单。"
    )
    body = f"""# GameLibrary v{version} 发布校验清单

## 发布资产

- `GameLibrary-Setup-v{version}.exe`：普通用户推荐下载的单个 NSIS 图形化当前用户安装器，内含 Tauri Desktop、.NET bridge 与后台 Host；默认安装到 `%LOCALAPPDATA%\\Programs\\GameLibrary`，可在安装向导中修改位置，不要求管理员权限。
- `GameLibrary-Portable-win-x64-v{version}.zip`：免安装包，只含 `GameLibrary.Desktop.exe`、`GameLibrary.TauriBridge.exe`、`GameLibrary.Host.exe` 与校验文件，不含脚本。
- `GameLibrary-Tools-win-x64-v{version}.zip`：高级用户工具包，只含单文件 Host、CLI、MCP 与校验文件，不含脚本。
- `GameLibrary-v{version}-SHA256SUMS.txt`：以上三个发布资产的 SHA-256。

安装器生成安装目录内的 `Uninstall.exe`，创建开始菜单中的“GameLibrary”和“卸载 GameLibrary”快捷方式，并在当前用户 HKCU 卸载项登记，因而显示在 Windows“已安装的应用”中。卸载只删除已知程序文件、快捷方式和该卸载项，保留默认 `%LOCALAPPDATA%\\GameLibrary` 数据及安装目录中的未知文件。安装器不修改环境变量、不安装 Windows 服务。

## 本次执行结果

""" + "\n".join(f"- [{check['status']}] {check['name']} — {check['evidence']}"
                 for check in CHECK_RESULTS) + f"""

## 仍需人工验证

- 在无 .NET 的干净 Windows 10/11 x64 机器双击安装器和 Desktop。
- Windows SmartScreen 提示与企业策略兼容性。
- 真实游戏的扫描、审核与启动；字体、DPI 和键盘操作。
- {signing}

未完成关键人工验收时，这些产物仅为待验收构建，不得标记稳定 Release。
"""
    with open(os.path.join(DIST, "RELEASE-CHECKLIST.md"), "w", encoding="utf-8", newline="\n") as target:
        target.write(body)
    report = {"version": version, "sourceCommit": SOURCE_COMMIT, "sourceDirty": SOURCE_DIRTY,
              "builtAt": datetime.now(timezone.utc).isoformat(), "signed": signed,
              "checks": CHECK_RESULTS, "manualAcceptance": "not-run", "stableReady": False}
    Path(DIST, "release-validation.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    log.append("release checklist written")


def make_portable_zip(version, log):
    path = os.path.join(DIST, f"GameLibrary-Portable-win-x64-v{version}.zip")
    if os.path.exists(path):
        os.remove(path)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, _, files in os.walk(USER_PAYLOAD):
            for name in files:
                full = os.path.join(root, name)
                archive.write(full, os.path.relpath(full, USER_PAYLOAD))
    validate_zip_layout(
        path,
        {"GameLibrary.Desktop.exe", "GameLibrary.TauriBridge.exe", "GameLibrary.Host.exe", "SHA256SUMS.txt"},
        log,
    )
    log.append(f"portable zip: {os.path.getsize(path) / 1048576:.1f} MiB")
    return path


def make_tools_zip(version, log):
    path = os.path.join(DIST, f"GameLibrary-Tools-win-x64-v{version}.zip")
    if os.path.exists(path):
        os.remove(path)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, _, files in os.walk(TOOLS_PAYLOAD):
            for name in files:
                full = os.path.join(root, name)
                archive.write(full, os.path.relpath(full, TOOLS_PAYLOAD))
    validate_zip_layout(
        path,
        {"GameLibrary.Host.exe", "gamelibrary.exe", "GameLibrary.Mcp.exe", "SHA256SUMS.txt"},
        log,
    )
    log.append(f"tools zip: {os.path.getsize(path) / 1048576:.1f} MiB")
    return path


def validate_zip_layout(path, required, log):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = {entry.filename.replace("\\", "/") for entry in entries if not entry.is_dir()}
        if len(entries) != len(names):
            raise RuntimeError("ZIP contains duplicate names or directory entries")
    missing = sorted(required - names)
    scripts = sorted(name for name in names if name.lower().endswith((".ps1", ".cmd", ".bat")))
    unexpected = sorted(names - required)
    if missing:
        raise RuntimeError(f"ZIP is missing required entries: {missing}")
    if scripts:
        raise RuntimeError(f"ZIP unexpectedly contains scripts: {scripts}")
    if unexpected:
        raise RuntimeError(f"ZIP unexpectedly contains extra entries: {unexpected}")
    log.append(f"zip layout {os.path.basename(path)}: files={len(names)}, scripts=0, directories=0")


def write_asset_manifest(version, assets, log):
    path = os.path.join(DIST, f"GameLibrary-v{version}-SHA256SUMS.txt")
    with open(path, "w", encoding="utf-8", newline="\n") as target:
        for asset in assets:
            target.write(f"{sha256(asset)}  {os.path.basename(asset)}\n")
    log.append(f"asset manifest: {path}")
    return path


def parse_args():
    parser = argparse.ArgumentParser(description="Build GameLibrary Windows release assets")
    parser.add_argument(
        "--require-signing",
        action="store_true",
        help="fail unless a trusted Authenticode signing identity is configured",
    )
    parser.add_argument("--check", action="store_true", help="check source versions without building")
    parser.add_argument("--dist", type=Path, help="isolated output directory; preserves earlier release assets")
    parser.add_argument("--makensis", help="explicit path to NSIS makensis.exe")
    parser.add_argument("--skip-desktop-smoke-reason",
                        help="produce an acceptance candidate without native smoke; records not-run and reason")
    parser.add_argument("--build-only-reason",
                        help="compile/package only; skip verification and record an explicit acceptance limitation")
    return parser.parse_args()


def main():
    global SOURCE_COMMIT, SOURCE_DIRTY, DIST, STAGING_ROOT, USER_PAYLOAD, TOOLS_PAYLOAD, SYMBOLS, COMPONENT_OUTPUTS
    args = parse_args()
    if args.dist is not None:
        DIST = str(args.dist.resolve())
        STAGING_ROOT = os.path.join(DIST, "staging", uuid.uuid4().hex)
        USER_PAYLOAD = os.path.join(STAGING_ROOT, "GameLibrary")
        TOOLS_PAYLOAD = os.path.join(STAGING_ROOT, "GameLibrary-Tools")
        SYMBOLS = os.path.join(STAGING_ROOT, "symbols")
        COMPONENT_OUTPUTS = os.path.join(STAGING_ROOT, "component-publish")
    version = read_version()
    assert_version_sync(version)
    if args.check:
        print(f"Version declarations agree: {version}")
        return
    SOURCE_COMMIT = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=WORKSPACE, text=True).strip()
    SOURCE_DIRTY = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=WORKSPACE, text=True).strip())
    os.makedirs(DIST, exist_ok=True)
    log = [f"version={version}"]
    signed = False
    try:
        project = os.path.join(WORKSPACE, "src", "GameLibrary.Tauri")
        checks = [
            ("operation generation", [sys.executable, "scripts/generate_operations.py", "--check"], WORKSPACE),
            ("localization", [sys.executable, "scripts/check_localization.py"], WORKSPACE),
            ("release documentation", [sys.executable, "scripts/check_release.py", "--self-test"], WORKSPACE),
            ("dotnet format", [DOTNET, "format", "--verify-no-changes"], WORKSPACE),
            ("dotnet Release build", [DOTNET, "build", "-c", "Release", "--nologo"], WORKSPACE),
            ("dotnet full tests", [DOTNET, "test", "-c", "Release", "--no-build", "--nologo"], WORKSPACE),
            ("frontend tests", [*npm_command(), "test"], project),
            ("frontend typecheck", [*npm_command(), "run", "typecheck"], project),
            ("cargo fmt", ["cargo", "fmt", "--check"], os.path.join(project, "src-tauri")),
        ]
        for name, command, directory in checks:
            if args.build_only_reason:
                CHECK_RESULTS.append({"name": name, "status": "not-run", "exitCode": None,
                                      "evidence": args.build_only_reason})
                continue
            print(f"CHECK: {name}", flush=True)
            rc, output = sh(command, cwd=directory, label=name)
            if rc != 0:
                raise RuntimeError(f"{name} failed:\n{output[-4000:]}")
        signing = signing_configuration(args.require_signing, log)
        clean_staging(log)
        publish_all(log, args.build_only_reason or args.skip_desktop_smoke_reason)
        separate_pdbs(log)
        sign_payload(signing, log)
        write_payload_checksums(USER_PAYLOAD, "user", log)
        write_payload_checksums(TOOLS_PAYLOAD, "tools", log)
        installer = make_installer(version, log, args.makensis)
        if signing is not None:
            sign_file(installer, signing, log)
            signed = True
        portable = make_portable_zip(version, log)
        tools = make_tools_zip(version, log)
        manifest = write_asset_manifest(version, [installer, portable, tools], log)
        if args.build_only_reason:
            CHECK_RESULTS.append({"name": "final asset integrity", "status": "not-run", "exitCode": None,
                                  "evidence": args.build_only_reason})
        else:
            rc, output = sh([sys.executable, "scripts/check_release.py", "--dist", DIST],
                            label="final asset integrity")
            if rc != 0:
                raise RuntimeError(output)
        log.append(f"portable sha256={sha256(portable)}")
        log.append(f"tools sha256={sha256(tools)}")
        log.append(f"installer sha256={sha256(installer)}")
        log.append(f"manifest sha256={sha256(manifest)}")
    finally:
        write_checklist(version, signed, log)
        with open(os.path.join(DIST, "package-log.txt"), "w", encoding="utf-8", newline="\n") as target:
            target.write("\n".join(log) + "\n")
    print("PACKAGED")
    print("\n".join(log))


if __name__ == "__main__":
    main()
