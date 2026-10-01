<p align="center">
  <img src="assets/readme/hero.svg" width="960" alt="GameLibrary — Windows game library">
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.en.md">English</a> · <a href="README.ja.md">日本語</a>
</p>

# GameLibrary

把散落在硬碟裡的遊戲放到同一處。選好資料夾、確認啟動項目，就能從遊戲庫開啟遊戲。

**[下載 Windows 安裝版](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe)** · [所有版本](https://github.com/sumingwang233/GameLibrary/releases) · [回報問題](https://github.com/sumingwang233/GameLibrary/issues)

[下載](#download) · [畫面](#demo) · [開始使用](#start) · [常見問題](#faq)

<a id="download"></a>
## 下載

目前公開穩定版為 **v1.5.5**，支援 **Windows 10 / 11 x64**。這一版的桌面介面為簡體中文；README 的翻譯不代表該安裝包已提供其他介面語言。

| 檔案 | 用途 |
|---|---|
| [`GameLibrary-Setup-v1.5.5.exe`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Setup-v1.5.5.exe) | 建議使用。可選擇安裝位置的目前使用者安裝程式。 |
| [`GameLibrary-Portable-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Portable-win-x64-v1.5.5.zip) | 解壓縮後執行 GameLibrary.Desktop.exe。 |
| [`GameLibrary-Tools-win-x64-v1.5.5.zip`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-Tools-win-x64-v1.5.5.zip) | Host、CLI 與 MCP，供命令列與自動化使用。 |
| [`GameLibrary-v1.5.5-SHA256SUMS.txt`](https://github.com/sumingwang233/GameLibrary/releases/download/v1.5.5/GameLibrary-v1.5.5-SHA256SUMS.txt) | 上述三個下載包的 SHA-256 校驗清單。 |

程式與安裝程式尚未進行 Authenticode 簽章，Windows 可能提示未知發行者。請從本儲存庫下載，並比對同版本的校驗清單：

```powershell
Get-FileHash .\GameLibrary-Setup-v1.5.5.exe -Algorithm SHA256
```

<a id="demo"></a>
## 看看畫面

![遊戲庫：封面、搜尋、收藏與導覽](website/public/assets/library.png)

![標籤管理：整理遊戲的分類與標籤](website/public/assets/tags.png)

畫面取自 v1.5.5 的隔離範例遊戲庫，使用應用程式預設封面，不含個人遊戲目錄。完整操作影片尚未製作；[錄影腳本](docs/demo-recording.md)已備妥。

<a id="start"></a>
## 三步開始

1. 安裝後開啟 GameLibrary，加入存放遊戲的資料夾。
2. 掃描完成後檢查待審核的遊戲名稱、目錄與啟動檔案，再確認加入遊戲庫。
3. 在遊戲庫選取遊戲並啟動。視需要補上封面、收藏與標籤。

掃描產生的是候選項目，不會把每個 EXE 都當作遊戲。未辨識的遊戲也能手動加入。

## 日常使用

| 事情 | 操作 |
|---|---|
| 找遊戲 | 搜尋名稱、依標籤篩選，將常玩的遊戲加入收藏。 |
| 整理收藏 | 編輯封面與標籤，切換封面網格或清單。 |
| 設定啟動 | 保留原始 EXE，視需要設定參數、工具或翻譯步驟。 |
| 使用腳本 | CLI 與 MCP 連接同一個本機 Host，使用同一份遊戲庫。 |

<a id="faq"></a>
## 資料與常見問題

<details>
<summary><strong>掃描或移出遊戲庫會刪除遊戲檔案嗎？</strong></summary>

加入、掃描與移出紀錄會保留原始檔案。明確選擇清理檔案時會另外確認，並使用 Windows 資源回收筒；確認前請檢查目錄。
</details>

<details>
<summary><strong>遊戲庫存在哪裡？解除安裝會清空嗎？</strong></summary>

預設資料目錄為 `%LOCALAPPDATA%\GameLibrary`，紀錄存於本機 SQLite。解除安裝會保留遊戲庫資料與原始遊戲檔案。搬移前請備份資料；程式目錄不是遊戲庫備份。
</details>

<details>
<summary><strong>需要網路、帳號或開發工具嗎？</strong></summary>

整理本機遊戲不需要帳號，也不會上傳遊戲庫遙測資料。檢查更新會存取 GitHub Release；啟動的遊戲或自行設定的工具可能連網。發布包包含 .NET 執行階段，使用者不必安裝 .NET SDK、Node.js 或 Rust。
</details>

<details>
<summary><strong>三種包有何差異？如何更新？</strong></summary>

安裝版提供精靈、開始功能表入口與解除安裝登記；可攜版解壓縮即可執行；工具包用於 CLI 與 MCP。更新前先退出程式，再安裝新版或替換可攜版的程式檔案，保留資料目錄。變更與限制請看該版本 Release 說明。
</details>

<details>
<summary><strong>目前原始碼：v1.7.3，待驗收預發布</strong></summary>

v1.7.3 修復舊資源 SWF 候選未按目錄歸併，造成待確認數量過大的問題。首次啟動立即核對受影響的掃描根，以完整目錄組取代舊逐檔候選；保留原遊戲檔案、ID 與中繼資料。維持 schema27，不再次重設忽略規則。保留 v1.7.2 的表紙貼上、Flash 審核、標籤顏色及本機 Unity 翻譯配置。參閱[驗證記錄](docs/releases/v1.7.3-validation.md)。

v1.6.1 新增建議啟動方式：優先中文入口、試執行驗證後自動設為預設、明確失敗自動廢棄並支援手動恢復；同步調整卡片時長、主題快捷切換和 Windows 工作列圖示。上方 v1.5.5 穩定下載包不含這些變更。[v1.6.1 說明](docs/releases/v1.6.1.md)記錄內容與驗收狀態。

v1.7.0 新增[遊戲名稱翻譯](docs/title-translation.md)：Google/Bing 免費免金鑰翻譯、批次進度與取消、中文譯名與原文切換；並補修兩種版面標題下的遊玩時長。體驗這些功能請使用 [v1.7.0 預發布包](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.0)，[驗證記錄](docs/releases/v1.7.0-validation.md)列明尚未完成的人工驗收。上方穩定版下載維持 v1.5.5。

v1.7.1 修正翻譯後的副標題標點：原文含 `-` 時，將引擎傳回的連續長破折號收為 `：` 並移除首尾包圍符。舊譯名可在詳情重新翻譯；原文與手動譯名保留。[v1.7.1 預發布包](https://github.com/sumingwang233/GameLibrary/releases/tag/v1.7.1)與[驗證記錄](docs/releases/v1.7.1-validation.md)提供本次修正與驗收狀態。
</details>

<details>
<summary><strong>開發與架構</strong></summary>

開發需要 Windows、`global.json` 鎖定的 .NET SDK 10.0.401、Node.js 22.12+ 與 Rust stable，另需 Windows MSVC Rust 工具鏈及對應 C++ 建置工具。

```powershell
dotnet format --verify-no-changes
dotnet build GameLibrary.slnx -c Release
dotnet test GameLibrary.slnx -c Release --no-build
npm --prefix src/GameLibrary.Tauri ci
npm --prefix src/GameLibrary.Tauri test
npm --prefix src/GameLibrary.Tauri run typecheck
npm --prefix src/GameLibrary.Tauri run tauri dev
```

主介面使用 Tauri + React，WPF 保留為遷移期備援。桌面端、CLI、MCP 依共用契約連接 .NET Host；Application 處理使用案例、Domain 處理業務規則，Infrastructure 處理 SQLite、檔案與系統整合。[架構](docs/code-review-graph/architecture.md) · [操作目錄](contracts/operations.v1.json) · [發布原則](docs/release-policy.md)

只有維護者明確要求時才打包，一般程式修改不會自動發布。
</details>

## 參與與授權

請在 [Issues](https://github.com/sumingwang233/GameLibrary/issues) 附上版本、錯誤與重現步驟，截圖先遮蔽個人路徑。提交程式碼前執行相關檢查；共用操作的契約、用戶端與測試須同步更新。

[MIT License](LICENSE)
