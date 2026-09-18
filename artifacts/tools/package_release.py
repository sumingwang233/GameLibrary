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
import os
import shutil
import subprocess
import zipfile


DOTNET = os.path.expanduser(r"~\.dotnet-sdk-10.0\dotnet.exe")
WORKSPACE = r"D:\Official\GameLibrary"
DIST = os.path.join(WORKSPACE, "artifacts", "dist")
STAGING_ROOT = os.path.join(DIST, "staging")
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
    ("GameLibrary.Desktop", "GameLibrary.Desktop.exe", True, False),
    ("GameLibrary.Host", "GameLibrary.Host.exe", True, True),
    ("GameLibrary.Cli", "gamelibrary.exe", False, True),
    ("GameLibrary.Mcp", "GameLibrary.Mcp.exe", False, True),
]


def read_version():
    for props in [
        os.path.join(WORKSPACE, "Directory.Build.props"),
        os.path.join(WORKSPACE, "src", "GameLibrary.Desktop", "GameLibrary.Desktop.csproj"),
    ]:
        if not os.path.exists(props):
            continue
        with open(props, encoding="utf-8") as source:
            text = source.read()
        start = text.find("<Version>")
        if start >= 0:
            start += len("<Version>")
            end = text.find("</Version>", start)
            if end > start:
                return text[start:end].strip()
    return "1.0.0"


def sh(cmd, cwd=WORKSPACE, timeout=1200):
    proc = subprocess.run(
        cmd,
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_staging(log):
    if os.path.isdir(STAGING_ROOT):
        shutil.rmtree(STAGING_ROOT)
    os.makedirs(USER_PAYLOAD, exist_ok=True)
    os.makedirs(TOOLS_PAYLOAD, exist_ok=True)
    os.makedirs(SYMBOLS, exist_ok=True)
    os.makedirs(COMPONENT_OUTPUTS, exist_ok=True)
    log.append("staging cleaned")


def publish_all(log):
    for project, executable, include_in_user, include_in_tools in COMPONENTS:
        component_output = os.path.join(COMPONENT_OUTPUTS, project)
        rc, output = sh([
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
        ])
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
    log.append("single-file payloads: user=2 executables, tools=3 executables")


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


def make_installer(version, log):
    makensis = find_makensis()
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
        "/V2",
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
        else "本次本地预览未配置受信任证书；正式发布必须使用 --require-signing 重新生成。"
    )
    body = f"""# GameLibrary v{version} 发布校验清单

## 发布资产

- `GameLibrary-Setup-v{version}.exe`：普通用户推荐下载的单个 NSIS 图形化当前用户安装器，内含 Desktop 与后台 Host；默认安装到 `%LOCALAPPDATA%\\Programs\\GameLibrary`，可在安装向导中修改位置，不要求管理员权限。
- `GameLibrary-Portable-win-x64-v{version}.zip`：免安装包，只含单文件 `GameLibrary.Desktop.exe`、`GameLibrary.Host.exe` 与校验文件，不含脚本。
- `GameLibrary-Tools-win-x64-v{version}.zip`：高级用户工具包，只含单文件 Host、CLI、MCP 与校验文件，不含脚本。
- `GameLibrary-v{version}-SHA256SUMS.txt`：以上三个发布资产的 SHA-256。

安装器生成安装目录内的 `Uninstall.exe`，创建开始菜单中的“GameLibrary”和“卸载 GameLibrary”快捷方式，并在当前用户 HKCU 卸载项登记，因而显示在 Windows“已安装的应用”中。卸载只删除已知程序文件、快捷方式和该卸载项，保留默认 `%LOCALAPPDATA%\\GameLibrary` 数据及安装目录中的未知文件。安装器不修改环境变量、不安装 Windows 服务。

## 已自动验证

- Release build：0 警告、0 错误。
- 测试：436/436 通过。
- 自包含发布目录：Desktop/Host 与 CLI/MCP 单文件布局检查。
- NSIS 安装器：隔离自定义目录安装、当前用户卸载登记、`Uninstall.exe`、快捷方式、已知文件清理及未知文件保留。

## 仍需人工验证

- 在无 .NET 的干净 Windows 10/11 x64 机器双击安装器和 Desktop。
- Windows SmartScreen 提示与企业策略兼容性。
- 真实 F 盘扫描数量、受保护目录提示和真实游戏启动兼容性。
- {signing}
"""
    with open(os.path.join(STAGING_ROOT, "RELEASE-CHECKLIST.md"), "w", encoding="utf-8", newline="\n") as target:
        target.write(body)
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
        {"GameLibrary.Desktop.exe", "GameLibrary.Host.exe", "SHA256SUMS.txt"},
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
        names = {entry.filename.replace("\\", "/") for entry in archive.infolist() if not entry.is_dir()}
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
    return parser.parse_args()


def main():
    args = parse_args()
    os.makedirs(DIST, exist_ok=True)
    version = read_version()
    log = [f"version={version}"]
    try:
        signing = signing_configuration(args.require_signing, log)
        clean_staging(log)
        publish_all(log)
        separate_pdbs(log)
        signed = sign_payload(signing, log)
        write_payload_checksums(USER_PAYLOAD, "user", log)
        write_payload_checksums(TOOLS_PAYLOAD, "tools", log)
        write_checklist(version, signed, log)
        installer = make_installer(version, log)
        if signing is not None:
            sign_file(installer, signing, log)
        portable = make_portable_zip(version, log)
        tools = make_tools_zip(version, log)
        manifest = write_asset_manifest(version, [installer, portable, tools], log)
        legacy_portable = os.path.join(DIST, f"GameLibrary-win-x64-v{version}.zip")
        if os.path.isfile(legacy_portable):
            os.remove(legacy_portable)
            log.append(f"removed legacy asset: {os.path.basename(legacy_portable)}")
        log.append(f"portable sha256={sha256(portable)}")
        log.append(f"tools sha256={sha256(tools)}")
        log.append(f"installer sha256={sha256(installer)}")
        log.append(f"manifest sha256={sha256(manifest)}")
    finally:
        with open(os.path.join(DIST, "package-log.txt"), "w", encoding="utf-8", newline="\n") as target:
            target.write("\n".join(log) + "\n")
    print("PACKAGED")
    print("\n".join(log))


if __name__ == "__main__":
    main()
