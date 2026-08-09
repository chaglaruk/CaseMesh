$ErrorActionPreference = "Stop"

$probe = ".\tools\HRCompanion.QualityProbe\HRCompanion.QualityProbe.csproj"
dotnet run --project $probe -c Release
exit $LASTEXITCODE
