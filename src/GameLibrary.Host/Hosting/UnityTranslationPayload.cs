using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLibrary.Infrastructure.Backups;

namespace GameLibrary.Host.Hosting;

/// <summary>Public fixed releases; no provider HTTP calls. Download hashes are pinned, not trust-on-first-use.</summary>
internal sealed class UnityTranslationPayload
{
    internal const string ReiSha256 = "71DB63000A5487E03D2DBE692C20EB970167DB469394B5DBA2BE5F6343B9D6E8";
    internal const string DeepSeekSha256 = "8FDC8C26E0C3129350541CE84EA0C818D980626E848991A9D1C4AC8FFC4C4CA7";
    internal const string ReiUrl = "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.0.0/XUnity.AutoTranslator-ReiPatcher-5.0.0.zip";
    internal const string DeepSeekUrl = "https://github.com/Tabing010102/DeepSeekTranslate/releases/download/v0.1.14/DeepSeekTranslate.dll";
    internal const string Il2CppPluginUrl = "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.6.2/XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip";
    internal const string Il2CppPluginSha256 = "639392D3EE3C7542CCA98C6C04B384DEEE4961FE965F3D80A4D9C8413884A0F3";
    internal const string Il2CppX64Sha256 = "AA47F95A19AB6FDC5924C3567AB6018805BF198E3662ED972871CCC2A371164A";
    internal const string Il2CppX86Sha256 = "53D1F7505EADFF2DC343CA4F6406B043D35E3F1F91C77347E96BD986A54C3C8D";
    private readonly string _cache;
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLibrary/1.7.8");
        return client;
    }
    public UnityTranslationPayload(string? cache = null) => _cache = cache ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibrary", "UnityTranslation", "payload", "5.0.0-0.1.14");

    public async Task<byte[]> EndpointAsync(CancellationToken ct) => await Download("DeepSeekTranslate.dll", DeepSeekUrl, DeepSeekSha256, ct);

    public async Task<byte[]> FontAsync(string generation, CancellationToken ct)
    {
        var hash = generation switch
        {
            "2018" => "9A799D1B41508B0D840FC80EC20DEFCDBE4904ABDD7B271215DB711C071BD0EC",
            "2019" => "97615AA0A55AB584059D4849D3563347CBB975C0231B77A7E650B385EC3C7D94",
            "2020" => "4CD9190B54731C6E2247D964EE9965C1EF7FE493C4F6EDAAEF3C18CC5525A2D3",
            "2021" => "0889F852792100D180A9936986BB82846FACE45DB1483423857684708E29B9F8",
            "2022" => "0F85774C4C27EAAD1EAD9D72993C8564D103468B679BD4513FB2117C17FED6B4",
            "2023" => "ECB0F1CF24353C0CD7901F4AB052017C44E56782C3E4065CB6EDDD935709AD64",
            "6000" => "1A6FE0028BA1B0AFC8B235F2EED515E9F5D30FCBE5376CE9BF826622938AEDC7",
            _ => throw new InvalidDataException("不支持的 Unity 字库版本")
        };
        var bytes = await Download("Moe-fonts-" + generation + ".zip",
            "https://github.com/sorrowmoil/sorrowmoil-MoeFont-for-XUnity.AutoTranslator/releases/download/Moe/" + generation + ".zip",
            hash, ct, 128 * 1024 * 1024);
        return ExtractFont(bytes, generation);
    }

    internal static byte[] ExtractFont(byte[] bytes, string generation)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 32 || archive.Entries.Any(e => e.FullName.Contains('\\') || e.FullName.StartsWith('/')
            || e.FullName.Contains(':') || e.FullName.Split('/').Any(p => p is "." or "..")
            || ((e.ExternalAttributes >> 16) & 0xf000) == 0xa000))
            throw new InvalidDataException("字体固定包包含非法路径");
        var entries = archive.Entries.Where(e => e.FullName == "xiaolai " + generation).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 16 or > 48 * 1024 * 1024)
            throw new InvalidDataException("字体固定包缺少唯一的受支持字库");
        using var input = entries[0].Open();
        using var output = new MemoryStream();
        var block = new byte[65536];
        int count;
        while ((count = input.Read(block)) > 0)
        {
            if (output.Length + count > 48 * 1024 * 1024) throw new InvalidDataException("字体解压超出大小限制");
            output.Write(block, 0, count);
        }
        var font = output.ToArray();
        var header = Encoding.ASCII.GetString(font, 0, Math.Min(128, font.Length));
        if (!header.StartsWith("UnityFS\0", StringComparison.Ordinal) || !header.Contains(generation + ".", StringComparison.Ordinal))
            throw new InvalidDataException("字体 AssetBundle 格式或 Unity 版本不匹配");
        return font;
    }

    internal static byte[] FontNotice()
    {
        using var stream = typeof(UnityTranslationPayload).Assembly.GetManifestResourceStream("GameLibrary.Host.Resources.UnityFonts.NOTICE.txt")
            ?? throw new InvalidDataException("缺少字体许可说明");
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> Il2CppAsync(string architecture, bool includeLoader, CancellationToken ct)
    {
        if (architecture is not ("x86" or "x64")) throw new InvalidDataException("无法确认 IL2CPP 游戏位数，拒绝安装加载器");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (includeLoader)
        {
            var name = $"BepInEx-Unity.IL2CPP-win-{architecture}-6.0.0-be.704+6b38cee.zip";
            var zip = await Download(name, "https://builds.bepinex.dev/projects/bepinex_be/704/" + Uri.EscapeDataString(name),
                architecture == "x64" ? Il2CppX64Sha256 : Il2CppX86Sha256, ct, 64 * 1024 * 1024);
            foreach (var pair in ExtractIl2CppArchive(zip, loader: true)) files.Add(pair.Key, pair.Value);
        }
        var plugin = await Download("XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip", Il2CppPluginUrl, Il2CppPluginSha256, ct);
        foreach (var pair in ExtractIl2CppArchive(plugin, loader: false)) files.Add(pair.Key, pair.Value);
        return files;
    }

    internal static IReadOnlyDictionary<string, byte[]> ExtractIl2CppArchive(byte[] bytes, bool loader)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        if (archive.Entries.Count > 1024) throw new InvalidDataException("IL2CPP 固定包文件过多");
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..")
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("IL2CPP 固定包包含非法路径");
            if (name.EndsWith('/')) continue;
            var allowed = name.StartsWith("BepInEx/core/", StringComparison.Ordinal)
                || (!loader && name.StartsWith("BepInEx/plugins/", StringComparison.Ordinal))
                || (loader && (name.StartsWith("dotnet/", StringComparison.Ordinal)
                    || name is "winhttp.dll" or "doorstop_config.ini" or ".doorstop_version" or "changelog.txt"));
            if (!allowed || entry.Length > 64 * 1024 * 1024 || (expanded += entry.Length) > 256 * 1024 * 1024)
                throw new InvalidDataException("IL2CPP 固定包包含非允许文件或超出大小限制");
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            if (!files.TryAdd(name, output.ToArray())) throw new InvalidDataException("IL2CPP 固定包包含重复路径");
        }
        string[] required = loader ? ["winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll"]
            : ["BepInEx/core/XUnity.Common.dll", "BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.Core.dll",
                "BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.BepInEx-IL2CPP.dll"];
        if (required.Any(p => !files.ContainsKey(p))) throw new InvalidDataException("IL2CPP 固定包缺少必要文件");
        return files;
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> RuntimeAsync(CancellationToken ct)
    {
        var archive = await Download("XUnity.AutoTranslator-ReiPatcher-5.0.0.zip", ReiUrl, ReiSha256, ct);
        var setup = ExtractSetup(archive);
        var assembly = Assembly.Load(setup); // Known pinned assembly, resources only. EntryPoint is never invoked.
        using var stream = assembly.GetManifestResourceStream("XUnity.AutoTranslator.Setup.Properties.Resources.resources")
            ?? throw new InvalidDataException("固定包缺少安装资源");
        using var reader = new ResourceReader(stream);
        var resources = reader.Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (byte[])entry.Value!);
        var required = new Dictionary<string, string>
        {
            ["0Harmony.dll"] = "_0Harmony",
            ["ExIni.dll"] = "ExIni",
            ["ReiPatcher.exe"] = "ReiPatcher",
            ["Mono.Cecil.dll"] = "Mono_Cecil_0_10_4_0",
            ["MonoMod.RuntimeDetour.dll"] = "MonoMod_RuntimeDetour",
            ["MonoMod.Utils.dll"] = "MonoMod_Utils",
            ["XUnity.Common.dll"] = "XUnity_Common",
            ["XUnity.ResourceRedirector.dll"] = "XUnity_ResourceRedirector",
            ["XUnity.AutoTranslator.Plugin.Core.dll"] = "XUnity_AutoTranslator_Plugin_Core",
            ["XUnity.AutoTranslator.Plugin.ExtProtocol.dll"] = "XUnity_AutoTranslator_Plugin_ExtProtocol",
        };
        var runtime = required.ToDictionary(pair => pair.Key, pair => resources[pair.Value]);
        var patcher = Path.Combine(_cache, "patcher");
        Directory.CreateDirectory(patcher);
        foreach (var pair in new Dictionary<string, string>
        {
            ["ExIni.dll"] = "ExIni",
            ["ReiPatcher.exe"] = "ReiPatcher",
            ["Mono.Cecil.dll"] = "Mono_Cecil",
            ["Mono.Cecil.Inject.dll"] = "Mono_Cecil_Inject",
            ["Mono.Cecil.Mdb.dll"] = "Mono_Cecil_Mdb",
            ["Mono.Cecil.Pdb.dll"] = "Mono_Cecil_Pdb",
            ["Mono.Cecil.Rocks.dll"] = "Mono_Cecil_Rocks",
            ["XUnity.AutoTranslator.Patcher.dll"] = "XUnity_AutoTranslator_Patcher",
        }) UnityTranslationVault.AtomicWrite(Path.Combine(patcher, pair.Key), resources[pair.Value]);
        return runtime;
    }

    internal static byte[] ExtractSetup(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "SetupReiPatcherAndAutoTranslator.exe"
            || archive.Entries[0].Length > 16 * 1024 * 1024)
            throw new InvalidDataException("固定包包含非允许的路径或文件");
        using var input = archive.Entries[0].Open();
        using var result = new MemoryStream();
        input.CopyTo(result);
        return result.ToArray();
    }

    private async Task<byte[]> Download(string name, string url, string hash, CancellationToken ct, int maxBytes = 16 * 1024 * 1024)
    {
        Directory.CreateDirectory(_cache);
        var path = Path.Combine(_cache, name);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct);
            if (Convert.ToHexString(SHA256.HashData(existing)) == hash) return existing;
            throw new InvalidDataException("Unity 翻译缓存 SHA-256 不匹配，拒绝使用");
        }
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("固定下载包超出大小限制");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new InvalidDataException("固定下载包超出大小限制");
            buffer.Write(block, 0, count);
        }
        var bytes = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Unity 翻译下载 SHA-256 不匹配");
        UnityTranslationVault.AtomicWrite(path, bytes);
        ControlAreaStore.WriteAtomic(Path.Combine(_cache, "payload-manifest.json"), JsonSerializer.Serialize(new
        {
            xunity = new { version = "5.0.0", url = ReiUrl, sha256 = ReiSha256 },
            deepSeekTranslate = new { version = "0.1.14", url = DeepSeekUrl, sha256 = DeepSeekSha256 },
            il2cpp = new { xunityVersion = "5.6.2", url = Il2CppPluginUrl, sha256 = Il2CppPluginSha256,
                bepinexVersion = "6.0.0-be.704+6b38cee", x64Sha256 = Il2CppX64Sha256, x86Sha256 = Il2CppX86Sha256 },
        }));
        return bytes;
    }

    public async Task<byte[]> PatchAsync(UnityTranslationLayout layout, IReadOnlyDictionary<string, byte[]> runtime, CancellationToken ct)
    {
        var stage = Path.Combine(_cache, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (var pair in runtime) await File.WriteAllBytesAsync(Path.Combine(stage, pair.Key), pair.Value, ct);
        var bootstrap = Path.Combine(stage, Path.GetFileName(layout.Bootstrap));
        File.Copy(layout.Bootstrap, bootstrap);
        var ini = Path.Combine(stage, "patch.ini");
        await File.WriteAllTextAsync(ini, "[ReiPatcher]\r\nAssembliesDir=" + stage + "\r\nPatchesDir=" + Path.Combine(_cache, "patcher") + "\r\n", ct);
        var output = Path.Combine(stage, "patched.dll");
        var script = Path.Combine(stage, "patch.ps1");
        await File.WriteAllTextAsync(script, PatchScript, ct);
        try
        {
            await RunHelper(script, [Path.Combine(_cache, "patcher"), ini, bootstrap, output, layout.Managed], ct);
            if (!UnityTranslationInspection.HasBootstrap(output)) throw new InvalidDataException("离线补丁未生成有效 Bootstrap 引用");
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            // Staging is app-owned, contains no credentials, and never includes game directories/saves.
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    public async Task VerifyEndpointAsync(UnityTranslationLayout layout, CancellationToken ct)
    {
        var endpoint = Path.Combine(layout.Translators, "DeepSeekTranslate.dll");
        if (!UnityTranslationInspection.IsAssembly(endpoint, "DeepSeekTranslate", "DeepSeekTranslateEndpoint"))
            throw new InvalidDataException("DeepSeekTranslate 端点元数据校验失败");
        if (layout.Runtime == "il2cpp")
        {
            // Interop assemblies only exist after the first game run. Inspect PE metadata, never load CoreCLR plugins into PowerShell's CLR.
            if (!UnityTranslationInspection.HasEndpointId(endpoint, "DeepSeekTranslate")
                || !UnityTranslationInspection.IsAssembly(layout.Core, "XUnity.AutoTranslator.Plugin.Core"))
                throw new InvalidDataException("IL2CPP 翻译核心或端点元数据校验失败");
            return;
        }
        var script = Path.Combine(_cache, "verify-endpoint.ps1");
        Directory.CreateDirectory(_cache);
        await File.WriteAllTextAsync(script, VerifyScript, ct);
        await RunHelper(script, [layout.Managed, Path.GetDirectoryName(layout.Core)!, layout.Translators, endpoint], ct);
    }

    private static async Task RunHelper(string script, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidDataException("无法启动离线验证助手");
        // Never return helper output: loader exceptions can contain game configuration values.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(45), ct); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidDataException(process.ExitCode == 3
            ? "离线补丁无法解析 Unity 模块依赖；未启动游戏或请求翻译 API"
            : "离线插件加载/补丁验证失败；未启动游戏或请求翻译 API");
    }

    internal const string PatchScript = """
        param($tools, $ini, $inputAssembly, $outputAssembly, $managed)
        $ErrorActionPreference = 'Stop'
        try {
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'ExIni.dll')) | Out-Null
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'Mono.Cecil.dll')) | Out-Null
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'Mono.Cecil.Inject.dll')) | Out-Null
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'ReiPatcher.exe')) | Out-Null
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'XUnity.AutoTranslator.Patcher.dll')) | Out-Null
          [ReiPatcher.RPConfig]::ConfigFilePath = $ini
          [ReiPatcher.RPConfig]::ConfigFile = [ExIni.IniFile]::FromFile($ini)
          $patcher = New-Object XUnity.AutoTranslator.Patcher.Patcher
          $patcher.PrePatch()
          $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
          $resolver.AddSearchDirectory((Split-Path -Parent $inputAssembly))
          if ($managed) { $resolver.AddSearchDirectory($managed) }
          $parameters = New-Object Mono.Cecil.ReaderParameters
          $parameters.AssemblyResolver = $resolver
          $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputAssembly, $parameters)
          $arguments = New-Object ReiPatcher.Patch.PatcherArguments($assembly, $inputAssembly, $false)
          if (-not $patcher.CanPatch($arguments)) { exit 2 }
          $patcher.Patch($arguments)
          $assembly.Write($outputAssembly)
          exit 0
        } catch {
          $cause = $_.Exception
          while ($cause) {
            if ($cause.GetType().FullName -eq 'Mono.Cecil.AssemblyResolutionException') { exit 3 }
            $cause = $cause.InnerException
          }
          exit 1
        }
        """;

    internal const string VerifyScript = """
        param($managed, $coreDirectory, $translators, $endpoint)
        $ErrorActionPreference = 'Stop'
        try {
          # CLR callbacks must not execute a PowerShell scriptblock: resolving PS dependencies can recurse.
          Add-Type -TypeDefinition @'
        using System;
        using System.IO;
        using System.Reflection;
        using System.Collections.Generic;
        public static class GameLibraryOfflineResolver {
          static string[] directories;
          static HashSet<string> resolving = new HashSet<string>();
          public static void Install(string[] paths) {
            directories = paths;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
          }
          static Assembly Resolve(object sender, ResolveEventArgs args) {
            lock(resolving) {
              if(!resolving.Add(args.Name)) return null;
              try {
                string name = new AssemblyName(args.Name).Name + ".dll";
                if(Path.GetFileName(name) != name) return null;
                foreach(string directory in directories) {
                  string path = Path.Combine(directory, name);
                  if(File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
              } finally { resolving.Remove(args.Name); }
            }
          }
        }
        '@
          [GameLibraryOfflineResolver]::Install(@($coreDirectory, $managed, $translators))
          $assembly = [Reflection.Assembly]::LoadFrom($endpoint)
          $type = $assembly.GetType('DeepSeekTranslate.DeepSeekTranslateEndpoint', $true)
          if (-not ($type.GetInterfaces() | Where-Object { $_.FullName -eq 'XUnity.AutoTranslator.Plugin.Core.Endpoints.ITranslateEndpoint' })) { exit 2 }
          # Resolve endpoint interface and all methods without constructing it or invoking Initialize/Translate.
          $type.GetMethods() | ForEach-Object { $_.ReturnType.FullName | Out-Null; $_.GetParameters() | Out-Null }
          exit 0
        } catch { exit 1 }
        """;
}
