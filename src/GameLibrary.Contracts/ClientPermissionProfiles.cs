using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLibrary.Contracts;

/// <summary>
/// 第一方客户端在握手时声明的权限集合。
///
/// 宿主至今仍把「未声明 GrantedPermissions」当作不限权，这是为旧客户端和本机脚本保留的
/// 兼容路径；随程序分发的几个入口从今以后全部显式声明，权限范围因此可审计。Tauri 桥接
/// 单独给最小集合：WebView 里的代码是最不可信的一层，即使页面被注入，也只能碰到界面
/// 真正会用的操作。access.admin 是宿主保留能力，任何声明都会被忽略，所以这里不列。
/// </summary>
public static class ClientPermissionProfiles
{
    /// <summary>Tauri 桥接（GameBatchBar、详情、扫描、启动、设置导入）实际用到的操作权限。</summary>
    public static readonly IReadOnlyList<string> TauriBridge =
    [
        "library.read",
        "library.write",
        "scan.manage",
        "launch.execute",
        "settings.write",
        "files.import",
        "tools.configure",
    ];

    /// <summary>关闭窗口时只做 host.stop，别的一概不给。</summary>
    public static readonly IReadOnlyList<string> HostExit = ["host.manage"];

    /// <summary>
    /// CLI / MCP / 桌面端：契约里除 access.admin 以外的全部权限。它们和宿主同属本机
    /// 可信入口，需要覆盖文件导入导出、备份、校验等完整能力；以后新增权限会自动包含。
    /// </summary>
    public static IReadOnlyList<string> Full { get; } = OperationCatalog.Catalog.Permissions
        .Where(permission => !string.Equals(permission, "access.admin", StringComparison.Ordinal))
        .ToArray();
}
