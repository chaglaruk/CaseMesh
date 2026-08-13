$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$Out = Join-Path $PWD 'artifacts\win-x64'
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }

dotnet publish .\src\CaseMesh.App\CaseMesh.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $Out

Write-Host "Published to $Out"
