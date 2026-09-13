using System.IO;
using GameLibrary.Contracts.Ipc;

namespace GameLibrary.Host.Hosting;

/// <summary>
/// 单数据目录单宿主守卫（契约 2.1）：命名互斥（当前用户）+ 数据目录内固定锁文件的独占句柄。
/// 锁文件属于目录而非路径别名，防止两个写入者；互斥名含规范化目录摘要。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream? _lockFile;
    private readonly Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex, FileStream lockFile)
    {
        _mutex = mutex;
        _lockFile = lockFile;
    }

    public bool IsPrimary { get; private set; }

    /// <summary>
    /// 尝试成为该数据目录唯一宿主。输掉竞争返回 <paramref name="primary"/>=false（另一宿主持有互斥）。
    /// </summary>
    public static SingleInstanceGuard TryAcquire(string canonicalDataDirectory, string comparisonKey)
    {
        var mutex = new Mutex(initiallyOwned: true, ChannelNames.MutexName(comparisonKey), out var createdNew);

        if (!createdNew)
        {
            return new SingleInstanceGuard(mutex, null!) { IsPrimary = false };
        }

        try
        {
            Directory.CreateDirectory(canonicalDataDirectory);
            var lockPath = Path.Combine(canonicalDataDirectory, ChannelNames.LockFileName);
            var lockFile = new FileStream(
                lockPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                8,
                FileOptions.DeleteOnClose);

            return new SingleInstanceGuard(mutex, lockFile) { IsPrimary = true };
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _lockFile?.Dispose();
        if (_mutex is not null)
        {
            if (IsPrimary)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 持有线退出时互斥已自动释放；忽略。
                }
            }

            _mutex.Dispose();
        }
    }
}
