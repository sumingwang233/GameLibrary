using System.Security.Cryptography;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Infrastructure.Backups;

namespace GameLibrary.Host.Hosting;

internal sealed record UnityTranslationFile(string RelativePath, string? BeforeHash, string? AfterHash);
internal sealed record UnityTranslationManifest(string Root, List<UnityTranslationFile> Files, bool Committed = false, bool Restored = false);

/// <summary>Encrypted originals outside the library; hashes prevent restore/rollback from overwriting later mod changes.</summary>
internal sealed class UnityTranslationTransaction
{
    private readonly string _directory;
    private readonly string _manifestPath;
    private UnityTranslationManifest _manifest;
    private readonly UnityTranslationManifest _beforeAttempt;
    private readonly List<(string Path, byte[]? Before, string AfterHash)> _writes = [];
    public string Id { get; }

    public UnityTranslationTransaction(UnityTranslationVault vault, string id, string root)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("备份 ID 无效");
        Id = id;
        _directory = Path.Combine(vault.DirectoryPath, "backups", id);
        _manifestPath = Path.Combine(_directory, "manifest.json");
        Directory.CreateDirectory(_directory);
        _manifest = File.Exists(_manifestPath)
            ? JsonSerializer.Deserialize<UnityTranslationManifest>(File.ReadAllText(_manifestPath), ContractJson.Options)!
            : new(Path.GetFullPath(root), []);
        if (_manifest is null || !string.Equals(_manifest.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份目录与当前游戏不一致，请先恢复原配置");
        _beforeAttempt = _manifest with { Files = _manifest.Files.ToList() };
    }

    public void Capture(string path)
    {
        var relative = Relative(path);
        if (_manifest.Restored) _manifest = new(_manifest.Root, []);
        if (_manifest.Files.Any(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase))) return;
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (bytes is not null) UnityTranslationVault.AtomicWrite(BackupPath(relative), UnityTranslationVault.Protect(bytes));
        _manifest.Files.Add(new(relative, bytes is null ? null : Hash(bytes), null));
        Save();
    }

    public void Write(string path, byte[] bytes)
        => WriteCore(path, bytes, null, configuration: false);

    public void WriteConfiguration(string path, byte[] bytes, string? expectedHash)
    {
        if (Path.GetFileName(path) is not ("Config.ini" or "AutoTranslatorConfig.ini"))
            throw new InvalidDataException("只允许更新翻译配置文件");
        WriteCore(path, bytes, expectedHash, configuration: true);
    }

    private void WriteCore(string path, byte[] bytes, string? expectedHash, bool configuration)
    {
        Capture(path);
        var relative = Relative(path);
        var index = _manifest.Files.FindIndex(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        var item = _manifest.Files[index];
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var actual = before is null ? null : Hash(before);
        // XUnity adds defaults to its INI on launch. The caller merges that exact snapshot;
        // original encrypted backups and strict checks for all binaries remain unchanged.
        if (actual != (configuration ? expectedHash : item.AfterHash ?? item.BeforeHash))
            throw new InvalidDataException("目标文件被其他程序修改，拒绝覆盖");
        _writes.Add((path, before, Hash(bytes)));
        _manifest.Files[index] = item with { AfterHash = Hash(bytes) };
        _manifest = _manifest with { Committed = false };
        Save(); // Write-ahead: an interrupted atomic replace can be recovered from either known hash.
        UnityTranslationVault.AtomicWrite(path, bytes);
    }

    public void Commit() { _manifest = _manifest with { Committed = true }; Save(); }

    public void RollbackAttempt()
    {
        foreach (var item in _writes)
        {
            Relative(item.Path);
            var actual = File.Exists(item.Path) ? Hash(File.ReadAllBytes(item.Path)) : null;
            if (actual != item.AfterHash && actual != (item.Before is null ? null : Hash(item.Before)))
                throw new InvalidDataException("本次配置文件已被其他程序修改，保留文件并拒绝回滚");
        }
        foreach (var item in _writes.AsEnumerable().Reverse())
        {
            if (item.Before is null) { if (File.Exists(item.Path)) File.Delete(item.Path); }
            else UnityTranslationVault.AtomicWrite(item.Path, item.Before);
        }
        _manifest = _beforeAttempt with { Files = _beforeAttempt.Files.ToList() };
        Save();
    }

    public void Restore()
    {
        if (_manifest.Restored) return;
        // Preflight all files and decrypt backups before the first filesystem change.
        var originals = new List<(string Path, byte[]? Bytes)>();
        foreach (var item in _manifest.Files)
        {
            var path = Path.Combine(_manifest.Root, item.RelativePath);
            UnityTranslationInspection.RejectReparse(_manifest.Root, path);
            var hash = File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
            if (hash != item.AfterHash && hash != item.BeforeHash)
                throw new InvalidDataException("翻译文件安装后已被其他程序修改，保留文件并拒绝恢复");
            var bytes = item.BeforeHash is null ? null : UnityTranslationVault.Unprotect(File.ReadAllBytes(BackupPath(item.RelativePath)));
            if (bytes is not null && Hash(bytes) != item.BeforeHash) throw new InvalidDataException("原始备份校验失败");
            originals.Add((path, bytes));
        }
        foreach (var item in originals.AsEnumerable().Reverse())
        {
            if (item.Bytes is null) { if (File.Exists(item.Path)) File.Delete(item.Path); }
            else UnityTranslationVault.AtomicWrite(item.Path, item.Bytes);
        }
        _manifest = _manifest with { Committed = false, Restored = true };
        Save();
    }

    private string Relative(string path)
    {
        UnityTranslationInspection.RejectReparse(_manifest.Root, path);
        return Path.GetRelativePath(_manifest.Root, path);
    }
    private string BackupPath(string relative) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative))) + ".bin");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private void Save() => ControlAreaStore.WriteAtomic(_manifestPath, JsonSerializer.Serialize(_manifest, ContractJson.Options));
}
