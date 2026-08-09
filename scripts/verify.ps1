$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

Write-Host '== HR Companion verification ==' -ForegroundColor Cyan
dotnet --info

dotnet restore .\HRCompanion.slnx
dotnet build .\HRCompanion.slnx -c Release --no-restore
dotnet test .\HRCompanion.slnx -c Release --no-build --logger "trx;LogFileName=hrcompanion-tests.trx"

Write-Host ''
Write-Host 'AUTOMATED_ONLY: build/tests passed. This does NOT verify Teams/process audio.' -ForegroundColor Yellow
Write-Host 'Run the AudioProbe and the real-device gates in docs/GATES.md.' -ForegroundColor Yellow
