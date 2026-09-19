using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameLibrary.Host.Observability;

/// <summary>
/// 数据目录内滚动文件日志（R45，补 T24 遗留）：detached 后台模式宿主的唯一观测出口。
/// 尺寸滚动（host.log → host.log.1 → host.log.2）；逐条打开追加、不驻留句柄——
/// IO 失败静默丢弃本条并随下次写入重试，日志路径的任何错误都不得影响宿主。
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _fileName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly Lock _sync = new();

    public RollingFileLoggerProvider(
        string directory,
        string fileName = "host.log",
        long maxBytes = 5 * 1024 * 1024,
        int maxFiles = 3)
    {
        _directory = directory;
        _fileName = fileName;
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // 逐条写入不驻留句柄，无需释放。
    }

    private void Write(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, _fileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    Rotate(path);
                }

                File.AppendAllText(path, FormatLine(categoryName, logLevel, message, exception), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 磁盘满/权限丢失/目录被删：静默丢弃本条，下次写入自动重试。
            }
        }
    }

    private void Rotate(string basePath)
    {
        var oldest = $"{basePath}.{_maxFiles - 1}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = $"{basePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{basePath}.{index + 1}", overwrite: true);
            }
        }

        File.Move(basePath, $"{basePath}.1", overwrite: true);
    }

    private static string FormatLine(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        var builder = new StringBuilder(128 + message.Length)
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(LevelShort(logLevel))
            .Append(" [").Append(categoryName).Append("] ")
            .AppendLine(message);
        if (exception is not null)
        {
            builder.Append("    ").AppendLine(exception.GetType().FullName)
                .Append("    ").AppendLine(exception.Message.ReplaceLineEndings("\n    ").TrimEnd(' ', '\n'));
        }

        return builder.ToString();
    }

    private static string LevelShort(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class Logger(RollingFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
