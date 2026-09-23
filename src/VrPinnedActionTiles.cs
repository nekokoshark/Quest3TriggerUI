using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Independent, head-relative quick-action tiles.  They deliberately do
    // not live below the radial canvas so closing/rebuilding the radial menu
    // cannot destroy a user's layout.
    internal sealed class VrPinnedActionTiles
    {
        private const float CanvasSize = 1600f;
        private const float TileWidth = 270f;
        private const float TileHeight = 92f;
        private const float TileScale = 0.00058f;
        private readonly Func<string, QuickActionDefinition> _find;
        private readonly float _opacity;
        private readonly List<Tile> _tiles = new List<Tile>();
        private Canvas _canvas;
        private Font _font;
        private Tile _hovered;
        private float _hoverExpires;
        private static readonly Color HoverColor = new Color(.96f,.62f,.10f,1f);
        private Tile _gripTile;
        private bool _dragging;
        private TriggerStateMachine _dragHeld;
        private Vector3 _gripStart;
        private Vector3 _tileStart;
        private readonly string _layoutPath;
        private bool _restored;
        private bool _disposed;
        private float _retryAt;

        internal VrPinnedActionTiles(Func<string, QuickActionDefinition> find, float opacity)
        {
            _find = find;
            _opacity = Mathf.Clamp(opacity, 0.2f, 1f);
            _layoutPath = Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.pinned-tiles.txt");
            // Awake precedes VaM's camera/UI setup. Do not create Unity UI here.
        }
        internal bool CapturingGrip { get { return _gripTile != null; } }
        // True while the pointer hovers any tile (or its removal button) —
        // the radial summon must yield so index long-press can drag.
        internal bool PointerOverTile { get; private set; }
        internal void Refresh() { for (int i = _tiles.Count - 1; i >= 0; i--) Rebind(_tiles[i]); }
        internal void Dispose() { if (_disposed) return; Save(); _disposed = true; ClearSurfaces(); }
        private void ClearSurfaces()
        {
            _hovered = null; _gripTile = null; _dragging = false;
            for (int i=0;i<_tiles.Count;i++) DestroyTile(_tiles[i]);
            _tiles.Clear();
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }
        private bool Ready()
        {
            return !_disposed && SuperController.singleton != null &&
                SuperController.singleton.centerCameraTarget != null;
        }
        internal bool Pin(QuickActionDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Id)) return false;
            if (!Ready() || !TryRestore()) return false;
            for (int i=0;i<_tiles.Count;i++) if (_tiles[i].Id == definition.Id) return false;
            EnsureCanvas();
            Tile tile = Create(definition, DefaultPosition(_tiles.Count));
            _tiles.Add(tile); Save(); return true;
        }
        internal void Tick(TriggerStateMachine index, TriggerStateMachine grip)
        {
            if (!Ready() || Time.unscaledTime < _retryAt) return;
            try
            {
                if (!TryRestore()) return;
                TickSurfaces(index, grip);
            }
            catch (Exception e)
            {
                // A failed tile must not abort the radial input handler later
                // in Update. Rebuild from the last committed layout, not debris.
                _restored = false;
                ClearSurfaces();
                _retryAt = Time.unscaledTime + 2f;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("Pinned tiles rebuild deferred: " + e);
            }
        }
        private void TickSurfaces(TriggerStateMachine index, TriggerStateMachine grip)
        {
            if (_canvas == null) return;
            Anchor();
            VrPointerPresentation.EnsureVisible();
            GameObject target = VrPointerPresentation.CurrentLookTarget();
            PinnedTileTarget hit = target == null ? null : target.GetComponentInParent<PinnedTileTarget>();
            PinnedTileRemoveTarget remove = target == null ? null : target.GetComponentInParent<PinnedTileRemoveTarget>();
            PinnedTileHaloTarget halo = target == null ? null : target.GetComponentInParent<PinnedTileHaloTarget>();
            Tile next = hit == null ? (remove == null ? null : remove.Tile) : hit.Tile;
            if (next != null) _hoverExpires = Time.unscaledTime + .35f;
            // Keep the removal control reachable across the short gap. The
            // transparent halo is enabled only while this tile is hovered.
            if (next != null || Time.unscaledTime >= _hoverExpires)
            {
                if (!ReferenceEquals(next, _hovered))
                {
                    if (_hovered != null) SetHover(_hovered, false);
                    _hovered = next;
                    if (next != null) VrHaptics.Hover();
                }
            }
            PointerOverTile = next != null;
            if (_hovered != null) SetHover(_hovered, !_dragging);
            if (index != null && index.TapFrame == Time.frameCount)
            {
                if (remove != null) { Remove(remove.Tile); return; }
                if (hit != null && halo == null && !_dragging) Activate(hit.Tile);
            }
            Vector3 handLocal;
            bool haveHand = TryHandLocal(out handLocal);
            // Hold-to-drag accepts either trigger: index long-press over a
            // tile drags it (the radial summon is suppressed on hover), and
            // the original grip path stays for grab-style dragging.
            if (index != null && index.LongPressStartFrame == Time.frameCount && hit != null && halo == null && remove == null)
            { _gripTile = hit.Tile; _dragHeld = index; _dragging = true; _gripStart = handLocal; _tileStart = _gripTile.Root.anchoredPosition3D; _gripTile.Remove.gameObject.SetActive(false); }
            if (grip != null && grip.PressedDownFrame == Time.frameCount && hit != null && halo == null && remove == null) { _gripTile = hit.Tile; _dragHeld = grip; _gripStart = handLocal; _tileStart = _gripTile.Root.anchoredPosition3D; }
            if (_gripTile != null && _dragHeld == grip && grip.LongPressStartFrame == Time.frameCount) { _dragging = true; _gripTile.Remove.gameObject.SetActive(false); }
            if (_dragging && _gripTile != null && _dragHeld != null && _dragHeld.LongPressActive && haveHand)
            {
                Vector3 delta = handLocal - _gripStart;
                Vector3 position = _tileStart + delta * 3f;
                Vector3 movement = position - _gripTile.Root.anchoredPosition3D;
                _gripTile.Root.anchoredPosition3D = position;
                for (int i=0;i<_gripTile.Children.Count;i++)
                    _gripTile.Children[i].Root.anchoredPosition3D += movement;
            }
            if (_gripTile != null && _dragHeld != null && _dragHeld.ReleasedFrame == Time.frameCount) { bool changed = _dragging; _gripTile = null; _dragHeld = null; _dragging = false; if (changed) Save(); }
        }
        // Right controller position in this canvas's local units. VaM's
        // rightHand transform exists under both OVR and OpenVR; OVRInput
        // only reports under the Oculus runtime so it cannot drive the drag.
        private bool TryHandLocal(out Vector3 local)
        {
            local = Vector3.zero;
            if (_canvas == null) return false;
            if (SuperController.singleton != null && SuperController.singleton.rightHand != null)
            {
                local = _canvas.transform.InverseTransformPoint(
                    SuperController.singleton.rightHand.transform.position);
                return true;
            }
            Transform anchor = null;
            if (SuperController.singleton != null && SuperController.singleton.OVRRig != null)
            {
                OVRCameraRig rig = SuperController.singleton.OVRRig.GetComponent<OVRCameraRig>();
                if (rig != null) anchor = rig.trackingSpace;
            }
            if (anchor != null)
            {
                Vector3 world = anchor.TransformPoint(
                    OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));
                local = _canvas.transform.InverseTransformPoint(world);
                return true;
            }
            return false;
        }
        private void SetHover(Tile tile, bool hovered)
        {
            tile.Remove.gameObject.SetActive(hovered);
            tile.Halo.SetActive(hovered);
            tile.Image.color = hovered ? HoverColor : (tile.Definition == null
                ? new Color(.12f,.12f,.12f,.75f) : ColorFor(tile.Definition));
        }
        private void Activate(Tile tile)
        {
            if (tile == null || tile.Definition == null) return;
            VrHaptics.Press();
            if (tile.Definition.HasChildren) { ToggleChildren(tile); return; }
            if (tile.Definition.Action != null) tile.Definition.Action();
        }
        private void ToggleChildren(Tile tile)
        {
            bool show = tile.Children.Count == 0 || !tile.Children[0].Root.gameObject.activeSelf;
            for (int i=0;i<_tiles.Count;i++) if (!ReferenceEquals(_tiles[i],tile)) for(int c=0;c<_tiles[i].Children.Count;c++) _tiles[i].Children[c].Root.gameObject.SetActive(false);
            if (tile.Children.Count == 0)
                for (int i=0;i<tile.Definition.Children.Count;i++) { QuickActionDefinition child=tile.Definition.Children[i]; Tile t=Create(child, tile.Root.anchoredPosition3D+new Vector3(0f,-(i+1)*106f,0f)); t.IsChild=true; tile.Children.Add(t); }
            for(int i=0;i<tile.Children.Count;i++) tile.Children[i].Root.gameObject.SetActive(show);
        }
        private void Remove(Tile tile)
        {
            if (tile == null) return;

            _tiles.Remove(tile); DestroyTile(tile); _hovered=null; Save();
        }
        private static void Unregister(Canvas surface)
        {
            if(surface==null || SuperController.singleton==null)return;
            try { SuperController.singleton.RemoveCanvas(surface); }
            catch(Exception e) { if(Quest3TriggerUIPlugin.Log!=null)Quest3TriggerUIPlugin.Log.LogWarning("Pinned canvas unregister failed: "+e.Message); }
        }
        private void DestroyTile(Tile tile) { if(tile==null)return; for(int i=0;i<tile.Children.Count;i++) DestroyTile(tile.Children[i]); Unregister(tile.Surface); if(tile.Root!=null) UnityEngine.Object.Destroy(tile.Root.gameObject); }
        private void Rebind(Tile tile)
        {
            tile.Definition = _find(tile.Id);
            bool available = tile.Definition != null;
            tile.Button.interactable = available;
            tile.Label.text = available ? tile.Definition.Label : tile.Id + "\n当前场景不可用";
            tile.Image.color = available ? ColorFor(tile.Definition) : new Color(.12f,.12f,.12f,.75f);
            for(int i=0;i<tile.Children.Count;i++) Rebind(tile.Children[i]);
        }
        private Tile Create(QuickActionDefinition definition, Vector3 position)
        {
            EnsureCanvas(); GameObject go=new GameObject("Pinned " + definition.Id,typeof(RectTransform));
            go.SetActive(false);
            Canvas surface = null;
            try
            {
            go.transform.SetParent(_canvas.transform,false);
            RectTransform rect=go.GetComponent<RectTransform>(); rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f); rect.sizeDelta=new Vector2(TileWidth,TileHeight); rect.anchoredPosition3D=position;
            Image image=go.AddComponent<Image>(); image.color=ColorFor(definition); Button button=go.AddComponent<Button>(); button.targetGraphic=image; button.transition=Selectable.Transition.None;
            Tile tile=new Tile { Id=definition.Id, Definition=definition, Root=rect, Image=image, Button=button };
            PinnedTileTarget target=go.AddComponent<PinnedTileTarget>(); target.Tile=tile;
            // VaM registers Canvas planes for VR hit testing. Each independent
            // depth must have its own plane; a canvas at the eye with offset
            // children is not a valid interactive surface.
            surface=go.AddComponent<Canvas>();surface.renderMode=RenderMode.WorldSpace;
            surface.overrideSorting=true;surface.sortingOrder=0;
            go.AddComponent<GraphicRaycaster>();tile.Surface=surface;
            CanvasGroup tileGroup=go.AddComponent<CanvasGroup>();tileGroup.alpha=_opacity;
            tile.Label=Text(go.transform,definition.Label,27);
            tile.Label.transform.SetAsLastSibling();
            GameObject halo = new GameObject("Tile hover margin",typeof(RectTransform));
            halo.transform.SetParent(go.transform,false);
            halo.transform.SetAsFirstSibling();
            RectTransform hr=halo.GetComponent<RectTransform>();
            hr.anchorMin=hr.anchorMax=hr.pivot=new Vector2(.5f,.5f);
            hr.anchoredPosition=Vector2.zero;
            hr.sizeDelta=new Vector2(TileWidth,TileHeight);
            AddHoverStrip(hr,new Vector2(-147,0),new Vector2(24,92));
            AddHoverStrip(hr,new Vector2(147,0),new Vector2(24,92));
            AddHoverStrip(hr,new Vector2(0,-58),new Vector2(318,24));
            AddHoverStrip(hr,new Vector2(0,82),new Vector2(318,72));
            halo.AddComponent<PinnedTileHaloTarget>(); tile.Halo=halo; halo.SetActive(false);
            GameObject remove=new GameObject("取消固定",typeof(RectTransform)); remove.transform.SetParent(go.transform,false); RectTransform rr=remove.GetComponent<RectTransform>(); rr.anchorMin=rr.anchorMax=new Vector2(.5f,1f);rr.pivot=new Vector2(.5f,0f);rr.anchoredPosition=new Vector2(0,5);rr.sizeDelta=new Vector2(112,32);Image ri=remove.AddComponent<Image>();ri.color=new Color(.62f,.12f,.12f,1);tile.Remove=rr; PinnedTileRemoveTarget rt=remove.AddComponent<PinnedTileRemoveTarget>();rt.Tile=tile;Text(remove.transform,"取消固定",16); remove.SetActive(false); SetLayer(go, LayerMask.NameToLayer("UI") < 0 ? 5 : LayerMask.NameToLayer("UI"));
            // Publish only complete surfaces, including label and removal target.
            SuperController.singleton.AddCanvas(surface);
            go.SetActive(true);
            return tile;
            }
            catch
            {
                Unregister(surface);
                UnityEngine.Object.Destroy(go);
                throw;
            }
        }
        private void EnsureCanvas()
        {
            if (_canvas!=null)return;
            _font=(Font)Resources.GetBuiltinResource(typeof(Font),"Arial.ttf");
            if(_font==null)throw new InvalidOperationException("Pinned tile font is not ready");
            GameObject go=new GameObject("Quest3 Pinned Action Tiles");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _canvas=go.AddComponent<Canvas>();_canvas.renderMode=RenderMode.WorldSpace;go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit=1;RectTransform r=go.GetComponent<RectTransform>();r.sizeDelta=new Vector2(CanvasSize,CanvasSize); SetLayer(go,LayerMask.NameToLayer("UI")<0?5:LayerMask.NameToLayer("UI"));Anchor();
        }
        private void Anchor(){if(_canvas==null||!Ready())return;Transform a=SuperController.singleton.centerCameraTarget.transform;_canvas.transform.position=a.position;_canvas.transform.rotation=a.rotation;_canvas.transform.localScale=Vector3.one*TileScale;}
        private Text Text(Transform p,string s,int size){GameObject go=new GameObject("Text",typeof(RectTransform));go.transform.SetParent(p,false);RectTransform r=go.GetComponent<RectTransform>();r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=new Vector2(6,4);r.offsetMax=new Vector2(-6,-4);Text t=go.AddComponent<Text>();t.font=_font;t.text=s;t.fontSize=size;t.alignment=TextAnchor.MiddleCenter;t.color=Color.white;t.resizeTextForBestFit=true;t.resizeTextMinSize=13;t.resizeTextMaxSize=size;t.raycastTarget=false;return t;}
        private static Color ColorFor(QuickActionDefinition d){return d.ActiveState!=null&&d.ActiveState()?new Color(.08f,.68f,.36f,1):new Color(.11f,.38f,.48f,1);}
        private static Vector3 DefaultPosition(int i){return new Vector3(((i%3+1)%3-1)*290f,-140f-((i/3)%3)*110f,1120f);}
        private static void AddHoverStrip(RectTransform parent,Vector2 position,Vector2 size)
        {
            GameObject strip=new GameObject("Hover strip",typeof(RectTransform));
            strip.transform.SetParent(parent,false);
            RectTransform rect=strip.GetComponent<RectTransform>();
            rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);
            rect.anchoredPosition=position;rect.sizeDelta=size;
            Image image=strip.AddComponent<Image>();image.color=Color.clear;image.raycastTarget=true;
        }
        private static void SetLayer(GameObject g,int l){g.layer=l;for(int i=0;i<g.transform.childCount;i++)SetLayer(g.transform.GetChild(i).gameObject,l);}
        private bool TryRestore()
        {
            if (_restored) return true;
            if (!Ready() || Time.unscaledTime < _retryAt) return false;
            string[] lines = File.Exists(_layoutPath) ? File.ReadAllLines(_layoutPath) : new string[0];
            for (int i=0;i<lines.Length;i++)
            {
                string[] x=lines[i].Split('|'); float a,b,c;
                if (x.Length!=4 || string.IsNullOrEmpty(x[0]) || !Coordinate(x[1],out a) ||
                    !Coordinate(x[2],out b) || !Coordinate(x[3],out c)) continue;
                bool duplicate=false;
                for(int j=0;j<_tiles.Count;j++) if(_tiles[j].Id==x[0]) duplicate=true;
                if(duplicate) continue;
                QuickActionDefinition d=_find(x[0]);
                // Dynamic scene actions may not exist yet. Keep a labelled,
                // removable placeholder and rebind on catalog revision.
                Tile t=Create(d ?? new QuickActionDefinition(x[0], x[0]+"\n当前场景不可用", null),new Vector3(a,b,c));
                _tiles.Add(t);
                Rebind(t);
            }
            _restored=true;
            if(Quest3TriggerUIPlugin.Log!=null) Quest3TriggerUIPlugin.Log.LogInfo("Pinned tile layout restored: "+_tiles.Count);
            return true;
        }
        private static bool Coordinate(string text, out float value)
        {
            bool parsed=float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value) ||
                float.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out value);
            return parsed && !float.IsNaN(value) && !float.IsInfinity(value);
        }
        private void Save(){if(!_restored || _disposed)return;try{List<string> lines=new List<string>();for(int i=0;i<_tiles.Count;i++){Vector3 p=_tiles[i].Root.anchoredPosition3D;lines.Add(_tiles[i].Id+"|"+p.x.ToString("R",CultureInfo.InvariantCulture)+"|"+p.y.ToString("R",CultureInfo.InvariantCulture)+"|"+p.z.ToString("R",CultureInfo.InvariantCulture));}File.WriteAllLines(_layoutPath,lines.ToArray());}catch(Exception e){if(Quest3TriggerUIPlugin.Log!=null)Quest3TriggerUIPlugin.Log.LogWarning("Pinned tile layout save failed: "+e.Message);}}
        internal sealed class Tile { internal Canvas Surface; internal string Id;internal QuickActionDefinition Definition;internal RectTransform Root,Remove;internal GameObject Halo;internal Image Image;internal Button Button;internal Text Label;internal bool IsChild;internal List<Tile> Children=new List<Tile>(); }
    }
    internal sealed class PinnedTileTarget:MonoBehaviour { internal VrPinnedActionTiles.Tile Tile; }
    internal sealed class PinnedTileRemoveTarget:MonoBehaviour { internal VrPinnedActionTiles.Tile Tile; }
    internal sealed class PinnedTileHaloTarget:MonoBehaviour { }
}


