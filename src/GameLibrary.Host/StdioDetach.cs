using System.Runtime.InteropServices;

namespace GameLibrary.Host;

/// <summary>
/// 后台宿主主动释放继承自启动方（CLI/MCP/Desktop 连接引导层）的标准句柄：
/// 否则宿主存活期间父进程的 stdout 管道永远无法读到 EOF，调用方会挂死。
/// 仅在 --detach-stdio 下调用；直接在终端运行宿主时保留控制台。
/// </summary>
internal static class StdioDetach
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static void CloseInheritedHandles()
    {
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        foreach (var handleId in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(handleId);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                CloseHandle(handle);
            }
        }
    }
}
