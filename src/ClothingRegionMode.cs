using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed class RegionClothingSlot : MonoBehaviour
    {
        internal DAZClothingItem Item;
        internal int Index;
    }

    internal static class ClothingRegionMode
    {
        internal static bool Active { get; private set; }
        private static readonly string[] Names = { "手掌", "手臂", "腿", "脚", "腰部", "背部", "胸部", "脖子", "肩部", "面部", "头部" };
        private static readonly string[][] Tags = {
            new[]{"gloves","glove","hands","手套","手掌"},
            new[]{"sleeves","armband","arms","手臂","袖套"},
            new[]{"stockings","pants","leggings","thighhigh","legs","丝袜","长裤","腿"},
            new[]{"shoes","boots","heels","socks","feet","鞋","靴","袜子","脚"},
            new[]{"panties","skirt","belt","waist","underwear","腰部","裙子","内裤","腰带"},
            new[]{"back","cape","wings","背部","披风","翅膀"},
            new[]{"bra","bikini top","shirt","top","dress","chest","胸部","胸罩","上衣","连衣裙"},
            new[]{"necklace","choker","collar","neck","项链","颈圈","脖子"},
            new[]{"shoulder","shoulders","肩部","肩饰"},
            new[]{"glasses","mask","face","眼镜","面罩","面部"},
            new[]{"hat","head","headwear","头部","帽子","头饰"}
        };
        private static readonly string[] HeadEyeTerms = { "eye", "eyes", "eyewear", "glasses", "goggles", "lens", "contact", "iris", "pupil", "eyelash", "lashes", "mask", "face", "眼", "眼镜", "面罩", "面部" };
        private static readonly List<string>[] Manual = new List<string>[11];
        private static readonly List<string>[] Order = new List<string>[11];
        private static readonly HashSet<string>[] Hidden = new HashSet<string>[11];
        private static DAZClothingItem _pointed;
        private static Atom _atom;
        private static DAZCharacterSelector _selector;
        private static readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
        private static readonly Dictionary<string,DAZClothingItem> ByUid = new Dictionary<string,DAZClothingItem>();
        private static readonly List<DAZClothingItem> _items = new List<DAZClothingItem>();
        private static readonly List<DAZClothingItem>[] Auto = new List<DAZClothingItem>[11];
        private static Canvas _canvas;
        private static RectTransform _root;
        private static Transform _cells;
        private static RectTransform _garmentHeader;
        private static readonly List<DAZClothingItem> _wornRegion = new List<DAZClothingItem>();
        private static Text _title;
        private static InputField _pageInput;
        private static bool _pageInputEditing;
        private static bool _syncingPageInput;
        private static Font _font;
        private static int _region = -1, _page;
        private static bool _register;
        private static float _miss, _nextIndex;
        private static float _panelRouteUntil;
        private const float PanelRouteGrace = .65f;
        private static int _candidateRegion = -1;
        private static float _candidateRegionSince;
        private const float PanelRegionDwell = .45f;
        private static RegionClothingSlot _drag, _hover;
        private static bool _dragging;
        private static readonly string[] Sides = { "l", "r" };
        private static Vector3 _anchor;
        private static string _message = "";
        private static string FilePath { get { return Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.clothing-regions.txt"); } }
        private static string PresetDir { get { return Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.clothing-presets"); } }
        private static string PresetPath(string uid)
        {
            var sb = new StringBuilder(uid.Length);
            foreach (var c in uid)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            return Path.Combine(PresetDir, sb.ToString() + ".json");
        }
        // Registration captures the garment's whole storable state (physics,
        // materials, colors — every JSONStorable on the item instance) so
        // wearing it later restores exactly that preset. DAZClothingItem is
        // not itself a storable: its params live on the JSONStorable[]
        // "jss" inside the dynamic instance, which unloads when unworn —
        // hence restore runs deferred until the instance rebuilds.
        private static FieldInfo _fiJss;
        private static JSONStorable[] ItemStorables(DAZClothingItem item)
        {
            if (_fiJss == null)
                _fiJss = typeof(JSONStorableDynamic).GetField("jss",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fiJss == null || item == null) return null;
            return _fiJss.GetValue(item) as JSONStorable[];
        }
        private static void SnapshotClothing(DAZClothingItem item)
        {
            try {
                var storables = ItemStorables(item);
                if (storables == null || storables.Length == 0) {
                    Quest3TriggerUIPlugin.Log.LogInfo("服装预设快照跳过（实例未加载）：" + item.uid);
                    return;
                }
                var root = new JSONClass();
                int n = 0;
                foreach (var js in storables) {
                    if (js == null || string.IsNullOrEmpty(js.storeId)) continue;
                    var jc = js.GetJSON(true, true, true);
                    if (jc != null) { root[js.storeId] = jc; n++; }
                }
                if (n == 0) return;
                Directory.CreateDirectory(PresetDir);
                File.WriteAllText(PresetPath(item.uid), root.ToString(""));
                Quest3TriggerUIPlugin.Log.LogInfo("服装预设已存：" + item.uid + " (" + n + " storables)");
            } catch (Exception e) {
                Quest3TriggerUIPlugin.Log.LogError("服装预设快照失败：" + e.Message);
            }
        }
        private static IEnumerator RestorePresetRoutine(DAZClothingItem item, string path)
        {
            JSONClass root;
            try { root = JSON.Parse(File.ReadAllText(path)).AsObject; }
            catch (Exception e) {
                Quest3TriggerUIPlugin.Log.LogError("服装预设解析失败：" + e.Message);
                yield break;
            }
            if (root == null) yield break;
            float deadline = Time.unscaledTime + 8f;
            while (item != null && Time.unscaledTime < deadline)
            {
                var storables = ItemStorables(item);
                if (storables != null && storables.Length > 0)
                {
                    int n = 0;
                    foreach (var js in storables) {
                        if (js == null || string.IsNullOrEmpty(js.storeId)) continue;
                        var st = root[js.storeId] == null ? null : root[js.storeId].AsObject;
                        if (st == null) continue;
                        js.RestoreFromJSON(st, true, true, null, false);
                        js.LateRestoreFromJSON(st, true, true, false);
                        n++;
                    }
                    Quest3TriggerUIPlugin.Log.LogInfo("服装预设已应用：" + item.uid + " (" + n + " storables)");
                    yield break;
                }
                yield return null;
            }
        }
        private static bool WearWithPreset(DAZClothingItem item)
        {
            _selector.SetActiveClothingItem(item, true);
            if (!item.active) return false;
            string path = PresetPath(item.uid);
            if (!File.Exists(path)) return false;
            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetRoutine(item, path));
            return true;
        }

        internal static bool PointerInside
        {
            get
            {
                var go = VrPointerPresentation.CurrentLookTarget(true);
                return Active && _root != null && _root.gameObject.activeSelf && go != null && go.transform.IsChildOf(_root);
            }
        }
        internal static void Toggle()
        {
            if (Active) { Shutdown(); return; }
            _atom = SceneQuickActions.FindClosestFemale();
            if (_atom == null) { Quest3TriggerUIPlugin.Log.LogInfo("手动服装：视线前方没有女性角色。"); return; }
            _selector = _atom.GetStorableByID("geometry") as DAZCharacterSelector;
            if (_selector == null) return;
            Load(); Bones.Clear();
            foreach (var bone in _atom.GetComponentsInChildren<DAZBone>(true))
                if (!Bones.ContainsKey(bone.name)) Bones.Add(bone.name, bone.transform);
            HairDebugMode.Shutdown();
            PluginListMode.Shutdown();
            Active = true; _region = -1; _page = 0; _register = false; _nextIndex = 0; _miss = Time.unscaledTime;
            Quest3TriggerUIPlugin.Log.LogInfo("手动服装目标：" + _atom.uid);
        }
        internal static void Shutdown()
        {
            Active = false; _pointed=null; _wornRegion.Clear(); _panelRouteUntil=0f; _candidateRegion=-1; ClothingSurfacePicker.Clear(); _drag = null; _hover = null; _dragging = false;
            if (_canvas != null && SuperController.singleton != null) SuperController.singleton.RemoveCanvas(_canvas);
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _canvas = null; _root = null; _cells = null; _garmentHeader = null; _atom = null; _selector = null;
            _pageInput = null; _pageInputEditing = false; _syncingPageInput = false;
            Bones.Clear(); ByUid.Clear(); _items.Clear(); _wornRegion.Clear();
            for(int i=0;i<11;i++) if(Auto[i]!=null) Auto[i].Clear();
        }
        private static void Load()
        {
            for(int i=0;i<11;i++) { Manual[i]=new List<string>(); Order[i]=new List<string>(); Hidden[i]=new HashSet<string>(); Auto[i]=new List<DAZClothingItem>(); }
            if(!File.Exists(FilePath)) return;
            foreach(var line in File.ReadAllLines(FilePath))
            {
                var p=line.Split('\t'); int r;
                if(p.Length!=3 || !int.TryParse(p[1],out r) || r<0 || r>=11) continue;
                if(p[0]=="M" && !Manual[r].Contains(p[2])) Manual[r].Add(p[2]);
                if(p[0]=="O" && !Order[r].Contains(p[2])) Order[r].Add(p[2]);
                if(p[0]=="H") Hidden[r].Add(p[2]);
            }
        }
        private static void Save()
        {
            var lines=new List<string>();
            for(int i=0;i<11;i++) {
                foreach(var uid in Manual[i]) lines.Add("M\t"+i+"\t"+uid);
                foreach(var uid in Order[i]) lines.Add("O\t"+i+"\t"+uid);
                foreach(var uid in Hidden[i]) lines.Add("H\t"+i+"\t"+uid);
            }
            File.WriteAllLines(FilePath,lines.ToArray());
        }
        internal static bool Matches(int region, string[] tags)
        {
            if(tags==null) return false;
            foreach(var tag in tags) foreach(var term in Tags[region])
                if(string.Equals(tag.Trim(),term,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static bool Belongs(DAZClothingItem item,int r)
        {
            if (r == 10 && IsEyeWear(item)) return false;
            // A user registration overrides automatic regional classification.
            bool registered=false;
            for(int i=0;i<11;i++) if(Manual[i].Contains(item.uid)) registered=true;
            return registered ? Manual[r].Contains(item.uid) : Matches(r,item.tagsArray);
        }
        private static bool IsEyeWear(DAZClothingItem item)
        {
            if (item == null) return false;
            if (item.tagsArray != null)
                foreach (var tag in item.tagsArray)
                    if (ContainsHeadEyeTerm(tag)) return true;
            return ContainsHeadEyeTerm(item.displayName) || ContainsHeadEyeTerm(item.uid);
        }
        private static bool ContainsHeadEyeTerm(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string token = "";
            for (int i = 0; i <= value.Length; i++) {
                if (i < value.Length && char.IsLetterOrDigit(value[i])) { token += char.ToLowerInvariant(value[i]); continue; }
                if (token.Length > 0) {
                    foreach (var term in HeadEyeTerms) if (token == term) return true;
                    token = "";
                }
            }
            return false;
        }
        private static bool PurgeHeadEyeRegistrations()
        {
            bool changed = false;
            for (int i = Manual[10].Count - 1; i >= 0; i--) {
                var item = Resolve(Manual[10][i]);
                if ((item != null && IsEyeWear(item)) || (item == null && ContainsHeadEyeTerm(Manual[10][i]))) { Manual[10].RemoveAt(i); changed = true; }
            }
            for (int i = Order[10].Count - 1; i >= 0; i--) {
                var item = Resolve(Order[10][i]);
                if ((item != null && IsEyeWear(item)) || (item == null && ContainsHeadEyeTerm(Order[10][i]))) { Order[10].RemoveAt(i); changed = true; }
            }
            return changed;
        }
        private static void Index()
        {
            ByUid.Clear();
            for(int r=0;r<11;r++) Auto[r].Clear();
            if(_selector.clothingItems==null) return;
            foreach(var item in _selector.clothingItems)
                if(item!=null) { ByUid[item.uid]=item; for(int r=0;r<11;r++) if(Belongs(item,r)) Auto[r].Add(item); }
            if (PurgeHeadEyeRegistrations()) Save();
            _nextIndex=float.PositiveInfinity;
        }
        private static DAZClothingItem Resolve(string uid)
        {
            DAZClothingItem item; return ByUid.TryGetValue(uid,out item)?item:null;
        }
        private static void Populate()
        {
            _items.Clear(); if(_region<0) return;
            if(_register) {
                foreach(var item in _selector.clothingItems) if(item!=null && item.active) _items.Add(item);
            } else {
                foreach(var uid in Manual[_region]) { var item=Resolve(uid); if(item!=null && !Hidden[_region].Contains(uid)) _items.Add(item); }
                foreach(var uid in Order[_region]) { var item=Resolve(uid); if(item!=null && Belongs(item,_region) && !Hidden[_region].Contains(uid) && !_items.Contains(item)) _items.Add(item); }
                foreach(var item in Auto[_region]) if(!Hidden[_region].Contains(item.uid) && !_items.Contains(item)) _items.Add(item);
            }
            _page=Mathf.Clamp(_page,0,Mathf.Max(0,(_items.Count-1)/8));
        }
        private static Vector3 Point(string bone,Vector3 fallback)
        { Transform t; return Bones.TryGetValue(bone,out t) && t!=null ? t.position : fallback; }
        private static void Probe(Ray ray,Vector3 a,Vector3 b,float radius,int region,ref float best,ref int hit,ref Vector3 anchor)
        {
            float depth; float d=HairDebugMode.SegmentDistance(ray,a,b,out depth);
            if(depth>0 && d<radius && depth-radius<best) { best=depth-radius; hit=region; anchor=(a+b)*.5f; }
        }
        private static int Hit(Ray ray,out Vector3 anchor)
        {
            Vector3 origin=_atom.mainController.transform.position;
            Vector3 hip=Point("hip",origin),chest=Point("chest",hip+Vector3.up*.45f),head=Point("head",chest+Vector3.up*.3f);
            Transform ct; Vector3 forward=Bones.TryGetValue("chest",out ct)?ct.forward:_atom.mainController.transform.forward;
            Vector3 hf=Bones.TryGetValue("head",out ct)?ct.forward:forward;
            float best=float.MaxValue; int hit=-1; anchor=head;
            Probe(ray,hip,Point("abdomen",hip+Vector3.up*.15f),.16f,4,ref best,ref hit,ref anchor);
            var front=chest+forward*.10f;var back=chest-forward*.12f;
            Probe(ray,front,front,.16f,6,ref best,ref hit,ref anchor);
            Probe(ray,back,back,.16f,5,ref best,ref hit,ref anchor);
            Probe(ray,Point("neck",head-Vector3.up*.14f),head-Vector3.up*.08f,.07f,7,ref best,ref hit,ref anchor);
            Probe(ray,head+Vector3.up*.08f,head+Vector3.up*.16f,.10f,10,ref best,ref hit,ref anchor);
            Probe(ray,head+hf*.10f,head+hf*.10f+Vector3.up*.04f,.09f,9,ref best,ref hit,ref anchor);
            foreach(var side in Sides) {
                var shoulder=Point(side+"Shldr",chest);var elbow=Point(side+"ForeArm",shoulder);var hand=Point(side+"Hand",elbow);
                Probe(ray,hand,hand,.08f,0,ref best,ref hit,ref anchor);
                Probe(ray,Vector3.Lerp(shoulder,elbow,.25f),elbow,.07f,1,ref best,ref hit,ref anchor);
                Probe(ray,elbow,Vector3.Lerp(elbow,hand,.85f),.065f,1,ref best,ref hit,ref anchor);
                Probe(ray,shoulder,shoulder,.09f,8,ref best,ref hit,ref anchor);
                var thigh=Point(side+"Thigh",hip);var shin=Point(side+"Shin",thigh);var foot=Point(side+"Foot",shin);
                Probe(ray,thigh,shin,.11f,2,ref best,ref hit,ref anchor);
                Probe(ray,shin,Vector3.Lerp(shin,foot,.88f),.085f,2,ref best,ref hit,ref anchor);
                Probe(ray,foot,Point(side+"Toe",foot),.08f,3,ref best,ref hit,ref anchor);
            }
            return hit;
        }
        // The worn strip is synchronized only after an explicit clothing operation
        // (or when the user points to a different region), never from Tick polling.
        private static void SyncWornRegion()
        {
            _wornRegion.Clear();
            if (_region < 0 || _selector == null || _selector.clothingItems == null) return;
            foreach (var item in _selector.clothingItems)
                if (item != null && item.active && Belongs(item, _region)) _wornRegion.Add(item);
        }
        private static void RefreshAfterClothingOperation(params DAZClothingItem[] affected)
        {
            if (_region < 0 || _root == null) return;
            // Keep cards for garments that were just removed while this panel is
            // still open. A fresh region entry calls SyncWornRegion and drops them.
            if (affected != null)
                foreach (var item in affected)
                    if (item != null && Belongs(item, _region) && !_wornRegion.Contains(item)) _wornRegion.Add(item);
            if (_selector != null && _selector.clothingItems != null)
                foreach (var item in _selector.clothingItems)
                    if (item != null && item.active && Belongs(item, _region) && !_wornRegion.Contains(item)) _wornRegion.Add(item);
            if (_pointed != null && !_pointed.active) _pointed = null;
            Rebuild();
        }
        private static void ArmPanelRoute()
        {
            _panelRouteUntil = Time.unscaledTime + PanelRouteGrace;
            _candidateRegion = -1;
        }
        private static bool RegionSwitchReady(int candidate, bool panelVisible, float now)
        {
            if (!panelVisible || candidate < 0 || candidate == _region) { _candidateRegion = -1; return true; }
            if (_candidateRegion != candidate) { _candidateRegion = candidate; _candidateRegionSince = now; return false; }
            return now - _candidateRegionSince >= PanelRegionDwell;
        }
        private static void ToggleGarment(DAZClothingItem item)
        {
            if (item == null) return;
            bool wear = !item.active;
            if (wear ? UiAssistHudLink.IsClothingBanned(_atom, item.uid) : UiAssistHudLink.IsClothingLocked(_atom, item.uid))
            { _message = wear ? "该服装已禁用" : "该服装已锁定"; UpdateTitle(); return; }
            bool preset = false;
            if (wear) preset = WearWithPreset(item);
            else _selector.SetActiveClothingItem(item, false);
            _message = !item.active ? "已脱下：" + item.displayName
                : (preset ? "已穿戴（预设已应用）：" : "已穿戴：") + item.displayName;
            RefreshAfterClothingOperation(item);
        }

        internal static void NotifyClothingChanged(DAZCharacterSelector selector)
        {
            if (!Active || selector == null || selector != _selector || _region < 0) return;
            RefreshAfterClothingOperation();
        }

        internal static void Tick()
        {
            if(!Active) return;
            try {
                if(_atom==null || _selector==null || !_atom.on) { Shutdown(); return; }
                if(Time.unscaledTime>=_nextIndex && _drag==null) { Index(); if(_region>=0) Rebuild(); }
                var trigger=Quest3TriggerUIPlugin.Trigger;
                bool over=PointerInside;
                var go=VrPointerPresentation.CurrentLookTarget(true);
                var slot=go==null || go.GetComponentInParent<UnityEngine.UI.Button>()!=null ? null : go.GetComponentInParent<RegionClothingSlot>();
                if(_hover!=slot) {
                    if(_hover!=null) _hover.GetComponent<Image>().color=new Color(.18f,.22f,.28f,.95f);
                    _hover=slot;
                    if(_hover!=null) _hover.GetComponent<Image>().color=new Color(.6f,.34f,.1f,.95f);
                }
                if(trigger!=null) {
                    if(trigger.LongPressStartFrame==Time.frameCount && slot!=null) { _drag=slot; _dragging=true; _message="拖到目标缩略图后松手（同优先级内排序）"; UpdateTitle(); }
                    if(trigger.ReleasedFrame==Time.frameCount && _dragging) {
                        if(slot!=null && _drag!=null && !_register) Reorder(_drag.Item.uid,slot.Item.uid);
                        _drag=null;_dragging=false;_message="";Rebuild();
                    } else if(trigger.TapFrame==Time.frameCount && slot!=null && !_dragging) Select(slot.Item);
                }
                if (_pageInputEditing || (_pageInput != null && _pageInput.isFocused)) {
                    _miss=Time.unscaledTime;
                    return;
                }
                if(over || _dragging) {
                    _miss=Time.unscaledTime;
                    if(over) ArmPanelRoute();
                    return;
                }
                // Give the user's ray a short, cheap transit window to reach the
                // panel.  Crossing another body region during that transit must
                // not replace the selected panel or trigger another surface pick.
                if(_root!=null && _root.gameObject.activeSelf && Time.unscaledTime < _panelRouteUntil) {
                    _miss=Time.unscaledTime;
                    return;
                }
                var sc=SuperController.singleton;var cam=sc==null?null:sc.rightControllerCamera;
                if(cam==null) return;
                var ray=cam.ViewportPointToRay(new Vector3(.5f,.5f,0));
                Vector3 anchor;var garment=ClothingSurfacePicker.Pick(_selector,ray,out anchor);
                if(garment!=null) {
                    _miss=Time.unscaledTime;
                    Vector3 unused;int r=Hit(ray,out unused);
                    bool reopening=_root==null || !_root.gameObject.activeSelf;
                    if (!RegionSwitchReady(r, !reopening, Time.unscaledTime)) return;
                    if(_pointed!=garment || _root==null || !_root.gameObject.activeSelf) {
                        _pointed=garment; _anchor=anchor; _register=false;
                        if(r>=0 && (r!=_region || reopening)) { _region=r; SyncWornRegion(); } else if(_region<0) { _region=4; SyncWornRegion(); }
                        Ensure();Rebuild();Place();_root.gameObject.SetActive(true);ArmPanelRoute();
                    }
                    return;
                }
                // Keep a just-removed garment accessible until the ray leaves
                // its region, so the same card can put it back on.
                int region=Hit(ray,out anchor);
                if(region>=0) {
                    _miss=Time.unscaledTime;
                    bool reopening=_root==null || !_root.gameObject.activeSelf;
                    if (!RegionSwitchReady(region, !reopening, Time.unscaledTime)) return;
                    if(region!=_region || _root==null || !_root.gameObject.activeSelf) {
                        _pointed=null;_region=region;_page=0;_register=false;_anchor=anchor;_message="";SyncWornRegion();Ensure();Rebuild();Place();_root.gameObject.SetActive(true);ArmPanelRoute();
                    }
                } else if(_root!=null && Time.unscaledTime-_miss>.8f) _root.gameObject.SetActive(false);
            } catch(Exception e) { Quest3TriggerUIPlugin.Log.LogError("手动服装："+e); Shutdown(); }
        }
        private static void Reorder(string from,string to)
        {
            bool manual=Manual[_region].Contains(from);
            if(manual!=Manual[_region].Contains(to)) { _message="手动注册始终优先于自动推荐";return; }
            var list=manual?Manual[_region]:Order[_region];
            if(!manual) foreach(var item in _items) if(!Manual[_region].Contains(item.uid) && !list.Contains(item.uid)) list.Add(item.uid);
            int fromIndex=list.IndexOf(from), toIndex=list.IndexOf(to);
            if(fromIndex<0 || toIndex<0 || fromIndex==toIndex) return;
            string swap=list[fromIndex];list[fromIndex]=list[toIndex];list[toIndex]=swap;Save();
        }
        private static void Select(DAZClothingItem item)
        {
            if(item==null) return;
            if(_register) {
                if (_region == 10 && IsEyeWear(item)) { _message="眼部服装不注册到头部"; UpdateTitle(); return; }
                Manual[_region].Remove(item.uid);Manual[_region].Insert(0,item.uid);Hidden[_region].Remove(item.uid);
                SnapshotClothing(item);
                Save();Index();_register=false;_page=0;_message="已注册（含当前物理/外观预设）："+item.displayName;Rebuild();return;
            }
            if(UiAssistHudLink.IsClothingBanned(_atom,item.uid)) { _message="该服装已在禁用栏中";UpdateTitle();return; }
            // Additive wear: do not remove other active garments from this region.
            bool preset = WearWithPreset(item);
            if(!item.active) { _message="穿戴未成功，保留原服装";UpdateTitle();return; }
            _message=(preset?"已穿戴（预设已应用）：":"已穿戴：")+item.displayName;UpdateTitle();
            RefreshAfterClothingOperation(item);
        }
        private static void TogglePointed()
        {
            ToggleGarment(_pointed);
        }
        private static void Delete(string uid)
        {
            Manual[_region].Remove(uid);Order[_region].Remove(uid);Hidden[_region].Add(uid);
            Save();Rebuild();
        }
        private static void ClearWornCard(DAZClothingItem item)
        {
            if (item == null || item.active) return;
            _wornRegion.Remove(item);
            Rebuild();
        }
        private static RectTransform Rect(Transform parent,string name,float x,float y,float width,float height)
        {
            var go=new GameObject(name,typeof(RectTransform));var rt=go.GetComponent<RectTransform>();rt.SetParent(parent,false);
            rt.anchorMin=rt.anchorMax=new Vector2(0,1);rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(x,-y);rt.sizeDelta=new Vector2(width,height);return rt;
        }
        private static Text Label(Transform parent,string text,float x,float y,float width,float height,int size)
        {
            var rt=Rect(parent,"Label",x,y,width,height);var t=rt.gameObject.AddComponent<Text>();t.font=_font;t.text=text;t.fontSize=size;t.color=Color.white;t.raycastTarget=false;t.alignment=TextAnchor.MiddleCenter;return t;
        }
        private static void Button(Transform parent,string label,float x,float y,float width,Action action)
        {
            var rt=Rect(parent,label,x,y,width,42);var image=rt.gameObject.AddComponent<Image>();image.color=new Color(.16f,.23f,.3f,.96f);
            var button=rt.gameObject.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=image;button.onClick.AddListener(delegate{action();});Label(rt,label,0,0,width,42,20);
        }
        private static InputField PageInput(Transform parent,float x,float y,float width,float height)
        {
            var rt=Rect(parent,"PageInput",x,y,width,height);var image=rt.gameObject.AddComponent<Image>();image.color=new Color(.10f,.14f,.19f,.98f);
            var field=rt.gameObject.AddComponent<InputField>();field.contentType=InputField.ContentType.IntegerNumber;field.lineType=InputField.LineType.SingleLine;field.characterLimit=4;
            var textRt=Rect(rt,"Text",8,0,width-16,height);var text=textRt.gameObject.AddComponent<Text>();text.font=_font;text.fontSize=19;text.color=Color.white;text.alignment=TextAnchor.MiddleCenter;text.raycastTarget=false;
            field.textComponent=text;field.text="1";
            field.onValueChanged.AddListener(delegate(string value){if(!_syncingPageInput) _pageInputEditing=true;});
            return field;
        }
        private static int PageCount()
        {
            return Mathf.Max(1,(_items.Count+7)/8);
        }
        private static void JumpToPage()
        {
            int page;
            if(_pageInput==null || !int.TryParse(_pageInput.text,out page)) { _message="请输入页码";UpdateTitle();return; }
            _page=Mathf.Clamp(page-1,0,PageCount()-1);_message="";_pageInputEditing=false;Rebuild();
        }
        private static void Ensure()
        {
            if(_root!=null) return;
            _font=(Font)Resources.GetBuiltinResource(typeof(Font),"Arial.ttf");
            _root=new GameObject("Q3 Regional Clothing",typeof(RectTransform)).GetComponent<RectTransform>();_root.sizeDelta=new Vector2(720,530);_root.localScale=Vector3.one*.00085f;
            _canvas=_root.gameObject.AddComponent<Canvas>();_canvas.renderMode=RenderMode.WorldSpace;_canvas.overrideSorting=true;_canvas.sortingOrder=32757;
            _root.gameObject.AddComponent<GraphicRaycaster>();_root.gameObject.AddComponent<Image>().color=new Color(.06f,.08f,.12f,.92f);
            SuperController.singleton.AddCanvas(_canvas);
            _title=Label(_root,"",8,5,704,56,20);
            Button(_root,"注册已穿服装",8,64,172,delegate{_pointed=null;_register=!_register;_page=0;Rebuild();});
            Button(_root,"刷新 TAG",190,64,126,delegate{Index();Rebuild();});
            Button(_root,"恢复隐藏",326,64,126,delegate{Hidden[_region].Clear();Save();Rebuild();});
            Button(_root,"退出手动",558,64,154,Shutdown);
            _cells=Rect(_root,"Candidates",8,120,704,340);
            Button(_root,"上一页",8,478,110,delegate{_page=Mathf.Max(0,_page-1);Rebuild();});
            Button(_root,"首页",124,478,92,delegate{_page=0;Rebuild();});
            _pageInput=PageInput(_root,222,478,110,42);
            Button(_root,"跳转",338,478,90,JumpToPage);
            Button(_root,"尾页",434,478,92,delegate{_page=PageCount()-1;Rebuild();});
            Button(_root,"下一页",540,478,172,delegate{_page=Mathf.Min(_page+1,PageCount()-1);Rebuild();});
        }
        private static void UpdateTitle()
        {
            if(_title!=null) _title.text=_atom.uid+" · "+Names[_region]+(_register?" · 点选已穿服装注册":"")+"  "+(_page+1)+"/"+PageCount()+"\n"+_message;
            if(_pageInput!=null && !_pageInput.isFocused) { _syncingPageInput=true; _pageInput.text=(_page+1).ToString(); _syncingPageInput=false; }
        }
        private static void Rebuild()
        {
            if(_root==null) return;Populate();
            foreach(Transform child in _cells) {child.gameObject.SetActive(false);UnityEngine.Object.Destroy(child.gameObject);}
            UpdateTitle();
            if(_garmentHeader!=null) {
                _garmentHeader.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_garmentHeader.gameObject);
                _garmentHeader=null;
            }
            if (_wornRegion.Count > 0) {
                float cardW = 136f;
                float headerW = Mathf.Max(720f, 16f + cardW * _wornRegion.Count);
                _garmentHeader=Rect(_root,"CurrentGarmentHeader",0,-146,headerW,134);
                _garmentHeader.gameObject.AddComponent<Image>().color=new Color(.06f,.08f,.12f,.92f);
                for (int wi=0; wi<_wornRegion.Count; wi++) {
                    var garment=_wornRegion[wi];
                    var card=Rect(_garmentHeader,"WornGarment",8f+wi*cardW,5f,cardW-8f,124f);
                    card.gameObject.AddComponent<Image>().color=new Color(.16f,.22f,.28f,.96f);
                    var image=Rect(card,"CurrentGarment",4,4,cardW-16f,76).gameObject.AddComponent<RawImage>();image.raycastTarget=false;
                    garment.GetThumbnail(delegate(Texture2D texture){if(image!=null)image.texture=texture;});
                    if (!garment.active) {
                        var clearRt=Rect(card,"ClearWornCard",4,4,cardW-16f,76);
                        var clearImage=clearRt.gameObject.AddComponent<Image>();clearImage.color=new Color(0f,0f,0f,0f);
                        var clearButton=clearRt.gameObject.AddComponent<UnityEngine.UI.Button>();clearButton.targetGraphic=clearImage;
                        clearButton.onClick.AddListener(delegate{ClearWornCard(garment);});
                    }
                    Label(card,garment.displayName,2,82,cardW-12f,22,14);
                    Button(card,garment.active?"脱下":"穿上",5,84,cardW-18f,delegate{ToggleGarment(garment);});
                }
            }
            for(int n=0;n<8;n++) {
                int index=_page*8+n;if(index>=_items.Count) break;
                var item=_items[index];var rt=Rect(_cells,"Clothing",(n%4)*176,(n/4)*172,166,164);
                var image=rt.gameObject.AddComponent<Image>();image.color=new Color(.18f,.22f,.28f,.95f);
                var slot=rt.gameObject.AddComponent<RegionClothingSlot>();slot.Item=item;slot.Index=index;
                var preview=Rect(rt,"Thumbnail",3,3,160,122).gameObject.AddComponent<RawImage>();preview.raycastTarget=true;
                item.GetThumbnail(delegate(Texture2D tex){if(preview!=null) preview.texture=tex;});
                Label(rt,(Manual[_region].Contains(item.uid)?"★ ":"")+item.displayName,2,126,162,36,16);
                if(!_register) {
                    var cross=Rect(rt,"RemoveCandidate",136,2,28,28);
                    var red=cross.gameObject.AddComponent<Image>();red.color=new Color(.70f,.06f,.06f,1f);
                    var remove=cross.gameObject.AddComponent<UnityEngine.UI.Button>();remove.targetGraphic=red;remove.onClick.AddListener(delegate{Delete(item.uid);});
                    Label(cross,"×",0,0,28,28,23);
                }
            }
            if(_items.Count==0) Label(_cells,_register?"当前角色没有穿戴服装":"此区域暂无候选，可注册已穿服装",0,80,690,80,24);
        }
        private static void Place()
        {
            var sc=SuperController.singleton;Transform head=sc.centerCameraTarget!=null?sc.centerCameraTarget.transform:sc.lookCamera.transform;
            Vector3 right=head.right;right.y=0;right.Normalize();
            _root.position=_anchor+right*.70f;
            _root.rotation=Quaternion.LookRotation(_root.position-head.position);
        }
    }
}
