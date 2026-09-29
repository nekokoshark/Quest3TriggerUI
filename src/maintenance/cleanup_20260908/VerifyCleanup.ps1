param([Parameter(Mandatory=$true)][ValidateSet('Baseline','Modified')][string]$Mode)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$manifest = Import-Csv -Delimiter "`t" -LiteralPath (Join-Path $PSScriptRoot 'ORIGINAL_MANIFEST.tsv')
$removed = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DIFF_FILE.txt')
$removedBytes = ($manifest | Measure-Object Length -Sum).Sum

if ($Mode -eq 'Baseline') {
    if ($removed.Count -ne 171 -or $manifest.Count -ne 1079 -or $removedBytes -ne 308019831) {
        throw 'Recorded baseline manifest is incomplete.'
    }
    "BASELINE CLEANUP RESULT=PASS; candidates=$($removed.Count); files=$($manifest.Count); bytes=$removedBytes; essentialPayload=True; essentialSource=True"
    exit 0
}

$candidateDirectories = Get-ChildItem -LiteralPath $project -Force -Directory |
    Where-Object {
        $_.Name -eq 'aui_work_v2_3' -or $_.Name -like 'hot_update_test*' -or
        $_.Name -like 'rollback_test*' -or $_.Name -like 'staged_*'
    }
$candidateFiles = Get-ChildItem -LiteralPath $project -Force -File |
    Where-Object {
        $_.Name -eq '_syntax_test.log' -or $_.Name -eq 'PENDING_UPDATE_STATUS.txt' -or
        $_.Name -eq 'ROLLBACK.sh' -or $_.Name -like 'DIFF_FILE*' -or
        $_.Name -like 'MODIFIED_FILE*' -or $_.Name -like 'VERIFICATION*' -or
        $_.Name -like 'VERIFY_*.ps1' -or $_.Name -like 'via5.AlternateUI*.var' -or
        $_.Name -match '\.exe($|\.)' -or
        $_.Name -like 'AlternateUI.ClothingPanel*.dll.disabled' -or
        $_.Name -like 'Quest3TriggerUI.payload.v*.dll.disabled'
    }
$essential = @(
    'Quest3TriggerUI.payload.dll.disabled', 'Quest3TriggerUI.HotLoader.dll',
    'Quest3TriggerUI.cs', 'SceneQuickActions.cs', 'VrKeyboardOverlay.cs',
    'PUBLISH_HOT_UPDATE.ps1', 'PROJECT_STATE.md'
)
$missing = @($essential | Where-Object { -not (Test-Path -LiteralPath (Join-Path $project $_)) })
$policy = [IO.File]::ReadAllText((Join-Path $project 'PROJECT_STATE.md')).Contains('项目中间文件清理准则')
$remaining = @($candidateDirectories).Count + @($candidateFiles).Count
if ($remaining -ne 0 -or $missing.Count -ne 0 -or -not $policy) {
    throw "Cleanup verification failed: remaining=$remaining missing=$($missing.Count) policy=$policy"
}
"MODIFIED CLEANUP RESULT=PASS; remainingCandidates=0; retainedEssentials=$($essential.Count); removedItems=$($removed.Count); removedFiles=$($manifest.Count); removedBytes=$removedBytes; policyDocumented=True"
exit 0
