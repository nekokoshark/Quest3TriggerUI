using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SimpleJSON;
namespace Quest3TriggerUI
{
    internal sealed class ExpressionBrowserPanel : IDisposable
    {
        private const int PageSize=24;
        private readonly MonoBehaviour host;
        private readonly SceneQuickActions quick;
        private GameObject root, rows;
        private bool dockHidden;
        private GameObject dockShow;
        private readonly Dictionary<GameObject,bool> dockChildren=new Dictionary<GameObject,bool>();
        internal void RevealDock(){dockHidden=false;}
        internal void BindEditorDock(RectTransform list,bool visible)
        {
            if(!visible||list==null)
            {
                if(root!=null&&root.activeSelf){if(Quest3TriggerUIPlugin.Instance!=null)Quest3TriggerUIPlugin.Instance.CloseFollowingTextKeyboard();root.SetActive(false);}
                return;
            }
            if(root==null)Build();
            if(catalog.Count==0){LoadCatalog();ReadOrder();preferences.Load(PreferencesPath);Refresh();}
            sessionScene=SuperController.singleton.loadJson;if(lifetime!=null)lifetime.enabled=true;
            root.SetActive(true);
            var rect=(RectTransform)root.transform;root.transform.rotation=list.rotation;root.transform.localScale=list.lossyScale;
            var edge=list.TransformPoint(new Vector3(list.rect.xMin,list.rect.yMax,0));
            root.transform.position=edge-list.right*((rect.rect.width*0.5f+24f)*list.lossyScale.x)-list.up*(rect.rect.height*0.5f*list.lossyScale.y);
            // Match the preset/clothing dock facing convention exactly.
            Camera viewer=SuperController.singleton.lookCamera;
            if(viewer!=null)
            {
                Vector3 away=root.transform.position-viewer.transform.position;
                root.transform.position-=away.normalized*(12f*list.lossyScale.x);
                if(away.sqrMagnitude>0.0001f && Vector3.Cross(away,list.up).sqrMagnitude>0.0001f)
                    root.transform.rotation=Quaternion.LookRotation(away,list.up);
            }
            ApplyDockHidden();
        }
        private void ApplyDockHidden()
        {
            if(root==null||dockShow==null)return;
            if(dockHidden&&dockChildren.Count==0)
            {
                for(int i=0;i<root.transform.childCount;i++){var go=root.transform.GetChild(i).gameObject;if(go==dockShow)continue;dockChildren[go]=go.activeSelf;go.SetActive(false);}
            }
            else if(!dockHidden&&dockChildren.Count>0)
            {
                foreach(var pair in dockChildren)if(pair.Key!=null)pair.Key.SetActive(pair.Value);dockChildren.Clear();
            }
            root.GetComponent<Image>().enabled=!dockHidden;dockShow.SetActive(dockHidden);
        }
        private ExpressionPanelLifetime lifetime;
        private JSONNode sessionScene;
        internal bool NeedsPlaybackLifetime { get { return busy || slot != null || timeline != null || shield.Active || quick.ExpressionActive; } }
        internal void ObservePlaybackLifetime()
        {
            var sc=SuperController.singleton;
            if(sc==null || sc.isLoading ||
                (sessionScene!=null && !object.ReferenceEquals(sessionScene,sc.loadJson)) ||
                (target==null && (slot!=null || baseline.Count>0 || morphFlags.Count>0)))
                ResetPlaybackForSceneChange();
        }
        internal void ResetPlaybackForSceneChange()
        {
            mergeMode=false;paused=false;selectedKey=null;
            try { StopPreview(true);quick.StopPanelExpressions(); }
            finally { shield.Dispose();Hide();sessionScene=null;if(lifetime!=null)lifetime.enabled=false; }
        }
        private Text status;
        private InputField search;
        private readonly List<Entry> catalog=new List<Entry>();
        private readonly List<Entry> filtered=new List<Entry>();
        private readonly List<string> sources=new List<string>();
        private string filter="全部";
        private int page;
        private bool seven, busy;
        private bool favoritesOnly, mergeMode, paused;
        private bool previewLoaded;
        private string playStage="idle";
        private static object Member(object value,string name)
        {
            if(value==null)return null;
            var t=value.GetType();var p=t.GetProperty(name,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance);
            if(p!=null)return p.GetValue(value,null);
            var f=t.GetField(name,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance);
            return f==null?null:f.GetValue(value);
        }
        private static bool PlayerReady(JSONStorable value)
        {
            var stop=value==null?null:value.GetAction("Stop");
            return value!=null && Member(value,"animation")!=null && Member(value,"serializer")!=null && stop!=null && stop.actionCallback!=null;
        }
        private void PlaybackFailure(Exception e)
        {
            Exception detail=e;while(detail.InnerException!=null)detail=detail.InnerException;
            if(Quest3TriggerUIPlugin.Log!=null)Quest3TriggerUIPlugin.Log.LogError("[expression-play] stage="+playStage+" type="+detail.GetType().Name+" message="+detail.Message+"\n"+detail.StackTrace);
            Message("表情播放失败（"+playStage+"）："+detail.Message);
        }
        private string selectedKey;
        private Text shieldLabel,pauseLabel,mergeLabel,favoritesLabel;
        private InputField renameInput;
        private string renameKey;
        private RectTransform mergeRect;
        private readonly ExpressionEntryPreferences preferences=new ExpressionEntryPreferences();
        private Plane panelDragPlane;
        private Vector3 panelDragOffset;
        private string PreferencesPath {get{return Path.Combine(BepInEx.Paths.ConfigPath,"Quest3TriggerUI.expression-library-user.json");}}
        private void SavePreferences(){preferences.Save(PreferencesPath);}
        private string Alias(string key,string fallback){string value;return preferences.Aliases.TryGetValue(key,out value)?value:fallback;}
        private void StopAllOwn(){StopPreview();quick.StopPanelExpressions();}
        private void UpdateStateLabels()
        {
            if(shieldLabel!=null)shieldLabel.text="屏蔽原场景："+(shield.Active?"开":"关");
            if(pauseLabel!=null)pauseLabel.text=paused?"恢复自有表情":"暂停/交还场景";
            if(favoritesLabel!=null)favoritesLabel.text=favoritesOnly?"返回全部条目":"收藏页面";
            if(mergeLabel!=null)mergeLabel.text="合并框（"+preferences.Merge.Count+"） — 拖入条目";
        }
        private void PauseResume()
        {
            if(!paused){StopAllOwn();shield.Dispose();paused=true;Message("已暂停：原场景接管表情");UpdateStateLabels();return;}
            if(string.IsNullOrEmpty(selectedKey)&&!mergeMode){Message("请先选择表情");return;}
            try{shield.Enable(SceneQuickActions.FindClosestFemale(),DirectoryPath);}
            catch(Exception e){Message("恢复未执行："+e.Message);UpdateStateLabels();return;}
            paused=false;
            if(mergeMode)BeginMerge();else StartKey(selectedKey);
            UpdateStateLabels();
        }
        private void BeginRename(string key)
        {
            renameKey=key;renameInput.gameObject.SetActive(true);renameInput.text=Alias(key,key);
            renameInput.ActivateInputField();VrTextInputBridge.SelectFollowing(renameInput);
        }
        private void CommitRename()
        {
            if(renameKey==null)return;
            string value=renameInput.text.Trim();if(value.Length>120)value=value.Substring(0,120);if(value.Length>0)preferences.Aliases[renameKey]=value;
            renameKey=null;renameInput.gameObject.SetActive(false);
            if(Quest3TriggerUIPlugin.Instance!=null)Quest3TriggerUIPlugin.Instance.CloseFollowingTextKeyboard();
            SavePreferences();Refresh();
        }
        private void DeleteEntry(string key)
        {
            preferences.Deleted.Add(key);preferences.Favorites.Remove(key);preferences.Merge.Remove(key);
            if(selectedKey==key){StopAllOwn();selectedKey=null;mergeMode=false;}
            SavePreferences();Refresh();
        }
        private void FavoriteEntry(string key)
        {
            if(!preferences.Favorites.Remove(key))preferences.Favorites.Add(key);SavePreferences();Refresh();
        }
        internal void BeginPanelDrag(bool right)
        {
            dragKey="__panel";clickAfter=float.MaxValue;
            RectTransform cursor=VrPointerPresentation.CurrentCursor(right);
            panelDragPlane=new Plane(root.transform.forward,root.transform.position);
            panelDragOffset=root.transform.position-(cursor==null?root.transform.position:cursor.position);
        }
        private string dragKey;
        private float clickAfter, hoverAfter;
        private int hoverDirection;
        private RectTransform prevRect,nextRect;
        private readonly List<string> order=new List<string>();
        private string OrderPath {get{return Path.Combine(BepInEx.Paths.ConfigPath,"Quest3TriggerUI.expression-order.txt");}}
        private void ReadOrder(){order.Clear();if(File.Exists(OrderPath))order.AddRange(File.ReadAllLines(OrderPath));}
        private void SaveOrder(){try{File.WriteAllLines(OrderPath,order.ToArray());}catch(Exception e){Message("排序保存失败："+e.Message);}}
        private int Rank(string key){int n=order.IndexOf(key);return n<0?int.MaxValue:n;}
        internal void BeginItemDrag(string key){dragKey=key;clickAfter=float.MaxValue;hoverDirection=0;}
        internal void EndItemDrag(){if(dragKey!=null && dragKey!="__panel")SaveOrder();dragKey=null;clickAfter=Time.unscaledTime+0.3f;}
        private static bool Inside(RectTransform rect, Vector3 world){return rect!=null&&rect.rect.Contains((Vector2)rect.InverseTransformPoint(world));}
        internal void TickItemDrag(bool right)
        {
            if(dragKey==null||!Visible)return;
            RectTransform cursor=VrPointerPresentation.CurrentCursor(right);if(cursor==null)return;
            if(dragKey=="__panel")
            {
                Transform hand=VrPointerPresentation.MotionController(SuperController.singleton,right);
                if(hand==null)return;float distance;
                Ray ray=new Ray(hand.position,cursor.position-hand.position);
                if(panelDragPlane.Raycast(ray,out distance)&&distance>0)root.transform.position=ray.GetPoint(distance)+panelDragOffset;
                return;
            }
            if(Inside(mergeRect,cursor.position))
            {
                if(dragKey=="s:0"){Message("中性/待机没有动画，不加入合并框");return;}
                if(preferences.Merge.Add(dragKey)){SavePreferences();UpdateStateLabels();}
                return;
            }
            int dir=Inside(prevRect,cursor.position)?-1:Inside(nextRect,cursor.position)?1:0;
            if(dir!=0){
                if(hoverDirection!=dir){hoverDirection=dir;hoverAfter=Time.unscaledTime+0.55f;return;}
                if(Time.unscaledTime<hoverAfter)return;hoverAfter=Time.unscaledTime+0.8f;
                if(seven)return;
                int next=Math.Max(0,Math.Min(Math.Max(0,(filtered.Count-1)/PageSize),page+dir));
                if(next==page)return;
                string anchor=EntryKey(filtered[next*PageSize]);MoveOrderBefore(dragKey,anchor);page=next;Refresh();return;
            }
            hoverDirection=0;
            GameObject hit=VrPointerPresentation.CurrentLookTarget(right);var tag=hit==null?null:hit.GetComponentInParent<ExpressionEntryTag>();
            if(tag!=null&&tag.Owner==this&&tag.Key!=dragKey && MoveOrderBefore(dragKey,tag.Key))Refresh();
        }
        private bool MoveOrderBefore(string key,string anchor)
        {
            // Explicit master order includes every entry, so unfiltered/next-page
            // items shift instead of disappearing during filtered reorder.
            foreach(var e in catalog)if(!order.Contains("u:"+e.Id))order.Add("u:"+e.Id);
            for(int i=0;i<SevenSeasonExpressionLibrary.All.Length;i++)if(!order.Contains("s:"+i))order.Add("s:"+i);
            return ExpressionEntryOrder.MoveBefore(order,key,anchor);
        }
        private void EntryButton(string key,string label,float x,float y,Action action)
        {
            GameObject go=ButtonAt(rows.transform,label,x,y,278,68,delegate{if(dragKey==null&&Time.unscaledTime>=clickAfter)action();});
            Text title=go.GetComponentInChildren<Text>();title.text=Alias(key,label);((RectTransform)title.transform).sizeDelta=new Vector2(262,40);title.fontSize=15;
            var tag=go.AddComponent<ExpressionEntryTag>();tag.Key=key;tag.Owner=this;
            ButtonAt(go.transform,"改名",4,42,82,24,delegate{BeginRename(key);});
            ButtonAt(go.transform,preferences.Favorites.Contains(key)?"★取消":"☆收藏",90,42,96,24,delegate{FavoriteEntry(key);});
            ButtonAt(go.transform,"删除",190,42,82,24,delegate{DeleteEntry(key);});
        }
        private Coroutine routine;
        private Atom target;
        private MVRPluginManager manager;
        private MVRPlugin slot;
        private JSONStorable timeline;
        private readonly Dictionary<string,Vector2> limits=new Dictionary<string,Vector2>();
        private readonly Dictionary<string,float> baseline=new Dictionary<string,float>();
        private readonly Dictionary<DAZMorph,bool> morphFlags=new Dictionary<DAZMorph,bool>();
        private readonly ExpressionSceneShield shield=new ExpressionSceneShield();
        private string DirectoryPath {get{return Path.Combine(Path.Combine(BepInEx.Paths.PluginPath,"Quest3TriggerUI"),"UjExpressionLibrary");}}
        internal bool Visible {get{return root!=null&&root.activeSelf;}}
        private sealed class Entry {internal string Id,Source,Name;}
        internal ExpressionBrowserPanel(MonoBehaviour h,SceneQuickActions q){host=h;quick=q;}
        internal void Toggle(){UiAssistHudLink.OpenExpressionDock(this);}
        private void LoadCatalog()
        {
            try{
                var index=JSON.Parse(File.ReadAllText(Path.Combine(DirectoryPath,"index.json")));
                foreach(JSONNode n in index["items"].AsArray){var e=new Entry{Id=n["id"],Source=n["source"],Name=n["name"]};catalog.Add(e);if(!sources.Contains(e.Source))sources.Add(e.Source);}
                sources.Sort(StringComparer.Ordinal);
            }catch(Exception e){Message("读取UJVAM库失败："+e.Message);}
        }
        private void Build()
        {
            root=new GameObject("Q3 Expression Browser",typeof(RectTransform));root.transform.localScale=Vector3.one*0.00065f;
            var rt=(RectTransform)root.transform;rt.sizeDelta=new Vector2(1060,860);
            var canvas=root.AddComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=SuperController.singleton.lookCamera;root.AddComponent<GraphicRaycaster>();SuperController.singleton.AddCanvas(canvas);
            lifetime=host.gameObject.AddComponent<ExpressionPanelLifetime>();lifetime.Owner=this;
            root.AddComponent<Image>().color=new Color(0.035f,0.055f,0.075f,0.30f);
            TextAt(root.transform,"表情收藏",8,4,160,38,20);
            ButtonAt(root.transform,"UJVAM",8,50,160,34,delegate{StopPreview();quick.StopPanelExpressions();seven=false;page=0;Refresh();});
            ButtonAt(root.transform,"SevenSeason",8,92,160,34,delegate{StopPreview();seven=true;page=0;Refresh();});
            pauseLabel=ButtonAt(root.transform,"暂停/恢复",8,220,160,34,PauseResume).GetComponentInChildren<Text>();
            shieldLabel=ButtonAt(root.transform,"屏蔽原场景：关",8,262,160,40,delegate{
                if(shield.Active){shield.Dispose();Message("屏蔽已关闭");}
                else try{shield.Enable(SceneQuickActions.FindClosestFemale(),DirectoryPath);Message("屏蔽已开启：已替换 "+shield.PatchedCalls+" 个面部采样调用，身体/声音继续。");}catch(Exception e){Message(e.Message);}
                UpdateStateLabels();}).GetComponentInChildren<Text>();
            ButtonAt(root.transform,"来源切换",8,178,160,34,delegate{int i=sources.IndexOf(filter);filter=i+1>=sources.Count?"全部":sources[i+1];page=0;Refresh();});
            var input=Box(root.transform,"搜索",184,12,860,40,new Color(0.7f,0.7f,0.7f,0.7f));search=input.AddComponent<InputField>();
            search.textComponent=TextAt(input.transform,"",8,3,844,34,18);search.textComponent.color=Color.black;
            var placeholder=TextAt(input.transform,"搜索场景/动画名/编号",8,3,844,34,16);placeholder.color=new Color(0.25f,0.25f,0.25f);search.placeholder=placeholder;
            search.onValueChanged.AddListener(delegate(string value){page=0;Refresh();});
            status=TextAt(root.transform,"请选择一项；不会加载原场景或身体动作",184,57,860,48,16);
            rows=Box(root.transform,"候选",184,110,870,650,new Color(0,0,0,0));rows.GetComponent<Image>().raycastTarget=false;
            prevRect=(RectTransform)ButtonAt(root.transform,"上一页",8,314,160,34,delegate{page=Math.Max(0,page-1);Refresh();}).transform;
            nextRect=(RectTransform)ButtonAt(root.transform,"下一页",8,356,160,34,delegate{page++;Refresh();}).transform;
            favoritesLabel=ButtonAt(root.transform,"收藏页面",8,134,160,34,delegate{favoritesOnly=!favoritesOnly;page=0;Refresh();}).GetComponentInChildren<Text>();
            mergeRect=(RectTransform)ButtonAt(root.transform,"合并框：拖入多个条目",8,406,160,112,BeginMerge).transform;mergeLabel=mergeRect.GetComponentInChildren<Text>();
            ButtonAt(root.transform,"清空合并",8,530,160,34,delegate{if(mergeMode){StopAllOwn();mergeMode=false;}preferences.Merge.Clear();SavePreferences();UpdateStateLabels();});
            ButtonAt(root.transform,"恢复删除项",8,572,160,34,delegate{preferences.Deleted.Clear();SavePreferences();Refresh();});
            var renameBox=Box(root.transform,"改名输入",184,790,670,40,new Color(0.85f,0.85f,0.85f,0.8f));renameInput=renameBox.AddComponent<InputField>();renameInput.textComponent=TextAt(renameBox.transform,"",5,0,660,40,18);renameInput.textComponent.color=Color.black;renameBox.SetActive(false);
            ButtonAt(root.transform,"确认改名",864,790,182,40,CommitRename);
            ButtonAt(root.transform,"服 装",8,640,160,34,delegate{UiAssistHudLink.SelectExpressionDockMode(0);});
            ButtonAt(root.transform,"预 设",8,682,160,34,delegate{UiAssistHudLink.SelectExpressionDockMode(1);});
            ButtonAt(root.transform,"场 景",8,724,160,34,delegate{UiAssistHudLink.SelectExpressionDockMode(2);});
            ButtonAt(root.transform,"隐 藏",8,786,160,34,Hide);
            dockShow=ButtonAt(root.transform,"显 示",1022,10,30,150,delegate{RevealDock();ApplyDockHidden();});
            dockShow.SetActive(false);root.SetActive(false);
        }
        private void Refresh()
        {
            if(rows==null)return;
            for(int i=rows.transform.childCount-1;i>=0;i--){var child=rows.transform.GetChild(i).gameObject;child.SetActive(false);UnityEngine.Object.Destroy(child);}
            filtered.Clear();UpdateStateLabels();
            if(favoritesOnly)
            {
                foreach(var e in catalog)if(preferences.Favorites.Contains("u:"+e.Id)&&!preferences.Deleted.Contains("u:"+e.Id))filtered.Add(e);
                var defs=SevenSeasonExpressionLibrary.All;
                for(int i=0;i<defs.Length;i++)if(preferences.Favorites.Contains("s:"+i)&&!preferences.Deleted.Contains("s:"+i))filtered.Add(new Entry{Id="s:"+i,Source="SevenSeason",Name=defs[i].Label});
                filtered.Sort(delegate(Entry a,Entry b){return Rank(EntryKey(a)).CompareTo(Rank(EntryKey(b)));});
                page=Math.Max(0,Math.Min(page,Math.Max(0,(filtered.Count-1)/PageSize)));
                for(int i=page*PageSize;i<Math.Min(filtered.Count,(page+1)*PageSize);i++){Entry e=filtered[i];int pos=i-page*PageSize;string key=EntryKey(e);EntryButton(key,e.Source+" / "+e.Name,(pos%3)*290,(pos/3)*78,delegate{StartKey(key);});}
                Message("收藏页："+filtered.Count+" 项 | 第 "+(page+1)+" 页");return;
            }
            if(seven){
                var defs=SevenSeasonExpressionLibrary.All;
                var ids=new List<int>();for(int i=0;i<defs.Length;i++)if(!preferences.Deleted.Contains("s:"+i)&&(!favoritesOnly||preferences.Favorites.Contains("s:"+i)))ids.Add(i);
                ids.Sort(delegate(int a,int b){int r=Rank("s:"+a).CompareTo(Rank("s:"+b));return r!=0?r:a.CompareTo(b);});
                for(int i=0;i<ids.Count;i++){int id=ids[i];EntryButton("s:"+id,defs[id].Label,(i%3)*290,(i/3)*78,delegate{StartKey("s:"+id);});}
                Message("SevenSeason：沿用原表情功能；屏蔽="+(shield.Active?"开":"关"));return;
            }
            string query=search==null?"":search.text;
            foreach(var e in catalog)if(!preferences.Deleted.Contains("u:"+e.Id)&&(!favoritesOnly||preferences.Favorites.Contains("u:"+e.Id)) && (filter=="全部"||filter==e.Source) && (string.IsNullOrEmpty(query)||(e.Source+" "+e.Name+" "+Alias("u:"+e.Id,"")+" "+e.Id).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0))filtered.Add(e);
            filtered.Sort(delegate(Entry a,Entry b){int r=Rank("u:"+a.Id).CompareTo(Rank("u:"+b.Id));return r!=0?r:int.Parse(a.Id).CompareTo(int.Parse(b.Id));});
            page=Math.Max(0,Math.Min(page,Math.Max(0,(filtered.Count-1)/PageSize)));
            for(int i=page*PageSize;i<Math.Min(filtered.Count,(page+1)*PageSize);i++){
                Entry e=filtered[i];int local=i-page*PageSize;
                EntryButton("u:"+e.Id,"#"+e.Id+" "+e.Source+"\n"+e.Name,(local%3)*290,(local/3)*78,delegate{StartPreview(e);});
            }
            Message("UJVAM "+filtered.Count+" 项 | 来源："+filter+" | 第 "+(page+1)+"/"+Math.Max(1,(filtered.Count+PageSize-1)/PageSize)+" 页 | 屏蔽="+(shield.Active?"开":"关"));
        }
        private void Message(string text){if(status!=null)status.text=text;}
        private void StartPreview(Entry e){StartKey("u:"+e.Id);}
        private static string EntryKey(Entry e){return e.Id.StartsWith("s:")?e.Id:"u:"+e.Id;}
        private Entry EntryForKey(string key)
        {
            if(key.StartsWith("s:")){int id=int.Parse(key.Substring(2));var def=SevenSeasonExpressionLibrary.All[id];return new Entry{Id=key,Source="SevenSeason",Name=def.Label};}
            string uid=key.Substring(2);return new Entry{Id=uid,Source="UJVAM",Name=Alias(key,"#"+uid)};
        }
        private void StartKey(string key)
        {
            if(busy && !mergeMode)return;StopAllOwn();mergeMode=false;paused=false;selectedKey=key;
            if(key=="s:0"){shield.Dispose();Message("已恢复中性并交还原场景");UpdateStateLabels();return;}
            busy=true;
            routine=host.StartCoroutine(Guarded(EntryForKey(key)));UpdateStateLabels();
        }
        private void BeginMerge()
        {
            if(preferences.Merge.Count==0){Message("先将条目拖入合并框");return;}
            StopAllOwn();mergeMode=true;paused=false;busy=true;
            routine=host.StartCoroutine(GuardedMerge());UpdateStateLabels();
        }
        private IEnumerator GuardedMerge()
        {
            IEnumerator work=MergeLoop();
            try{while(true){bool more;try{more=work.MoveNext();}catch(Exception e){PlaybackFailure(e);mergeMode=false;StopPreview(true);yield break;}if(!more)yield break;yield return work.Current;}}
            finally{var d=work as IDisposable;if(d!=null)d.Dispose();busy=false;routine=null;}
        }
        private IEnumerator MergeLoop()
        {
            while(mergeMode && preferences.Merge.Count>0)
            {
                var keys=new List<string>(preferences.Merge);selectedKey=keys[UnityEngine.Random.Range(0,keys.Count)];
                IEnumerator play=Play(EntryForKey(selectedKey));while(play.MoveNext())yield return play.Current;
                object animation=timeline.GetType().GetProperty("animation").GetValue(timeline,null);
                var isPlaying=animation.GetType().GetProperty("isPlaying");
                yield return null;
                while(mergeMode && timeline!=null && object.Equals(isPlaying.GetValue(animation,null),true))yield return null;
            }
        }
        private IEnumerator Guarded(Entry e)
        {
            IEnumerator work=Play(e);
            try{while(true){bool more;try{more=work.MoveNext();}catch(Exception ex){StopPreview(true);PlaybackFailure(ex);yield break;}if(!more)yield break;yield return work.Current;}}
            finally{var disposable=work as IDisposable;if(disposable!=null)disposable.Dispose();busy=false;routine=null;}
        }
        private IEnumerator Play(Entry e)
        {
            yield return null;playStage="查找目标女性";
            // Reuse belongs to this one actor/scene only. A different actor
            // releases the old slot, rather than accumulating per-actor pools.
            Atom selectedActor=mergeMode && target!=null ? target : SceneQuickActions.FindClosestFemale();
            if(selectedActor==null)throw new InvalidOperationException("视线前方没有女性角色");
            if(target!=selectedActor)ReleasePreviewChannel();
            target=selectedActor;
            if(slot!=null && (manager==null || target.GetStorableByID("PluginManager")!=manager ||
                (timeline!=null && target.GetStorableByID(timeline.storeId)!=timeline)))
                ReleasePreviewChannel();
            target=selectedActor;
            playStage="读取动画数据";
            JSONClass config=e.Id.StartsWith("s:") ? JSON.Parse(SevenSeasonExpressionLibrary.All[int.Parse(e.Id.Substring(2))].TimelineJson).AsObject : JSON.Parse(File.ReadAllText(Path.Combine(DirectoryPath,e.Id+".json"))).AsObject;
            if(config==null){Message("中性表情无需动画播放");yield break;}
            JSONArray clips=config["Animation"]["Clips"].AsArray;
            if(clips==null||clips.Count==0)throw new InvalidOperationException("动画没有剪辑");
            // SevenSeason may have several authored variants; the chosen one
            // becomes an isolated, face-only single cycle for merging.
            if(e.Id.StartsWith("s:")){JSONNode chosen=clips[UnityEngine.Random.Range(0,clips.Count)];clips=new JSONArray();clips.Add(chosen);config["Animation"]["Clips"]=clips;}
            var clip=clips[0];clip["AnimationName"]="Preview";clip["AnimationSegment"]="Q3UJVAM";clip["AnimationLayer"]="Main";
            clip["Loop"]=mergeMode?"0":"1";clip["Controllers"]=new JSONArray();clip["Triggers"]=new JSONArray();clip.AsObject.Remove("NextAnimationName");clip.AsObject.Remove("NextAnimationTime");clip.AsObject.Remove("Pose");
            config["Animation"]["Master"]="0";config["Animation"]["SyncWithPeers"]="0";config["Animation"]["Speed"]="1";
            DAZCharacterSelector geometry=target.GetStorableByID("geometry") as DAZCharacterSelector;
            if(geometry==null)throw new InvalidOperationException("角色无geometry");
            playStage="绑定面部曲线";
            JSONArray tracks=config["Animation"]["Clips"][0]["FloatParams"].AsArray;
            if(tracks==null)throw new InvalidOperationException("动画没有面部曲线");
            var available=new JSONArray();int missing=0;
            foreach(JSONNode tr in tracks.Childs){
                string name=tr["Name"];JSONStorableFloat value=geometry.GetFloatJSONParam(name);
                if(value==null && geometry.morphsControlUI!=null){
                    string display=name.StartsWith("morph: ")?name.Substring(7):name;
                    DAZMorph morph=geometry.morphsControlUI.GetMorphByDisplayName(display);
                    if(morph!=null){if(!morphFlags.ContainsKey(morph))morphFlags[morph]=morph.animatable;morph.animatable=true;value=geometry.GetFloatJSONParam(name);}
                }
                if(value==null){missing++;continue;}if(!baseline.ContainsKey(name)){baseline[name]=value.val;limits[name]=new Vector2(value.min,value.max);}available.Add(tr);
            }
            if(available.Count==0)throw new InvalidOperationException("目标角色缺少此动画的表情morph依赖");
            config["Animation"]["Clips"][0]["FloatParams"]=available;
            manager=target.GetStorableByID("PluginManager") as MVRPluginManager;if(manager==null)throw new InvalidOperationException("缺少PluginManager");
            if(slot==null)
            {
                slot=manager.CreatePlugin();
                if(slot==null)throw new InvalidOperationException("表情专用插件槽创建失败");
                slot.pluginURLJSON.val=SevenSeasonExpressionLibrary.TimelineUrl;
                LogPreviewChannel("created");
            }
            else LogPreviewChannel("reused");
            playStage="等待Timeline初始化";
            string id=slot.uid+"_VamTimeline.AtomPlugin";float until=Time.unscaledTime+12f;
            while(target!=null && Time.unscaledTime<until){timeline=target.GetStorableByID(id);if(PlayerReady(timeline))break;yield return null;}
            if(target==null||!PlayerReady(timeline))throw new InvalidOperationException("表情Timeline初始化未完成");
            yield return null;
            if(target==null||!PlayerReady(timeline))throw new InvalidOperationException("目标或Timeline在初始化后退出");
            var serializer=timeline.GetType().Assembly.GetType("VamTimeline.AtomAnimationSerializer");
            var version=serializer==null?null:serializer.GetField("SerializeVersion",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);
            if(version==null)throw new InvalidOperationException("无法核对当前Timeline序列化版本");
            object serializedVersion=version.IsLiteral?version.GetRawConstantValue():version.GetValue(null);
            if(serializedVersion==null)throw new InvalidOperationException("Timeline版本值为空");
            config["Animation"]["SerializeVersion"]=serializedVersion.ToString();
            // A newly initialized Timeline has no loaded current clip yet.
            // Only a previously successful preview can be stopped/reset safely.
            if(previewLoaded){var stop=timeline.GetAction("Stop");if(stop!=null&&stop.actionCallback!=null)stop.actionCallback();}
            timeline.enabled=false;
            foreach(var old in baseline){var f=geometry.GetFloatJSONParam(old.Key);if(f!=null)f.val=old.Value;}
            config["id"]=timeline.storeId;config["pluginLabel"]="Q3UJVAM #"+e.Id;
            playStage="加载专用表情Timeline";
            timeline.RestoreFromJSON(config,true,true,null,true);SceneQuickActions.LoadExpressionTimeline(timeline,config);
            yield return null;
            timeline.enabled=true;var on=timeline.GetBoolJSONParam("enabled");if(on!=null)on.val=true;
            playStage="启动表情";
            var play=timeline.GetAction("Play Segment Q3UJVAM");
            if(play==null||play.actionCallback==null)throw new InvalidOperationException("Timeline尚未注册此表情播放动作");
            previewLoaded=true;play.actionCallback();Message("预览 #"+e.Id+" "+e.Source+" / "+e.Name+" | 可用轨道 "+available.Count+"，缺失 "+missing);
        }
        private void StopPreview(bool releaseChannel=false)
        {
            if(routine!=null){host.StopCoroutine(routine);routine=null;}busy=false;
            try
            {
                if(timeline!=null)
                {
                    try { var stop=timeline.GetAction("Stop");if(previewLoaded&&stop!=null&&stop.actionCallback!=null)stop.actionCallback(); }
                    finally { timeline.enabled=false;var enabled=timeline.GetBoolJSONParam("enabled");if(enabled!=null)enabled.val=false; }
                }
                if(target!=null){var g=target.GetStorableByID("geometry");foreach(var pair in baseline){var f=g==null?null:g.GetFloatJSONParam(pair.Key);if(f!=null){Vector2 old;if(limits.TryGetValue(pair.Key,out old)){f.min=old.x;f.max=old.y;}f.val=pair.Value;}}}
            }
            catch(Exception e){releaseChannel=true;Message("停止表情："+e.Message);}
            finally
            {
                foreach(var pair in morphFlags)if(pair.Key!=null)pair.Key.animatable=pair.Value;
                baseline.Clear();limits.Clear();morphFlags.Clear();
                // Keep one disabled native channel and at most its last loaded
                // face clip. Next Load replaces that data; no source assignment
                // or CreatePlugin occurs for normal same-actor previews.
                if(releaseChannel)ReleasePreviewChannel();
            }
        }
        private int previewCreates,previewReuses,previewReleases;
        private void LogPreviewChannel(string operation)
        {
            if(operation=="created")previewCreates++;
            else if(operation=="reused")previewReuses++;
            else previewReleases++;
            if(Quest3TriggerUIPlugin.Log!=null)
                Quest3TriggerUIPlugin.Log.LogInfo("[expression-channel] "+operation+" creates="+previewCreates+" reuses="+previewReuses+" releases="+previewReleases+" actor="+(target==null?"none":target.uid)+" slot="+(slot==null?"none":slot.uid)+" compileCountsNotMeasured=True");
        }
        private void ReleasePreviewChannel()
        {
            try
            {
                if(manager!=null&&slot!=null)
                {
                    var remove=manager.GetType().GetMethod("RemovePlugin",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public,null,new Type[]{typeof(MVRPlugin)},null);
                    if(remove==null)throw new MissingMethodException("MVRPluginManager.RemovePlugin");
                    remove.Invoke(manager,new object[]{slot});
                    LogPreviewChannel("released");
                }
            }
            catch(Exception e){Message("释放表情通道："+e.Message);}
            finally{timeline=null;slot=null;manager=null;target=null;previewLoaded=false;}
        }
        internal void Hide()
        {
            EndItemDrag();
            if(root!=null && Quest3TriggerUIPlugin.Instance!=null)Quest3TriggerUIPlugin.Instance.CloseFollowingTextKeyboard();
            dockHidden=true;ApplyDockHidden();
            if(lifetime!=null)lifetime.enabled=NeedsPlaybackLifetime;
        }
        public void Dispose()
        {
            try { ResetPlaybackForSceneChange(); }
            finally
            {
                if(lifetime!=null){lifetime.Owner=null;UnityEngine.Object.Destroy(lifetime);lifetime=null;}
                if(root!=null){var c=root.GetComponent<Canvas>();if(SuperController.singleton!=null)SuperController.singleton.RemoveCanvas(c);UnityEngine.Object.Destroy(root);}
                UiAssistHudLink.DetachExpressionDock(this);
                root=null;rows=null;status=null;search=null;order.Clear();preferences.Clear();dockChildren.Clear();dockShow=null;sessionScene=null;
            }
        }
        private static GameObject Box(Transform parent,string name,float x,float y,float width,float height,Color color)
        {
            var go=new GameObject(name,typeof(RectTransform));var r=(RectTransform)go.transform;r.SetParent(parent,false);r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(width,height);go.AddComponent<Image>().color=color;return go;
        }
        private static Text TextAt(Transform p,string name,float x,float y,float w,float h,int size)
        {
            var go=new GameObject("Text",typeof(RectTransform));var r=(RectTransform)go.transform;r.SetParent(p,false);r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);var t=go.AddComponent<Text>();t.font=Resources.GetBuiltinResource<Font>("Arial.ttf");t.fontSize=size;t.text=name;t.color=Color.white;t.alignment=TextAnchor.MiddleLeft;t.raycastTarget=false;return t;
        }
        private static GameObject ButtonAt(Transform p,string label,float x,float y,float w,float h,Action action)
        {
            var go=Box(p,label,x,y,w,h,new Color(0.08f,0.21f,0.27f,0.55f));var b=go.AddComponent<Button>();b.targetGraphic=go.GetComponent<Image>();b.onClick.AddListener(delegate{action();});var t=TextAt(go.transform,label,8,0,w-16,h,17);t.alignment=TextAnchor.MiddleCenter;return go;
        }
    }
    internal sealed class ExpressionPanelDrag : MonoBehaviour
    {
        internal ExpressionBrowserPanel Owner;
        internal void Configure(RectTransform panel,ExpressionBrowserPanel owner){Owner=owner;}
    }
}
