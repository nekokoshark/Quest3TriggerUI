using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
namespace Quest3TriggerUI
{
    internal sealed partial class VrKeyboardOverlay
    {
        private readonly VrVideoRecorder _recorder = new VrVideoRecorder();
        private QuickActionDefinition _recordDefinition;
        private GameObject _recordPanel;
        private Text _recordStatus;
        private ShortcutActionButton _recordToggle;
        private bool _recordWasActive;
        private string _recordLastStatus;
        private QuickActionDefinition RecordingToggleDefinition()
        {
            return new QuickActionDefinition("record.toggle", _recorder.Active?"停止":"开始", ToggleRecording, delegate{return _recorder.Active;});
        }
        private QuickActionDefinition BuildRecordingAction()
        {
            _recordDefinition = new QuickActionDefinition("record", "录制", ShowRecordingSettings,
                delegate{return _recorder.Active;},new List<QuickActionDefinition>{RecordingToggleDefinition(),
                new QuickActionDefinition("record.settings","设置",ShowRecordingSettings)});
            return _recordDefinition;
        }
        private void ToggleRecording()
        {
            try { if(_recorder.Active) _recorder.Stop(); else if(!_recorder.Busy) _recorder.Start(); }
            catch(Exception e) { _recorder.Status="录制启动失败："+e.Message; }
            TickRecording();
        }
        private void TickRecording()
        {
            if(_recorder.Active) _recorder.SetDlss(_dlss.SrActive);
            _recorder.Tick();
            if(_recordWasActive!=_recorder.Active)
            {
                _recordWasActive=_recorder.Active;
                _recordDefinition.Children[0]=RecordingToggleDefinition(); _quickActionRevision++;
                if(_recordToggle!=null) { Text t=_recordToggle.GetComponentInChildren<Text>(); if(t!=null)t.text=_recorder.Active?"停止":"开始"; }
            }
            if(_recordLastStatus!=_recorder.Status)
            {
                _recordLastStatus=_recorder.Status;
                if(_recordStatus!=null)_recordStatus.text=_recorder.Status;
                UpdateHelpText(_recorder.Status);
                UnityEngine.Debug.Log("[VR Recording] "+_recorder.Status);
            }
        }
        private void ShowRecordingSettings()
        {
            if(_canvas==null)Build(); if(!Visible)Toggle();
            if(_recordPanel!=null) { _recordPanel.SetActive(!_recordPanel.activeSelf);return; }
            _recordPanel=CreateUiObject("Recording settings",_canvas.GetComponent<RectTransform>());
            RectTransform p=_recordPanel.GetComponent<RectTransform>(); SetTopLeft(p,380,-570,1450,520);
            _recordPanel.AddComponent<Image>().color=new Color(.04f,.06f,.09f,.99f);
            GameObject title=CreateUiObject("Title",p);SetTopLeft(title.GetComponent<RectTransform>(),10,5,1250,50);
            AddText(title.transform,"VR录制 · 单眼 · 可选帧率 · " + VrVideoRecorder.OutputDirectory + " · 含系统混音",30,TextAnchor.MiddleCenter,Color.white,4);
            CreateBindableActionButton(p,"关闭",1300,5,140,50,delegate{_recordPanel.SetActive(false);},"rec.close");
            for(int i=0;i<VrVideoRecorder.Scales.Length;i++)
            {
                int index=i; float scale=VrVideoRecorder.Scales[i];
                int w=VrVideoRecorder.Dimension(XRSettings.eyeTextureWidth,scale),h=VrVideoRecorder.Dimension(XRSettings.eyeTextureHeight,scale);
                CreateBindableActionButton(p,scale.ToString("0.0")+"倍\n"+w+"×"+h,10+i*238,75,228,90,delegate{
                    if(_recorder.Busy){UpdateHelpText("停止录制后再更改设置");return;} _recorder.ScaleIndex=index; RecordingSelection();},"rec.scale."+i);
            }
            string[] codecs={"H.264 (NVENC)","HEVC (NVENC)","AV1 (NVENC)"};
            for(int i=0;i<3;i++){int index=i;CreateBindableActionButton(p,codecs[i],10+i*477,180,465,65,delegate{
                if(_recorder.Busy){UpdateHelpText("停止录制后再更改设置");return;} _recorder.CodecIndex=index;RecordingSelection();},"rec.codec."+i);}
            for(int fi=0;fi<VrVideoRecorder.FrameRates.Length;fi++){int fj=fi;CreateBindableActionButton(p,VrVideoRecorder.FrameRates[fi]+" FPS",10+fj*220,340,210,55,delegate{if(!_recorder.Busy){_recorder.FrameRateIndex=fj;RecordingSelection();}},"rec.fps."+fi);}
            CreateBindableActionButton(p,"码率 -5 Mbps",10,265,350,65,delegate{if(!_recorder.Busy){_recorder.Bitrate=Math.Max(5,_recorder.Bitrate-5);RecordingSelection();}},"rec.bitrate.down");
            CreateBindableActionButton(p,"码率 +5 Mbps",380,265,350,65,delegate{if(!_recorder.Busy){_recorder.Bitrate=Math.Min(200,_recorder.Bitrate+5);RecordingSelection();}},"rec.bitrate.up");
            _recordToggle=CreateBindableActionButton(p,_recorder.Active?"停止":"开始",750,265,680,65,ToggleRecording,"rec.toggle");
            GameObject status=CreateUiObject("Status",p);SetTopLeft(status.GetComponent<RectTransform>(),10,420,1430,80);
            _recordStatus=AddText(status.transform,"",27,TextAnchor.MiddleCenter,Color.white,4);
            RecordingSelection();
        }
        private void RecordingSelection()
        {
            _recordStatus.text="选择："+VrVideoRecorder.Scales[_recorder.ScaleIndex].ToString("0.0")+"倍 · "+_recorder.Bitrate+" Mbps · "+VrVideoRecorder.Codecs[_recorder.CodecIndex]+" · "+VrVideoRecorder.FrameRates[_recorder.FrameRateIndex]+" FPS\n"+_recorder.Status;
        }
    }
}
