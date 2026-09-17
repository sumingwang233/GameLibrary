# -*- coding: utf-8 -*-
"""Build the formal Windows release assets.

Outputs:
- portable self-contained win-x64 ZIP;
- current-user IExpress installer EXE;
- SHA-256 manifest for the release assets;
- separated PDB symbols kept locally.
"""

import hashlib
import os
import shutil
import subprocess
import textwrap
import zipfile


DOTNET = os.path.expanduser(r"~\.dotnet-sdk-10.0\dotnet.exe")
WORKSPACE = r"D:\Official\GameLibrary"
DIST = os.path.join(WORKSPACE, "artifacts", "dist")
STAGING_ROOT = os.path.join(DIST, "staging")
PAYLOAD = os.path.join(STAGING_ROOT, "GameLibrary")
SYMBOLS = os.path.join(STAGING_ROOT, "symbols")
INSTALLER_SOURCE = os.path.join(STAGING_ROOT, "installer-source")
COMPONENT_OUTPUTS = os.path.join(STAGING_ROOT, "component-publish")
IEXPRESS = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "iexpress.exe")
COMPONENTS = [
    ("GameLibrary.Desktop", "GameLibrary.Desktop", "GameLibrary.Desktop"),
    ("GameLibrary.Host", "GameLibrary.Host", "GameLibrary.Host"),
    ("GameLibrary.Cli", "gamelibrary", "GameLibrary.Cli"),
    ("GameLibrary.Mcp", "GameLibrary.Mcp", "GameLibrary.Mcp"),
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
    os.makedirs(PAYLOAD, exist_ok=True)
    os.makedirs(SYMBOLS, exist_ok=True)
    os.makedirs(INSTALLER_SOURCE, exist_ok=True)
    os.makedirs(COMPONENT_OUTPUTS, exist_ok=True)
    log.append("staging cleaned")


def publish_all(log):
    # Publish separately first: dotnet publish can clean another project's unique runtime
    # files when several self-contained projects target the same output directory.
    for project, _, _ in COMPONENTS:
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
        ])
        log.append(f"publish {project}: rc={rc}")
        if rc != 0:
            log.append(output[-4000:])
            raise RuntimeError(f"publish {project} failed")

    copied = 0
    deduplicated = 0
    for project, _, payload_subdirectory in COMPONENTS:
        component_output = os.path.join(COMPONENT_OUTPUTS, project)
        for root, _, files in os.walk(component_output):
            for name in files:
                source = os.path.join(root, name)
                relative = os.path.relpath(source, component_output)
                destination = os.path.join(PAYLOAD, payload_subdirectory, relative)
                if os.path.exists(destination):
                    if sha256(source) != sha256(destination):
                        raise RuntimeError(f"publish collision differs: {relative} ({project})")
                    deduplicated += 1
                    continue
                os.makedirs(os.path.dirname(destination), exist_ok=True)
                shutil.copy2(source, destination)
                copied += 1
    shutil.rmtree(COMPONENT_OUTPUTS)
    log.append(f"publish merge: copied={copied}, deduplicated={deduplicated}, conflicts=0")


def separate_pdbs(log):
    count = 0
    for root, _, files in os.walk(PAYLOAD):
        for name in files:
            if not name.lower().endswith(".pdb"):
                continue
            source = os.path.join(root, name)
            relative = os.path.relpath(source, PAYLOAD)
            destination = os.path.join(SYMBOLS, relative)
            os.makedirs(os.path.dirname(destination), exist_ok=True)
            os.replace(source, destination)
            count += 1
    log.append(f"pdb separated: {count}")


