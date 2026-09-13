using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GameLibrary.Infrastructure.Shell;

/// <summary>
/// Windows Shell LNK 互操作（IShellLinkW + IPersistFile）。
/// 仅用于读取与测试生成快捷方式；解析器不做任何写操作。
/// </summary>
[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellLinkW
{
    public void GetPath(
        [Out] System.Text.StringBuilder file,
        int max,
        out _Win32FindDataW findData,
        int flags);

    public void GetIDList(out nint idList);

    public void SetIDList(nint idList);

    public void GetDescription([Out] System.Text.StringBuilder name, int max);

    public void SetDescription(string name);

    public void GetWorkingDirectory([Out] System.Text.StringBuilder dir, int max);

    public void SetWorkingDirectory(string dir);

    public void GetArguments([Out] System.Text.StringBuilder args, int max);

    public void SetArguments(string args);

    public void GetHotkey(out short hotkey);

    public void SetHotkey(short hotkey);

    public void GetShowCmd(out int showCmd);

    public void SetShowCmd(int showCmd);

    public void GetIconLocation([Out] System.Text.StringBuilder iconPath, int max, out int iconIndex);

    public void SetIconLocation(string iconPath, int iconIndex);

    public void SetRelativePath(string relativePath, int reserved);

    public void Resolve(IntPtr hwnd, int flags);

    public void SetPath(string path);
}

/// <summary>ShellLink COM 类的 CLSID（%windir%\System32\shell32.dll 的标准快捷方式对象）。</summary>
public static class ShellLinkInterop
{
    public const string ShellLinkClsid = "00021401-0000-0000-C000-000000000046";

    public const string PersistFileIid = "0000010B-0000-0000-C000-000000000046";

    /// <summary>在 STA 线程上执行 Shell COM 调用（Shell 对象要求单线程套间）。</summary>
    public static T RunOnSta<T>(Func<T> action)
    {
        Exception? captured = null;
        var result = default(T)!;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured is null ? result : throw captured;
    }

    public static IShellLinkW CreateShellLink()
    {
        var type = Type.GetTypeFromCLSID(new Guid(ShellLinkClsid))
            ?? throw new InvalidOperationException("无法创建 ShellLink COM 对象");
        return (IShellLinkW)Activator.CreateInstance(type)!;
    }
}

/// <summary>GetPath 使用的 WIN32_FIND_DATAW（仅声明布局相关字段）。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct _Win32FindDataW
{
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string FileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string AlternateFileName;
}
