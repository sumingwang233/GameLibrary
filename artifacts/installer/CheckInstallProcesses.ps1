param([string]$InstallDirectory, [switch]$LegacyUninstaller, [string]$PreviousVersion)

function Test-InstallProcesses([string]$Directory, [switch]$LegacyUninstaller, [string]$PreviousVersion) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { return 2 }
    try {
        $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + '\'
        [version]$version = $null
        # Older uninstallers kill by image name; require all instances closed before invoking them.
        $checkAll = $LegacyUninstaller -and (-not [version]::TryParse($PreviousVersion, [ref]$version) -or $version -lt [version]'1.7.14')
        $processes = @(Get-CimInstance Win32_Process -Filter "Name='GameLibrary.Desktop.exe' OR Name='GameLibrary.Host.exe' OR Name='GameLibrary.TauriBridge.exe'" -ErrorAction Stop)
        foreach ($process in $processes) {
            if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) { return 2 }
            $path = [IO.Path]::GetFullPath($process.ExecutablePath)
            if ($checkAll -or $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return 1 }
        }
        return 0
    } catch { return 2 }
}

if ($MyInvocation.InvocationName -ne '.') { exit (Test-InstallProcesses $InstallDirectory -LegacyUninstaller:$LegacyUninstaller -PreviousVersion $PreviousVersion) }
