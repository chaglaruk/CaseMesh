param(
    [string]$Remote = 'https://github.com/chaglaruk/HRCompanion.git'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not (Test-Path .git)) { git init -b main }

Write-Host 'This script does not stage, commit, or push automatically.' -ForegroundColor Yellow
Write-Host 'After reviewing the working tree, run only the Git operations you explicitly intend:'
Write-Host '  git add -- .editorconfig .github .gitignore AGENTS.md Directory.Build.props Directory.Packages.props HRCompanion.slnx README.md docs evals scripts src templates tests tools'
Write-Host '  git commit -m "feat: bootstrap HR Companion"'
Write-Host "  git remote add origin $Remote"
Write-Host '  git push -u origin main'
