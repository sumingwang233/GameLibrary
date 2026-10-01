using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace GameLibrary.Desktop;

public partial class MainWindow
{
    private bool _automaticUpdateCheckStarted;

    private async Task CheckForUpdatesAsync(bool userInitiated, Window owner)
    {
        if (!userInitiated)
        {
            if (_automaticUpdateCheckStarted)
            {
                return;
            }

            _automaticUpdateCheckStarted = true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var release = await GitHubReleaseChecker.CheckAsync(timeout.Token);
            if (release is null)
            {
                if (userInitiated)
                {
                    MessageBox.Show(owner, L10n.T("GitHub 上暂时没有可用的公开版本。"), L10n.T("检查更新"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            if (!GitHubReleaseChecker.IsNewer(release.Version, GitHubReleaseChecker.CurrentVersion))
            {
                if (userInitiated)
                {
                    MessageBox.Show(owner,
                        L10n.F($"当前已是最新版本 v{GitHubReleaseChecker.CurrentVersion}。"),
                        L10n.T("检查更新"), MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            var answer = MessageBox.Show(owner,
                L10n.F($"发现新版本 {release.TagName}（当前 v{GitHubReleaseChecker.CurrentVersion}）。\n\n是否前往 GitHub 下载？"),
                L10n.T("GameLibrary 有可用更新"), MessageBoxButton.YesNo, MessageBoxImage.Information,
                MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (userInitiated)
            {
                MessageBox.Show(owner, L10n.F($"暂时无法连接 GitHub：{ex.Message}"), L10n.T("检查更新"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
