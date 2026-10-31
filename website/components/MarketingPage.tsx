import { asset, home, languages, languageNames, release, repository, stableVersion, translate, type Language } from "../lib/site";
import { Header, LocalTooltip, Screenshot } from "./GlassControls";


export default function MarketingPage({ language }: { language: Language }) {
  const t = translate(language);
  const installer = `GameLibrary-Setup-v${stableVersion}.exe`;
  const steps = [
    [t("扫描指定目录", "掃描指定目錄", "Scan your folders", "フォルダーをスキャン"), t("添加存放游戏的文件夹。只扫描指定位置，识别常见引擎并过滤安装器和卸载器。", "新增存放遊戲的資料夾。只掃描指定位置，辨識常見引擎並過濾安裝程式和解除安裝程式。", "Add your game folders. Only those locations are scanned; common engines are recognized and installers are filtered out.", "ゲームのフォルダーを追加。指定した場所だけをスキャンし、一般的なエンジンを識別してインストーラーを除外します。")],
    [t("确认游戏入口", "確認遊戲入口", "Review the results", "起動先を確認"), t("候选先进入“待确认”。核对名称、目录和启动文件，确认后再加入游戏库。", "候選先進入「待確認」。核對名稱、目錄和啟動檔案，確認後再加入遊戲庫。", "Candidates go to the review list first. Check the name, folder and launcher before adding a game.", "候補はまず確認リストに表示。名前、フォルダー、起動ファイルを確認してから登録します。")],
    [t("从游戏库启动", "從遊戲庫啟動", "Launch from your library", "ライブラリから起動"), t("选好启动文件和参数，下次从库里打开。原始游戏文件继续留在原处。", "選好啟動檔案和參數，下次從庫裡開啟。原始遊戲檔案繼續留在原處。", "Save the launcher and its arguments, then open the game from your library. Original files stay where they are.", "起動ファイルと引数を保存して、ライブラリから起動。元のゲームファイルはそのままです。")],
  ];
  const questions = [
    [t("GameLibrary 会移动或修改我的游戏文件吗？", "GameLibrary 會移動或修改我的遊戲檔案嗎？", "Will GameLibrary move or change my game files?", "ゲームのファイルは移動・変更されますか？"), t("扫描和入库不会搬动游戏。常规移除只删除库记录；将游戏文件移入回收站需要你明确确认。", "掃描與入庫不會搬動遊戲。一般移除只刪除庫記錄；將遊戲檔案移入資源回收筒需由你明確確認。", "Scanning and adding games leave files in place. Regular removal deletes only the library record; moving game files to the Recycle Bin requires your explicit confirmation.", "スキャンや登録でファイルを移動しません。通常の削除は登録情報のみを削除し、ゲームをゴミ箱へ移すには明示的な確認が必要です。")],
    [t("哪些游戏适合放进来？", "哪些遊戲適合放進來？", "What kinds of games can I add?", "どんなゲームを登録できますか？"), t("解压即玩的独立游戏、老游戏、视觉小说、SWF 文件、Windows 快捷方式，以及多个硬盘中的本地游戏。游戏需自行获取，GameLibrary 不提供游戏下载。", "解壓縮即可玩的獨立遊戲、老遊戲、視覺小說、SWF 檔案、Windows 捷徑，以及多個硬碟中的本機遊戲。遊戲需自行取得，GameLibrary 不提供遊戲下載。", "Extract-and-play indie games, older titles, visual novels, SWF files, Windows shortcuts, and local games across multiple drives. Bring your own games; GameLibrary does not distribute them.", "解凍して遊べるゲーム、旧作、ビジュアルノベル、SWF、Windows のショートカットなどを登録できます。ゲームは別途用意してください。ゲームの配布は行いません。")],
    [t("会自动把所有 EXE 加进游戏库吗？", "會自動把所有 EXE 加進遊戲庫嗎？", "Is every EXE added automatically?", "すべての EXE が自動登録されますか？"), t("不会。扫描只生成可能的入口，候选由你审核后入库。也可以手动添加游戏。", "不會。掃描只產生可能的入口，候選由你審核後入庫。也可手動新增遊戲。", "No. Scanning produces possible launchers for you to review before adding them. You can also add games manually.", "いいえ。スキャンで見つかった候補を確認してから登録します。手動での追加も可能です。")],
    [t("为什么 SWF 游戏需要手动配置播放器？", "為什麼 SWF 遊戲需要手動設定播放器？", "Why do SWF games need a configured player?", "SWF 用のプレイヤー設定が必要なのはなぜですか？"), t("GameLibrary 使用 Windows 为 .swf 关联的播放器，未内置 Flash 播放器。请先配置可用的播放器，再从游戏库启动。", "GameLibrary 使用 Windows 為 .swf 關聯的播放器，未內建 Flash 播放器。請先設定可用的播放器，再從遊戲庫啟動。", "GameLibrary uses the player associated with .swf in Windows. It has no built-in Flash player, so configure a compatible player first.", "Windows で .swf に関連付けられたプレイヤーを使用します。Flash プレイヤーは内蔵していないため、事前に対応プレイヤーを設定してください。")],
    [t("需要账号或联网吗？", "需要帳號或連線嗎？", "Do I need an account or internet access?", "アカウントやネット接続は必要ですか？"), t("管理本地游戏库无需账号，离线也能使用。应用的更新检查会访问 GitHub；启动的游戏或辅助程序可能有自己的网络行为。使用 Unity 中文翻译时需自行填写翻译接口的密钥，密钥保存在本机。", "管理本機遊戲庫無需帳號，離線也能使用。應用程式的更新檢查會存取 GitHub；啟動的遊戲或輔助程式可能有自己的網路行為。使用 Unity 中文翻譯時需自行填寫翻譯介面的金鑰，金鑰保存在本機。", "You can manage your local library without an account, offline. Update checks access GitHub; games or companion programs you launch may make their own network requests. Using Unity Chinese translation requires your own translation API key, which is stored locally.", "ローカルライブラリはアカウントなしで、オフラインでも管理できます。更新確認は GitHub に接続します。起動したゲームや補助ツールは独自に通信する場合があります。Unity の中国語翻訳を使う場合は翻訳 API キーを自分で入力する必要があり、キーは本機に保存されます。")],
    [t("卸载后，游戏和收藏还在吗？", "解除安裝後，遊戲和收藏還在嗎？", "What happens when I uninstall?", "アンインストール後もゲームや登録情報は残りますか？"), t("卸载不会删除原始游戏文件，也不会自动删除游戏库数据。应用数据默认保存在 %LOCALAPPDATA%\\GameLibrary。", "解除安裝不會刪除原始遊戲檔案，也不會自動刪除遊戲庫資料。應用程式資料預設儲存在 %LOCALAPPDATA%\\GameLibrary。", "Uninstalling leaves your game files and library data in place. App data is stored in %LOCALAPPDATA%\\GameLibrary by default.", "元のゲームファイルやライブラリデータは自動削除されません。アプリのデータは標準で %LOCALAPPDATA%\\GameLibrary に保存されます。")],
  ];

  return <>
    <a className="skip-link" href="#main">{t("跳至正文", "跳至正文", "Skip to content", "本文へスキップ")}</a>
    <Header language={language} />
    <main id="main" tabIndex={-1}>
      <section className="hero container" aria-labelledby="hero-title">
        <div className="hero-grid">
          <div className="hero-copy">
          <p className="platform-label"><span className="status-dot" aria-hidden="true" />Windows 10 / 11 · x64 · v{stableVersion}</p>
          <h1 id="hero-title">{t("游戏散在各处，", "遊戲散在各處，", "Games on different drives.", "あちこちのゲームを、")}<span>{t("在这里一起管理。", "在這裡一起管理。", "One library to find them.", "ひとつのライブラリに。")}</span></h1>
          <p className="hero-description">{t("添加游戏目录，扫描后确认入库。用标签分类，从游戏库直接启动，原文件留在原处。", "新增遊戲目錄，掃描後確認入庫。用標籤分類，從遊戲庫直接啟動，原始檔案留在原處。", "Scan your game folders, review the results, and add them to your library. Organize with tags and launch from here. Your files stay where they are.", "ゲームフォルダーをスキャンし、確認して登録。タグで整理して、ここから起動できます。ファイルは元の場所に残ります。")}</p>
          <div className="hero-actions">
            <a className="button" href={release + installer}>{t("下载 Windows 版", "下載 Windows 版", "Download for Windows", "Windows 版をダウンロード")}<span aria-hidden="true">↓</span></a>
            <a className="text-link" href="#app-preview">{t("看看实际界面", "看看實際介面", "See the app", "アプリの画面を見る")}<span aria-hidden="true">↗</span></a>
          </div>
          <div className="hero-facts"><LocalTooltip language={language} /><span aria-hidden="true">·</span><span>{t("离线可用", "離線可用", "Works offline", "オフライン対応")}</span></div>
        </div>
          <figure className="hero-art">
            <span className="art-index" aria-hidden="true">COLLECTION / 001</span>
            <img className="hero-illustration" src={asset("hero-paper-pixel-1440.webp")} srcSet={[640, 960, 1440].map(size => `${asset(`hero-paper-pixel-${size}.webp`)} ${size}w`).join(", ")} sizes="(max-width: 920px) calc(100vw - 44px), (max-width: 1280px) 54vw, 650px" width="1536" height="1024" fetchPriority="high" loading="eager" decoding="async" alt={t("纸雕收藏夹里收纳着山林、星空与冒险场景的像素封面卡片。", "紙雕收藏夾裡收納著山林、星空與冒險場景的像素封面卡片。", "A paper archive folder holding pixel-art cover cards of forests, starfields and adventures.", "山や森、星空、冒険のピクセルアートカードを収めた紙のフォルダー。")} />
            <figcaption className="art-caption">{t("收藏概念插画", "收藏概念插畫", "An illustrated collection", "コレクションのイメージイラスト")}</figcaption>
          </figure>
        </div>
        <div className="hero-index">
          <span><b aria-hidden="true">01</b>{t("目录由你指定", "目錄由你指定", "Your folders", "フォルダーを自分で選ぶ")}</span>
          <span><b aria-hidden="true">02</b>{t("入库先经确认", "入庫先經確認", "Review before adding", "登録前に確認する")}</span>
          <span><b aria-hidden="true">03</b>{t("原文件留在原处", "原檔案留在原處", "Original files stay put", "元のファイルはそのまま")}</span>
        </div>
      </section>

      <section className="section" id="experience" aria-labelledby="workflow-title">
        <div className="container workflow">
          <div className="workflow-heading"><div className="section-copy"><p className="eyebrow">{t("从目录开始", "從目錄開始", "Start with a folder", "フォルダーを追加")}</p><h2 id="workflow-title">{t("先扫描，", "先掃描，", "Scan first. ", "スキャンして、")}<span>{t("再确认入库。", "再確認入庫。", "Then review.", "確認して登録。")}</span></h2></div><p className="section-intro">{t("只扫描你添加的目录。结果先进入“待确认”，核对启动文件后再加入游戏库。", "只掃描你新增的目錄。結果先進入「待確認」，核對啟動檔案後再加入遊戲庫。", "Only the folders you add are scanned. Results go to a review list so you can check each launcher before adding it.", "追加した場所だけをスキャンします。見つかった候補の起動ファイルを確認してから、ライブラリに登録します。")}</p></div>
          <figure className="journey-board">
            <img src={asset("pixel-journey-1440.webp")} srcSet={[640, 960, 1440].map(size => `${asset(`pixel-journey-${size}.webp`)} ${size}w`).join(", ")} sizes="(max-width: 640px) calc(100vw - 44px), (max-width: 1280px) calc(100vw - 96px), 1184px" width="1774" height="887" loading="lazy" decoding="async" alt={t("像素文件夹、收藏书架与手柄入口，由一条路径连接，表示扫描、确认和启动。", "像素資料夾、收藏書架與手把入口，由一條路徑連接，表示掃描、確認和啟動。", "A pixel folder, library shelf and gamepad portal connected by a path, illustrating scan, review and launch.", "ピクセルのフォルダー、本棚、ゲームパッドの入口を道でつないだ、スキャン・確認・起動のイラスト。")} />
            <figcaption className="art-caption">{t("流程概念插画", "流程概念插畫", "An illustrated workflow", "操作のイメージイラスト")}</figcaption>
          </figure>
          <ol className="steps">{steps.map(([title, text], index) => <li key={title}><span className="step-number" aria-hidden="true">0{index + 1}</span><div><h3>{title}</h3><p>{text}</p></div></li>)}</ol>
        </div>
      </section>

      <section className="section tinted" id="app-preview" aria-labelledby="preview-title">
        <div className="container">
          <div className="preview-heading">
            <div className="section-copy"><p className="eyebrow">{t("真实应用", "真實應用", "The actual app", "実際のアプリ")}</p><h2 id="preview-title">{t("封面、收藏和标签，", "封面、收藏和標籤，", "Covers, favorites, tags.", "カバーも、お気に入りも。")}<span>{t("在同一处找到。", "在同一處找到。", "Together in one place.", "ひとつの場所に。")}</span></h2></div>
            <p className="section-intro">{t("这是 v1.8.0 的实际界面，使用独立示例库。图片随网页切换浅色或深色，点击可放大查看。", "這是 v1.8.0 的實際介面，使用獨立範例庫。圖片隨網頁切換淺色或深色，點擊可放大檢視。", "The actual v1.8.0 app, using an isolated sample library. Screenshots match the page's light or dark theme. Click to enlarge.", "独立したサンプルライブラリを使った v1.8.0 の実画面です。ページのライト・ダーク設定に合わせて切り替わり、クリックで拡大できます。")}</p>
          </div>
          <div className="product-preview"><Screenshot name="library.png" wide language={language} caption={t("实际界面 · v1.8.0 · 简体中文 · 隔离示例库", "實際介面 · v1.8.0 · 簡體中文 · 隔離範例庫", "Actual app · v1.8.0 · Chinese UI · Isolated sample library", "実際の画面 · v1.8.0 · 中国語 UI · 隔離サンプルライブラリ")} alt={t("GameLibrary v1.8.0 实机界面，包含游戏网格、搜索、批量整理和标签筛选。", "GameLibrary v1.8.0 實機介面，包含遊戲網格、搜尋、批次整理和標籤篩選。", "The actual GameLibrary v1.8.0 app with a game grid, search, bulk actions and tag filters.", "実際の GameLibrary v1.8.0。ゲーム一覧、検索、一括操作、タグの絞り込み。")} /></div>
          <div className="compatibility"><p>{t("可识别的游戏引擎", "可辨識的遊戲引擎", "Recognized game engines", "識別できるゲームエンジン")}</p><ul aria-label={t("游戏引擎", "遊戲引擎", "Game engines", "ゲームエンジン")}><li>Unity</li><li>Godot</li><li>RPG Maker MV / MZ</li><li>Ren’Py</li><li>Kirikiri</li><li>Flash</li></ul></div>
        </div>
      </section>

      <section className="section" id="updates" aria-labelledby="updates-title"><div className="container advantages">
        <div className="section-copy"><p className="eyebrow">v1.8.0</p><h2 id="updates-title">{t("游戏多了，", "遊戲多了，", "A growing library.", "ゲームが増えても、")}<span>{t("整理不用一项项来。", "整理不用逐項來。", "Fewer repetitive steps.", "まとめて整理できます。")}</span></h2><p className="section-intro">{t("批量收藏、添加标签和移出游戏库，逐项返回结果。这一版也改进了审核、连接恢复和键盘操作。", "批次收藏、新增標籤和移出遊戲庫，逐項回傳結果。這一版也改進了審核、連線恢復和鍵盤操作。", "Favorite, tag and remove games in bulk, with a result for each item. This release also improves review, connection recovery and keyboard controls.", "お気に入り、タグ付け、登録の削除をまとめて行い、各項目の結果を確認できます。確認作業、接続の復旧、キーボード操作も改善しました。")}</p></div>
        <div className="adv-grid">
          <article><p className="adv-index">01</p><h3>{t("选好一批，一起整理", "選好一批，一起整理", "Organize a whole selection", "選んだゲームを一括整理")}</h3><p>{t("勾选多个游戏后，一次收藏、加标签或移出库。移出记录保留原始游戏文件。", "勾選多個遊戲後，一次收藏、加標籤或移出庫。移出記錄保留原始遊戲檔案。", "Select multiple games to favorite, tag or remove them from the library together. Removing records leaves the original game files in place.", "複数のゲームを選び、お気に入り、タグ、登録の削除を一括で操作。登録を削除しても元のゲームファイルは残ります。")}</p></article>
          <article><p className="adv-index">02</p><h3>{t("审核失败，不用重新挑", "審核失敗，不用重新選", "Keep your selection after a failed review", "確認に失敗した項目は選択を保持")}</h3><p>{t("审核时没处理成功的项目继续保持勾选。扫描或名称翻译遇到任务丢失、连续连接失败时，会停止等待并给出提示。", "審核時未處理成功的項目繼續保持勾選。掃描或名稱翻譯遇到工作遺失、連續連線失敗時，會停止等待並顯示提示。", "Items that fail during review stay selected. Scans and title translation stop waiting and show a message if a job disappears or the connection keeps failing.", "確認に失敗した項目は選択したまま残ります。スキャンやタイトル翻訳の処理が見つからない場合や接続が繰り返し失敗した場合は、待機を終了して知らせます。")}</p></article>
          <article><p className="adv-index">03</p><h3>{t("后台重启后，重新连接", "背景服務重啟後，重新連線", "Reconnect after a service restart", "バックグラウンドサービスの再起動後に再接続")}</h3><p>{t("后台服务重启导致连接失效时，应用会重建连接并重试当前请求一次，减少手动重启程序的麻烦。", "背景服務重啟導致連線失效時，應用程式會重建連線並重試目前請求一次，減少手動重啟程式的麻煩。", "If a service restart breaks the connection, the app reconnects and retries the current request once, reducing the need to restart the app yourself.", "サービスの再起動で接続が切れた場合は再接続し、現在のリクエストを一度だけ再試行。アプリを手動で再起動する手間を減らします。")}</p></article>
          <article><p className="adv-index">04</p><h3>{t("键盘操作与阅读更清楚", "鍵盤操作與閱讀更清楚", "Clearer reading and keyboard controls", "読みやすさとキーボード操作を改善")}</h3><p>{t("调整徽章文字对比度与详情面板焦点顺序，分开游戏卡的选择和启动操作；动画尊重系统的减少动态效果设置。", "調整徽章文字對比度與詳細資料面板焦點順序，分開遊戲卡的選取和啟動操作；動畫遵循系統的減少動態效果設定。", "Improved badge contrast and details-panel focus order, with separate selection and launch controls on game cards. Animations respect your system's reduced-motion setting.", "バッジの文字コントラストと詳細パネルのフォーカス順を改善し、ゲームカードの選択と起動を別の操作にしました。アニメーションは OS の動きを減らす設定に従います。")}</p></article>
        </div>
      </div></section>

      <section className="section" id="advantages" aria-labelledby="advantages-title"><div className="container advantages">
        <div className="section-copy"><p className="eyebrow">{t("独特优势", "獨特優勢", "Why GameLibrary", "独自の強み")}</p><h2 id="advantages-title">{t("本地优先，", "本機優先，", "Local first. ", "ローカル優先、")}<span>{t("文件仍归你管。", "檔案仍歸你管。", "Your files stay yours.", "ファイルはあなたのもの。")}</span></h2><p className="section-intro">{t("游戏库数据保存在本机。扫描和入库保留原文件，哪些游戏进库由你确认。", "遊戲庫資料儲存在本機。掃描和入庫保留原檔案，哪些遊戲進庫由你確認。", "Library data stays on your PC. Scanning and adding games preserve original files, and you decide what enters the library.", "ライブラリのデータはこの PC に保存。スキャンや登録で元のファイルを変えず、何を登録するかは自分で決められます。")}</p></div>
        <div className="adv-grid">
          <article><p className="adv-index">01</p><h3>{t("数据留在本机，无需账号", "資料留在本機，無需帳號", "Data stays on your PC, no account", "データはこの PC に、アカウント不要")}</h3><p>{t("游戏库、标签、设置与启动记录都保存在这台电脑上。管理本地游戏不必注册或登录，断网也能使用。", "遊戲庫、標籤、設定與啟動記錄都儲存在這台電腦上。管理本機遊戲不必註冊或登入，斷網也能使用。", "Your library, tags, settings and launch history live on this PC. You can manage local games without signing up or logging in, even offline.", "ライブラリ、タグ、設定、起動履歴はこの PC に保存されます。登録やログインなしで、オフラインでも管理できます。")}</p></article>
          <article><p className="adv-index">02</p><h3>{t("原文件不动", "原檔案不動", "Original files stay put", "元のファイルはそのまま")}</h3><p>{t("扫描、入库和移除记录都不会搬动游戏文件。把文件移入回收站，需要你单独确认。", "掃描、入庫與移除記錄都不會搬動遊戲檔案。把檔案移入資源回收筒，需要你另行確認。", "Scanning, adding and removing records never move your game files. Sending files to the Recycle Bin needs your separate confirmation.", "スキャン、登録、登録の削除でゲームファイルを移動しません。ゴミ箱への移動は別途確認が必要です。")}</p></article>
          <article><p className="adv-index">03</p><h3>{t("入库前先经你确认", "入庫前先經你確認", "You confirm before it is added", "登録前にあなたが確認")}</h3><p>{t("扫描只生成候选入口，核对启动文件后再加入游戏库，不会自动把一堆无关程序收进来。", "掃描只產生候選入口，核對啟動檔案後再加入遊戲庫，不會自動把一堆無關程式收進來。", "Scanning only suggests candidate launchers. You check each one before it enters the library, so unrelated programs are not added automatically.", "スキャンは候補の起動先を提示するだけ。起動ファイルを確認してから登録するので、無関係なプログラムが自動で入ることはありません。")}</p></article>
          <article><p className="adv-index">04</p><h3>{t("认得主流独立游戏引擎", "認得主流獨立遊戲引擎", "Recognizes common indie engines", "主要なインディーゲームエンジンを識別")}</h3><p>{t("自动识别 Unity、Godot、RPG Maker MV / MZ、Ren’Py、Kirikiri 与 Flash，也支持手动添加 EXE、LNK 和 SWF。", "自動辨識 Unity、Godot、RPG Maker MV / MZ、Ren’Py、Kirikiri 與 Flash，也支援手動新增 EXE、LNK 和 SWF。", "Detects Unity, Godot, RPG Maker MV / MZ, Ren’Py, Kirikiri and Flash automatically, and also supports adding EXE, LNK and SWF manually.", "Unity、Godot、RPG Maker MV / MZ、Ren’Py、Kirikiri、Flash を自動で識別。EXE、LNK、SWF の手動追加にも対応します。")}</p></article>
        </div>
      </div></section>

      <section className="section tinted" id="collection" aria-labelledby="collection-title"><div className="container collection">
        <div className="section-copy"><p className="eyebrow">{t("整理与查找", "整理與尋找", "Organize and find", "整理と検索")}</p><h2 id="collection-title">{t("记不住名字，", "記不住名字，", "Forgot the name? ", "名前を忘れても、")}<span>{t("也能按标签找。", "也能依標籤找。", "Find it by tag.", "タグで探せます。")}</span></h2><p className="section-intro">{t("按标题、路径或标签搜索。游戏多了，也不用逐个打开文件夹找。", "依標題、路徑或標籤搜尋。遊戲多了，也不用逐一打開資料夾找。", "Search by title, path or tag, without opening each folder to find a game.", "タイトル、パス、タグで検索できます。フォルダーをひとつずつ開いて探す手間を減らせます。")}</p>
          <div className="feature-list">
            <article><h3>{t("标签自己定，一批一起加", "標籤自己訂，一批一起加", "Your tags, applied in bulk", "自分のタグをまとめて付ける")}</h3><p>{t("按引擎、类型或游玩状态分类，一个游戏可以关联多个标签。也可以选中多个游戏，一次加上同一标签。", "依引擎、類型或遊玩狀態分類，一個遊戲可關聯多個標籤。也可選取多個遊戲，一次加上同一標籤。", "Group games by engine, genre or play status, with multiple tags per game. Select several games to apply the same tag to all of them.", "エンジン、ジャンル、プレイ状況で分類し、ひとつのゲームに複数のタグを付けられます。複数のゲームに同じタグをまとめて設定することもできます。")}</p></article>
            <article><h3>{t("常玩的放前面", "常玩的放前面", "Keep favorites handy", "よく遊ぶゲームを手近に")}</h3><p>{t("收藏常玩的游戏。给标签设置星级，常用分类就能排在前面。", "收藏常玩的遊戲。為標籤設定星級，常用分類就能排在前面。", "Favorite the games you play often. Set star ratings on tags to bring useful categories to the top.", "よく遊ぶゲームをお気に入りに登録。タグに星を付けて、よく使う分類を上に表示できます。")}</p></article>
            <article><h3>{t("标签归类更顺手", "標籤歸類更順手", "Easier tag grouping", "タグ整理がスムーズ")}</h3><p>{t("在游戏详情里，把标签拖进任意分类（包括空分类），也可以改用分类选择器。", "在遊戲詳情裡，把標籤拖進任意分類（包括空分類），也可以改用分類選擇器。", "In the game details, drag a tag into any category (including empty ones), or use the category picker instead.", "ゲーム詳細で、タグを任意の分類（空の分類も含む）へドラッグ。分類の選択画面でも変更できます。")}</p></article>
          </div>
        </div>
        <div className="collection-visual">
          <p className="shelf-label" aria-hidden="true">YOUR COLLECTION / 002</p>
          <Screenshot name="tags.png" language={language} caption={t("标签管理 · v1.8.0 · 简体中文 · 隔离示例库", "標籤管理 · v1.8.0 · 簡體中文 · 隔離範例庫", "Tag management · v1.8.0 · Chinese UI · Isolated sample library", "タグ管理 · v1.8.0 · 中国語 UI · 隔離サンプルライブラリ")} alt={t("GameLibrary v1.8.0 真实标签管理界面，包含分类、关联数量和标签星级。", "GameLibrary v1.8.0 真實標籤管理介面，包含分類、關聯數量和標籤星級。", "The actual GameLibrary v1.8.0 tag management screen with categories, game counts and tag star ratings.", "実際の GameLibrary v1.8.0 のタグ管理画面。分類、登録数、タグの星評価。")} />
          <ul className="collection-tags" aria-label={t("标签示例", "標籤範例", "Example tags", "タグの例")}><li>{t("周末想玩", "週末想玩", "For the weekend", "週末に遊びたい")}</li><li>{t("轻松一刻", "輕鬆一刻", "A little downtime", "ひと息つくとき")}</li><li>{t("故事与冒险", "故事與冒險", "Stories & adventures", "物語と冒険")}</li></ul>
        </div>
      </div></section>

      <section className="section tinted" id="translation" aria-labelledby="translation-title"><div className="container translation">
        <div className="section-copy"><p className="eyebrow">{t("中文翻译", "中文翻譯", "Chinese translation", "中国語翻訳")}</p><h2 id="translation-title">{t("Unity 游戏，", "Unity 遊戲，", "Unity games. ", "Unity ゲームにも")}<span>{t("也能配好中文。", "也能配好中文。", "Set up Chinese translation.", "中国語を。")}</span></h2><p className="section-intro">{t("给库里的 Unity 游戏配置中文翻译：安装固定版本的加载器与翻译插件，安装前先备份，随时可以恢复。", "為庫裡的 Unity 遊戲設定中文翻譯：安裝固定版本的載入器與翻譯外掛，安裝前先備份，隨時可以還原。", "Configure Chinese translation for Unity games in your library: install fixed-version loaders and a translation plugin, with a backup first and restore anytime.", "ライブラリの Unity ゲームに中国語翻訳を設定できます。固定バージョンのローダーと翻訳プラグインを導入し、事前にバックアップして、いつでも復元できます。")}</p><p className="translation-note">{t("需自行配置翻译服务，缺少安装组件时需要联网获取。支持情况和实际效果因游戏而异。", "需自行設定翻譯服務，缺少安裝元件時需要連線取得。支援情況和實際效果因遊戲而異。", "Bring your own translation service. Missing installation components require a download. Support and results vary by game.", "翻訳サービスの設定が必要です。導入用のコンポーネントが不足している場合はダウンロードします。対応状況や効果はゲームによって異なります。")}</p></div>
        <div className="feature-list">
          <article><h3>{t("Mono 与 IL2CPP 分别处理", "Mono 與 IL2CPP 分別處理", "Mono and IL2CPP handled separately", "Mono と IL2CPP を個別に対応")}</h3><p>{t("按主程序的真实位数匹配对应的加载器与插件；识别不了或相互冲突的安装保持原样，不强行覆盖。", "依主程式的真實位元數匹配對應的載入器與外掛；辨識不了或相互衝突的安裝保持原樣，不強行覆蓋。", "The loader and plugin match the real bitness of the executable. Unknown or conflicting installs are left as they are, never overwritten by force.", "本体実行ファイルの実ビット数に合わせてローダーとプラグインを選びます。判別できない・競合する構成は元のままにし、強制的に上書きしません。")}</p></article>
          <article><h3>{t("补齐中文字体", "補齊中文字型", "Fills in Chinese fonts", "中国語フォントを補完")}</h3><p>{t("为 Unity UI 与 TextMeshPro 配置可用的中文字体回退，减少菜单和对话里的缺字。", "為 Unity UI 與 TextMeshPro 設定可用的中文字型回退，減少選單和對話裡的缺字。", "Configures a usable Chinese font fallback for Unity UI and TextMeshPro, reducing missing glyphs in menus and dialogue.", "Unity UI と TextMeshPro に中国語フォントのフォールバックを設定し、メニューや会話の欠字を減らします。")}</p></article>
          <article><h3>{t("先备份，可回滚", "先備份，可還原", "Backup first, roll back if needed", "先にバックアップ、ロールバック可能")}</h3><p>{t("安装前备份原始文件，失败时自动回滚；是否生效由你启动游戏后自行确认。", "安裝前備份原始檔案，失敗時自動還原；是否生效由你啟動遊戲後自行確認。", "Original files are backed up before installing and rolled back automatically on failure. You confirm the result by launching the game.", "導入前に元ファイルをバックアップし、失敗時は自動でロールバック。効果の有無はゲームを起動して確認します。")}</p></article>
          <article><h3>{t("密钥只存本机", "金鑰只存本機", "Keys stay on your PC", "キーはローカル保存のみ")}</h3><p>{t("使用 DeepSeek 或 OpenAI 兼容的翻译接口，密钥保存在本机，不随发布包分发。", "使用 DeepSeek 或 OpenAI 相容的翻譯介面，金鑰保存在本機，不隨發布包分發。", "Uses a DeepSeek- or OpenAI-compatible translation API. Your key is stored locally and is never bundled with the release.", "DeepSeek または OpenAI 互換の翻訳 API を使用。キーは本機に保存され、配布物には含まれません。")}</p></article>
        </div>
      </div></section>

      <section className="section" id="launch" aria-labelledby="launch-title"><div className="container launch">
        <div className="section-copy"><p className="eyebrow">{t("打开游戏", "打開遊戲", "Launch your games", "ゲームを起動")}</p><h2 id="launch-title">{t("选好启动文件，", "選好啟動檔案，", "Set the launcher. ", "起動先を選んで、")}<span>{t("下次直接打开。", "下次直接打開。", "Use it next time.", "次回もそのまま起動。")}</span></h2><p className="section-intro">{t("启动程序和参数保存在游戏记录里，不需要每次重新设置。", "啟動程式與參數儲存在遊戲記錄裡，不需要每次重新設定。", "The launcher and its arguments are saved with the game, ready for the next time you play.", "起動プログラムと引数はゲームの登録情報に保存され、次回も同じ設定で起動できます。")}</p></div>
        <div className="feature-list launch-list">
          <article><p className="format-label">EXE / LNK</p><h3>{t("也可以手动添加", "也可以手動新增", "Add a game manually", "手動での追加にも対応")}</h3><p>{t("指定 EXE 或 Windows 快捷方式作为入口。游戏文件夹可以继续放在原来的硬盘。", "指定 EXE 或 Windows 捷徑作為入口。遊戲資料夾可繼續放在原來的硬碟。", "Choose an EXE or a Windows shortcut as the launcher. The game folder can stay on its current drive.", "EXE や Windows のショートカットを起動先に指定。ゲームフォルダーは元のドライブに置いたままで使えます。")}</p></article>
          <article><p className="format-label">SWF</p><h3>{t("用已配置的播放器启动", "用已設定的播放器啟動", "Use your configured player", "設定済みのプレイヤーで起動")}</h3><p>{t("Flash 游戏通过 Windows 为 .swf 关联的播放器打开。应用未内置 Flash 播放器，需要先自行配置。把整个合集目录入库时，会为其中的每个 SWF / EXE 生成建议启动方式。", "Flash 遊戲透過 Windows 為 .swf 關聯的播放器開啟。應用程式未內建 Flash 播放器，需先自行設定。把整個合輯目錄入庫時，會為其中的每個 SWF / EXE 產生建議啟動方式。", "Flash games open with the player associated with .swf in Windows. A Flash player is not included; configure one first. When you add a whole collection folder, a suggested launch method is generated for each SWF / EXE inside it.", "Flash ゲームは Windows で .swf に関連付けたプレイヤーで開きます。プレイヤーは内蔵していないため、事前に設定してください。コレクション全体のフォルダーを登録すると、その中の各 SWF / EXE に推奨起動方法を生成します。")}</p></article>
        </div>
      </div></section>

      <section className="section tinted" id="safety" aria-labelledby="safety-title"><div className="container privacy">
        <div className="section-copy"><p className="eyebrow">{t("数据与文件", "資料與檔案", "Data and files", "データとファイル")}</p><h2 id="safety-title">{t("游戏留在原处。", "遊戲留在原處。", "Files stay put. ", "ファイルは元の場所に。")}<span>{t("记录保存在本机。", "記錄儲存在本機。", "Records stay local.", "登録情報はこの PC に。")}</span></h2><p className="section-intro">{t("管理游戏库无需注册账号，离线也能使用。", "管理遊戲庫無需註冊帳號，離線也能使用。", "Manage your library offline, without signing up for an account.", "アカウント登録なしで、オフラインでもゲームを管理できます。")}</p></div>
        <div className="feature-list">
          <article><h3>{t("不上传扫描结果", "不上傳掃描結果", "Scan results stay local", "スキャン結果は送信しません")}</h3><p>{t("目录、标签、设置和启动历史保存在本机数据库。更新检查会访问 GitHub，启动的游戏或辅助程序可能自行联网。", "目錄、標籤、設定與啟動歷史儲存在本機資料庫。更新檢查會存取 GitHub，啟動的遊戲或輔助程式可能自行連線。", "Folders, tags, settings and launch history are stored on this PC. Update checks access GitHub; games and companion tools may make their own network requests.", "フォルダー、タグ、設定、起動履歴はこの PC に保存。更新確認は GitHub に接続します。ゲームや補助ツールは独自に通信する場合があります。")}</p></article>
          <article><h3>{t("按需开放操作权限", "依需要開放操作權限", "Permissions fit each client", "クライアントごとに必要な権限を設定")}</h3><p>{t("桌面程序、CLI 和 MCP 分别声明需要的操作权限，不再默认获得全部能力。", "桌面程式、CLI 和 MCP 分別宣告需要的操作權限，不再預設取得全部能力。", "The desktop app, CLI and MCP server each declare the permissions they need instead of receiving every capability by default.", "デスクトップアプリ、CLI、MCP サーバーはそれぞれ必要な操作権限を申告し、すべての機能が既定で付与されることはありません。")}</p></article>
          <article><h3>{t("移除记录和删除文件分开", "移除記錄與刪除檔案分開", "Removing a record leaves the files", "登録の削除とファイル削除は別です")}</h3><p>{t("扫描、入库和常规移除记录不会搬动游戏。将游戏文件移入回收站，需要单独确认，默认数据保存在 %LOCALAPPDATA%\\GameLibrary。", "掃描、入庫及一般移除記錄不會搬動遊戲。將遊戲檔案移入資源回收筒，需要另行確認，預設資料儲存在 %LOCALAPPDATA%\\GameLibrary。", "Scanning, adding games and regular record removal leave your files in place. Moving game files to the Recycle Bin requires separate confirmation. App data is stored in %LOCALAPPDATA%\\GameLibrary by default.", "スキャン、登録、通常の登録削除ではゲームを移動しません。ファイルをゴミ箱へ移す操作には、別途確認が必要です。データは標準で %LOCALAPPDATA%\\GameLibrary に保存されます。")}</p></article>
        </div>
      </div></section>

      <section className="section download" id="download" aria-labelledby="download-title"><div className="container"><div className="download-folder"><p className="folder-tab">Windows / x64</p>
        <div className="download-heading"><div><p className="eyebrow">{t("开始使用", "開始使用", "Get started", "使い始める")}</p><h2 id="download-title">{t("下载 GameLibrary", "下載 GameLibrary", "Download GameLibrary", "GameLibrary をダウンロード")}</h2></div><p className="section-intro">Windows 10 / 11 · x64<br />{t("安装版、便携版，按需要选择。", "安裝版、可攜版，依需要選擇。", "Choose the installer or portable version.", "インストーラー版とポータブル版を選べます。")}</p></div>
        <div className="download-grid">
          <article className="installer-card">
            <div className="package-heading"><img src={asset("icon.png")} width="56" height="56" loading="lazy" alt="" /><div><p className="release-label">v{stableVersion} · {t("正式版", "正式版", "Official release", "正式版")}</p><h3>{t("Windows 安装版", "Windows 安裝版", "Windows installer", "Windows インストーラー")}</h3></div></div>
            <p>{t("跟随安装向导即可使用，包含桌面程序和运行环境，无需另装 .NET。", "依照安裝精靈即可使用，包含桌面程式及執行環境，無需另裝 .NET。", "Follow the setup wizard. The desktop app and runtime are included, so no separate .NET installation is needed.", "セットアップの案内に従ってインストール。アプリと実行環境を含み、.NET の別途インストールは不要です。")}</p>
            <code className="filename">{installer}</code><a className="button" href={release + installer}>{t("下载安装版", "下載安裝版", "Download installer", "インストーラーをダウンロード")}<span aria-hidden="true">↓</span></a><p className="package-note">{t("升级前请从托盘退出应用。安装新版会保留游戏库数据与原始游戏文件。", "升級前請從系統匣結束應用程式。安裝新版會保留遊戲庫資料與原始遊戲檔案。", "Exit the app from the tray before upgrading. Your library data and original game files are preserved.", "更新前にトレイからアプリを終了してください。ライブラリのデータと元のゲームファイルは保持されます。")}</p>
          </article>
          <article className="other-packages"><h3>{t("其他下载", "其他下載", "Other downloads", "その他のダウンロード")}</h3>{[
            [t("便携版 · 解压运行", "可攜版 · 解壓縮執行", "Portable · Extract and run", "ポータブル版 · 解凍して実行"), `GameLibrary-Portable-win-x64-v${stableVersion}.zip`],
            [t("工具包 · CLI & MCP", "工具包 · CLI & MCP", "Tools · CLI & MCP", "ツール · CLI & MCP"), `GameLibrary-Tools-win-x64-v${stableVersion}.zip`],
            [t("SHA-256 校验清单", "SHA-256 校驗清單", "SHA-256 checksums", "SHA-256 チェックサム"), `GameLibrary-v${stableVersion}-SHA256SUMS.txt`],
          ].map(([label, filename]) => <a className="package-link" href={release + filename} key={filename}><span><strong>{label}</strong><small>{filename}</small></span><span aria-hidden="true">↓</span></a>)}
            <p className="download-note">{t("Windows 程序未签名，请在下载后核对校验值。", "Windows 程式未簽署，請在下載後核對校驗值。", "Windows executables are unsigned. Verify the checksums after downloading.", "Windows 実行ファイルは未署名です。ダウンロード後にチェックサムをご確認ください。")}</p>
          </article>
        </div>
        <div className="preview-note" id="preview"><a href={repository + "/releases/tag/v" + stableVersion}>{t("查看发布说明", "查看發布說明", "Read release notes", "リリースノートを見る")} ↗</a></div>
      </div></div></section>

      <section className="section tinted faq" id="questions" aria-labelledby="faq-title"><div className="container faq-layout">
        <div className="section-copy"><p className="eyebrow">FAQ</p><h2 id="faq-title">{t("下载前，", "下載前，", "Before ", "ダウンロード前に")}<span>{t("你可能想知道。", "你可能想知道。", "you download.", "よくある質問。")}</span></h2></div>
        <div className="questions">{questions.map(([question, answer]) => <details key={question}><summary>{question}<span aria-hidden="true">+</span></summary><p>{answer}</p></details>)}</div>
      </div></section>
    </main>
    <footer className="site-footer"><div className="container">
      <div className="footer-top"><a className="brand" href={home(language)}><img src={asset("icon.png")} width="32" height="32" loading="lazy" alt="" />GameLibrary</a><nav aria-label={t("项目链接", "專案連結", "Project links", "プロジェクトリンク")}><a href={repository}>GitHub ↗</a><a href={repository + "/issues"}>{t("反馈问题", "回報問題", "Report an issue", "問題を報告")}</a><a href={repository + "/blob/main/LICENSE"}>MIT License</a></nav></div>
      <div className="footer-bottom"><p>{t("本地游戏管理工具 · Windows", "本機遊戲管理工具 · Windows", "A local game library for Windows", "Windows 用ローカルゲーム管理ツール")}</p><nav aria-label={t("页脚语言", "頁尾語言", "Footer languages", "言語選択")}>{languages.map(locale => <a key={locale} href={home(locale)} lang={locale} hrefLang={locale} aria-current={locale === language ? "page" : undefined}>{languageNames[locale]}</a>)}</nav></div>
    </div></footer>
  </>;
}
