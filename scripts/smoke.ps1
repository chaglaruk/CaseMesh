$ErrorActionPreference = "Stop"

$probe = ".\tools\HRCompanion.SmokeProbe\HRCompanion.SmokeProbe.csproj"
dotnet run --project $probe -c Release -- --credential-check
if ($LASTEXITCODE -eq 2) { exit 2 }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$smokeRoot = Join-Path $tempBase ("HRCompanion-Smoke-" + [Guid]::NewGuid().ToString("N"))
$hrWave = Join-Path $smokeRoot "hr.wav"
$userWave = Join-Path $smokeRoot "user.wav"

try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    Add-Type -AssemblyName System.Speech
    $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
        24000,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono)

    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $synth.SetOutputToWaveFile($hrWave, $format)
        $synth.Speak("Could you confirm the next step in this synthetic meeting?")
        $synth.SetOutputToNull()
        $synth.SetOutputToWaveFile($userWave, $format)
        $synth.Speak("I need time to consider the synthetic proposal.")
        $synth.SetOutputToNull()
    }
    finally {
        $synth.Dispose()
    }

    dotnet run --project $probe -c Release -- $hrWave $userWave
    exit $LASTEXITCODE
}
finally {
    $resolved = [IO.Path]::GetFullPath($smokeRoot)
    if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
