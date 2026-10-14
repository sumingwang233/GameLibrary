$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\artifacts\installer\CheckInstallProcesses.ps1"
function Get-CimInstance { param($ClassName, $Filter, $ErrorAction) return $script:Processes }
function Assert-Code($Expected, $Directory, [switch]$LegacyUninstaller, [string]$PreviousVersion) {
    $actual = Test-InstallProcesses $Directory -LegacyUninstaller:$LegacyUninstaller -PreviousVersion $PreviousVersion
    if ($actual -ne $Expected) { throw "Expected $Expected, got $actual" }
}
$script:Processes = @()
Assert-Code 0 'C:\isolated install'
$script:Processes = @([pscustomobject]@{ ExecutablePath = 'C:\isolated install\GameLibrary.Host.exe' })
Assert-Code 1 'c:\isolated install\.'
$script:Processes = @([pscustomobject]@{ ExecutablePath = 'C:\isolated install other\GameLibrary.Host.exe' })
Assert-Code 0 'C:\isolated install'
Assert-Code 1 'C:\isolated install' -LegacyUninstaller -PreviousVersion '1.7.13'
Assert-Code 1 'C:\isolated install' -LegacyUninstaller -PreviousVersion 'unknown'
Assert-Code 0 'C:\isolated install' -LegacyUninstaller -PreviousVersion '1.7.14'
$script:Processes = @([pscustomobject]@{ ExecutablePath = $null })
Assert-Code 2 'C:\isolated install'
Assert-Code 2 ''
Remove-Item Function:\Get-CimInstance

# Real harmless stubs exercise the production CIM query as well as path comparison.
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\local\installer-process-$([Guid]::NewGuid().ToString('N'))"))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\local')) + '\'
if (-not $taskRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid isolated test path' }
$stubRoot = Join-Path $PSScriptRoot '..\tests\GameLibrary.TestProcessStub\bin\Release\net10.0-windows'
if (-not (Test-Path -LiteralPath (Join-Path $stubRoot 'GameLibrary.TestProcessStub.exe'))) { throw 'Build GameLibrary.TestProcessStub in Release first' }
$target = Join-Path $taskRoot 'target install'
$sibling = Join-Path $taskRoot 'target install other'
$started = @()
try {
    foreach ($directory in @($target, $sibling)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Copy-Item -Path (Join-Path $stubRoot '*') -Destination $directory
        Copy-Item -LiteralPath (Join-Path $directory 'GameLibrary.TestProcessStub.exe') -Destination (Join-Path $directory 'GameLibrary.Host.exe')
        $started += Start-Process -FilePath (Join-Path $directory 'GameLibrary.Host.exe') -ArgumentList '--hold-ms', '60000' -WindowStyle Hidden -PassThru
    }
    foreach ($process in $started) {
        if (-not (Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop)) { throw 'Stub exited early' }
    }
    Assert-Code 1 $target
    Assert-Code 1 $sibling
    if ($started.Where({ $_.HasExited }).Count) { throw 'Guard terminated a process' }
    Stop-Process -Id $started[0].Id -Force
    $started[0].WaitForExit()
    Assert-Code 0 $target
    Assert-Code 1 $target -LegacyUninstaller -PreviousVersion '1.7.13'
    Assert-Code 0 $target -LegacyUninstaller -PreviousVersion '1.7.14'
    Assert-Code 1 $sibling
    if ($started[1].HasExited) { throw 'Sibling installation was affected' }
} finally {
    foreach ($process in $started) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $taskRoot) { Remove-Item -LiteralPath $taskRoot -Recurse -Force }
}
'Installer process guard passed: real isolated stubs, scoped path, spaces, case, sibling installation and unknown path.'
