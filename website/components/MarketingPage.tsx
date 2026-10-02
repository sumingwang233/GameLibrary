import { asset, home, languages, languageNames, release, repository, stableVersion, translate, type Language } from "../lib/site";
import { Header, LocalTooltip, Screenshot } from "./GlassControls";

function FeatureIcon({ kind }: { kind: "folder" | "tag" | "star" | "play" | "database" | "shield" }) {
  const paths = {
    folder: "M3 7V5a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Zm5 6h8m-4-4v8",
    tag: "M20 13 11 22 2 13V3h10l8 8a1.4 1.4 0 0 1 0 2ZM7 8h.01",
    star: "m12 3 2.8 5.7 6.3.9-4.6 4.4 1.1 6.3-5.6-3-5.6 3 1.1-6.3L3 9.6l6.2-.9Z",
    play: "m9 5 11 7-11 7Z",
    database: "M20 6c0 2-4 3-8 3S4 8 4 6s4-3 8-3 8 1 8 3ZM4 6v12c0 2 4 3 8 3s8-1 8-3V6M4 12c0 2 4 3 8 3s8-1 8-3",
    shield: "m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Zm-4 9 3 3 5-6",
  };
  return <svg className="feature-icon" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[kind]} /></svg>;
}

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
          <h1 id="hero-title">{t("你的游戏，", "你的遊戲，", "Your games,", "あなたのゲームを、")}<span>{t("值得好好收藏。", "值得好好收藏。", "thoughtfully collected.", "大切なコレクションに。")}</span></h1>
          <p className="hero-description">{t("扫描、审核、整理并启动硬盘里的游戏。", "掃描、審核、整理並啟動硬碟裡的遊戲。", "Scan, review, organize and launch the games on your drives.", "フォルダーをスキャンし、確認・整理してゲームを起動。")}</p>
          <p className="hero-description secondary">{t("无需账号，文件留在原来的位置。", "無需帳號，檔案留在原來的位置。", "No account required. Your files stay in place.", "アカウント不要。ファイルは元の場所に。")}</p>
          <div className="hero-actions"><a className="button" href={release + installer}><span aria-hidden="true">↓</span>{t("下载 Windows 版", "下載 Windows 版", "Download for Windows", "Windows 版をダウンロード")}</a><a className="text-link" href="#experience">{t("了解如何使用", "瞭解如何使用", "See how it works", "使い方を見る")} <span aria-hidden="true">→</span></a></div>
          <div className="hero-facts"><LocalTooltip language={language} /><a href={repository + "/blob/main/LICENSE"}>MIT {t("开源", "開源", "open source", "オープンソース")} ↗</a></div>
        </div>
        <Screenshot name="library.png" priority language={language} caption={t("游戏库 · v1.5.5 · 简体中文 · 隔离示例库", "遊戲庫 · v1.5.5 · 簡體中文 · 隔離範例庫", "Library · v1.5.5 · Chinese UI · Sample data", "ライブラリ · v1.5.5 · 中国語 UI · サンプルデータ")} alt={t("GameLibrary 实机界面：游戏网格、搜索、收藏和标签筛选。", "GameLibrary 實機介面：遊戲網格、搜尋、收藏和標籤篩選。", "The actual GameLibrary app with a searchable game grid, favorites and tag filters.", "実際の GameLibrary アプリ。ゲーム一覧、検索、お気に入り、タグの絞り込み。")} />
      </section>

      <section className="section tinted" id="experience" aria-labelledby="workflow-title"><div className="container workflow">
        <div><h2 id="workflow-title">{t("添加目录，审核入库", "新增目錄，審核入庫", "Add folders. Review what you find.", "フォルダーを追加して、確認・登録。")}</h2><p className="section-intro">{t("把分散的文件夹变成有序的收藏。自动扫描帮你寻找入口，最终选择始终由你决定。", "把分散的資料夾變成有序的收藏。自動掃描幫你尋找入口，最終選擇始終由你決定。", "Turn scattered folders into an organized collection. Scanning finds possible launchers; you make the final call.", "散らばったフォルダーを整ったコレクションに。スキャンで起動候補を見つけ、最後は自分で選びます。")}</p>
          <ol className="steps">{steps.map(([title, text], index) => <li key={title}><span className="step-number" aria-hidden="true">0{index + 1}</span><div><h3>{title}</h3><p>{text}</p></div></li>)}</ol>
        </div>
        <div className="scan-guide"><div className="scan-symbol"><FeatureIcon kind="folder" /></div><h3>{t("你的目录，你来决定", "你的目錄，由你決定", "Your folders. Your choice.", "自分のフォルダーを、自分で選ぶ。")}</h3><p>{t("从添加目录到确认入库，游戏文件一直留在原处。", "從新增目錄到確認入庫，遊戲檔案一直留在原處。", "From the first scan to your library, game files stay in their original folders.", "スキャンから登録まで、ゲームのファイルは元のフォルダーに残ります。")}</p>
          <div className="scan-flow"><span>{t("选择目录", "選擇目錄", "Choose folders", "場所を選択")}</span><span aria-hidden="true">→</span><span>{t("扫描候选", "掃描候選", "Scan candidates", "候補を検出")}</span><span aria-hidden="true">→</span><span>{t("确认入库", "確認入庫", "Review & add", "確認・登録")}</span></div>
          <p className="guide-note">{t("流程示意 · 扫描不会自动运行候选程序", "流程示意 · 掃描不會自動執行候選程式", "Workflow illustration · Scanning does not run candidates", "操作の流れ · 候補を自動実行することはありません")}</p>
        </div>
      </div></section>

      <section className="section" id="collection" aria-labelledby="collection-title"><div className="container collection">
        <Screenshot name="tags.png" language={language} caption={t("标签管理 · v1.5.5 · 简体中文 · 隔离示例库", "標籤管理 · v1.5.5 · 簡體中文 · 隔離範例庫", "Tags · v1.5.5 · Chinese UI · Sample data", "タグ管理 · v1.5.5 · 中国語 UI · サンプルデータ")} alt={t("GameLibrary 真实标签管理界面：标签分类、关联数量和标签星级置顶。", "GameLibrary 真實標籤管理介面：標籤分類、關聯數量和標籤星級置頂。", "The actual tag management screen with categories, associated game counts and tag star ratings.", "実際のタグ管理画面。分類、登録数、タグの星評価による並べ替え。")} />
        <div><h2 id="collection-title">{t("随心分类，", "隨心分類，", "A collection", "好きな分け方で、")}<br />{t("搭建你的私人游戏架", "打造你的私人遊戲架", "that feels like yours.", "自分だけのゲーム棚に。")}</h2><p className="section-intro">{t("为想玩的游戏留个位置。按标题、路径或标签查找，让下一次重逢更容易。", "為想玩的遊戲留個位置。依標題、路徑或標籤尋找，讓下一次重逢更容易。", "Make room for the games you want to return to. Search by title, path or tag to find your next play.", "また遊びたいゲームに居場所を。タイトル・パス・タグから、次に遊ぶ一本を見つけられます。")}</p>
          <div className="feature-pair"><article><FeatureIcon kind="tag" /><h3>{t("自由维度标签", "自由維度標籤", "Tags, your way", "自由なタグ分類")}</h3><p>{t("引擎、玩法、心情。自定义标签，按自己的习惯整理。", "引擎、玩法、心情。自訂標籤，依自己的習慣整理。", "Engine, genre or mood. Organize with labels that make sense to you.", "エンジン、ジャンル、気分。自分に合うタグで整理できます。")}</p></article><article><FeatureIcon kind="star" /><h3>{t("收藏与标签置顶", "收藏與標籤置頂", "Favorites & pinned tags", "お気に入りとタグの優先表示")}</h3><p>{t("收藏常玩的游戏，给标签设置星级，让常用分类靠前。", "收藏常玩的遊戲，為標籤設定星級，讓常用分類靠前。", "Favorite games you love; rate tags to keep useful categories at the top.", "よく遊ぶゲームをお気に入りに。タグに星を付けて、よく使う分類を上に表示できます。")}</p></article></div>
        </div>
      </div></section>

      <section className="section tinted" id="launch" aria-labelledby="launch-title"><div className="container launch">
        <div className="center-heading"><h2 id="launch-title">{t("直接启动，继续你的冒险", "直接啟動，繼續你的冒險", "Pick a game. Continue your adventure.", "ゲームを選んで、冒険の続きを。")}</h2><p className="section-intro">{t("保存熟悉的启动方式，下次打开游戏库就能接着玩。", "儲存熟悉的啟動方式，下次打開遊戲庫就能接著玩。", "Save the launch setup that works for you, and return to it next time.", "いつもの起動設定を保存して、次回もすぐに続きを遊べます。")}</p></div>
        <div className="feature-pair launch-features"><article><FeatureIcon kind="play" /><h3>{t("文件原地保留", "檔案原地保留", "Games stay where they are", "元の場所から起動")}</h3><p>{t("保存启动程序与参数，也可以手动添加 EXE 或 Windows 快捷方式，无需搬动整个游戏文件夹。", "儲存啟動程式與參數，也可手動新增 EXE 或 Windows 捷徑，無需搬動整個遊戲資料夾。", "Save the launcher and its arguments, or manually add an EXE or Windows shortcut. No need to move the game folder.", "起動プログラムと引数を保存。EXE や Windows のショートカットも手動で追加でき、フォルダーの移動は不要です。")}</p><p className="feature-footnote">EXE / LNK · {t("保存启动配置", "儲存啟動設定", "Saved launch configurations", "起動設定を保存")}</p></article><article><FeatureIcon kind="folder" /><h3>{t("关联你的运行环境", "關聯你的執行環境", "Use your existing players", "使い慣れた実行環境で")}</h3><p>{t("支持识别 Unity、RPG Maker MV / MZ、Ren’Py、Kirikiri 和 Flash。SWF 启动使用 Windows 关联播放器，需先手动配置。", "支援辨識 Unity、RPG Maker MV / MZ、Ren’Py、Kirikiri 和 Flash。SWF 啟動使用 Windows 關聯播放器，需先手動設定。", "Recognizes Unity, RPG Maker MV / MZ, Ren’Py, Kirikiri and Flash. SWF files use the player associated in Windows; configure it first.", "Unity、RPG Maker MV / MZ、Ren’Py、Kirikiri、Flash を識別。SWF は事前に設定した Windows の関連付けプレイヤーで起動します。")}</p><p className="feature-footnote">{t("未内置 Flash 播放器", "未內建 Flash 播放器", "No built-in Flash player", "Flash プレイヤーは非内蔵")}</p></article></div>
      </div></section>

      <section className="section" id="safety" aria-labelledby="safety-title"><div className="container">
        <h2 id="safety-title">{t("数据保存在本机，文件不搬动", "資料儲存在本機，檔案不搬動", "Your data stays local. Your files stay put.", "データはこの PC に。ファイルは元の場所に。")}</h2><p className="section-intro">{t("把注意力留给想玩的游戏。你的收藏，由你掌握。", "把注意力留給想玩的遊戲。你的收藏，由你掌握。", "Keep your attention on the games you want to play. Your collection stays in your hands.", "遊びたいゲームに集中。コレクションは自分の手元で管理できます。")}</p>
        <div className="feature-pair privacy-features"><article><FeatureIcon kind="database" /><h3>{t("本地元数据存储", "本機中繼資料儲存", "A local library database", "ローカルでデータ管理")}</h3><p>{t("目录、设置、标签和启动历史保存在本机 SQLite 数据库。无需注册，不上传扫描结果或使用数据。", "目錄、設定、標籤與啟動歷史儲存在本機 SQLite 資料庫。無需註冊，不上傳掃描結果或使用資料。", "Folders, settings, tags and launch history live in a local SQLite database. No registration, telemetry or uploaded scan results.", "フォルダー、設定、タグ、起動履歴はローカルの SQLite に保存。登録不要で、スキャン結果や利用データを送信しません。")}</p></article><article><FeatureIcon kind="shield" /><h3>{t("尊重原始文件", "尊重原始檔案", "Respect for your game files", "ゲームのファイルをそのままに")}</h3><p>{t("扫描和入库不搬动文件。移除库记录与回收游戏文件是不同操作；更新检查仅访问 GitHub 公开版本信息。", "掃描與入庫不搬動檔案。移除庫記錄與回收遊戲檔案是不同操作；更新檢查僅存取 GitHub 公開版本資訊。", "Scanning and adding games leave files in place. Removing a record and recycling game files are separate actions. Update checks read public GitHub release metadata.", "スキャンや登録でファイルを移動しません。登録の削除とファイルをゴミ箱に移す操作は別です。更新確認は GitHub の公開バージョン情報を参照します。")}</p></article></div>
      </div></section>

      <section className="section tinted" id="download" aria-labelledby="download-title"><div className="container">
        <h2 id="download-title">{t("获取 GameLibrary", "取得 GameLibrary", "Get GameLibrary", "GameLibrary をダウンロード")}</h2><p className="section-intro">{t("适配 Windows 10 / 11 x64，为你的收藏准备。", "適用 Windows 10 / 11 x64，為你的收藏準備。", "For Windows 10 / 11 x64. A home for your collection.", "Windows 10 / 11 x64 対応。コレクションの新しい居場所に。")}</p>
        <div className="download-grid"><article className="installer-card"><div className="package-heading"><img src={asset("icon.png")} width="56" height="56" loading="lazy" alt="" /><div><p className="release-label">v{stableVersion} · {t("公开稳定版", "公開穩定版", "Public stable release", "公開安定版")}</p><h3>{t("GameLibrary 安装版", "GameLibrary 安裝版", "GameLibrary installer", "GameLibrary インストーラー")}</h3></div></div><p>{t("图形安装向导，适合日常使用。包含桌面界面与所需运行环境，无需另装 .NET。", "圖形安裝精靈，適合日常使用。包含桌面介面與所需執行環境，無需另裝 .NET。", "A guided setup for everyday use. Includes the desktop app and required runtime; no separate .NET installation.", "日常利用向けのセットアップ。アプリと実行環境を含み、.NET の別途インストールは不要です。")}</p><code className="filename">{installer}</code><a className="button" href={release + installer}><span aria-hidden="true">↓</span>{t("下载 Windows 安装版", "下載 Windows 安裝版", "Download installer", "インストーラーをダウンロード")}</a><p className="package-note">{t("桌面界面：简体中文", "桌面介面：簡體中文", "Desktop interface: Simplified Chinese", "アプリの UI：簡体字中国語")}</p></article>
          <article className="other-packages"><h3>{t("其他格式与扩展组件", "其他格式與擴充元件", "Other formats & tools", "その他の形式・ツール")}</h3>{[
            [t("便携版（解压即用）", "可攜版（解壓縮即用）", "Portable (extract and run)", "ポータブル版（解凍して実行）"), `GameLibrary-Portable-win-x64-v${stableVersion}.zip`],
            [t("工具包（CLI & MCP）", "工具包（CLI & MCP）", "Tools (CLI & MCP)", "ツール（CLI & MCP）"), `GameLibrary-Tools-win-x64-v${stableVersion}.zip`],
            [t("SHA-256 校验清单", "SHA-256 校驗清單", "SHA-256 checksums", "SHA-256 チェックサム"), `GameLibrary-v${stableVersion}-SHA256SUMS.txt`],
          ].map(([label, filename]) => <a className="package-link" href={release + filename} key={filename}><span><strong>{label}</strong><small>{filename}</small></span><span aria-hidden="true">↓</span></a>)}
          <p className="download-note">{t("v1.5.5 的 Windows 程序未签名，下载后请核对校验值。", "v1.5.5 的 Windows 程式未簽署，下載後請核對校驗值。", "Windows executables in v1.5.5 are unsigned. Verify checksums after downloading.", "v1.5.5 の Windows 実行ファイルは未署名です。ダウンロード後にチェックサムをご確認ください。")} <a href={repository + "/releases/latest"}>{t("稳定版说明", "穩定版說明", "Stable release notes", "安定版の詳細")} ↗</a></p></article>
        </div>
        <div className="preview-note" id="preview"><p><strong>{t("预发布 · 待验收", "預先發行 · 待驗收", "Prerelease · Awaiting acceptance", "プレリリース · 検証待ち")}</strong>{t("v1.7.3 提供后续改进与进阶功能，使用前请阅读对应版本说明。", "v1.7.3 提供後續改進與進階功能，使用前請閱讀對應版本說明。", "v1.7.3 includes later improvements and advanced features. Read its release notes before trying it.", "v1.7.3 は追加の改善と高度な機能を含みます。利用前にリリースノートをご確認ください。")}</p><a href={repository + "/releases/tag/v1.7.3"}>{t("查看预发布", "查看預先發行", "View prerelease", "プレリリースを見る")} ↗</a></div>
      </div></section>

      <section className="section faq" id="questions" aria-labelledby="faq-title"><div className="container"><h2 id="faq-title">{t("常问问题", "常見問題", "A few common questions", "よくある質問")}</h2><div className="questions">{questions.map(([question, answer]) => <details key={question}><summary>{question}<span aria-hidden="true">+</span></summary><p>{answer}</p></details>)}</div></div></section>
    </main>
    <footer className="site-footer"><div className="container"><div className="footer-top"><a className="brand" href={home(language)}><img src={asset("icon.png")} width="32" height="32" loading="lazy" alt="" />GameLibrary</a><nav aria-label={t("项目链接", "專案連結", "Project links", "プロジェクトリンク")}><a href={repository}>GitHub ↗</a><a href={repository + "/issues"}>{t("反馈问题", "回報問題", "Report an issue", "問題を報告")}</a><a href={repository + "/blob/main/LICENSE"}>MIT License</a></nav></div><div className="footer-bottom"><p>{t("你的游戏，值得好好收藏。", "你的遊戲，值得好好收藏。", "Your games, thoughtfully collected.", "あなたのゲームを、大切なコレクションに。")}</p><nav aria-label={t("页脚语言", "頁尾語言", "Footer languages", "言語選択")}>{languages.map(locale => <a key={locale} href={home(locale)} lang={locale} hrefLang={locale} aria-current={locale === language ? "page" : undefined}>{languageNames[locale]}</a>)}</nav></div></div></footer>
  </>;
}
