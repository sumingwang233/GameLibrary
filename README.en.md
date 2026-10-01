<p align="center">
  <img src="assets/readme/hero.svg" width="960" alt="GameLibrary — Windows game library">
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.en.md">English</a> · <a href="README.ja.md">日本語</a>
</p>

# GameLibrary

Keep games scattered across your drives in one library. Choose a folder, check the launch entry, and open the game from your collection.

**[Download the Windows installer](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe)** · [All releases](https://github.com/sumingwang233/GameLibrary/releases) · [Report an issue](https://github.com/sumingwang233/GameLibrary/issues)

[Download](#download) · [Screenshots](#demo) · [Get started](#start) · [FAQ](#faq)

<a id="download"></a>
## Download

The public stable release is **v1.5.5**, for **Windows 10 / 11 x64**. Its desktop interface is in Simplified Chinese. Translated READMEs do not imply that this installer includes translated interfaces.

| File | Use |
|---|---|
| [`GameLibrary-Setup-v1.5.5.exe`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe) | Recommended. Current-user installer with a choice of installation folder. |
| [`GameLibrary-Portable-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Portable-win-x64-v1.5.5.zip) | Extract and run GameLibrary.Desktop.exe. |
| [`GameLibrary-Tools-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Tools-win-x64-v1.5.5.zip) | Host, CLI and MCP for command-line use and automation. |
| [`GameLibrary-v1.5.5-SHA256SUMS.txt`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-v1.5.5-SHA256SUMS.txt) | SHA-256 checksums for the three packages above. |

The executables and installer are not Authenticode-signed. Windows may show an unknown-publisher warning. Download from this repository and compare the hash with the same-version checksum file:

```powershell
Get-FileHash .\GameLibrary-Setup-v1.5.5.exe -Algorithm SHA256
```

<a id="demo"></a>
## Screenshots

![Library with covers, search, favorites and navigation](website/public/assets/library.png)

![Tag management for organizing the collection](website/public/assets/tags.png)

Captured from v1.5.5 in an isolated sample library, with the application's default covers and no personal game directories. A full recording has not been produced yet; the [recording brief](docs/demo-recording.md) is ready.

<a id="start"></a>
## Get started

1. Open GameLibrary and add a folder containing your games.
2. After scanning, review the proposed names, folders and launch files before accepting them.
3. Select a game in the library and launch it. Add covers, favorites and tags as needed.

Scanning creates candidates; it does not treat every EXE as a game. You can add an unrecognized game manually.

## Everyday use

| Task | How |
|---|---|
| Find a game | Search by name, filter by tags, or mark favorites. |
| Organize the collection | Edit covers and tags; switch between the cover grid and list. |
| Configure launching | Keep the original EXE and set arguments, tools or translation steps as needed. |
| Use scripts | CLI and MCP connect to the same local Host and library. |

<a id="faq"></a>
## Data and FAQ

<details>
<summary><strong>Does scanning or removing a record delete game files?</strong></summary>

Adding, scanning and removing records preserve the original files. Explicit file cleanup asks for confirmation and uses the Windows Recycle Bin. Check the directory before confirming.
</details>

<details>
<summary><strong>Where is my library? Does uninstalling remove it?</strong></summary>

The default data folder is `%LOCALAPPDATA%\GameLibrary`. Records are stored in local SQLite. Uninstalling preserves library data and original game files. Back up your data before moving it; the application folder is not a library backup.
</details>

<details>
<summary><strong>Do I need an account, network access or developer tools?</strong></summary>

Organizing local games requires no account and sends no library telemetry. Update checks access GitHub Releases; launched games and configured tools may use the network. Packages include the .NET runtime, so users need no .NET SDK, Node.js or Rust.
</details>

<details>
<summary><strong>Which package should I use, and how do I update?</strong></summary>

The installer provides a wizard, Start menu entries and uninstallation registration. The portable package runs after extraction; the tools package is for CLI and MCP. Quit before updating, install the new version or replace the portable application files, and keep your data folder. Read that version's Release notes for changes and limitations.
</details>

<details>
<summary><strong>Current source: v1.7.1, acceptance pre-release</strong></summary>

v1.6.1 adds suggested launch profiles, Chinese entry preference, automatic default promotion after trial verification, and manual recovery of clearly failed entries. It also updates card playtime, the titlebar theme shortcut and Windows taskbar icon. These changes are not in the stable v1.5.5 downloads above. See the [v1.6.1 notes](docs/releases/v1.6.1.md) for changes and acceptance status.

v1.7.0 adds [game title translation](docs/title-translation.md) with free Google/Bing engines requiring no key, batch progress and cancellation, and persistent original/Chinese title switching. It also fixes playtime below titles in both layouts. Try these features with the [v1.7.0 pre-release packages](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.0); the [validation record](docs/releases/v1.7.0-validation.md) lists pending manual acceptance. Stable downloads above remain at v1.5.5.

v1.7.1 fixes subtitle punctuation in translations: when the original contains `-`, repeated em dashes become `：` and surrounding dashes are removed. Retranslate existing titles in details to apply the fix; original and manually edited titles are preserved. See the [v1.7.1 pre-release packages](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.1) and [validation record](docs/releases/v1.7.1-validation.md).
</details>

<details>
<summary><strong>Development and architecture</strong></summary>

Use Windows, .NET SDK 10.0.401 pinned by `global.json`, Node.js 22.12+ and Rust stable. Install the Windows MSVC Rust toolchain and corresponding C++ build tools.

```powershell
dotnet format --verify-no-changes
dotnet build GameLibrary.slnx -c Release
dotnet test GameLibrary.slnx -c Release --no-build
npm --prefix src/GameLibrary.Tauri ci
npm --prefix src/GameLibrary.Tauri test
npm --prefix src/GameLibrary.Tauri run typecheck
npm --prefix src/GameLibrary.Tauri run tauri dev
```

Tauri + React is the main desktop UI; WPF remains a migration fallback. Desktop, CLI and MCP use a shared contract to reach the .NET Host. Application owns use cases, Domain owns business rules, and Infrastructure owns SQLite, files and OS integration. [Architecture](docs/code-review-graph/architecture.md) · [Operation catalog](contracts/operations.v1.json) · [Release policy](docs/release-policy.md)

Packaging runs only when explicitly requested by the maintainer; ordinary code changes do not publish a release.
</details>

## Contributing and license

Report the version, error and reproduction steps in [Issues](https://github.com/sumingwang233/GameLibrary/issues); redact personal paths from screenshots. Run relevant checks before submitting code. Shared-operation changes must keep contracts, clients and tests in sync.

[MIT License](LICENSE)
