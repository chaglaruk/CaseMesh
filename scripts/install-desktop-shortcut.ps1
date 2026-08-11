$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishScript = Join-Path $PSScriptRoot 'publish-local.ps1'
$ExePath = Join-Path $RepoRoot 'artifacts\win-x64\HRCompanion.exe'

# The published app loads native SQLite (e_sqlite3.dll). Windows keeps that DLL locked while
# HR Companion is still running, which prevents publish-local.ps1 from replacing the output folder.
# Stop only the HRCompanion process whose executable is the local published build we are about to replace.
$runningLocal = @(Get-Process -Name 'HRCompanion' -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Path -and ([System.IO.Path]::GetFullPath($_.Path) -eq [System.IO.Path]::GetFullPath($ExePath))
    }
    catch {
        $false
    }
})

if ($runningLocal.Count -gt 0) {
    Write-Host 'Closing the currently running local HR Companion build before publishing...'
    $runningLocal | Stop-Process -Force

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 150
        $stillRunning = @($runningLocal | Where-Object {
            try { -not $_.HasExited } catch { $false }
        })
    } while ($stillRunning.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($stillRunning.Count -gt 0) {
        throw 'HR Companion did not exit within 5 seconds. Close it in Task Manager and run this installer again.'
    }
}

Write-Host 'Publishing HR Companion...'
& $PublishScript

if (-not (Test-Path $ExePath)) {
    throw "Published executable was not found: $ExePath"
}

$Desktop = [Environment]::GetFolderPath('Desktop')
$ShortcutPath = Join-Path $Desktop 'HR Companion.lnk'
$Shell = New-Object -ComObject WScript.Shell
$Shortcut = $Shell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $ExePath
$Shortcut.WorkingDirectory = $RepoRoot
$Shortcut.IconLocation = "$ExePath,0"
$Shortcut.Description = 'Launch HR Companion'
$Shortcut.Save()

Write-Host "Desktop shortcut created: $ShortcutPath"
Write-Host 'Use this shortcut for the meeting. Re-run this installer after pulling a newer app build.'
