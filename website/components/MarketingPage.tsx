import { glass } from "../lib/glass";
import { asset, home, release, repository, type Language } from "../lib/site";
import { Header, LocalTooltip, Screenshot } from "./GlassControls";

export default function MarketingPage({ language }: { language: Language }) {
  const en = language === "en";
  const t = (zh: string, english: string) => en ? english : zh;
  const steps = [
    [t("添加目录", "Add folders"), t("选择存放游戏的文件夹，再开始扫描。可以添加多个目录，应用只扫描你添加的位置。", "Select the folders containing your games and start scanning. You can add multiple folders; only those locations are scanned.")],
    [t("审核候选", "Review candidates"), t("打开“待确认”，查看找到的入口。认识的游戏加入库，其他项目可以暂不处理或忽略。", "Check the discovered launchers. Add games you recognize, and defer or ignore the other results.")],
    [t("核对后启动", "Check and launch"), t("在游戏详情页检查启动方式。需要时添加标签、标记收藏，再点击“开始游戏”。", "Check the launch configuration in the game's detail page. Add tags or mark it as a favorite, then select the play button.")],
  ];
  const features = [
    [t("标签、收藏与封面", "Tags, favorites, and covers"), t("为游戏添加自定义标签、标记收藏，也可以导入封面。引擎标签和自定义标签都能用来筛选。", "Add custom tags, mark favorites, and import covers. Filter your library with engine tags or your own tags.")],
    [t("保存启动方式", "Save launch configurations"), t("设置启动程序和参数，查看启动历史。也能手动添加 EXE、SWF 和 Windows 快捷方式。", "Set the launcher and its arguments, and view launch history. You can also add EXE files, SWF files, and Windows shortcuts manually.")],
    [t("桌面、CLI 和 MCP", "Desktop, CLI, and MCP"), t("三个入口操作同一份本地游戏库。桌面程序用于日常管理，CLI 和 MCP 可接入自动化工具。", "All three use the same local game library. Use the desktop app for daily management, or connect the CLI and MCP to automation tools.")],
  ];
  const safety = [
    [t("扫描与入库不搬动游戏", "Scanning leaves games in place"), t("游戏库记录与磁盘文件分开保存。常规移除只删除库记录；将文件移入回收站需要你明确确认。", "Library records are separate from game files. Regular removal deletes only the record; moving files to the Recycle Bin requires your explicit confirmation.")],
    [t("游戏库数据保存在本机", "Your collection stays local"), t("目录、设置、标签和启动历史使用本地 SQLite 保存。GameLibrary 不包含遥测，不上传扫描结果或使用数据。", "Folders, settings, tags, and launch history live in a local SQLite database. GameLibrary has no telemetry and uploads no scan results or usage data.")],
    [t("离线也能管理收藏", "Manage your library offline"), t("本地游戏库不依赖在线账号。应用会读取 GitHub 最新公开版本的元数据来提示更新；检查失败不影响本地使用。", "No online account is needed. The app reads public GitHub release metadata to check for updates; a failed check does not affect your local library.")],
  ];
  const packages = [
    [t("安装版", "Installer"), t("图形安装向导，适合日常使用。", "A guided setup for everyday use."), "GameLibrary-Setup-v1.5.5.exe", t("下载 Windows 版", "Download for Windows"), ".EXE"],
    [t("便携版", "Portable"), t("解压即可使用，保留桌面界面与本地 Host。", "Extract and run, with the desktop app and local Host included."), "GameLibrary-Portable-win-x64-v1.5.5.zip", t("下载便携版", "Download portable"), ".ZIP"],
    [t("CLI & MCP 工具包", "CLI & MCP tools"), t("用于命令行操作与自动化集成。", "For command-line use and automation integrations."), "GameLibrary-Tools-win-x64-v1.5.5.zip", t("下载工具包", "Download tools"), ".ZIP"],
  ];
  const questions = [
    [t("哪些游戏适合放进来？", "What kinds of games can I add?"), t("解压即玩的独立游戏、老游戏、视觉小说、Flash 游戏、Windows 快捷方式，以及多个硬盘中的本地游戏目录。它不会要求你把游戏移动到统一文件夹。", "Extract-and-play indie games, older titles, visual novels, Flash games, Windows shortcuts, and local game folders across multiple drives. There is no need to move them into a single folder.")],
    [t("会自动把所有 EXE 加进游戏库吗？", "Does every EXE get added automatically?"), t("不会。扫描会找出可能的游戏入口，并排除常见安装器、卸载器等文件。候选需要由你审核后才会入库，也可以手动添加游戏。", "No. Scanning finds possible launchers and filters common installers and uninstallers. You review candidates before they enter the library, or add games manually.")],
    [t("需要账号或联网吗？", "Do I need an account or internet access?"), t("管理本地游戏库不需要账号。应用的版本更新检查会访问 GitHub；你启动的游戏或辅助程序可能有它们自己的网络行为。", "No account is needed to manage your local library. The app checks GitHub for updates; games and companion programs you launch may make their own network requests.")],
    [t("可以直接运行 Flash 游戏吗？", "Can I launch Flash games?"), t("可以将 SWF 加入游戏库。启动时使用 Windows 当前为 .swf 关联的播放器，因此需要你先配置可用的 Flash 播放器。", "You can add SWF files. They open with the player currently associated with .swf in Windows, so you need to configure a compatible Flash player first.")],
    [t("卸载后，游戏和收藏还在吗？", "What happens when I uninstall?"), t("卸载不会删除原始游戏文件，也不会自动删除游戏库数据。应用数据默认保存在 %LOCALAPPDATA%\\GameLibrary。", "Uninstalling does not delete your original game files or automatically remove your library data. App data is stored in %LOCALAPPDATA%\\GameLibrary by default.")],
  ];

  return <>
    <div className="ambient" aria-hidden="true"><div className="orb orb-cyan" /><div className="orb orb-violet" /><div className="orb orb-warm" /></div>
    <a className="skip-link" href="#main">{t("跳至正文", "Skip to content")}</a>
    <Header language={language} />
    <main id="main" tabIndex={-1}>
      <section className="hero container" aria-labelledby="hero-title">
        <p className="eyebrow"><span className="status-dot" aria-hidden="true" />Windows 10 / 11 · x64 <span className="eyebrow-separator">/</span> GameLibrary</p>
        <div className="hero-heading">
          <h1 id="hero-title">{t("管理硬盘里的", "Manage your")}<br /><span>{t("本地游戏。", "local games.")}</span></h1>
          <div className="hero-intro">
            <p>{t("添加游戏目录，扫描后确认入库。游戏文件留在原来的位置。", "Add your game folders and review the scan results. Your game files stay where they are.")}</p>
            <p className="secondary">{t("按标题或路径搜索，用标签和收藏整理游戏，保存启动方式。适合解压即玩、分布在多个硬盘里的本地游戏。", "Search by title or path, organize with tags and favorites, and save launch configurations. For local games stored across your PC and drives.")}</p>
            <div className="hero-actions"><a className="button" href="#download">{t("下载 Windows 版", "Download for Windows")}<span aria-hidden="true">↓</span></a><a className="text-link" href={repository}>{t("查看源码", "View source")} ↗</a></div>
          </div>
        </div>
        <Screenshot name="library.png" priority language={language} caption={t("游戏库界面（示例数据）", "Library with sample data · Desktop interface in Chinese")} alt={t("GameLibrary 的真实游戏库界面：示例游戏以网格排列，可搜索、收藏并按标签筛选。", "The actual GameLibrary desktop interface showing a sample library in a searchable game grid, with favorites and tag filters.")} />
        <div className="platform-strip" aria-label={t("支持的平台与特点", "Platform and highlights")}><span>{t("安装版 & 便携版", "Installer & portable")}</span><LocalTooltip language={language} /><a href={`${repository}/blob/main/LICENSE`}>{t("MIT 开源", "MIT open source")} ↗</a></div>
      </section>

      <section className="workflow container section" id="experience" aria-labelledby="workflow-title">
        <p className="section-label">01 / {t("扫描与审核", "Scan & review")}</p>
        <div className="section-heading"><h2 id="workflow-title">{t("找到可能的入口，", "Review what the")}<br />{t("由你确认入库。", "scan finds.")}</h2><p>{t("扫描会识别常见游戏引擎的目录结构，排除安装器、卸载器等文件。剩下的候选会出现在“待确认”列表中。", "Scanning recognizes common engine layouts and filters installers and uninstallers. Possible game launchers appear in the review list.")}</p></div>
        <ol className="steps">{steps.map(([title, text], i) => <li key={title}><span className="step-number" aria-hidden="true">0{i + 1}</span><div><h3>{title}</h3><p>{text}</p></div></li>)}</ol>
      </section>

      <section className="collection container section" id="collection" aria-labelledby="collection-title">
        <p className="section-label">02 / {t("整理游戏", "Your collection")}</p>
        <div className="section-heading"><h2 id="collection-title">{t("按标题搜索，", "Search by title.")}<br />{t("按标签筛选。", "Filter by tag.")}</h2><p>{t("游戏多了，可以按名字、路径或标签查找，也可以切换排序和列表视图。", "Find games by name, path, or tag. Change the sort order or switch between grid and list views.")}</p></div>
        <div className="collection-body"><Screenshot name="tags.png" language={language} caption={t("标签管理界面（示例数据）", "Tags with sample data · Desktop interface in Chinese")} alt={t("GameLibrary 真实标签管理界面：示例标签可按类别整理，并显示关联游戏数量。", "The actual GameLibrary tag management interface with sample tags organized by category and associated game counts.")} /><div className="feature-notes">{features.map(([title, text]) => <article key={title}><h3>{title}</h3><p>{text}</p></article>)}</div></div>
        <div className="engine-list"><p>{t("支持以下引擎的识别，其他 EXE / LNK 也可生成候选。", "Recognizes these engines. Other EXE and LNK files can also produce candidates.")}</p><ul aria-label={t("可识别的游戏引擎", "Recognized game engines")}>{["Unity", "RPG Maker MV / MZ", "Ren’Py", "Kirikiri", "Flash"].map(engine => <li key={engine}>{engine}</li>)}</ul></div>
      </section>

      <section className="safety container section" id="safety" aria-labelledby="safety-title">
        <div><p className="section-label">03 / {t("本地数据", "Local data")}</p><h2 id="safety-title">{t("入库后，", "Added to the library.")}<br /><span>{t("文件仍在原处。", "Files stay in place.")}</span></h2></div>
        <div className="safety-notes">{safety.map(([title, text]) => <article key={title}><h3>{title}</h3><p>{text}</p></article>)}</div>
      </section>

      <section className="download container section" id="download" aria-labelledby="download-title">
        <div className={glass("download-panel")}>
          <div className="download-heading"><img src={asset("icon.png")} width="64" height="64" loading="lazy" alt="" /><div><p className="release-label">{t("公开稳定版", "Public stable release")} · v1.5.5</p><h2 id="download-title">{t("下载 GameLibrary", "Download GameLibrary")}</h2><p>{t("Windows 10 / 11 x64。发布包已包含运行环境，无需预先安装 .NET。", "For Windows 10 / 11 x64. Release packages include the runtime; no separate .NET installation is needed.")}</p></div></div>
          <div className="downloads">{packages.map(([title, text, filename, label, extension], index) => <article key={filename}><span className="file-type">{extension}</span><h3>{title}</h3><p>{text}</p><a className={index === 0 ? "button" : "text-link"} href={release + filename}>{label}<span aria-hidden="true">↓</span></a></article>)}</div>
          <p className="download-note">{t("v1.5.5 的 Windows 程序未签名。下载后请核对", "The Windows executables in v1.5.5 are unsigned. Verify the")} <a href={release + "GameLibrary-v1.5.5-SHA256SUMS.txt"}>{t("SHA-256 校验值", "SHA-256 checksums")}</a>{t("。", " after downloading. ")} <a href={`${repository}/releases/latest`}>{t("查看最新版本与发行说明", "See the latest release and release notes")}</a>{t("。", ".")}</p>
        </div>
      </section>

      <section className="faq container section" id="questions" aria-labelledby="faq-title">
        <div><p className="section-label">04 / FAQ</p><h2 id="faq-title">{t("常见问题", "Common questions")}</h2></div>
        <div className="questions">{questions.map(([question, answer]) => <details key={question}><summary>{question}<span aria-hidden="true">+</span></summary><p>{answer}</p></details>)}<details><summary>{t("在哪里反馈问题或参与开发？", "Where can I report issues or contribute?")}<span aria-hidden="true">+</span></summary><p>{t("在", "Report a problem on")} <a href={`${repository}/issues`}>GitHub Issues</a>{t(" 报告问题；构建说明和项目代码都在", ". Source code and build instructions are in the")} <a href={repository}>{t("GitHub 仓库", "GitHub repository")}</a>{t("，以 MIT 许可证开放。", ", available under the MIT license.")}</p></details></div>
      </section>
    </main>
    <footer className="site-footer container"><a className="brand" href={home(language)}><img src={asset("icon.png")} width="26" height="26" loading="lazy" alt="" />GameLibrary</a><p>{t("Windows 本地游戏管理", "Local game manager for Windows")}</p><nav aria-label={t("页脚导航", "Footer navigation")}><a href={repository}>GitHub</a><a href={`${repository}/issues`}>{t("反馈问题", "Report an issue")}</a><a href={home(en ? "zh-CN" : "en")} lang={en ? "zh-CN" : "en"} hrefLang={en ? "zh-CN" : "en"}>{en ? "中文" : "English"}</a></nav></footer>
  </>;
}
