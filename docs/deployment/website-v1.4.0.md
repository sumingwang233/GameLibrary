# GameLibrary 网站 v1.4.0 发布记录

日期：2026-10-21。维护者确认本地预览视觉，并明确授权上传 GitHub、部署线上版本。

## 范围与来源

- 从远端 main `3d868f1523216e6998bcf6921cb5d3f989824751` 建立独立检出，迁入本地 `9e2430c` 的网站文件及两张参考海报。
- 本次不迁入本地尚未上传的桌面程序与安装器改动。下载仍对应稳定版 v1.7.13；网站独立版本为 1.4.0。
- 发布依赖使用 Next.js 16.3.8、source-map-js 1.2.2。依据：[Next.js 修复公告](https://github.com/advisories/GHSA-3w37-wq28-93x7)、[source-map-js 修复公告](https://github.com/advisories/GHSA-68fv-2mgg-jv7q)。没有运行 `npm audit fix --force` 或跨小版本升级。
- 前期代码图谱已随视觉重构更新；依赖清单的额外影响查询提示该类文件未被索引，不能把 0 节点当作无影响证明。

## 检查与发布

| 项目 | 结果 |
|---|---|
| Next.js 16.3.7 初始生产构建与四语言导出 | passed |
| 初始类型检查与现有两项主题测试 | passed |
| 初始网站静态门禁与负例自检 | passed |
| 修补依赖后的最终 GitHub Website 门禁 | pending |
| GitHub Pages 部署 | pending |
| 线上四语言、样式与新插画核对 | pending |
| 多尺寸浏览器与完整交互验收 | not-run；维护者已确认本地预览视觉 |

最终部署必须使用 `.github/workflows/website.yml` 的 `workflow_dispatch publish=true`，由 check 通过后才能执行 deploy。初始本地检查使用修补前的依赖，不代替最终提交的 CI 结果。

线上地址：[https://sumingwang233.github.io/GameLibrary/](https://sumingwang233.github.io/GameLibrary/)。工作流与实际结果在发布完成后补齐。
