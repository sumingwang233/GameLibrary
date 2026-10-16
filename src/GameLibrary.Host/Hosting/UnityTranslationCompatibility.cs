using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace GameLibrary.Host.Hosting;

internal static class UnityTranslationCompatibility
{
    internal static IReadOnlyList<string> ShadowingMonoAssemblies(UnityTranslationLayout layout)
    {
        if (layout.Runtime != "mono" || layout.Loader != "bepinex") return [];
        var result = new List<string>();
        // Mono probes Managed before BepInEx's resolver. Removing only the Rei bootstrap call leaves these active.
        foreach (var name in new[] { "XUnity.AutoTranslator.Plugin.Core.dll", "XUnity.AutoTranslator.Plugin.ExtProtocol.dll",
            "XUnity.Common.dll", "XUnity.ResourceRedirector.dll" })
        {
            var path = Path.Combine(layout.Managed, name);
            UnityTranslationInspection.RejectReparse(layout.Root, path);
            if (File.Exists(path) && !string.Equals(path, layout.Core, StringComparison.OrdinalIgnoreCase)) result.Add(path);
        }
        return result;
    }

    internal static bool IsBepInEx6Mono(UnityTranslationLayout layout)
    {
        if (layout.Runtime != "mono" || layout.Loader != "bepinex") return false;
        var core = Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, "BepInEx", "core", "BepInEx.Core.dll");
        UnityTranslationInspection.RejectReparse(layout.Root, core);
        return File.Exists(core) && UnityTranslationFonts.PluginVersion(core)?.Major == 6;
    }

    internal static bool HasBepInEx6Entry(UnityTranslationLayout layout)
    {
        var directory = Path.GetDirectoryName(layout.Core)!;
        foreach (var path in new[] { Path.Combine(directory, "XUnity.AutoTranslator.Plugin.BepInEx.dll"),
            Path.Combine(Path.GetDirectoryName(directory)!, "XUnity.ResourceRedirector", "XUnity.ResourceRedirector.BepInEx.dll") })
        {
            UnityTranslationInspection.RejectReparse(layout.Root, path);
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            if (!metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Select(type => type.BaseType)
                .Where(handle => handle.Kind == HandleKind.TypeReference).Select(handle => metadata.GetTypeReference((TypeReferenceHandle)handle)).Any(type =>
                    metadata.GetString(type.Namespace) == "BepInEx.Unity.Mono" && metadata.GetString(type.Name) == "BaseUnityPlugin"
                    && type.ResolutionScope.Kind == HandleKind.AssemblyReference
                    && metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "BepInEx.Unity.Mono")) return false;
        }
        return true;
    }

    internal static bool NeedsFontBridge(UnityTranslationLayout layout)
    {
        if (layout.Runtime != "il2cpp" || UnityTranslationFonts.Generation(layout) != "6000") return false;
        var font = Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, "BepInEx", "interop", "UnityEngine.TextRenderingModule.dll");
        UnityTranslationInspection.RejectReparse(layout.Root, font);
        UnityTranslationInspection.RejectReparse(layout.Root, layout.Core);
        return File.Exists(layout.Core) && File.Exists(font)
            && !UnityTranslationAssembly.HasArrayFontFactory(File.ReadAllBytes(layout.Core));
    }

    internal static bool NeedsRepair(UnityTranslationLayout layout) => IsBepInEx6Mono(layout) && !HasBepInEx6Entry(layout)
        || NeedsFontBridge(layout) || ShadowingMonoAssemblies(layout).Count != 0;
}

internal sealed partial class UnityTranslationPayload
{
    internal sealed record CompatibilityFile(byte[] Bytes, string OriginalHash);