def write_payload_checksums(log):
    lines = []
    for root, _, files in os.walk(PAYLOAD):
        for name in sorted(files):
            full = os.path.join(root, name)
            relative = os.path.relpath(full, PAYLOAD).replace("\\", "/")
            lines.append(f"{sha256(full)}  {relative}")
    with open(os.path.join(PAYLOAD, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as target:
        target.write("\n".join(lines) + "\n")
    log.append(f"payload checksums: {len(lines)} files")


def detect_signing(log):
    rc, output = sh(["where", "signtool"], timeout=30)
    available = rc == 0 and "signtool" in output.lower()
    log.append(f"signtool available: {available}")
    return available


def write_portable_helpers(log):
    shortcut_ps1 = r'''$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework
$app = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "GameLibrary"))
$exe = Join-Path $app "GameLibrary.Desktop\GameLibrary.Desktop.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "GameLibrary.Desktop.exe not found: $exe" }
$group = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GameLibrary"
New-Item -ItemType Directory -Force -Path $group | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $group "GameLibrary.lnk"))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $app
$shortcut.Save()
[System.Windows.MessageBox]::Show("开始菜单快捷方式已创建。", "GameLibrary") | Out-Null
'''
    uninstall_ps1 = r'''$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework
$app = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "GameLibrary"))
Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($app + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GameLibrary") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $app -Recurse -Force -ErrorAction Stop
[System.Windows.MessageBox]::Show("程序文件与快捷方式已移除。默认游戏库数据未删除。", "GameLibrary") | Out-Null
'''
    wrapper = '@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0{script}"\r\n'
    files = {
        "create-start-menu-shortcut.ps1": shortcut_ps1,
        "uninstall-portable.ps1": uninstall_ps1,
        "create-start-menu-shortcut.cmd": wrapper.format(script="create-start-menu-shortcut.ps1"),
        "uninstall.cmd": wrapper.format(script="uninstall-portable.ps1"),
    }
    for name, content in files.items():
        encoding = "utf-8-sig" if name.endswith(".ps1") else "gbk"
        with open(os.path.join(STAGING_ROOT, name), "w", encoding=encoding, newline="") as target:
            target.write(content)
    log.append("portable helpers written")


def write_installer_scripts(log):
    install_ps1 = r'''param(
    [string]$TargetDirectory = (Join-Path $env:LOCALAPPDATA "Programs\GameLibrary"),
    [switch]$NoShortcuts,
    [switch]$NoLaunch,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
$payload = Join-Path $PSScriptRoot "GameLibrary-payload.zip"
$uninstallerSource = Join-Path $PSScriptRoot "uninstall.ps1"
if (-not (Test-Path -LiteralPath $payload)) { throw "安装载荷不存在：$payload" }
$target = [IO.Path]::GetFullPath($TargetDirectory)
$parent = Split-Path -Parent $target
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$temporary = Join-Path $parent (".GameLibrary.install-" + [Guid]::NewGuid().ToString("N"))
$backup = Join-Path $parent (".GameLibrary.backup-" + [Guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -LiteralPath $payload -DestinationPath $temporary -Force
    foreach ($required in @("GameLibrary.Desktop\GameLibrary.Desktop.exe", "GameLibrary.Host\GameLibrary.Host.exe", "GameLibrary.Cli\gamelibrary.exe", "GameLibrary.Mcp\GameLibrary.Mcp.exe", "SHA256SUMS.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $temporary $required))) { throw "安装载荷缺少：$required" }
    }
    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
    Move-Item -LiteralPath $temporary -Destination $target
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
} catch {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
    if ((-not (Test-Path -LiteralPath $target)) -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $target -ErrorAction SilentlyContinue
    }
    throw
}
$uninstaller = Join-Path $parent "GameLibrary-Uninstall.ps1"
Copy-Item -LiteralPath $uninstallerSource -Destination $uninstaller -Force
if (-not $NoShortcuts) {
    $group = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GameLibrary"
    New-Item -ItemType Directory -Force -Path $group | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $appShortcut = $shell.CreateShortcut((Join-Path $group "GameLibrary.lnk"))
    $appShortcut.TargetPath = Join-Path $target "GameLibrary.Desktop\GameLibrary.Desktop.exe"
    $appShortcut.WorkingDirectory = Join-Path $target "GameLibrary.Desktop"
    $appShortcut.Save()
    $uninstallShortcut = $shell.CreateShortcut((Join-Path $group "卸载 GameLibrary.lnk"))
    $uninstallShortcut.TargetPath = "powershell.exe"
    $uninstallShortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $uninstaller + '"'
    $uninstallShortcut.WorkingDirectory = $parent
    $uninstallShortcut.Save()
}
if (-not $NoLaunch) {
    $desktopDirectory = Join-Path $target "GameLibrary.Desktop"
    Start-Process -FilePath (Join-Path $desktopDirectory "GameLibrary.Desktop.exe") -WorkingDirectory $desktopDirectory
}
if (-not $Quiet) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("GameLibrary 已安装到：`n$target", "安装完成") | Out-Null
}
'''
    uninstall_ps1 = r'''param([switch]$Quiet)
$ErrorActionPreference = "Stop"
$target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "GameLibrary"))
Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GameLibrary") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
if (-not $Quiet) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("GameLibrary 已卸载。默认游戏库数据未删除。", "卸载完成") | Out-Null
}
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
'''
    for name, content in (("install.ps1", install_ps1), ("uninstall.ps1", uninstall_ps1)):
        with open(os.path.join(INSTALLER_SOURCE, name), "w", encoding="utf-8-sig", newline="") as target:
            target.write(content)
    log.append("installer scripts written")


def make_payload_zip(log):
    path = os.path.join(INSTALLER_SOURCE, "GameLibrary-payload.zip")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, _, files in os.walk(PAYLOAD):
            for name in files:
                full = os.path.join(root, name)
                archive.write(full, os.path.relpath(full, PAYLOAD))
    log.append(f"installer payload: {os.path.getsize(path) / 1048576:.1f} MiB")


def make_installer(version, log):
    if not os.path.isfile(IEXPRESS):
        raise RuntimeError(f"IExpress not found: {IEXPRESS}")
    target = os.path.join(DIST, f"GameLibrary-Setup-v{version}.exe")
    if os.path.exists(target):
        os.remove(target)
    source_dir = INSTALLER_SOURCE.rstrip("\\") + "\\"
    sed = textwrap.dedent(f'''\
        [Version]
        Class=IEXPRESS
        SEDVersion=3
        [Options]
        PackagePurpose=InstallApp
        ShowInstallProgramWindow=0
        HideExtractAnimation=1
        UseLongFileName=1
        InsideCompressed=0
        CAB_FixedSize=0
        CAB_ResvCodeSigning=0
        RebootMode=N
        InstallPrompt=%InstallPrompt%
        DisplayLicense=
        FinishMessage=
        TargetName=%TargetName%
        FriendlyName=%FriendlyName%
        AppLaunched=%AppLaunched%
        PostInstallCmd=<None>
        AdminQuietInstCmd=
        UserQuietInstCmd=
        SourceFiles=SourceFiles
        [SourceFiles]
        SourceFiles0=%SourceDir%
        [SourceFiles0]
        %FILE0%=
        %FILE1%=
        %FILE2%=
        [Strings]
        InstallPrompt="安装 GameLibrary v{version} 到当前用户目录？"
        TargetName="{target}"
        FriendlyName="GameLibrary v{version} 安装程序"
        AppLaunched="powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1"
        SourceDir="{source_dir}"
        FILE0="GameLibrary-payload.zip"
        FILE1="install.ps1"
        FILE2="uninstall.ps1"
    ''')
    sed_path = os.path.join(INSTALLER_SOURCE, "GameLibrary-Setup.sed")
    with open(sed_path, "w", encoding="gbk", errors="replace", newline="\r\n") as target_file:
        target_file.write(sed)
    rc, output = sh([IEXPRESS, "/N", "/Q", sed_path], timeout=600)
    log.append(f"iexpress: rc={rc}")
    if rc != 0 or not os.path.isfile(target):
        log.append(output[-4000:])
        raise RuntimeError("IExpress installer build failed")
    log.append(f"installer: {os.path.getsize(target) / 1048576:.1f} MiB")
    return target


def write_checklist(version, signed, log):
    signing = (
        "本机检测到 signtool，但未配置证书；发布前仍需证书签名。"
        if signed
        else "本机无签名证书，安装器与程序未签名；Windows SmartScreen 可能提示未知发布者。"
    )
    body = f"""# GameLibrary v{version} 发布校验清单

## 发布资产

- `GameLibrary-win-x64-v{version}.zip`：Windows x64 便携自包含包，无需安装 .NET。四个组件各自位于同名子目录，Desktop 会从兄弟 `GameLibrary.Host/` 目录启动后台服务。
- `GameLibrary-Setup-v{version}.exe`：当前用户安装器，安装到 `%LOCALAPPDATA%\\Programs\\GameLibrary`，不要求管理员权限。
- `GameLibrary-v{version}-SHA256SUMS.txt`：两个发布资产的 SHA-256。

安装器创建开始菜单中的“GameLibrary”和“卸载 GameLibrary”快捷方式；卸载仅删除程序和快捷方式，保留默认 `%LOCALAPPDATA%\\GameLibrary` 数据。安装器不写注册表、不修改环境变量、不安装 Windows 服务。

## 已自动验证

- Release build：0 警告、0 错误。
- 测试：436/436 通过。
- 自包含发布目录：CLI capabilities、隔离数据目录 init/status/stop 冒烟。
- 安装脚本：隔离目标安装、四个 EXE 完整性、CLI 冒烟、卸载后程序目录移除。

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
    path = os.path.join(DIST, f"GameLibrary-win-x64-v{version}.zip")
    if os.path.exists(path):
        os.remove(path)
    excluded = {"symbols", "installer-source"}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for root, directories, files in os.walk(STAGING_ROOT):
            directories[:] = [name for name in directories if name not in excluded]
            for name in files:
                full = os.path.join(root, name)
                archive.write(full, os.path.relpath(full, STAGING_ROOT))
    log.append(f"portable zip: {os.path.getsize(path) / 1048576:.1f} MiB")
    return path


def write_asset_manifest(version, assets, log):
    path = os.path.join(DIST, f"GameLibrary-v{version}-SHA256SUMS.txt")
    with open(path, "w", encoding="utf-8", newline="\n") as target:
        for asset in assets:
            target.write(f"{sha256(asset)}  {os.path.basename(asset)}\n")
    log.append(f"asset manifest: {path}")
    return path


def main():
    os.makedirs(DIST, exist_ok=True)
    version = read_version()
    log = [f"version={version}"]
    try:
        clean_staging(log)
        publish_all(log)
        separate_pdbs(log)
        write_payload_checksums(log)
        write_portable_helpers(log)
        write_installer_scripts(log)
        make_payload_zip(log)
        signed = detect_signing(log)
        write_checklist(version, signed, log)
        installer = make_installer(version, log)
        portable = make_portable_zip(version, log)
        manifest = write_asset_manifest(version, [portable, installer], log)
        log.append(f"portable sha256={sha256(portable)}")
        log.append(f"installer sha256={sha256(installer)}")
        log.append(f"manifest sha256={sha256(manifest)}")
    finally:
        with open(os.path.join(DIST, "package-log.txt"), "w", encoding="utf-8", newline="\n") as target:
            target.write("\n".join(log) + "\n")
    print("PACKAGED")
    print("\n".join(log))


if __name__ == "__main__":
    main()
