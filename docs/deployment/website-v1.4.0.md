# GameLibrary 网站 v1.4.0 发布记录

日期：2026-10-21。维护者确认本地预览视觉，并明确授权上传 GitHub、部署线上版本。

- 官网：[GameLibrary](https://sumingwang233.github.io/GameLibrary/)
- 发布源码：[031587d](https://github.com/sumingwang233/GameLibrary/commit/031587d48ef75d783a8d0975bac0d8694f6360df)
- 发布工作流：[Website · 39464025286](https://github.com/sumingwang233/GameLibrary/actions/runs/39464025286)

## 范围与来源

从远端 main `3d868f1523216e6998bcf6921cb5d3f989824751` 建立独立检出，迁入本地 `9e2430c` 的网站文件及两张参考海报。原工作区尚未上传的桌面程序和安装器改动没有随本次网站推送。网站独立版本为 1.4.0，下载仍对应稳定版 v1.7.13。

发布依赖修补为 Next.js 16.3.8、source-map-js 1.2.2。依据：[Next.js 修复公告](https://github.com/advisories/GHSA-3w37-wq28-93x7)、[source-map-js 修复公告](https://github.com/advisories/GHSA-68fv-2mgg-jv7q)。只采用最小补丁，没有跨小版本升级。

前期代码图谱已随视觉重构更新。依赖清单的额外影响查询提示该类文件未被索引，不能把 0 节点当作无影响证明。

## 检查结果

| 项目 | 结果 |
|---|---|
| Next.js 16.3.8 生产构建、四语言静态导出 | passed；本地与 GitHub CI |
| 类型检查、现有两项主题测试 | passed；本地与 GitHub CI |
| 网站静态门禁、负例自检 | passed；本地与 GitHub CI |
| 完整 npm 依赖审计 | passed；0 项公告命中 |
| GitHub Website check | passed；28 秒 |
| GitHub Pages deploy | passed；9 秒 |
| 线上四语言页面 | passed；均 HTTP 200，语言标记和新主视觉/流程引用正确 |
| 线上样式与原图一致性 | passed；1 份 CSS、2 张插画 PNG 的 SHA-256 与本地一致 |
| 响应式插画预览 | passed；两图各 640/960/1440px WebP 均 HTTP 200 |
| 多尺寸浏览器与完整交互验收 | not-run；维护者已确认本地预览视觉 |

发布使用 `.github/workflows/website.yml` 的 `workflow_dispatch publish=true`，check 通过后才运行 deploy。没有创建桌面产品 tag、Release 或安装包。

线上请求的结构化结果见 [website-v1.4.0-online.json](website-v1.4.0-online.json)。HTTP、内容与字节核对不等于浏览器响应式或交互验收。

## 发布异常与重试

首次触发时 GitHub 返回 HTTP 500，并生成了[没有任何作业的 queued 记录](https://github.com/sumingwang233/GameLibrary/actions/runs/39462824248)；普通取消和强制取消均返回“尚未进入队列”的 HTTP 409。核实仓库 Actions 已启用后重新触发，同一源码的第二次工作流正常执行并完成部署。没有重复修改或回退网站源码。
