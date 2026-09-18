using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
namespace Quest3TriggerUI
{
    // Read rendered garment vertices, never use the huge GPU renderer bounds
    // as an identification proxy. No colliders are added to the scene.
    internal static class ClothingSurfacePicker
    {
        private sealed class Surface
        {
            internal DAZClothingItem item;
            internal DAZSkinWrap wrap;
            internal Mesh mesh;
            internal int[] triangles;
            internal Vector3[] points;
            internal Bounds bounds;
            internal AsyncGPUReadbackRequest request;
            internal bool pending,valid;
            internal float updated;
        }
        private static readonly FieldInfo BufferField=typeof(DAZSkinWrap).GetField("_drawVerticesBuffer",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        private static readonly List<Surface> Surfaces=new List<Surface>();
        private static float _scan,_read,_nextHit;
        private static DAZClothingItem _lastHit;
        private static Vector3 _lastPoint;
        private static int _cursor;
        internal static void Clear(){Surfaces.Clear();_scan=0;_read=0;_cursor=0;_nextHit=0;_lastHit=null;}
        internal static DAZClothingItem Pick(DAZCharacterSelector selector,Ray ray,out Vector3 point)
        {
            float now=Time.unscaledTime;
            if(now>=_scan){
                _scan=now+1f;
                Surfaces.RemoveAll(delegate(Surface s){return s.item==null || !s.item.active || s.wrap==null;});
                foreach(var item in selector.clothingItems) {
                    if(item==null || !item.active) continue;
                    foreach(var wrap in item.GetComponentsInChildren<DAZSkinWrap>(true)) {
                        if(Surfaces.Exists(delegate(Surface s){return s.wrap==wrap;})) continue;
                        Mesh mesh=wrap.Mesh;
                        if(mesh!=null) Surfaces.Add(new Surface{item=item,wrap=wrap,mesh=mesh,triangles=mesh.triangles});
                    }
                }
            }
            int pending=0;
            foreach(var s in Surfaces){
                if(!s.pending)continue;
                if(!s.request.done){pending++;continue;}
                s.pending=false;
                if(s.request.hasError){s.valid=false;continue;}
                var data=s.request.GetData<Vector3>();
                if(s.points==null || s.points.Length!=data.Length)s.points=new Vector3[data.Length];
                data.CopyTo(s.points);s.valid=s.points.Length>0;
                if(s.valid){s.bounds=new Bounds(s.points[0],Vector3.zero);foreach(var p in s.points)s.bounds.Encapsulate(p);s.updated=now;}
            }
            if(now>=_read && pending<2 && Surfaces.Count>0 && SystemInfo.supportsAsyncGPUReadback){
                _read=now+.05f;
                for(int n=0;n<Surfaces.Count;n++){
                    var s=Surfaces[_cursor++%Surfaces.Count];if(s.pending || s.wrap==null)continue;
                    var buffer=BufferField==null?null:BufferField.GetValue(s.wrap) as ComputeBuffer;
                    if(buffer==null || buffer.stride!=12)continue;
                    s.request=AsyncGPUReadback.Request(buffer);s.pending=true;break;
                }
            }
            if(now<_nextHit){point=_lastPoint;return _lastHit!=null && _lastHit.active?_lastHit:null;}
            _nextHit=now+.05f;
            float nearest=float.MaxValue;DAZClothingItem hit=null;point=Vector3.zero;
            // Native garment colliders also cover rigid accessories.
            foreach(var h in Physics.RaycastAll(ray,8f)){
                var item=h.collider.GetComponentInParent<DAZClothingItem>();
                if(item!=null && item.active && item.characterSelector==selector && h.distance<nearest){nearest=h.distance;hit=item;}
            }
            foreach(var s in Surfaces){
                float entry;
                if(!s.valid || s.item==null || !s.item.active || now-s.updated>1.5f || !s.bounds.IntersectRay(ray,out entry))continue;
                var t=s.triangles;var p=s.points;
                for(int i=0;i+2<t.Length;i+=3){
                    if(t[i]>=p.Length || t[i+1]>=p.Length || t[i+2]>=p.Length)continue;
                    float depth;
                    if(Triangle(ray,p[t[i]],p[t[i+1]],p[t[i+2]],out depth) && depth<nearest){nearest=depth;hit=s.item;}
                }
            }
            if(hit!=null)point=ray.GetPoint(nearest);
            _lastHit=hit;_lastPoint=point;return hit;
        }
        internal static bool Triangle(Ray ray,Vector3 a,Vector3 b,Vector3 c,out float depth)
        {
            depth=0;var e1=b-a;var e2=c-a;var p=Vector3.Cross(ray.direction,e2);float det=Vector3.Dot(e1,p);
            if(Mathf.Abs(det)<1e-8f)return false;
            float inv=1f/det;var t=ray.origin-a;float u=Vector3.Dot(t,p)*inv;if(u<0 || u>1)return false;
            var q=Vector3.Cross(t,e1);float v=Vector3.Dot(ray.direction,q)*inv;if(v<0 || u+v>1)return false;
            depth=Vector3.Dot(e2,q)*inv;return depth>0 && depth<=8f;
        }
    }
}
