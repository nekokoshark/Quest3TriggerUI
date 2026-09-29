@echo off
setlocal
set "XR_API_LAYER_PATH=F:\vam1.22.0.12\BepInEx\plugins\Quest3TriggerUI\maintenance\ofxr_activation_20260909"
set "XR_ENABLE_API_LAYERS=XR_APILAYER_XRFrameBridge_diagnostic"
cd /d "F:\vam1.22.0.12"
start "VaM" VaM.exe -vrmode OpenVR
exit /b 0