    internal async Task<IReadOnlyDictionary<string, string>> ShadowingMonoAssembliesAsync(UnityTranslationLayout layout, CancellationToken ct)
    {
        var paths = UnityTranslationCompatibility.ShadowingMonoAssemblies(layout);
        if (paths.Count == 0) return new Dictionary<string, string>();
        var legacyCore = Path.Combine(layout.Managed, "XUnity.AutoTranslator.Plugin.Core.dll");
        var version = File.Exists(legacyCore) ? UnityTranslationFonts.PluginVersion(legacyCore)?.ToString(3) : null;
        var versions = version is null ? new[] { "5.0.0", "5.4.5", "5.6.2" } : new[] { version };
        var originals = new List<IReadOnlyDictionary<string, byte[]>>();
        foreach (var candidate in versions) originals.Add(await RuntimeAsync(ct, candidate));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("旧 Mono 翻译程序集与固定原版不一致，保留文件并等待人工检查");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!originals.Any(files => files.TryGetValue(Path.GetFileName(path), out var bytes)
                && Convert.ToHexString(SHA256.HashData(bytes)) == hash))
                throw new InvalidDataException("旧 Mono 翻译程序集与固定原版不一致，保留文件并等待人工检查");
            result.Add(path, hash);
        }
        return result;
    }

    internal async Task<IReadOnlyDictionary<string, CompatibilityFile>> BepInExMonoAsync(UnityTranslationLayout layout, CancellationToken ct)
    {
        var bepinex6 = UnityTranslationCompatibility.IsBepInEx6Mono(layout);
        if (bepinex6 && UnityTranslationCompatibility.HasBepInEx6Entry(layout)) return new Dictionary<string, CompatibilityFile>();
        var version = UnityTranslationFonts.PluginVersion(layout.Core)?.ToString(3);
        var original = await MonoBepInExAsync(version ?? "", ct);
        var updated = await MonoBepInExAsync("5.6.2", ct);
        var directory = Path.GetDirectoryName(layout.ExecutablePath)!;
        UnityTranslationInspection.RejectReparse(layout.Root, Path.Combine(directory, "BepInEx", "core", "BepInEx.Unity.Mono.dll"));
        var expectedCore = Path.Combine(directory, "BepInEx", "plugins", "XUnity.AutoTranslator", "XUnity.AutoTranslator.Plugin.Core.dll");
        if (!string.Equals(expectedCore, layout.Core, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BepInEx 6 的翻译插件路径无法安全核验，保留现有模组");
        var result = new Dictionary<string, CompatibilityFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in updated)
        {
            var bytes = pair.Value;
            if (bepinex6 && (pair.Key.EndsWith(".Plugin.BepInEx.dll", StringComparison.Ordinal) || pair.Key.EndsWith(".ResourceRedirector.BepInEx.dll", StringComparison.Ordinal)))
                bytes = await RewriteCompatibility(bytes, "bepinex6", Path.Combine(directory, "BepInEx", "core"), ct);
            if (!original.TryGetValue(pair.Key, out var before))
                throw new InvalidDataException("固定翻译包的文件清单不匹配，保留现有模组");
            result.Add(Path.Combine(directory, pair.Key), new(bytes, Convert.ToHexString(SHA256.HashData(before))));
        }
        return result;
    }

    internal async Task<CompatibilityFile> Il2CppFontBridgeAsync(UnityTranslationLayout layout, CancellationToken ct)
    {
        UnityTranslationInspection.RejectReparse(layout.Root, Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, "BepInEx", "core", "Il2CppInterop.Runtime.dll"));
        var originals = await Il2CppAsync(layout.Architecture!, false, ct);
        var core = originals["BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.Core.dll"];
        // Build from the fixed public binary, never from an unknown user's modified plugin.
        var bytes = await RewriteCompatibility(core, "font-array", Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, "BepInEx"), ct);
        return new(bytes, Convert.ToHexString(SHA256.HashData(core)));
    }

    internal async Task<IReadOnlyDictionary<string, byte[]>> MonoBepInExAsync(string version, CancellationToken ct)
    {
        var hash = version switch
        {
            "5.4.5" => "1A037CB25159B9775D63284E73C5096F16490D4D627E2AB32F8F3D5C00F822AE",
            "5.6.2" => "836A4066B9369B0D23DC1B0EF6336AD7FE7AE16880931259EC4D157AFF7A81C0",
            _ => throw new InvalidDataException("BepInEx 6 的现有翻译组件版本无法验证，保留现有模组")
        };
        var bytes = await Download("XUnity.AutoTranslator-BepInEx-" + version + ".zip",
            "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v" + version + "/XUnity.AutoTranslator-BepInEx-" + version + ".zip", hash, ct);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > 128) throw new InvalidDataException("Mono 翻译固定包文件过多");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..")
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Mono 翻译固定包路径非法");
            if (name.EndsWith('/') || !name.EndsWith(".dll", StringComparison.Ordinal)) continue;
            if (!(name == "BepInEx/core/XUnity.Common.dll" || name.StartsWith("BepInEx/plugins/", StringComparison.Ordinal))
                || entry.Length > 4 * 1024 * 1024 || (expanded += entry.Length) > 16 * 1024 * 1024)
                throw new InvalidDataException("Mono 翻译固定包文件超出允许范围");
            using var source = entry.Open(); using var output = new MemoryStream(); source.CopyTo(output);
            if (!files.TryAdd(name, output.ToArray())) throw new InvalidDataException("Mono 翻译固定包存在重复文件");
        }
        if (!files.ContainsKey("BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.BepInEx.dll"))
            throw new InvalidDataException("Mono 翻译固定包缺少入口");
        return files;
    }

    private async Task<byte[]> RewriteCompatibility(byte[] input, string mode, string references, CancellationToken ct)
    {
        var runtime = await RuntimeAsync(ct); // Reuse the fixed Rei package's Cecil 0.10.4, not the older patcher-only Cecil.
        var stage = Path.Combine(_cache, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var source = Path.Combine(stage, "input.dll"); var output = Path.Combine(stage, "output.dll"); var script = Path.Combine(stage, "compatibility.ps1");
            await File.WriteAllBytesAsync(Path.Combine(stage, "Mono.Cecil.dll"), runtime["Mono.Cecil.dll"], ct);
            await File.WriteAllBytesAsync(source, input, ct); await File.WriteAllTextAsync(script, CompatibilityScript, ct);
            await RunHelper(script, [stage, source, output, mode, references], ct);
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    internal const string CompatibilityScript = """
        param($tools, $inputAssembly, $outputAssembly, $mode, $references)
        $ErrorActionPreference='Stop'
        try {
          [Reflection.Assembly]::LoadFrom((Join-Path $tools 'Mono.Cecil.dll')) | Out-Null
          $resolver=New-Object Mono.Cecil.DefaultAssemblyResolver
          $resolver.AddSearchDirectory($references)
          $parameters=New-Object Mono.Cecil.ReaderParameters
          $parameters.AssemblyResolver=$resolver
          $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputAssembly,$parameters)
          $module=$assembly.MainModule
          if ($mode -eq 'bepinex6') {
            $core=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $references 'BepInEx.Core.dll'))
            $mono=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $references 'BepInEx.Unity.Mono.dll'))
            if ($core.Name.Name -ne 'BepInEx.Core' -or $core.Name.Version.Major -ne 6 -or $mono.Name.Name -ne 'BepInEx.Unity.Mono') { exit 2 }
            $coreRef=New-Object Mono.Cecil.AssemblyNameReference($core.Name.Name,$core.Name.Version)
            $monoRef=New-Object Mono.Cecil.AssemblyNameReference($mono.Name.Name,$mono.Name.Version)
            $module.AssemblyReferences.Add($coreRef); $module.AssemblyReferences.Add($monoRef)
            $changed=0
            foreach ($type in $module.GetTypeReferences()) {
              if ($type.Scope.Name -ne 'BepInEx') { continue }
              if ($type.FullName -eq 'BepInEx.BaseUnityPlugin') { $type.Namespace='BepInEx.Unity.Mono'; $type.Scope=$monoRef; $changed++ }
              else { $type.Scope=$coreRef }
            }
            if ($changed -ne 1) { exit 2 }
            foreach ($reference in @($module.AssemblyReferences | Where-Object { $_.Name -eq 'BepInEx' })) { $module.AssemblyReferences.Remove($reference) | Out-Null }
            foreach ($member in $module.GetMemberReferences()) {
              if ($member.DeclaringType.Scope.Name -in @('BepInEx.Core','BepInEx.Unity.Mono') -and !$member.Resolve()) { exit 2 }
            }
          } elseif ($mode -eq 'font-array') {
            $runtime=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $references 'core/Il2CppInterop.Runtime.dll'))
            $fontModule=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $references 'interop/UnityEngine.TextRenderingModule.dll'))
            $array=$runtime.MainModule.Types | Where-Object { $_.FullName -eq 'Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray' }
            $ctor=@($array.Methods | Where-Object { $_.IsConstructor -and $_.IsPublic -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.String[]' })
            $font=$fontModule.MainModule.Types | Where-Object { $_.FullName -eq 'UnityEngine.Font' }
            $factory=@($font.Methods | Where-Object { $_.Name -eq 'CreateDynamicFontFromOSFont' -and $_.IsPublic -and $_.IsStatic -and $_.Parameters.Count -eq 2 -and $_.Parameters[0].ParameterType.FullName -eq $array.FullName -and $_.Parameters[1].ParameterType.FullName -eq 'System.Int32' })
            $helper=$module.Types | Where-Object { $_.FullName -eq 'XUnity.AutoTranslator.Plugin.Core.Fonts.FontHelper' }
            $method=@($helper.Methods | Where-Object { $_.Name -eq 'GetTextFont' })
            if ($ctor.Count -ne 1 -or $factory.Count -ne 1 -or $method.Count -ne 1) { exit 2 }
            $method=$method[0]
            $setting=@($method.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldsfld' -and $_.Operand.Name -eq 'OverrideFont' })
            $keep=@($method.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Call' -and $_.Operand.Name -eq 'DontDestroyOnLoad' })
            if ($setting.Count -ne 1 -or $keep.Count -ne 1) { exit 2 }
            $settingRef=$setting[0].Operand; $keepRef=$keep[0].Operand
            $method.Body.Instructions.Clear(); $method.Body.ExceptionHandlers.Clear(); $method.Body.Variables.Clear(); $method.Body.InitLocals=$false
            $il=$method.Body.GetILProcessor()
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Newarr,$module.TypeSystem.String)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Dup)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldsfld,$settingRef)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Stelem_Ref)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Newobj,$module.ImportReference($ctor[0]))
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Call,$module.ImportReference($factory[0]))
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Dup)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Call,$keepRef)
            $il.Emit([Mono.Cecil.Cil.OpCodes]::Ret)
          } else { exit 2 }
          $assembly.Write($outputAssembly)
          exit 0
        } catch { exit 1 }
        """;
}
