using GameLibrary.Contracts;
using Xunit;

namespace GameLibrary.ContractTests;

/// <summary>
/// 第一方客户端的握手权限名单必须和契约里声明的权限名对得上：
/// 拼错一个名字，那个操作就会在运行时被拒，而编译和目录校验都不会报错。
/// </summary>
public sealed class ClientPermissionProfileTests
{
    private static readonly IReadOnlyList<string> CatalogPermissions = OperationCatalog.Catalog.Permissions;

    [Fact]
    public void Full_IsWholeCatalogExceptHostReservedAdmin()
    {
        var expected = CatalogPermissions.Where(p => p != "access.admin").OrderBy(p => p, StringComparer.Ordinal);
        var actual = ClientPermissionProfiles.Full.OrderBy(p => p, StringComparer.Ordinal);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("access.admin", ClientPermissionProfiles.Full);
    }

    [Fact]
    public void TauriBridge_IsLeastPrivilegeSubsetOfCatalog()
    {
        foreach (var permission in ClientPermissionProfiles.TauriBridge)
        {
            Assert.Contains(permission, CatalogPermissions);
        }

        Assert.DoesNotContain("access.admin", ClientPermissionProfiles.TauriBridge);
        Assert.DoesNotContain("host.manage", ClientPermissionProfiles.TauriBridge);
        Assert.DoesNotContain("files.export", ClientPermissionProfiles.TauriBridge);
        Assert.DoesNotContain("backup.create", ClientPermissionProfiles.TauriBridge);
        Assert.DoesNotContain("backup.restore", ClientPermissionProfiles.TauriBridge);
        Assert.DoesNotContain("tools.verify", ClientPermissionProfiles.TauriBridge);
    }

    [Fact]
    public void HostExit_DeclaresOnlyShutdownPermission()
    {
        Assert.Single(ClientPermissionProfiles.HostExit);
        Assert.Equal("host.manage", ClientPermissionProfiles.HostExit[0]);
    }

    [Fact]
    public void Profiles_HaveNoDuplicates()
    {
        Assert.Equal(ClientPermissionProfiles.TauriBridge.Count, ClientPermissionProfiles.TauriBridge.Distinct().Count());
        Assert.Equal(ClientPermissionProfiles.Full.Count, ClientPermissionProfiles.Full.Distinct().Count());
    }
}
