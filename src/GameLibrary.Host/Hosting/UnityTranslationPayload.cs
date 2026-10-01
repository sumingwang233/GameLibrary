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
    private readonly string _cache;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };
    public UnityTranslationPayload(string? cache = null) => _cache = cache ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibrary", "UnityTranslation", "payload", "5.0.0-0.1.14");

    public async Task<byte[]> EndpointAsync(CancellationToken ct) => await Download("DeepSeekTranslate.dll", DeepSeekUrl, DeepSeekSha256, ct);

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

    private async Task<byte[]> Download(string name, string url, string hash, CancellationToken ct)
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
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidDataException("固定下载包超出大小限制");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("固定下载包超出大小限制");
            buffer.Write(block, 0, count);
        }
        var bytes = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Unity 翻译下载 SHA-256 不匹配");
        UnityTranslationVault.AtomicWrite(path, bytes);
        ControlAreaStore.WriteAtomic(Path.Combine(_cache, "payload-manifest.json"), JsonSerializer.Serialize(new
        {
            xunity = new { version = "5.0.0", url = ReiUrl, sha256 = ReiSha256 },
            deepSeekTranslate = new { version = "0.1.14", url = DeepSeekUrl, sha256 = DeepSeekSha256 },
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
            await RunHelper(script, [Path.Combine(_cache, "patcher"), ini, bootstrap, output], ct);
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
        if (process.ExitCode != 0) throw new InvalidDataException("离线插件加载/补丁验证失败；未启动游戏或请求翻译 API");
    }

    internal const string PatchScript = """
        param($tools, $ini, $inputAssembly, $outputAssembly)
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
          $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputAssembly)
          $arguments = New-Object ReiPatcher.Patch.PatcherArguments($assembly, $inputAssembly, $false)
          if (-not $patcher.CanPatch($arguments)) { exit 2 }
          $patcher.Patch($arguments)
          $assembly.Write($outputAssembly)
          exit 0
        } catch { exit 1 }
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
