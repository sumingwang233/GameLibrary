using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace GameLibrary.Host.Hosting;

internal sealed record UnityTranslationLayout(string ExecutablePath, string Root, string Data, string Managed,
    string Bootstrap, string Loader, string Core, string Translators, string Config, string? Reason = null,
    string Runtime = "mono", string? Architecture = null, bool ReiConflict = false);

/// <summary>Inspect actual launch EXE and managed assembly metadata; a BepInEx directory alone is not an active loader.</summary>
internal static class UnityTranslationInspection
{
    public static UnityTranslationLayout Inspect(string executable)
    {
        executable = Path.GetFullPath(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var name = Path.GetFileNameWithoutExtension(executable);
        var data = Path.Combine(directory, name + "_Data");
        // Some releases put the EXE inside its own *_Data directory.
        if (!Directory.Exists(data) && directory.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(Path.Combine(directory, "Managed")) || Directory.Exists(Path.Combine(directory, "il2cpp_data")))) data = directory;
        // XUnity v5 uses parent(Application.dataPath), even when the EXE is itself inside *_Data.
        var root = Path.GetDirectoryName(data)!;
        var managed = Path.Combine(data, "Managed");
        var bootstrap = Path.Combine(managed, "UnityEngine.CoreModule.dll");
        if (!File.Exists(bootstrap)) bootstrap = Path.Combine(managed, "UnityEngine.dll");
        var config = Path.Combine(root, "AutoTranslator", "Config.ini");
        var core = Path.Combine(managed, "XUnity.AutoTranslator.Plugin.Core.dll");
        var translators = Path.Combine(managed, "Translators");
        var runtime = File.Exists(Path.Combine(root, "GameAssembly.dll")) || File.Exists(Path.Combine(directory, "GameAssembly.dll"))
            || Directory.Exists(Path.Combine(data, "il2cpp_data")) ? "il2cpp" : "mono";
        var architecture = ReadArchitecture(executable);
        if (runtime == "il2cpp")
        {
            config = Path.Combine(directory, "BepInEx", "config", "AutoTranslatorConfig.ini");
            core = Path.Combine(directory, "BepInEx", "plugins", "XUnity.AutoTranslator", "XUnity.AutoTranslator.Plugin.Core.dll");
            translators = Path.Combine(Path.GetDirectoryName(core)!, "Translators");
        }
        UnityTranslationLayout Result(string loader, string? reason = null) => new(executable, root, data, managed,
            bootstrap, loader, core, translators, config, reason, runtime, architecture);

        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(data)) return Result("unknown", "未找到所选 EXE 对应的 Unity Data 目录");
        RejectReparse(root, executable);
        RejectReparse(root, managed);
        RejectReparse(root, config);
        if (runtime == "il2cpp")
        {
            var gameAssembly = Path.Combine(directory, "GameAssembly.dll");
            RejectReparse(root, gameAssembly);
            if (architecture is null || ReadArchitecture(gameAssembly) != architecture)
                return Result("unknown", "无法确认 IL2CPP 游戏 EXE 与 GameAssembly 位数一致");
        }
        else if (!File.Exists(bootstrap) || !File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
            return Result("unknown", "无法确认受支持的 Unity Mono 游戏");

        var rei = runtime == "mono" && HasBootstrap(bootstrap) && IsAssembly(core, "XUnity.AutoTranslator.Plugin.Core");
        var proxy = File.Exists(Path.Combine(directory, "winhttp.dll")) || File.Exists(Path.Combine(directory, "version.dll"))
            || File.Exists(Path.Combine(directory, "doorstop.dll"));
        var doorstop = Path.Combine(directory, "doorstop_config.ini");
        var active = false;
        if (proxy && File.Exists(doorstop))
        {
            var ini = UnityTranslationIni.Read(doorstop);
            var enabled = (ini.Get("General", "enabled") ?? ini.Get("UnityDoorstop", "enabled"))?.Split(['#', ';'], 2)[0].Trim();
            active = enabled is not null && (enabled.Equals("true", StringComparison.OrdinalIgnoreCase) || enabled == "1");
            if (enabled is null || !(enabled.Equals("false", StringComparison.OrdinalIgnoreCase) || enabled == "0" || active))
                return Result("unknown", "无法确认 Doorstop 加载器启用状态");
        }
        else if (proxy) return Result("unknown", "检测到未知代理 DLL，保留现有文件");
        if (active)
        {
            config = Path.Combine(directory, "BepInEx", "config", "AutoTranslatorConfig.ini");
            RejectReparse(root, config);
            var ini = UnityTranslationIni.Read(doorstop);
            var target = ini.Get("UnityDoorstop", "targetAssembly") ?? ini.Get("General", "target_assembly");
            if (string.IsNullOrWhiteSpace(target) || !target.Replace('\\', '/').StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase))
                return Result("conflict", "Doorstop 启动目标不是可确认的 BepInEx 核心");
            var targetPath = Path.GetFullPath(Path.Combine(directory, target));
            RejectReparse(root, targetPath);
            if (!File.Exists(targetPath)) return Result("conflict", "已启用的 BepInEx 启动目标不存在");
            if (runtime == "mono" && !(IsAssembly(targetPath, "BepInEx.Preloader")
                || IsAssembly(targetPath, "BepInEx.Unity.Mono.Preloader")))
                return Result("conflict", "Mono 游戏的 BepInEx 启动目标无法验证，保留现有模组");
            if (runtime == "il2cpp")
            {
                if (!IsAssembly(targetPath, "BepInEx.Unity.IL2CPP")) return Result("conflict", "IL2CPP 游戏需要专用 BepInEx 加载器，保留现有模组");
                var coreclr = ini.Get("Il2Cpp", "coreclr_path");
                var corlib = ini.Get("Il2Cpp", "corlib_dir");
                if (string.IsNullOrWhiteSpace(coreclr) || string.IsNullOrWhiteSpace(corlib))
                    return Result("conflict", "IL2CPP 加载器缺少 CoreCLR 配置");
                var coreclrPath = Path.GetFullPath(Path.Combine(directory, coreclr));
                var corlibPath = Path.GetFullPath(Path.Combine(directory, corlib, "mscorlib.dll"));
                RejectReparse(root, coreclrPath);
                RejectReparse(root, corlibPath);
                if (ReadArchitecture(coreclrPath) != architecture || !File.Exists(corlibPath))
                    return Result("conflict", "IL2CPP 加载器运行环境缺失或位数不匹配");
                var proxyPath = new[] { "winhttp.dll", "version.dll", "doorstop.dll" }.Select(p => Path.Combine(directory, p)).First(File.Exists);
                RejectReparse(root, proxyPath);
                if (ReadArchitecture(proxyPath) != architecture) return Result("conflict", "IL2CPP 代理 DLL 位数不匹配");
            }
            var pluginDirectory = Path.Combine(directory, "BepInEx", "plugins");
            var bepinCore = Path.Combine(directory, "BepInEx", "core");
            if (!Directory.Exists(bepinCore) || !(File.Exists(Path.Combine(bepinCore, "BepInEx.dll"))
                || File.Exists(Path.Combine(bepinCore, "BepInEx.Core.dll")))) return Result("conflict", "检测到其他已启用的 Doorstop 加载器");
            if (runtime == "mono" && !(IsAssembly(Path.Combine(bepinCore, "BepInEx.dll"), "BepInEx")
                || IsAssembly(Path.Combine(bepinCore, "BepInEx.Core.dll"), "BepInEx.Core")))
                return Result("conflict", "BepInEx Mono 核心无法验证，保留现有模组");
            if (!Directory.Exists(pluginDirectory)) return runtime == "il2cpp" ? Result("bepinex-empty")
                : Result("conflict", "已启用的 BepInEx 未安装受支持翻译插件");
            RejectReparse(root, pluginDirectory);
            var plugins = Directory.EnumerateFiles(pluginDirectory, "XUnity.AutoTranslator.Plugin.BepIn*.dll", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive }).ToArray();
            if (plugins.Length == 0 && runtime == "il2cpp" && !File.Exists(core)) return Result("bepinex-empty");
            if (plugins.Length != 1) return Result("conflict", "已启用的 BepInEx 翻译入口缺失或不唯一");
            var pluginRoot = Path.GetDirectoryName(plugins[0])!;
            core = Path.Combine(pluginRoot, "XUnity.AutoTranslator.Plugin.Core.dll");
            translators = Path.Combine(pluginRoot, "Translators");
            if (!IsAssembly(core, "XUnity.AutoTranslator.Plugin.Core")) return Result("conflict", "现有 BepInEx 翻译核心无法验证");
            if (runtime == "il2cpp" && !IsAssembly(plugins[0], "XUnity.AutoTranslator.Plugin.BepInEx-IL2CPP"))
                return Result("conflict", "IL2CPP 游戏不能使用 Mono 版翻译插件");
            if (runtime == "mono" && !IsAssembly(plugins[0], "XUnity.AutoTranslator.Plugin.BepInEx"))
                return Result("conflict", "BepInEx Mono 翻译入口无法验证，保留现有模组");
            return Result("bepinex") with { ReiConflict = rei };
        }
        if (rei) return Result("rei");
        if (Directory.Exists(Path.Combine(root, "MelonLoader")) || File.Exists(Path.Combine(managed, "IPA.Injector.dll"))
            || File.Exists(Path.Combine(managed, "UnityInjector.dll")) || File.Exists(core)
            || Directory.Exists(Path.Combine(root, "ReiPatcher"))
            || (runtime == "il2cpp" && !HasOnlyInactiveIl2CppOutputs(root, directory)))
            return Result("unknown", "检测到现有或不完整的加载器，保留现有模组并等待人工检查");
        return Result("none");
    }

    /// <summary>Inspect enabled Chinese configuration and a constant endpoint ID without loading plugin code.</summary>
    public static bool IsConfiguredChineseTranslator(UnityTranslationLayout layout)
    {
        if (layout.Reason is not null || layout.ReiConflict || layout.Loader is not ("rei" or "bepinex")) return false;
        RejectReparse(layout.Root, layout.Config);
        var ini = UnityTranslationIni.Read(layout.Config);
        var language = ini.Get("General", "Language")?.Trim().ToLowerInvariant();
        if (language is not ("zh" or "zh-cn" or "zh-hans")) return false;
        var enabled = ini.Get("Behaviour", "EnableTranslation")?.Split(['#', ';'], 2)[0].Trim();
        if (enabled is not null && !enabled.Equals("true", StringComparison.OrdinalIgnoreCase) && enabled != "1") return false;
        var endpoint = ini.Get("Service", "Endpoint")?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (endpoint == "DeepSeekTranslate" && string.IsNullOrWhiteSpace(ini.Get("DeepSeek", "ApiKey"))) return false;
        IEnumerable<string> assemblies = Directory.Exists(layout.Translators)
            ? Directory.EnumerateFiles(layout.Translators, "*.dll", SearchOption.TopDirectoryOnly).Take(64).Prepend(layout.Core)
            : [layout.Core];
        foreach (var assembly in assemblies)
        {
            RejectReparse(layout.Root, assembly);
            if (HasEndpointId(assembly, endpoint)) return true;
        }
        return false;
    }

    internal static bool HasEndpointId(string assembly, string endpoint)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if (metadata.GetString(method.Name) != "get_Id" || method.RelativeVirtualAddress == 0) continue;
                var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                var endpointTypes = type.GetInterfaceImplementations().Select(h => metadata.GetInterfaceImplementation(h).Interface).Append(type.BaseType);
                if (!endpointTypes.Any(h => h.Kind == HandleKind.TypeReference
                    && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)h).Namespace).StartsWith("XUnity.AutoTranslator.Plugin.Core.Endpoints", StringComparison.Ordinal)
                    && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)h).Name) is "ITranslateEndpoint" or "HttpEndpoint" or "AbstractTranslateEndpoint")) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                // ponytail: recognize constant ID getters only; dynamic IDs keep the tag for explicit review.
                var offset = il.Length == 6 && il[5] == 0x2a ? 0
                    : il.Length == 11 && il[0] == 0 && il[6] == 0x0a && il[7] == 0x2b && il[8] == 0 && il[9] == 6 && il[10] == 0x2a ? 1 : -1;
                if (offset < 0 || il[offset] != 0x72) continue;
                var token = BitConverter.ToInt32(il, offset + 1);
                if ((token & unchecked((int)0xff000000)) != 0x70000000) continue;
                var id = metadata.GetUserString(System.Reflection.Metadata.Ecma335.MetadataTokens.UserStringHandle(token & 0x00ffffff));
                if (id == endpoint) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        return false;
    }

    public static bool HasBootstrap(string assembly)
    {
        try
        {
            return UnityTranslationAssembly.HasBootstrap(File.ReadAllBytes(assembly));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasOnlyInactiveIl2CppOutputs(string root, string directory)
    {
        // Restore removes installed binaries, not generated caches. Only recognized inactive output is tolerated;
        // no proxy/core/plugin is ignored and nothing here is deleted or overwritten on retry.
        foreach (var folder in new[] { "BepInEx", "dotnet" })
        {
            var start = Path.Combine(directory, folder);
            if (!Directory.Exists(start)) continue;
            RejectReparse(root, start);
            foreach (var path in Directory.EnumerateFileSystemEntries(start, "*", SearchOption.AllDirectories))
            {
                RejectReparse(root, path);
                if (Directory.Exists(path)) continue;
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative is "BepInEx/config/BepInEx.cfg" or "BepInEx/config/AutoTranslatorConfig.ini"
                    or "BepInEx/LogOutput.log" or "BepInEx/ErrorLog.log"
                    or "BepInEx/interop/MethodXrefScanCache.db" or "BepInEx/interop/MethodAddressToToken.db"
                    or "BepInEx/interop/assembly-hash.txt") continue;
                if (relative.StartsWith("BepInEx/Translation/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                if ((Path.GetDirectoryName(relative)?.Replace('\\', '/') is "BepInEx/interop" or "BepInEx/unity-libs")
                    && IsGeneratedLibrary(path, relative.StartsWith("BepInEx/interop/", StringComparison.Ordinal))) continue;
                return false;
            }
        }
        return true;
    }

    private static bool IsGeneratedLibrary(string path, bool interop)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (name != Path.GetFileNameWithoutExtension(path) || pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress != 0) return false;
            var references = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
            return interop ? references.Contains("Il2CppInterop.Runtime", StringComparer.Ordinal)
                : name.StartsWith("UnityEngine", StringComparison.Ordinal)
                    && references.All(r => r is "netstandard" or "mscorlib" || r.StartsWith("UnityEngine", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsAssembly(string path, string assemblyName, string? typeName = null)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return metadata.GetString(metadata.GetAssemblyDefinition().Name) == assemblyName
                && (typeName is null || metadata.TypeDefinitions.Any(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == typeName));
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    internal static string? ReadArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine switch { Machine.Amd64 => "x64", Machine.I386 => "x86", _ => null };
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                // Unable to inspect a same-name process is not proof that the game is stopped.
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
            }
        }
        return false;
    }

    public static void RejectReparse(string root, string target)
    {
        root = Path.GetFullPath(root);
        target = Path.GetFullPath(target);
        if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目标文件超出游戏目录");
        for (var path = target; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("游戏目录包含链接，拒绝自动修改");
        }
    }
}
