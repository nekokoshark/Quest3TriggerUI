param([string]$GameRoot='F:\vam1.22.0.12')
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
$dest=Join-Path $workspace '运行快照'
$paths=@(
 'VaM (OpenVR).bat','VaM (OpenVR+OFXR).bat','VaM (Desktop Mode).bat',
 'doorstop_config.ini','opencomposite.ini','vrperfkit_RSF.yml',
 'BepInEx\config\OFXR\OFXR_V068.manifest.json',
 'BepInEx\config\local.vam.quest3-trigger-ui.cfg',
 'BepInEx\config\uncleburrito.vamdlssnr.cfg',
 'BepInEx\plugins\Quest3TriggerUI\Quest3TriggerUI.HotLoader.dll',
 'BepInEx\plugins\Quest3TriggerUI\Quest3TriggerUI.payload.dll.disabled',
 'BepInEx\plugins\Quest3TriggerUI\pinyin_dict.txt',
 'BepInEx\plugins\Quest3TriggerUI\pinyin_initials.txt',
 'BepInEx\plugins\Quest3TriggerUI\pinyin_fuzzy.txt',
 'BepInEx\plugins\Quest3TriggerUI\pinyin_syllables.txt',
 'BepInEx\plugins\PostMagicEventBridge\PostMagicEventBridge.dll',
 'BepInEx\plugins\VamDlssNr\VamDlssNrPlugin.dll',
 'BepInEx\plugins\VamDlssNr\nvngx.dll\VamDlssNr.dll',
 'BepInEx\plugins\VamDlssNr\vamdlssnr',
 'BepInEx\plugins\VamDlssNr\build-id.txt',
 'VaM_Data\Plugins\openvr_api.dll','dxgi.dll','winhttp.dll'
)
$manifest=@()
foreach($relative in $paths){
 $source=Join-Path $GameRoot $relative
 if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing snapshot input: $source"}
 $target=Join-Path $dest $relative
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
 $before=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
 Copy-Item -LiteralPath $source -Destination $target -Force
 $hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
 if($before -ne $hash -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash){throw "Source changed during snapshot: $source"}
 $manifest += [pscustomobject]@{Path=$relative.Replace('\','/');Bytes=(Get-Item -LiteralPath $target).Length;SHA256=$hash}
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $dest 'MANIFEST.json') -Encoding UTF8
Write-Output "SNAPSHOT PASS: $($manifest.Count) files copied and hash-verified; game files read-only"
