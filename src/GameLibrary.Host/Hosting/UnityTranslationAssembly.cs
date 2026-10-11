using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace GameLibrary.Host.Hosting;

/// <summary>Metadata-only, same-size edits of verified loader calls. Never load game assemblies.</summary>
internal static class UnityTranslationAssembly
{
    internal const string InteropPath = "BepInEx/core/Il2CppInterop.Runtime.dll";
    internal const string OriginalInteropHash = "E4BADD01DA4A5E6251A040F683F96EB3635E6E4783F761946436AD04471F81F0";
    internal const string PatchedInteropHash = "1B2969A2FC0132FA44CC18BD880910C9BE5AEBBDE393879DCA27613D7E95DAB2";
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => unchecked((ushort)c.Value));

    internal static bool NeedsInteropRepair(UnityTranslationLayout layout)
    {
        if (layout.Runtime != "il2cpp" || UnityTranslationFonts.Generation(layout) != "6000") return false;
        var path = Path.Combine(Path.GetDirectoryName(layout.ExecutablePath)!, InteropPath);
        UnityTranslationInspection.RejectReparse(layout.Root, path);
        return File.Exists(path) && Hash(File.ReadAllBytes(path)) != PatchedInteropHash;
    }

    internal static byte[] PatchInterop(byte[] bytes)
    {
        if (Hash(bytes) == PatchedInteropHash) return bytes;
        if (Hash(bytes) != OriginalInteropHash) throw new InvalidDataException("Unity 6 的 IL2CPP 运行库版本无法验证，保留现有模组");
        using var pe = new PEReader(new MemoryStream(bytes));
        var metadata = pe.GetMetadataReader();
        var method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).Single(m =>
            metadata.GetString(m.Name) == "FindTargetMethod"
            && metadata.GetString(metadata.GetTypeDefinition(m.GetDeclaringType()).Name) == "GenericMethod_GetMethod_Hook");
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var calls = Instructions(il).Where(i => i.Code == OpCodes.Call && BitConverter.ToInt32(il, i.Offset + 1) == 0x2b00005a).ToArray();
        if (calls.Length != 4 || calls[1].Offset != 238) throw new InvalidDataException("IL2CPP Hook 方法形态不匹配");
        // Upstream 11bdcbf: Unity 6 chooses the first getVirtualMethod xref, not the last.
        // Both MethodSpecs are Enumerable<IntPtr> with identical signatures; no metadata rebuild.
        var patched = bytes.ToArray();
        BitConverter.GetBytes(0x2b00001a).CopyTo(patched, IlOffset(pe, bytes, method.RelativeVirtualAddress) + calls[1].Offset + 1);
        if (Hash(patched) != PatchedInteropHash) throw new InvalidDataException("IL2CPP Hook 补丁校验失败");
        return patched;
    }

    internal static bool HasBootstrap(byte[] bytes) => BootstrapCalls(bytes).Count != 0;

    internal static byte[] RemoveBootstrap(byte[] bytes)
    {
        var calls = BootstrapCalls(bytes);
        if (calls.Count == 0) throw new InvalidDataException("无法验证 Rei 翻译启动补丁，保留现有模组");
        var patched = bytes.ToArray();
        // The verified static void/no-argument call consumes no stack values. Keep every other IL byte.
        foreach (var offset in calls) Array.Clear(patched, offset, 5);
        if (HasBootstrap(patched)) throw new InvalidDataException("Rei 翻译启动补丁撤销失败");
        return patched;
    }

    private static List<int> BootstrapCalls(byte[] bytes)
    {
        using var pe = new PEReader(new MemoryStream(bytes));
        var metadata = pe.GetMetadataReader();
        var entries = metadata.MemberReferences.Where(h =>
        {
            var member = metadata.GetMemberReference(h);
            if (metadata.GetString(member.Name) != "LoadThroughBootstrapper" || member.Parent.Kind != HandleKind.TypeReference
                || !metadata.GetBlobBytes(member.Signature).AsSpan().SequenceEqual(new byte[] { 0, 0, 1 })) return false;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            return metadata.GetString(type.Name) == "PluginLoader" && metadata.GetString(type.Namespace) == "XUnity.AutoTranslator.Plugin.Core"
                && type.ResolutionScope.Kind == HandleKind.AssemblyReference
                && metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "XUnity.AutoTranslator.Plugin.Core";
        }).Select(h => MetadataTokens.GetToken(h)).ToHashSet();
        var calls = new List<int>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            if (method.RelativeVirtualAddress == 0 || metadata.GetString(method.Name) != ".cctor"
                || metadata.GetString(type.Namespace) != "UnityEngine" || metadata.GetString(type.Name) is not ("Input" or "Display")) continue;
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
            foreach (var instruction in Instructions(il))
                if (instruction.Code == OpCodes.Call && entries.Contains(BitConverter.ToInt32(il, instruction.Offset + 1)))
                    calls.Add(IlOffset(pe, bytes, method.RelativeVirtualAddress) + instruction.Offset);
        }
        return calls;
    }

    private static int IlOffset(PEReader pe, byte[] bytes, int rva)
    {
        var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
        var start = section.PointerToRawData + rva - section.VirtualAddress;
        return start + ((bytes[start] & 3) == 2 ? 1 : (bytes[start + 1] >> 4) * 4);
    }

    private static IEnumerable<(int Offset, OpCode Code)> Instructions(byte[] il)
    {
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            ushort value = il[offset++];
            if (value == 0xfe && offset < il.Length) value = (ushort)(0xfe00 | il[offset++]);
            if (!Codes.TryGetValue(value, out var code)) throw new BadImageFormatException("无效 IL 指令");
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch when offset + 4 <= il.Length => checked(4 + 4 * BitConverter.ToInt32(il, offset)),
                _ => 4
            };
            if (size < 0 || size > il.Length - offset) throw new BadImageFormatException("无效 IL 操作数");
            yield return (start, code);
            offset += size;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
