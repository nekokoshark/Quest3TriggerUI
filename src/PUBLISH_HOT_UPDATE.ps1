param(
    [Parameter(Mandatory = $true)]
    [string] $Candidate,
    [string] $PluginDirectory = 'F:\vam1.22.0.12\BepInEx\plugins\Quest3TriggerUI'
)

$ErrorActionPreference = 'Stop'
$candidatePath = (Resolve-Path -LiteralPath $Candidate).Path
$pluginDirectoryPath = (Resolve-Path -LiteralPath $PluginDirectory).Path
$payloadPath = Join-Path $pluginDirectoryPath 'Quest3TriggerUI.payload.dll.disabled'
$temporaryPath = $payloadPath + '.new'
$backupPath = $payloadPath + '.replace-backup'

# Validate the candidate as a managed assembly before publishing it. The stable
# loader reads the complete replacement into memory and never locks this file.
[void][Reflection.AssemblyName]::GetAssemblyName($candidatePath)
$bytes = [IO.File]::ReadAllBytes($candidatePath)
[IO.File]::WriteAllBytes($temporaryPath, $bytes)

try {
    if (Test-Path -LiteralPath $payloadPath) {
        [IO.File]::Replace($temporaryPath, $payloadPath, $backupPath)
        Remove-Item -LiteralPath $backupPath -Force
    } else {
        [IO.File]::Move($temporaryPath, $payloadPath)
    }
} finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Force
    }
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $payloadPath).Hash
Write-Output "HOT UPDATE PUBLISH RESULT=PASS; Payload=$payloadPath; SHA256=$hash; RestartRequired=False"
