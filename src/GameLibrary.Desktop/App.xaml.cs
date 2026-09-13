using System.Windows;

namespace GameLibrary.Desktop;

public partial class App : Application
{
    /// <summary>启动参数（MainWindow 解析 --data-dir 等）。</summary>
    public static string[] Args { get; } = Environment.GetCommandLineArgs();
}
