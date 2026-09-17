using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameLibrary.Infrastructure.Shell;

/// <summary>按 Windows 引号/反斜杠规则把 LNK 原始参数拆为 ArgumentList 项；不调用 Shell。</summary>
public static class WindowsCommandLine
{
    public static IReadOnlyList<string> ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        if (raw.Length > 8192 || raw.Contains('\0'))
        {
            throw new ArgumentException("快捷方式参数过长或包含空字符", nameof(raw));
        }

        // CommandLineToArgvW 解析完整命令行；补一个固定 argv[0]，之后只取参数。
        var argv = CommandLineToArgvW("game.exe " + raw, out var count);
        if (argv == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析快捷方式参数");
        }

        try
        {
            if (count is < 1 or > 129)
            {
                throw new ArgumentException("快捷方式参数数量超限", nameof(raw));
            }

            var result = new string[count - 1];
            for (var i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))
                    ?? throw new InvalidOperationException("快捷方式参数解析结果为空指针");
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
