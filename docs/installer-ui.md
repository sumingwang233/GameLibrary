# Windows 安装向导

安装器继续使用 `artifacts/installer/GameLibrary.nsi` 中的 NSIS Modern UI 2，沿用当前用户安装、开始菜单快捷方式和原有卸载规则。此轮没有改变应用版本、发布包文件名、程序载荷或权限范围。

## 页面与样式

- 白色内容区、深灰文字、右上角应用图标，底部保留系统按钮和浅灰操作区。使用 Microsoft YaHei UI 9pt、原生输入框和单选按钮；保留 DPI awareness。
- 顺序为欢迎、旧版本选择（检测到可用卸载器时）、MIT 许可、安装位置、准备安装、安装进度和完成。
- 许可页嵌入仓库的 `LICENSE`，使用同意／不同意选项。没有照搬参考软件的 GPL、隐私政策或服务条款。
- 准备安装页列出目标路径、当前用户范围、升级方式和已有的开始菜单快捷方式。回退修改路径或升级选择后，摘要重新生成。
- 旧版本处理从安装过程中的弹窗移到单独页面；直到点击“安装”才执行卸载。默认先卸载旧版，覆盖选项及静默 `IDNO` 行为保留，执行前重新核对卸载项。
- 完成页保留启动应用和项目主页入口。卸载仍只清理已知程序文件，保留游戏库数据和未知文件。

界面采用 [NSIS Modern UI 2](https://nsis.sourceforge.io/Docs/Modern%20UI%202/Readme.html) 和 [nsDialogs](https://nsis.sourceforge.io/Docs/nsDialogs/Readme.html) 的原生页面机制，没有引入网页框架或额外安装运行时。

## 图标素材

`header.bmp`（150×57）和 `wizard.bmp`（164×314）由已批准的 `src/GameLibrary.Desktop/App.ico` 生成。更新 logo 后，在 Windows 下运行：

```powershell
./scripts/generate_installer_branding.ps1
```

这两个位图与 NSIS 源文件一同提交；发布打包器直接读取它们。

## 验证及本地预览

```powershell
python scripts/check_installer.py --makensis <makensis.exe路径> --smoke
```

脚本使用 Python 标准库、已安装的 NSIS 和虚拟载荷。测试具有唯一的 HKCU 卸载键及开始菜单目录，程序文件放在工作区内；`TEST_BUILD` 禁用原有 taskkill，仅清理自己生成的安装实例。

2026-10-01 已完成：

- 正常、`IDNO`、普通预览和旧版本预览四种构建，启用 `/WX`，无编译警告。
- 带空格路径下的新安装、先卸载旧版再安装、保留旧版文件、卸载及测试快捷方式和登记清理。
- 数据哨兵和未知文件保留。
- 两种预览均未创建安装目录、快捷方式或卸载登记。

检查生成的 `artifacts/installer-ui/GameLibrary-Installer-Preview.exe` 与 `GameLibrary-Upgrade-Preview.exe` 是原生界面的只读预览。标题注明“界面预览”，安装阶段只显示提示，不复制程序、不卸载旧版，也不启动应用；旧版本页标注示例版本。

当前会话没有可用的原生窗口自动化控制，因此没有将页面截图、键盘导航或 100%／125%／150%／200% DPI 显示标记为实机通过。发布前仍须用上述预览程序在 Windows 10／11 检查这些项目，并用正式载荷重新生成发行安装包。
