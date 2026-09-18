using System;
using System.Reflection;
using UnityEngine;
public sealed class DlssUiOverlay : MonoBehaviour {
 static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 RenderTexture sceneFlip;
 int completedFrame=-1;
 Camera source, overlay; RenderTexture warmup; Component capture; FieldInfo eyes, applied, presented; bool drawing; int logged; float nextFind;
 public static void Stop(){foreach(var b in Resources.FindObjectsOfTypeAll<MonoBehaviour>())if(b!=null && b.GetType().FullName=="DlssUiOverlay")DestroyImmediate(b.gameObject);}
 public static void Begin(){Stop();var go=new GameObject("DLSS UI completion");DontDestroyOnLoad(go);go.AddComponent<DlssUiOverlay>();}
 void OnEnable(){Camera.onPostRender+=AfterCamera;}
 void OnDisable(){Camera.onPostRender-=AfterCamera;}
 void OnDestroy(){if(overlay!=null)Destroy(overlay.gameObject);if(warmup!=null){warmup.Release();Destroy(warmup);}if(sceneFlip!=null){sceneFlip.Release();Destroy(sceneFlip);}}
 void AfterCamera(Camera camera){
  if(drawing || camera==null || camera.name!="CameraHook" || !camera.stereoEnabled)return;
  try{
   if(capture==null && Time.unscaledTime>=nextFind){nextFind=Time.unscaledTime+1f;foreach(var c in Camera.allCameras)foreach(var b in c.GetComponents<MonoBehaviour>())if(b!=null && b.GetType().FullName=="VamDlssNr.NrCapture"){
    var t=b.GetType();var primary=t.GetField("Primary",F);if(primary==null || !(bool)primary.GetValue(b))continue;
    capture=b;source=c;eyes=t.GetField("_eyeOut",F);applied=t.GetField("_vrScaleApplied",F);presented=t.GetField("_srPresented",F);
   }}
   if(capture==null || eyes==null || applied==null || presented==null || !(bool)applied.GetValue(capture) || !(bool)presented.GetValue(capture))return;
   var targets=eyes.GetValue(capture) as RenderTexture[];
   if(targets==null || targets.Length!=2 || targets[0]==null || targets[1]==null || !targets[0].IsCreated() || !targets[1].IsCreated())return;
   if(completedFrame==Time.frameCount)return;
   int mask=camera.cullingMask & ~source.cullingMask;
   if(overlay==null){var go=new GameObject("DLSS UI eyes");DontDestroyOnLoad(go);overlay=go.AddComponent<Camera>();overlay.enabled=false;}
   drawing=true;
   var previous=RenderTexture.active;
   bool single=Shader.IsKeywordEnabled("UNITY_SINGLE_PASS_STEREO");
   bool instanced=Shader.IsKeywordEnabled("STEREO_INSTANCING_ON");
   bool multiview=Shader.IsKeywordEnabled("STEREO_MULTIVIEW_ON");
   try{
    Shader.DisableKeyword("UNITY_SINGLE_PASS_STEREO");Shader.DisableKeyword("STEREO_INSTANCING_ON");Shader.DisableKeyword("STEREO_MULTIVIEW_ON");
    overlay.CopyFrom(camera);overlay.enabled=false;overlay.stereoTargetEye=StereoTargetEyeMask.None;overlay.cullingMask=mask;
    overlay.clearFlags=CameraClearFlags.Depth;overlay.depthTextureMode=DepthTextureMode.None;overlay.rect=new Rect(0,0,1,1);
    overlay.transform.position=camera.transform.position;overlay.transform.rotation=camera.transform.rotation;
    if(warmup==null){warmup=new RenderTexture(1,1,0);warmup.Create();}
    overlay.targetTexture=warmup;overlay.cullingMask=0;overlay.clearFlags=CameraClearFlags.Nothing;overlay.Render();
    // Correct the scene pixels, not the runtime UV bounds or the already-correct UI.
    if(sceneFlip==null || sceneFlip.width!=targets[0].width || sceneFlip.height!=targets[0].height || sceneFlip.format!=targets[0].format){
     if(sceneFlip!=null){sceneFlip.Release();Destroy(sceneFlip);}
     sceneFlip=new RenderTexture(targets[0].width,targets[0].height,0,targets[0].format,targets[0].sRGB?RenderTextureReadWrite.sRGB:RenderTextureReadWrite.Linear);
     sceneFlip.useMipMap=false;sceneFlip.autoGenerateMips=false;
     if(!sceneFlip.Create())throw new InvalidOperationException("DLSS scene flip target creation failed");
    }
    bool previousSrgb=GL.sRGBWrite;
    try{
     GL.sRGBWrite=targets[0].sRGB;
     for(int i=0;i<2;i++){
      Graphics.Blit(targets[i],sceneFlip,new Vector2(1,-1),new Vector2(0,1));
      Graphics.CopyTexture(sceneFlip,targets[i]);
     }
    }finally{GL.sRGBWrite=previousSrgb;}
    overlay.cullingMask=mask;overlay.clearFlags=CameraClearFlags.Depth;
    for(int i=0;i<2;i++){
     var eye=i==0?Camera.StereoscopicEye.Left:Camera.StereoscopicEye.Right;
     overlay.targetTexture=targets[i];overlay.worldToCameraMatrix=camera.GetStereoViewMatrix(eye);
     // The runtime submits these reconstructed textures with reversed V bounds.
     // Match that origin in the late UI pass, including off-centre projection.
     Matrix4x4 projection=camera.GetStereoProjectionMatrix(eye);
     projection.m10=-projection.m10;projection.m11=-projection.m11;
     projection.m12=-projection.m12;projection.m13=-projection.m13;
     overlay.projectionMatrix=projection;
     bool previousCulling=GL.invertCulling;
     try{GL.invertCulling=!previousCulling;overlay.Render();}
     finally{GL.invertCulling=previousCulling;}
    }
    completedFrame=Time.frameCount;
    if(Quest3TriggerUI.VrVideoRecorder.Current!=null) Quest3TriggerUI.VrVideoRecorder.Current.Capture(targets[0],false,false,true);
   }finally{overlay.targetTexture=null;RenderTexture.active=previous;RestoreKeyword("UNITY_SINGLE_PASS_STEREO",single);RestoreKeyword("STEREO_INSTANCING_ON",instanced);RestoreKeyword("STEREO_MULTIVIEW_ON",multiview);drawing=false;}
   if(logged++==0)Debug.Log("[DLSS UI] completed both reconstructed eyes; mask="+mask+" targets="+targets[0].width+"x"+targets[0].height);
  }catch(Exception e){Debug.LogError("[DLSS UI] "+e);enabled=false;Camera.onPostRender-=AfterCamera;}
 }
 static void RestoreKeyword(string name,bool enabled){if(enabled)Shader.EnableKeyword(name);else Shader.DisableKeyword(name);}
}
