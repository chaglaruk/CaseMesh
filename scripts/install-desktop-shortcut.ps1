$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishScript = Join-Path $PSScriptRoot 'publish-local.ps1'
$ExePath = Join-Path $RepoRoot 'artifacts\win-x64\HRCompanion.exe'

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
