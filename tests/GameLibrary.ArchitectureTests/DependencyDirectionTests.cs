using System.Reflection;
using Xunit;

namespace GameLibrary.ArchitectureTests;

/// <summary>
/// 依赖方向门禁（主策划案 3.2）：
/// Domain ← Application ← Infrastructure；Contracts 独立；
/// HostClient 被 Desktop/Cli/Mcp 引用；三入口不触碰 Infrastructure/Host。
/// 运行时引用集只会少于工程引用集，因此本测试防的是“越界使用”而非“漏引”。
/// </summary>
public sealed class DependencyDirectionTests
{
    private const string Prefix = "GameLibrary";

    /// <summary>键为程序集全名；CLI 发布名固定为小写 gamelibrary。</summary>
    public static TheoryData<string, string[]> ForbiddenReferences => new()
    {
        { "GameLibrary.Domain", ["GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Contracts", "GameLibrary.Host", "GameLibrary.HostClient", "GameLibrary.Desktop", "gamelibrary", "GameLibrary.Mcp"] },
        { "GameLibrary.Application", ["GameLibrary.Infrastructure", "GameLibrary.Contracts", "GameLibrary.Host", "GameLibrary.HostClient", "GameLibrary.Desktop", "gamelibrary", "GameLibrary.Mcp"] },
        { "GameLibrary.Infrastructure", ["GameLibrary.Contracts", "GameLibrary.Host", "GameLibrary.HostClient", "GameLibrary.Desktop", "gamelibrary", "GameLibrary.Mcp"] },
        { "GameLibrary.Contracts", ["GameLibrary.Domain", "GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Host", "GameLibrary.HostClient", "GameLibrary.Desktop", "gamelibrary", "GameLibrary.Mcp"] },
        { "GameLibrary.HostClient", ["GameLibrary.Domain", "GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Host", "GameLibrary.Desktop", "gamelibrary", "GameLibrary.Mcp"] },
        { "GameLibrary.Desktop", ["GameLibrary.Domain", "GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Host"] },
        { "gamelibrary", ["GameLibrary.Domain", "GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Host"] },
        { "GameLibrary.Mcp", ["GameLibrary.Domain", "GameLibrary.Application", "GameLibrary.Infrastructure", "GameLibrary.Host"] },
    };

    [Theory]
    [MemberData(nameof(ForbiddenReferences))]
    public void Assembly_DoesNotReferenceForbiddenPeers(string assemblyName, string[] forbidden)
    {
        var assembly = Assembly.Load(assemblyName);
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith(Prefix + ".", StringComparison.Ordinal)
                || name.Equals("gamelibrary", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var violations = forbidden
            .Where(referenced.Contains)
            .ToList();

        Assert.True(violations.Count == 0,
            $"{assemblyName} 不得引用：{string.Join(", ", violations)}；实际引用：{string.Join(", ", referenced)}");
    }
}
