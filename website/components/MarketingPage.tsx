import { asset, home, languages, languageNames, release, repository, stableVersion, translate, type Language } from "../lib/site";
import { Header, LocalTooltip, Screenshot } from "./GlassControls";


export default function MarketingPage({ language }: { language: Language }) {
  const t = translate(language);
  const installer = `GameLibrary-Setup-v${stableVersion}.exe`;
  const steps = [
    [t("添加自定义游戏目录", "新增自訂遊戲目錄", "Add your game folders", "ゲームフォルダーを追加"), t("选择存放游戏的文件夹，可以添加多个目录。应用只扫描你添加的位置。", "選擇存放遊戲的資料夾，可新增多個目錄。應用程式只掃描你新增的位置。", "Choose the folders containing your games. Add as many as you need; only those locations are scanned.", "ゲームを保存したフォルダーを追加。複数の場所を指定でき、指定した場所だけをスキャンします。")],
    [t("扫描可能的游戏入口", "掃描可能的遊戲入口", "Scan for possible launchers", "起動ファイルを検出"), t("识别常见引擎的目录结构，排除常见安装器、卸载器，生成待确认候选。", "辨識常見引擎的目錄結構，排除常見安裝程式、解除安裝程式，產生待確認候選。", "Recognize common engine layouts, filter installers and uninstallers, and find candidates to review.", "一般的なエンジンの構造を識別し、インストーラーなどを除外して確認用の候補を見つけます。")],
    [t("逐项核对，确认入库", "逐項核對，確認入庫", "Review before adding", "確認してライブラリへ"), t("在“待确认”中核对入口。认识的游戏加入库，其他项目可以稍后处理或忽略。", "在「待確認」中核對入口。認識的遊戲加入庫，其他項目可稍後處理或忽略。", "Check the launchers in the review list. Add games you recognize; defer or ignore the other results.", "候補の起動先を確認。知っているゲームを登録し、それ以外は後で確認するか無視できます。")],
  ];
  const questions = [
    [t("GameLibrary 会移动或修改我的游戏文件吗？", "GameLibrary 會移動或修改我的遊戲檔案嗎？", "Will GameLibrary move or change my game files?", "ゲームのファイルは移動・変更されますか？"), t("扫描和入库不会搬动游戏。常规移除只删除库记录；将游戏文件移入回收站需要你明确确认。", "掃描與入庫不會搬動遊戲。一般移除只刪除庫記錄；將遊戲檔案移入資源回收筒需由你明確確認。", "Scanning and adding games leave files in place. Regular removal deletes only the library record; moving game files to the Recycle Bin requires your explicit confirmation.", "スキャンや登録でファイルを移動しません。通常の削除は登録情報のみを削除し、ゲームをゴミ箱へ移すには明示的な確認が必要です。")],
    [t("哪些游戏适合放进来？", "哪些遊戲適合放進來？", "What kinds of games can I add?", "どんなゲームを登録できますか？"), t("解压即玩的独立游戏、老游戏、视觉小说、SWF 文件、Windows 快捷方式，以及多个硬盘中的本地游戏。游戏需自行获取，GameLibrary 不提供游戏下载。", "解壓縮即可玩的獨立遊戲、老遊戲、視覺小說、SWF 檔案、Windows 捷徑，以及多個硬碟中的本機遊戲。遊戲需自行取得，GameLibrary 不提供遊戲下載。", "Extract-and-play indie games, older titles, visual novels, SWF files, Windows shortcuts, and local games across multiple drives. Bring your own games; GameLibrary does not distribute them.", "解凍して遊べるゲーム、旧作、ビジュアルノベル、SWF、Windows のショートカットなどを登録できます。ゲームは別途用意してください。ゲームの配布は行いません。")],
    [t("会自动把所有 EXE 加进游戏库吗？", "會自動把所有 EXE 加進遊戲庫嗎？", "Is every EXE added automatically?", "すべての EXE が自動登録されますか？"), t("不会。扫描只生成可能的入口，候选由你审核后入库。也可以手动添加游戏。", "不會。掃描只產生可能的入口，候選由你審核後入庫。也可手動新增遊戲。", "No. Scanning produces possible launchers for you to review before adding them. You can also add games manually.", "いいえ。スキャンで見つかった候補を確認してから登録します。手動での追加も可能です。")],
    [t("为什么 SWF 游戏需要手动配置播放器？", "為什麼 SWF 遊戲需要手動設定播放器？", "Why do SWF games need a configured player?", "SWF 用のプレイヤー設定が必要なのはなぜですか？"), t("GameLibrary 使用 Windows 为 .swf 关联的播放器，未内置 Flash 播放器。请先配置可用的播放器，再从游戏库启动。", "GameLibrary 使用 Windows 為 .swf 關聯的播放器，未內建 Flash 播放器。請先設定可用的播放器，再從遊戲庫啟動。", "GameLibrary uses the player associated with .swf in Windows. It has no built-in Flash player, so configure a compatible player first.", "Windows で .swf に関連付けられたプレイヤーを使用します。Flash プレイヤーは内蔵していないため、事前に対応プレイヤーを設定してください。")],
    [t("需要账号或联网吗？", "需要帳號或連線嗎？", "Do I need an account or internet access?", "アカウントやネット接続は必要ですか？"), t("管理本地游戏库无需账号，离线也能使用。应用的更新检查会访问 GitHub；启动的游戏或辅助程序可能有自己的网络行为。", "管理本機遊戲庫無需帳號，離線也能使用。應用程式的更新檢查會存取 GitHub；啟動的遊戲或輔助程式可能有自己的網路行為。", "No account is required, and your local library works offline. Update checks access GitHub; games or companion programs you launch may make their own network requests.", "アカウント不要で、ライブラリはオフラインでも利用できます。更新確認は GitHub に接続します。起動したゲームや補助ツールは独自に通信する場合があります。")],
    [t("卸载后，游戏和收藏还在吗？", "解除安裝後，遊戲和收藏還在嗎？", "What happens when I uninstall?", "アンインストール後もゲームや登録情報は残りますか？"), t("卸载不会删除原始游戏文件，也不会自动删除游戏库数据。应用数据默认保存在 %LOCALAPPDATA%\\GameLibrary。", "解除安裝不會刪除原始遊戲檔案，也不會自動刪除遊戲庫資料。應用程式資料預設儲存在 %LOCALAPPDATA%\\GameLibrary。", "Uninstalling leaves your game files and library data in place. App data is stored in %LOCALAPPDATA%\\GameLibrary by default.", "元のゲームファイルやライブラリデータは自動削除されません。アプリのデータは標準で %LOCALAPPDATA%\\GameLibrary に保存されます。")],
  ];

  return <>
    <a className="skip-link" href="#main">{t("跳至正文", "跳至正文", "Skip to content", "本文へスキップ")}</a>
    <Header language={language} />
    <main id="main" tabIndex={-1}>
      <section className="hero container" aria-labelledby="hero-title">
        <div className="hero-copy">
          <p className="platform-label"><span className="status-dot" aria-hidden="true" />Windows 10 / 11 · x64 · v{stableVersion}</p>
          <h1 id="hero-title">{t("游戏放在各处，", "遊戲放在各處，", "Games on different drives.", "あちこちのゲームを、")}<span>{t("在这里一起管理。", "在這裡一起管理。", "One library to find them.", "ひとつのライブラリに。")}</span></h1>
          <p className="hero-description">{t("添加游戏目录，扫描后确认入库。用标签分类，从游戏库直接启动，原文件留在原处。", "新增遊戲目錄，掃描後確認入庫。用標籤分類，從遊戲庫直接啟動，原始檔案留在原處。", "Scan your game folders, review the results, and add them to your library. Organize with tags and launch from here. Your files stay where they are.", "ゲームフォルダーをスキャンし、確認して登録。タグで整理して、ここから起動できます。ファイルは元の場所に残ります。")}</p>
          <div className="hero-actions">
            <a className="button" href={release + installer}>{t("下载 Windows 版", "下載 Windows 版", "Download for Windows", "Windows 版をダウンロード")}<span aria-hidden="true">↓</span></a>
            <a className="button button-secondary" href="#app-preview">{t("看看实际界面", "看看實際介面", "See the app", "アプリの画面を見る")}<span aria-hidden="true">↗</span></a>
          </div>
          <div className="hero-facts"><LocalTooltip language={language} /><span aria-hidden="true">·</span><a href={repository + "/blob/main/LICENSE"}>MIT {t("开源", "開源", "open source", "オープンソース")} ↗</a></div>
        </div>
        <div className="product-preview" id="app-preview">
          <Screenshot name="library.png" priority language={language} caption={t("实际界面 · v1.5.5 · 简体中文 · 示例库", "實際介面 · v1.5.5 · 簡體中文 · 範例庫", "Actual app · v1.5.5 · Chinese UI · Sample library", "実際の画面 · v1.5.5 · 中国語 UI · サンプルライブラリ")} alt={t("GameLibrary 实机界面，包含游戏网格、搜索、收藏和标签筛选。", "GameLibrary 實機介面，包含遊戲網格、搜尋、收藏和標籤篩選。", "The actual GameLibrary app with a game grid, search, favorites and tag filters.", "実際の GameLibrary アプリ。ゲーム一覧、検索、お気に入り、タグの絞り込み。")} />
        </div>
        <div className="compatibility"><p>{t("可识别的游戏引擎", "可辨識的遊戲引擎", "Recognized game engines", "識別できるゲームエンジン")}</p><ul aria-label={t("游戏引擎", "遊戲引擎", "Game engines", "ゲームエンジン")}><li>Unity</li><li>RPG Maker MV / MZ</li><li>Ren’Py</li><li>Kirikiri</li><li>Flash</li></ul></div>
      </section>

      <section className="section" id="experience" aria-labelledby="workflow-title"><div className="container workflow">
        <div className="section-copy"><p className="eyebrow">{t("从目录开始", "從目錄開始", "Start with a folder", "フォルダーを追加")}</p><h2 id="workflow-title">{t("先扫描，", "先掃描，", "Scan first.", "スキャンして、")}<br />{t("再确认入库。", "再確認入庫。", "Then review.", "確認して登録。")}</h2><p className="section-intro">{t("只扫描你添加的目录。结果先进入“待确认”，核对启动文件后再加入游戏库。", "只掃描你新增的目錄。結果先進入「待確認」，核對啟動檔案後再加入遊戲庫。", "Only the folders you add are scanned. Results go to a review list so you can check each launcher before adding it.", "追加した場所だけをスキャンします。見つかった候補の起動ファイルを確認してから、ライブラリに登録します。")}</p></div>
        <ol className="steps">{steps.map(([title, text], index) => <li key={title}><span className="step-number" aria-hidden="true">0{index + 1}</span><div><h3>{title}</h3><p>{text}</p></div></li>)}</ol>
      </div></section>

      <section className="section" id="collection" aria-labelledby="collection-title"><div className="container collection">
        <div className="section-copy"><p className="eyebrow">{t("整理与查找", "整理與尋找", "Organize and find", "整理と検索")}</p><h2 id="collection-title">{t("记不住名字，", "記不住名字，", "Forgot the name?", "名前を忘れても、")}<br />{t("也能按标签找。", "也能依標籤找。", "Find it by tag.", "タグで探せます。")}</h2><p className="section-intro">{t("按标题、路径或标签搜索。游戏多了，也不用逐个打开文件夹找。", "依標題、路徑或標籤搜尋。遊戲多了，也不用逐一打開資料夾找。", "Search by title, path or tag, without opening each folder to find a game.", "タイトル、パス、タグで検索できます。フォルダーをひとつずつ開いて探す手間を減らせます。")}</p>
          <div className="feature-list">
            <article><h3>{t("标签自己定", "標籤自己訂", "Use your own tags", "自分で決めるタグ")}</h3><p>{t("按引擎、类型或游玩状态分类，一个游戏可以关联多个标签。", "依引擎、類型或遊玩狀態分類，一個遊戲可關聯多個標籤。", "Group games by engine, genre or play status. A game can have more than one tag.", "エンジン、ジャンル、プレイ状況で分類。ひとつのゲームに複数のタグを付けられます。")}</p></article>
            <article><h3>{t("常玩的放前面", "常玩的放前面", "Keep favorites handy", "よく遊ぶゲームを手近に")}</h3><p>{t("收藏常玩的游戏。给标签设置星级，常用分类就能排在前面。", "收藏常玩的遊戲。為標籤設定星級，常用分類就能排在前面。", "Favorite the games you play often. Set star ratings on tags to bring useful categories to the top.", "よく遊ぶゲームをお気に入りに登録。タグに星を付けて、よく使う分類を上に表示できます。")}</p></article>
          </div>
        </div>
        <Screenshot name="tags.png" language={language} caption={t("标签管理 · v1.5.5 · 简体中文 · 示例库", "標籤管理 · v1.5.5 · 簡體中文 · 範例庫", "Tag management · v1.5.5 · Chinese UI · Sample library", "タグ管理 · v1.5.5 · 中国語 UI · サンプルライブラリ")} alt={t("GameLibrary 真实标签管理界面，包含分类、关联数量和标签星级。", "GameLibrary 真實標籤管理介面，包含分類、關聯數量和標籤星級。", "The actual tag management screen with categories, game counts and tag star ratings.", "実際のタグ管理画面。分類、登録数、タグの星評価。")} />
      </div></section>

      <section className="section tinted" id="launch" aria-labelledby="launch-title"><div className="container launch">
        <div className="section-copy"><p className="eyebrow">{t("打开游戏", "打開遊戲", "Launch your games", "ゲームを起動")}</p><h2 id="launch-title">{t("选好启动文件，", "選好啟動檔案，", "Set the launcher.", "起動先を選んで、")}<br />{t("下次直接打开。", "下次直接打開。", "Use it next time.", "次回もそのまま起動。")}</h2><p className="section-intro">{t("启动程序和参数保存在游戏记录里，不需要每次重新设置。", "啟動程式與參數儲存在遊戲記錄裡，不需要每次重新設定。", "The launcher and its arguments are saved with the game, ready for the next time you play.", "起動プログラムと引数はゲームの登録情報に保存され、次回も同じ設定で起動できます。")}</p></div>
        <div className="feature-list launch-list">
          <article><p className="format-label">EXE / LNK</p><h3>{t("也可以手动添加", "也可以手動新增", "Add a game manually", "手動での追加にも対応")}</h3><p>{t("指定 EXE 或 Windows 快捷方式作为入口。游戏文件夹可以继续放在原来的硬盘。", "指定 EXE 或 Windows 捷徑作為入口。遊戲資料夾可繼續放在原來的硬碟。", "Choose an EXE or a Windows shortcut as the launcher. The game folder can stay on its current drive.", "EXE や Windows のショートカットを起動先に指定。ゲームフォルダーは元のドライブに置いたままで使えます。")}</p></article>
          <article><p className="format-label">SWF</p><h3>{t("用已配置的播放器启动", "用已設定的播放器啟動", "Use your configured player", "設定済みのプレイヤーで起動")}</h3><p>{t("Flash 游戏通过 Windows 为 .swf 关联的播放器打开。应用未内置 Flash 播放器，需要先自行配置。", "Flash 遊戲透過 Windows 為 .swf 關聯的播放器開啟。應用程式未內建 Flash 播放器，需先自行設定。", "Flash games open with the player associated with .swf in Windows. A Flash player is not included; configure one first.", "Flash ゲームは Windows で .swf に関連付けたプレイヤーで開きます。プレイヤーは内蔵していないため、事前に設定してください。")}</p></article>
        </div>
      </div></section>

      <section className="section" id="safety" aria-labelledby="safety-title"><div className="container privacy">
        <div className="section-copy"><p className="eyebrow">{t("数据与文件", "資料與檔案", "Data and files", "データとファイル")}</p><h2 id="safety-title">{t("游戏留在原处。", "遊戲留在原處。", "Files stay put.", "ファイルは元の場所に。")}<br />{t("记录保存在本机。", "記錄儲存在本機。", "Records stay local.", "登録情報はこの PC に。")}</h2><p className="section-intro">{t("管理游戏库无需注册账号，离线也能使用。", "管理遊戲庫無需註冊帳號，離線也能使用。", "Manage your library offline, without signing up for an account.", "アカウント登録なしで、オフラインでもゲームを管理できます。")}</p></div>
        <div className="feature-list">
          <article><h3>{t("不上传扫描结果", "不上傳掃描結果", "Scan results stay local", "スキャン結果は送信しません")}</h3><p>{t("目录、标签、设置和启动历史保存在本机数据库。更新检查会访问 GitHub，启动的游戏或辅助程序可能自行联网。", "目錄、標籤、設定與啟動歷史儲存在本機資料庫。更新檢查會存取 GitHub，啟動的遊戲或輔助程式可能自行連線。", "Folders, tags, settings and launch history are stored on this PC. Update checks access GitHub; games and companion tools may make their own network requests.", "フォルダー、タグ、設定、起動履歴はこの PC に保存。更新確認は GitHub に接続します。ゲームや補助ツールは独自に通信する場合があります。")}</p></article>
          <article><h3>{t("移除记录和删除文件分开", "移除記錄與刪除檔案分開", "Removing a record leaves the files", "登録の削除とファイル削除は別です")}</h3><p>{t("扫描、入库和常规移除记录不会搬动游戏。将游戏文件移入回收站，需要单独确认。", "掃描、入庫及一般移除記錄不會搬動遊戲。將遊戲檔案移入資源回收筒，需要另行確認。", "Scanning, adding games and regular record removal leave your files in place. Moving game files to the Recycle Bin requires separate confirmation.", "スキャン、登録、通常の登録削除ではゲームを移動しません。ファイルをゴミ箱へ移す操作には、別途確認が必要です。")}</p></article>
        </div>
      </div></section>

      <section className="section download" id="download" aria-labelledby="download-title"><div className="container">
        <div className="download-heading"><div><p className="eyebrow">{t("开始使用", "開始使用", "Get started", "使い始める")}</p><h2 id="download-title">{t("下载 GameLibrary", "下載 GameLibrary", "Download GameLibrary", "GameLibrary をダウンロード")}</h2></div><p className="section-intro">Windows 10 / 11 · x64<br />{t("安装版、便携版，按需要选择。", "安裝版、可攜版，依需要選擇。", "Choose the installer or portable version.", "インストーラー版とポータブル版を選べます。")}</p></div>
        <div className="download-grid">
          <article className="installer-card">
            <div className="package-heading"><img src={asset("icon.png")} width="56" height="56" loading="lazy" alt="" /><div><p className="release-label">v{stableVersion} · {t("正式版 · 未验收", "正式版 · 未驗收", "Official · Acceptance not run", "正式版 · 検証未実施")}</p><h3>{t("Windows 安装版", "Windows 安裝版", "Windows installer", "Windows インストーラー")}</h3></div></div>
            <p>{t("跟随安装向导即可使用，包含桌面程序和运行环境，无需另装 .NET。", "依照安裝精靈即可使用，包含桌面程式及執行環境，無需另裝 .NET。", "Follow the setup wizard. The desktop app and runtime are included, so no separate .NET installation is needed.", "セットアップの案内に従ってインストール。アプリと実行環境を含み、.NET の別途インストールは不要です。")}</p>
            <code className="filename">{installer}</code><a className="button" href={release + installer}>{t("下载安装版", "下載安裝版", "Download installer", "インストーラーをダウンロード")}<span aria-hidden="true">↓</span></a><p className="package-note">{t("已完成生产编译，未完成验收。", "已完成正式編譯，未完成驗收。", "Production build completed; acceptance not run.", "本番ビルド完了・検証未実施。")}</p>
          </article>
          <article className="other-packages"><h3>{t("其他下载", "其他下載", "Other downloads", "その他のダウンロード")}</h3>{[
            [t("便携版 · 解压运行", "可攜版 · 解壓縮執行", "Portable · Extract and run", "ポータブル版 · 解凍して実行"), `GameLibrary-Portable-win-x64-v${stableVersion}.zip`],
            [t("工具包 · CLI & MCP", "工具包 · CLI & MCP", "Tools · CLI & MCP", "ツール · CLI & MCP"), `GameLibrary-Tools-win-x64-v${stableVersion}.zip`],
            [t("SHA-256 校验清单", "SHA-256 校驗清單", "SHA-256 checksums", "SHA-256 チェックサム"), `GameLibrary-v${stableVersion}-SHA256SUMS.txt`],
          ].map(([label, filename]) => <a className="package-link" href={release + filename} key={filename}><span><strong>{label}</strong><small>{filename}</small></span><span aria-hidden="true">↓</span></a>)}
            <p className="download-note">{t("v1.7.5 的 Windows 程序未签名，请在下载后核对校验值。", "v1.7.5 的 Windows 程式未簽署，請在下載後核對校驗值。", "Windows executables in v1.7.5 are unsigned. Verify the checksums after downloading.", "v1.7.5 の Windows 実行ファイルは未署名です。ダウンロード後にチェックサムをご確認ください。")} <a href={repository + "/releases/latest"}>{t("版本说明", "版本說明", "Release notes", "リリースノート")} ↗</a></p>
          </article>
        </div>
        <div className="preview-note" id="preview"><p><strong>{t("关于此版本", "關於此版本", "About this release", "このバージョンについて")}</strong>{t("v1.7.5 经维护者明确豁免验收后正式发布，仅完成生产编译。测试、独立检查和人工验收均未执行。", "v1.7.5 經維護者明確豁免驗收後正式發布，僅完成正式編譯。測試、獨立檢查及人工驗收均未執行。", "v1.7.5 was officially released under an explicit maintainer waiver. Only production compilation was completed; tests, separate checks and manual acceptance were not run.", "v1.7.5 は管理者の明示的な検証免除により正式公開しました。本番ビルドのみ完了し、テスト、個別チェック、手動検証は未実施です。")}</p><a href={repository + "/releases/tag/v1.7.5"}>{t("查看发布说明", "查看發布說明", "Read release notes", "リリースノートを見る")} ↗</a></div>
      </div></section>

      <section className="section faq" id="questions" aria-labelledby="faq-title"><div className="container faq-layout">
        <div className="section-copy"><p className="eyebrow">FAQ</p><h2 id="faq-title">{t("下载前，", "下載前，", "Before", "ダウンロード前に")}<br />{t("你可能想知道。", "你可能想知道。", "you download.", "よくある質問。")}</h2></div>
        <div className="questions">{questions.map(([question, answer]) => <details key={question}><summary>{question}<span aria-hidden="true">+</span></summary><p>{answer}</p></details>)}</div>
      </div></section>
    </main>
    <footer className="site-footer"><div className="container">
      <div className="footer-top"><a className="brand" href={home(language)}><img src={asset("icon.png")} width="32" height="32" loading="lazy" alt="" />GameLibrary</a><nav aria-label={t("项目链接", "專案連結", "Project links", "プロジェクトリンク")}><a href={repository}>GitHub ↗</a><a href={repository + "/issues"}>{t("反馈问题", "回報問題", "Report an issue", "問題を報告")}</a><a href={repository + "/blob/main/LICENSE"}>MIT License</a></nav></div>
      <div className="footer-bottom"><p>{t("本地游戏管理工具 · Windows", "本機遊戲管理工具 · Windows", "A local game library for Windows", "Windows 用ローカルゲーム管理ツール")}</p><nav aria-label={t("页脚语言", "頁尾語言", "Footer languages", "言語選択")}>{languages.map(locale => <a key={locale} href={home(locale)} lang={locale} hrefLang={locale} aria-current={locale === language ? "page" : undefined}>{languageNames[locale]}</a>)}</nav></div>
    </div></footer>
  </>;
}
