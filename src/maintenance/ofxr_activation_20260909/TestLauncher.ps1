param([Parameter(Mandatory=$true)][string]$Path,[Parameter(Mandatory=$true)][ValidateSet('Baseline','Modified')][string]$Mode)
$text=[IO.File]::ReadAllText($Path)
if($Mode -eq 'Baseline'){
  if($text -ne "START `"VaM`" VaM.exe -vrmode OpenVR`r`n"){throw 'Unexpected original launcher'}
  'BASELINE RESULT=PASS; OpenVR launch present; OFXR registration absent'
}else{
  foreach($required in @('XR_API_LAYER_PATH=','XR_ENABLE_API_LAYERS=XR_APILAYER_XRFrameBridge_diagnostic','VaM.exe -vrmode OpenVR')){if(-not $text.Contains($required)){throw "Missing: $required"}}
  'MODIFIED RESULT=PASS; OFXR V068 is enabled process-locally before OpenVR launch'
}
