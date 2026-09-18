using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed class AceFavBarTag : MonoBehaviour { }
    internal sealed class AceFavSlotTag : MonoBehaviour
    {
        internal string Uid;
        internal RawImage Thumb;
        internal RawImage Dim;
    }

    internal sealed class AceFavDragSource
    {
        internal bool FromBar;
        internal bool FromBan;
        internal bool FromLock;
        internal string Uid;
        internal string DisplayName;
        internal string CreatorName;
        internal DAZClothingItem Item;
        internal Texture2D Texture;
    }

    internal static partial class UiAssistHudLink
    {
        // Transparent two-column strip docked to the right edge of the ACE list.
        // Entries are DAZClothingItem uids, so the bar is character-independent.
        private const float FavCellW = 96f;
        private const float FavCellH = 112f;
        private const float FavSpacing = 6f;
        private const float FavPad = 8f;

        private static GameObject _favList;
        private static Canvas _favCanvas;
        private static RectTransform _favDock;
        private static RectTransform _favCells;
        private static Image _favBackground;
        private static Atom _favAtom;
        private static bool _favDirty = true;
        private static bool _favLoaded;
        private static bool _favPositionLogged;
        private static bool _favProbeLogged;
        // Independent log channel: bypasses Quest3TriggerUIPlugin.Log so a
        // broken static field in a stale hot-loaded assembly cannot mute
        // diagnostics.
        private static readonly BepInEx.Logging.ManualLogSource _diagLog =
            BepInEx.Logging.Logger.CreateLogSource("Q3FavDiag");
        private static RawImage _favGhost;
        private static GameObject _favGhostRoot;
        private static readonly List<string[]> _favorites = new List<string[]>();
        private static readonly Dictionary<string, Texture2D> _favThumbs =
            new Dictionary<string, Texture2D>();
        // Paging: the bar never grows past the ACE list height; overflow
        // entries move to additional pages navigated with the ◀ ▶ row.
        private const float FavNavH = 30f;
        private static int _favPage;
        private static int _favPages = 1;
        private static float _favListHeight;
        private static GameObject _favNav;
        private static Text _favPageText;

        private static string FavPath
        {
            get { return Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.clothing-favorites.txt"); }
        }

        // Per-favorite customization snapshot: the clothing item is itself a
        // JSONStorable, so GetJSON captures every user tweak (physics, color,
        // non-default preset params). Stored per asset path under config/.
        private static string FavStatePath(string uid)
        {
            string key = BanKey(uid);
            foreach (char c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            key = key.Replace('/', '_').Replace('\\', '_');
            return Path.Combine(Paths.ConfigPath,
                "Quest3TriggerUI.clothing-favstate." + key + ".json");
        }

        private static void CaptureFavoriteState(AceFavDragSource source)
        {
            try
            {
                DAZClothingItem item = source.Item;
                if (item == null) item = ResolveClothingItem(source.Uid, _favAtom);
                if (item == null) return;
                // User tweaks live on the item's child JSONStorables
                // (DAZClothingItemControl etc.): physics sim, color, and any
                // preset params. Snapshot each storable keyed by its storeId.
                JSONClass json = new JSONClass();
                foreach (JSONStorable storable in
                    item.GetComponentsInChildren<JSONStorable>(true))
                {
                    if (storable == null || string.IsNullOrEmpty(storable.storeId))
                        continue;
                    JSONClass sub = storable.GetJSON(true, true, true);
                    if (sub != null) json[storable.storeId] = sub;
                }
                if (json.Count == 0) return;
                File.WriteAllText(FavStatePath(source.Uid), json.ToString());
            }
            catch (Exception e) { Error(e); }
        }

        private static void ApplyFavoriteState(DAZClothingItem item)
        {
            try
            {
                string path = FavStatePath(item.uid);
                if (!File.Exists(path)) return;
                JSONClass json = JSONNode.Parse(File.ReadAllText(path)) as JSONClass;
                if (json == null) return;
                int applied = 0;
                foreach (JSONStorable storable in
                    item.GetComponentsInChildren<JSONStorable>(true))
                {
                    if (storable == null || string.IsNullOrEmpty(storable.storeId))
                        continue;
                    JSONClass sub = json[storable.storeId] as JSONClass;
                    if (sub == null) continue;
                    storable.RestoreFromJSON(sub, true, true, new JSONArray(), false);
                    applied++;
                }
                if (applied > 0)
                    Log("已恢复收藏状态：" + (item.displayName ?? item.uid) +
                        "（" + applied + " 组参数）");
            }
            catch (Exception e) { Error(e); }
        }

        private static void ClearFavoritesBar()
        {
            if (_favCanvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_favCanvas);
            if (_favGhostRoot != null) UnityEngine.Object.Destroy(_favGhostRoot);
            if (_favDock != null) UnityEngine.Object.Destroy(_favDock.gameObject);
            _favGhost = null;
            _favGhostRoot = null;
            _favDock = null;
            _favCells = null;
            _favBackground = null;
            _favCanvas = null;
            _favList = null;
            _favAtom = null;
            _favNav = null;
            _favPageText = null;
            _favPage = 0;
            _favPages = 1;
            _favPositionLogged = false;
        }

        private static void LoadFavorites()
        {
            if (_favLoaded) return;
            _favLoaded = true;
            try
            {
                if (!File.Exists(FavPath)) return;
                foreach (string line in File.ReadAllLines(FavPath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 1 && parts[0].Length > 0)
                        _favorites.Add(new string[] {
                            parts[0],
                            parts.Length > 1 ? parts[1] : "",
                            parts.Length > 2 ? parts[2] : "" });
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void SaveFavorites()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (string[] f in _favorites)
                    lines.Add(f[0] + "|" + f[1] + "|" + f[2]);
                File.WriteAllLines(FavPath, lines.ToArray());
            }
            catch (Exception e) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogWarning("Clothing favorites save failed: " + e.Message); }
        }

        private static void UpdateFavoritesBar(Snapshot state, GameObject list)
        {
            if (!_favProbeLogged)
            {
                _favProbeLogged = true;
                _diagLog.LogInfo("UpdateFavoritesBar probe: list=" +
                    (list == null ? "null" : list.name) +
                    " active=" + list.activeInHierarchy +
                    " favs=" + _favorites.Count +
                    " asmHash=" + typeof(UiAssistHudLink).Assembly.GetHashCode());
            }
            LoadFavorites();
            if (_favList != list || _favDock == null)
            {
                ClearFavoritesBar();
                _favList = list;
                _favAtom = state.Target;
                CreateFavoritesBar(list);
            }
            if (_favAtom != state.Target)
            {
                _favAtom = state.Target;
                _favDirty = true; // resolvable state differs per character
            }
            if (_favDirty)
            {
                _favDirty = false;
                RebuildFavoriteCells();
            }
        }

        private static void CreateFavoritesBar(GameObject list)
        {
            try
            {
                GameObject go = new GameObject("Quest3 ACE Favorites Bar", typeof(RectTransform));
                go.SetActive(false);
                go.AddComponent<AceFavBarTag>();
                go.layer = list.layer;
                _favDock = (RectTransform)go.transform;
                _favDock.sizeDelta = new Vector2(FavCellW * 2f + FavSpacing + FavPad * 2f, 64f);
                _favDock.pivot = new Vector2(0.5f, 0.5f);
                _favCanvas = go.AddComponent<Canvas>();
                _favCanvas.renderMode = RenderMode.WorldSpace;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas != null) _favCanvas.worldCamera = parentCanvas.worldCamera;
                go.AddComponent<GraphicRaycaster>();
                _favBackground = go.AddComponent<Image>();
                _favBackground.color = new Color(0f, 0f, 0f, 0.18f);
                _favBackground.raycastTarget = true;

                GameObject cellsGo = new GameObject("Cells", typeof(RectTransform));
                _favCells = (RectTransform)cellsGo.transform;
                _favCells.SetParent(_favDock, false);
                _favCells.anchorMin = new Vector2(0f, 1f);
                _favCells.anchorMax = new Vector2(1f, 1f);
                _favCells.pivot = new Vector2(0.5f, 1f);
                _favCells.anchoredPosition = new Vector2(0f, -FavPad);
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(FavCellW, FavCellH);
                grid.spacing = new Vector2(FavSpacing, FavSpacing);
                grid.padding = new RectOffset((int)FavPad, (int)FavPad, 0, (int)FavPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                ContentSizeFitter fitter = cellsGo.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

                _favListHeight = ((RectTransform)list.transform).rect.height;
                CreateFavNav();

                SuperController.singleton.AddCanvas(_favCanvas);
                _favDirty = true;
                Log("服装收藏栏已创建（" + _favorites.Count + " 项），等待定位。");
            }
            catch (Exception e) { ClearFavoritesBar(); Error(e); }
        }

        private static void TickFavoritesBar()
        {
            if (_favDock == null || _favList == null) return;
            SuperController sc = SuperController.singleton;
            bool visible = _favList.activeInHierarchy && sc != null &&
                sc.MainHUDVisible && !_presetBrowsing;
            if (_favDock.gameObject.activeSelf != visible)
                _favDock.gameObject.SetActive(visible);
            if (!visible) return;
            if (!_favPositionLogged)
            {
                _favPositionLogged = true;
                Log("收藏栏可见，位置已锁定于列表右缘。");
            }

            RectTransform list = (RectTransform)_favList.transform;
            list.GetWorldCorners(_dockCorners);
            _favDock.rotation = list.rotation;
            _favDock.localScale = list.lossyScale;
            // Right edge of the ACE list, offset outward by half our own width.
            Vector3 rightCenter = (_dockCorners[2] + _dockCorners[3]) * 0.5f;
            _favDock.position = rightCenter + list.right *
                (24f * list.lossyScale.x + _favDock.rect.width * _favDock.lossyScale.x * 0.5f);
            Camera viewer = sc.lookCamera;
            if (viewer != null)
            {
                Vector3 away = _favDock.position - viewer.transform.position;
                // Pull slightly toward the viewer so the strip renders on top of
                // whatever editor panel may share the list's right edge.
                _favDock.position -= away.normalized *
                    (12f * list.lossyScale.x);
                if (away.sqrMagnitude > 0.0001f &&
                    Vector3.Cross(away, list.up).sqrMagnitude > 0.0001f)
                    _favDock.rotation = Quaternion.LookRotation(away, list.up);
            }
        }

        // Bottom-center ◀ n/m ▶ row, only visible when entries exceed one page.
        private static void CreateFavNav()
        {
            GameObject nav = new GameObject("FavNav", typeof(RectTransform));
            RectTransform navRect = (RectTransform)nav.transform;
            navRect.SetParent(_favDock, false);
            navRect.anchorMin = new Vector2(0.5f, 0f);
            navRect.anchorMax = new Vector2(0.5f, 0f);
            navRect.pivot = new Vector2(0.5f, 0f);
            navRect.anchoredPosition = new Vector2(0f, 4f);
            navRect.sizeDelta = new Vector2(190f, FavNavH);
            nav.AddComponent<AceFavBarTag>();

            CreateFavNavButton(navRect, "◀", -72f, delegate { FavPageStep(-1); });
            CreateFavNavButton(navRect, "▶", 72f, delegate { FavPageStep(1); });

            GameObject textGo = new GameObject("Page", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(navRect, false);
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = new Vector2(90f, FavNavH);
            _favPageText = textGo.AddComponent<Text>();
            _favPageText.alignment = TextAnchor.MiddleCenter;
            _favPageText.fontSize = 20;
            _favPageText.color = new Color(1f, 1f, 1f, 0.8f);
            _favPageText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _favPageText.raycastTarget = false;
            _favPageText.text = "1/1";

            _favNav = nav;
            nav.SetActive(false);
        }

        private static void CreateFavNavButton(
            RectTransform parent, string label, float x,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("Nav " + label, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(46f, FavNavH - 4f);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(delegate { VrHaptics.Press(); action(); });
            Text text = new GameObject("Label", typeof(RectTransform)).AddComponent<Text>();
            RectTransform textRect = (RectTransform)text.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 20;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        private static void FavPageStep(int delta)
        {
            if (_favPages <= 1) return;
            int page = Mathf.Clamp(_favPage + delta, 0, _favPages - 1);
            if (page == _favPage) return;
            _favPage = page;
            _favDirty = true;
        }

        private static void RebuildFavoriteCells()
        {
            if (_favCells == null) return;
            foreach (Transform child in _favCells)
                UnityEngine.Object.Destroy(child.gameObject);
            int count = _favorites.Count;
            // Rows that fit the list height with and without the nav row; the
            // nav row is only shown when entries exceed the no-nav capacity.
            float listH = _favListHeight > 0f ? _favListHeight : 400f;
            int rowsNoNav = Mathf.Max(1, Mathf.FloorToInt(
                (listH - FavPad * 2f + FavSpacing) / (FavCellH + FavSpacing)));
            int rowsNav = Mathf.Max(1, Mathf.FloorToInt(
                (listH - FavPad * 2f - FavNavH - 4f + FavSpacing) /
                (FavCellH + FavSpacing)));
            bool nav = count > rowsNoNav * 2;
            int capacity = Mathf.Max(2, (nav ? rowsNav : rowsNoNav) * 2);
            _favPages = Mathf.Max(1, Mathf.CeilToInt(count / (float)capacity));
            _favPage = Mathf.Clamp(_favPage, 0, _favPages - 1);
            int start = _favPage * capacity;
            int end = Mathf.Min(count, start + capacity);
            for (int i = start; i < end; i++)
                CreateFavoriteSlot(_favorites[i][0]);
            if (count == 0)
                CreateEmptyHintSlot();
            if (_favNav != null && _favNav.activeSelf != nav)
                _favNav.SetActive(nav);
            if (nav && _favPageText != null)
                _favPageText.text = (_favPage + 1) + "/" + _favPages;
            int rowsShown = Mathf.Max(1,
                Mathf.CeilToInt(Mathf.Max(1, end - start) / 2f));
            float height = FavPad * 2f + rowsShown * (FavCellH + FavSpacing) +
                (nav ? FavNavH + 4f : 0f);
            _favDock.sizeDelta = new Vector2(_favDock.sizeDelta.x,
                Mathf.Min(listH, Mathf.Max(64f, height)));
        }

        // An empty bar must still be findable: one dashed-feel placeholder cell
        // marks the drop zone where the first clothing item lands.
        private static void CreateEmptyHintSlot()
        {
            GameObject slot = new GameObject("FavHint", typeof(RectTransform));
            slot.transform.SetParent(_favCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0.3f, 0.5f, 1f, 0.18f);
            bg.raycastTarget = true;
            slot.AddComponent<AceFavBarTag>();
            GameObject textGo = new GameObject("Hint", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(slot.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            Text hint = textGo.AddComponent<Text>();
            hint.text = "收藏";
            hint.alignment = TextAnchor.MiddleCenter;
            hint.fontSize = 22;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hint.raycastTarget = false;
        }

        private static void CreateFavoriteSlot(string uid)
        {
            GameObject slot = new GameObject("FavSlot", typeof(RectTransform));
            slot.transform.SetParent(_favCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);
            bg.raycastTarget = true;
            Button button = slot.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;

            GameObject thumbGo = new GameObject("Thumb", typeof(RectTransform));
            RectTransform thumbRect = (RectTransform)thumbGo.transform;
            thumbRect.SetParent(slot.transform, false);
            thumbRect.anchorMin = Vector2.zero;
            thumbRect.anchorMax = Vector2.one;
            thumbRect.offsetMin = new Vector2(3f, 3f);
            thumbRect.offsetMax = new Vector2(-3f, -3f);
            RawImage thumb = thumbGo.AddComponent<RawImage>();
            thumb.raycastTarget = false;

            GameObject dimGo = new GameObject("Dim", typeof(RectTransform));
            RectTransform dimRect = (RectTransform)dimGo.transform;
            dimRect.SetParent(slot.transform, false);
            dimRect.anchorMin = Vector2.zero;
            dimRect.anchorMax = Vector2.one;
            dimRect.offsetMin = Vector2.zero;
            dimRect.offsetMax = Vector2.zero;
            RawImage dim = dimGo.AddComponent<RawImage>();
            dim.color = new Color(0f, 0f, 0f, 0.65f);
            dim.raycastTarget = false;

            AceFavSlotTag tag = slot.AddComponent<AceFavSlotTag>();
            tag.Uid = uid;
            tag.Thumb = thumb;
            tag.Dim = dim;
            string captured = uid;
            button.onClick.AddListener(delegate { WearFavorite(captured); });
            RefreshSlotVisual(tag);
        }

        private static void RefreshSlotVisual(AceFavSlotTag tag)
        {
            Texture2D tex;
            _favThumbs.TryGetValue(tag.Uid, out tex);
            if (tex != null)
            {
                tag.Thumb.texture = tex;
                tag.Thumb.color = Color.white;
            }
            else
            {
                tag.Thumb.texture = null;
                tag.Thumb.color = new Color(0.25f, 0.25f, 0.3f, 1f);
                FetchFavoriteThumb(tag);
            }
            tag.Dim.gameObject.SetActive(ResolveClothingItem(tag.Uid, _favAtom) == null);
        }

        private static void FetchFavoriteThumb(AceFavSlotTag tag)
        {
            DAZClothingItem item = ResolveClothingItem(tag.Uid, _favAtom);
            if (item == null) item = ResolveClothingItemAnywhere(tag.Uid);
            if (item == null) return;
            AceFavSlotTag captured = tag;
            item.GetThumbnail(delegate(Texture2D tex)
            {
                if (tex == null) return;
                _favThumbs[tag.Uid] = tex;
                if (captured != null && captured.Thumb != null && captured.Uid == tag.Uid)
                {
                    captured.Thumb.texture = tex;
                    captured.Thumb.color = Color.white;
                }
            });
        }

        private static DAZClothingItem ResolveClothingItem(string uid, Atom atom)
        {
            if (atom == null || atom.gameObject == null) return null;
            DAZCharacterSelector selector = atom.GetComponentInChildren<DAZCharacterSelector>();
            if (selector == null || selector.clothingItems == null) return null;
            string path = BanKey(uid);
            foreach (DAZClothingItem item in selector.clothingItems)
                if (item != null &&
                    (item.uid == uid || BanKey(item.uid) == path)) return item;
            return null;
        }

        private static DAZClothingItem ResolveClothingItemAnywhere(string uid)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return null;
            foreach (Atom atom in sc.GetAtoms())
            {
                if (atom == null || atom.category != "People") continue;
                DAZClothingItem item = ResolveClothingItem(uid, atom);
                if (item != null) return item;
            }
            return null;
        }

        private static void WearFavorite(string uid)
        {
            VrHaptics.Press();
            try
            {
                SuperController sc = SuperController.singleton;
                Atom atom = _favAtom;
                if (sc == null || atom == null || atom.gameObject == null ||
                    sc.GetAtomByUid(atom.uid) != atom)
                {
                    Snapshot state = FindEditor(sc);
                    atom = state == null ? null : state.Target;
                    _favAtom = atom;
                }
                DAZClothingItem item = ResolveClothingItem(uid, atom);
                if (item == null)
                {
                    Log("该服装不适用于当前角色（性别不符或包未启用）。");
                    return;
                }
                if (IsClothingBanned(item.containingAtom, item.uid))
                {
                    Log("该服装已被禁用：" + (item.displayName ?? uid) +
                        "（先从禁用栏移除）");
                    return;
                }
                item.characterSelector.SetActiveClothingItem(item, !item.active);
                Log((item.active ? "已穿戴：" : "已脱下：") + item.displayName);
                if (item.active) ApplyFavoriteState(item);
            }
            catch (Exception e) { Error(e); }
        }

        // ---- drag plumbing ----

        internal static AceFavDragSource BeginFavoriteCandidate(GameObject target)
        {
            if (target == null) return null;
            try
            {
                AceFavSlotTag slot = target.GetComponentInParent<AceFavSlotTag>();
                if (slot != null)
                {
                    return new AceFavDragSource {
                        FromBar = true, Uid = slot.Uid,
                        Texture = slot.Thumb == null ? null : slot.Thumb.texture as Texture2D };
                }
                AceBanSlotTag banSlot = target.GetComponentInParent<AceBanSlotTag>();
                if (banSlot != null)
                {
                    return new AceFavDragSource {
                        FromBan = true, Uid = banSlot.Uid,
                        Texture = banSlot.Thumb == null ? null : banSlot.Thumb.texture as Texture2D };
                }
                AceLockSlotTag lockSlot = target.GetComponentInParent<AceLockSlotTag>();
                if (lockSlot != null)
                {
                    return new AceFavDragSource {
                        FromLock = true, Uid = lockSlot.Uid,
                        Texture = lockSlot.Thumb == null ? null : lockSlot.Thumb.texture as Texture2D };
                }
                // UIAssist's scroll-list rows are the authoritative source for
                // the clothing editor.  Resolve that row before looking at
                // generic VUI.MouseCallbacks: a callback can live on a shared
                // ancestor and expose one stale ClothingPanel, which makes
                // every row drag the same clothing item.
                if (_favList != null && target.transform.IsChildOf(_favList.transform))
                {
                    AceFavDragSource row = ResolveScrollListRow(target);
                    if (row != null) return row;
                }
                foreach (Component component in target.GetComponentsInParent<Component>())
                {
                    if (component.GetType().FullName != "VUI.MouseCallbacks") continue;
                    object widget = Read(component.GetType(), component, "Widget");
                    if (widget == null || widget.GetType().FullName != "AUI.ClothingUI.ClothingPanel") continue;
                    DAZClothingItem item = Read(widget.GetType(), widget, "ci_") as DAZClothingItem;
                    if (item == null) return null;
                    return new AceFavDragSource {
                        FromBar = false, Uid = item.uid, Item = item,
                        DisplayName = item.displayName, CreatorName = item.creatorName };
                }
            }
            catch (Exception e) { Error(e); }
            return null;
        }

        // One diagnostic line per failed trigger press: both hands' pointer
        // targets with full paths and leaf components. Tells us whether the
        // UI pointer is on the left hand and exactly what the row looks like.
        internal static void LogPressMiss(GameObject right, GameObject left)
        {
            try
            {
                _diagLog.LogInfo("press miss | right=" + DescribePressTarget(right) +
                    " | left=" + DescribePressTarget(left) +
                    " | favBar=" + (_favDock != null) +
                    " | asmHash=" + typeof(UiAssistHudLink).Assembly.GetHashCode());
            }
            catch { }
        }

        private static string DescribePressTarget(GameObject target)
        {
            if (target == null) return "null";
            System.Text.StringBuilder path = new System.Text.StringBuilder(target.name);
            Transform node = target.transform.parent;
            int depth = 0;
            while (node != null && depth < 8) { path.Insert(0, node.name + "/"); node = node.parent; depth++; }
            Component[] comps = target.GetComponents<Component>();
            System.Text.StringBuilder names = new System.Text.StringBuilder();
            foreach (Component c in comps)
            {
                if (c == null) continue;
                if (names.Length > 0) names.Append(',');
                names.Append(c.GetType().Name);
            }
            bool underList = _favList != null && target.transform.IsChildOf(_favList.transform);
            return path + "{ace=" + underList + ",comps=" + names + "}";
        }

        private static void MatchScrollRow(
            List<string> texts, DAZCharacterSelector selector,
            out DAZClothingItem best, out int bestScore)
        {
            best = null;
            bestScore = 0;
            if (texts == null || selector == null || selector.clothingItems == null)
                return;
            foreach (DAZClothingItem item in selector.clothingItems)
            {
                if (item == null || string.IsNullOrEmpty(item.displayName)) continue;
                // The row renders one combined label "name (creator)" plus
                // button captions like 移除 — displayName never appears alone.
                string combined = string.IsNullOrEmpty(item.creatorName)
                    ? item.displayName
                    : item.displayName + " (" + item.creatorName + ")";
                int score = 0;
                foreach (string t in texts)
                {
                    if (t == combined) score += 3;
                    else if (t == item.displayName) score += 2;
                    else if (!string.IsNullOrEmpty(item.creatorName) &&
                             t == item.creatorName) score += 1;
                }
                if (score > bestScore) { bestScore = score; best = item; }
            }
        }

        private static AceFavDragSource ResolveScrollListRow(GameObject target)
        {
            Atom atom = _favAtom;
            if (atom == null || atom.gameObject == null)
            {
                Snapshot state = FindEditor(SuperController.singleton);
                atom = state == null ? null : state.Target;
            }
            if (atom == null)
            {
                _diagLog.LogInfo("row resolve fail: no target atom (favAtom=" +
                    (_favAtom == null ? "null" : _favAtom.name) + ")");
                return null;
            }
            DAZCharacterSelector selector =
                atom.GetComponentInChildren<DAZCharacterSelector>();
            if (selector == null || selector.clothingItems == null)
            {
                _diagLog.LogInfo("row resolve fail: atom='" + atom.name +
                    "' selector=" + (selector == null ? "null" : "ok") +
                    " items=" + (selector == null || selector.clothingItems == null
                        ? "null" : selector.clothingItems.Length.ToString()));
                return null;
            }

            // Resolve the nearest ancestor whose subtree contains this row's
            // clothing name.  The old implementation selected the first item
            // whenever the whole visible list had <=12 labels, because it
            // climbed all the way to the shared content root and scored every
            // row together.  Matching at each ancestor stops at the actual row
            // and keeps the ray-selected thumbnail identity.
            Transform rowRoot = null;
            List<string> texts = null;
            Transform node = target.transform;
            while (node != null && node != _favList.transform)
            {
                List<string> probe = new List<string>();
                foreach (Text label in node.GetComponentsInChildren<Text>(false))
                    if (!string.IsNullOrEmpty(label.text)) probe.Add(label.text);
                DAZClothingItem probeItem;
                int probeScore;
                MatchScrollRow(probe, selector, out probeItem, out probeScore);
                if (probeItem != null && probeScore >= 2)
                {
                    rowRoot = node;
                    texts = probe;
                    break;
                }
                node = node.parent;
            }
            if (texts == null || texts.Count == 0)
            {
                // Maybe the row uses non-UGUI labels; report what text-bearing
                // components actually exist so the next patch can read them.
                Component[] leaf = target.GetComponents<Component>();
                System.Text.StringBuilder kinds = new System.Text.StringBuilder();
                foreach (Component c in leaf)
                { if (c != null) { if (kinds.Length > 0) kinds.Append(','); kinds.Append(c.GetType().FullName); } }
                Log("滚动列表行无 UGUI.Text，叶子组件：" + kinds);
                return null;
            }

            DAZClothingItem best;
            int bestScore;
            MatchScrollRow(texts, selector, out best, out bestScore);
            if (best == null || bestScore < 2)
            {
                System.Text.StringBuilder seen = new System.Text.StringBuilder();
                foreach (string t in texts) { if (seen.Length > 0) seen.Append(" | "); seen.Append(t); }
                Log("行文本未匹配任何服装（score=" + bestScore + "）：" + seen);
                return null;
            }
            Log("收藏候选识别（滚动列表）：" + best.displayName);
            return new AceFavDragSource {
                FromBar = false, Uid = best.uid, Item = best,
                DisplayName = best.displayName, CreatorName = best.creatorName };
        }

        internal static bool BeginFavoriteDrag(AceFavDragSource source)
        {
            if (source == null || _favDock == null) return false;
            VrHaptics.Press();
            try
            {
                // The ghost needs its own world-space canvas: a RawImage with
                // no Canvas ancestor never renders, so it cannot just be a
                // detached child of the bar.
                GameObject go = new GameObject("AceFavDragGhost", typeof(RectTransform));
                Canvas ghostCanvas = go.AddComponent<Canvas>();
                ghostCanvas.renderMode = RenderMode.WorldSpace;
                if (_favCanvas != null)
                {
                    ghostCanvas.worldCamera = _favCanvas.worldCamera;
                    go.transform.localScale = _favCanvas.transform.localScale;
                }
                else
                {
                    go.transform.localScale = new Vector3(0.0007f, 0.0007f, 0.0007f);
                }
                ((RectTransform)go.transform).sizeDelta = new Vector2(88f, 104f);

                Image back = go.AddComponent<Image>();
                back.color = new Color(0f, 0f, 0f, 0.55f);
                back.raycastTarget = false;

                GameObject imgGo = new GameObject("Thumb", typeof(RectTransform));
                imgGo.transform.SetParent(go.transform, false);
                RectTransform imgRect = (RectTransform)imgGo.transform;
                imgRect.anchorMin = Vector2.zero;
                imgRect.anchorMax = Vector2.one;
                imgRect.offsetMin = new Vector2(4f, 4f);
                imgRect.offsetMax = new Vector2(-4f, -4f);
                _favGhost = imgGo.AddComponent<RawImage>();
                _favGhost.raycastTarget = false;
                _favGhost.color = new Color(1f, 1f, 1f, 0.9f);
                _favGhostRoot = go;

                if (source.Texture != null) _favGhost.texture = source.Texture;
                else if (source.Item != null)
                {
                    source.Item.GetThumbnail(delegate(Texture2D tex)
                    {
                        if (_favGhost != null && tex != null) _favGhost.texture = tex;
                    });
                }
                TickFavoriteDrag();
                return true;
            }
            catch (Exception e) { Error(e); return false; }
        }

        internal static void TickFavoriteDrag()
        {
            if (_favGhost == null || _favGhostRoot == null) return;
            SuperController sc = SuperController.singleton;
            Camera viewer = sc == null ? null : sc.lookCamera;
            // Follow the laser cursor (its world position is the exact UI hit
            // point) of whichever pointer holds a look target; fall back to the
            // right hand when the laser is over empty space mid-drag.
            RectTransform cursor = null;
            if (VrPointerPresentation.CurrentLookTarget(true) != null)
                cursor = VrPointerPresentation.CurrentCursor(true);
            else if (VrPointerPresentation.CurrentLookTarget(false) != null)
                cursor = VrPointerPresentation.CurrentCursor(false);
            Transform root = _favGhostRoot.transform;
            if (cursor != null)
            {
                Vector3 away = viewer == null
                    ? -cursor.forward
                    : (cursor.position - viewer.transform.position).normalized;
                root.position = cursor.position + away * 0.03f;
            }
            else
            {
                Transform hand = VrPointerPresentation.MotionController(sc, true);
                if (hand != null)
                    root.position = hand.position + hand.forward * 0.18f;
            }
            if (viewer != null)
                root.rotation = Quaternion.LookRotation(
                    root.position - viewer.transform.position);
            if (_favBackground != null)
            {
                bool over = PointerOverFavoritesBar();
                _favBackground.color = over
                    ? new Color(0.2f, 0.5f, 1f, 0.25f)
                    : new Color(0f, 0f, 0f, 0.18f);
            }
            if (_banBackground != null)
            {
                bool overBan = PointerOverBanBar();
                _banBackground.color = overBan
                    ? new Color(0.9f, 0.25f, 0.2f, 0.3f)
                    : new Color(0.4f, 0f, 0f, 0.22f);
            }
            if (_lockBackground != null)
            {
                bool overLock = PointerOverLockBar();
                _lockBackground.color = overLock
                    ? new Color(0.3f, 0.85f, 0.35f, 0.3f)
                    : new Color(0f, 0.28f, 0.08f, 0.22f);
            }
        }

        private static bool PointerOverFavoritesBar()
        {
            GameObject target = VrPointerPresentation.CurrentLookTarget(true);
            if (target != null && target.GetComponentInParent<AceFavBarTag>() != null)
                return true;
            target = VrPointerPresentation.CurrentLookTarget(false);
            return target != null && target.GetComponentInParent<AceFavBarTag>() != null;
        }

        internal static void EndFavoriteDrag(AceFavDragSource source)
        {
            try
            {
                if (source == null) return;
                bool overFav = PointerOverFavoritesBar();
                bool overBan = PointerOverBanBar();
                bool overLock = PointerOverLockBar();
                bool acted = false;
                if (!source.FromBar && !source.FromBan && !source.FromLock)
                {
                    // Dragged out of the editor list.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overBan)
                        acted = AddBan(source);
                    else if (overFav)
                    {
                        AddFavorite(source);
                        acted = true;
                    }
                }
                else if (source.FromBar)
                {
                    // Favorites-bar slot: dropping back on its own bar keeps it,
                    // another bar moves it, anywhere else removes it.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overBan)
                        acted = AddBan(source);
                    else if (!overFav)
                        acted = RemoveFavorite(source.Uid, true);
                }
                else if (source.FromBan)
                {
                    // Ban-bar slot: same rules mirrored.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overFav)
                    {
                        AddFavorite(source);
                        acted = true;
                    }
                    else if (!overBan)
                        acted = RemoveBan(source.Uid);
                }
                else
                {
                    // Lock-bar slot: dropping on favorites adds without
                    // unlocking (lock and favorite coexist), dropping on ban
                    // moves it, anywhere else removes it.
                    if (overFav)
                    {
                        AddFavorite(source);
                        acted = true;
                    }
                    else if (overBan)
                    {
                        RemoveLock(source.Uid);
                        acted = AddBan(source);
                    }
                    else if (!overLock)
                        acted = RemoveLock(source.Uid);
                }
                if (acted) VrHaptics.Confirm();
            }
            catch (Exception e) { Error(e); }
            finally
            {
                if (_favGhostRoot != null) UnityEngine.Object.Destroy(_favGhostRoot);
                _favGhost = null;
                _favGhostRoot = null;
                if (_favBackground != null)
                    _favBackground.color = new Color(0f, 0f, 0f, 0.18f);
                if (_banBackground != null)
                    _banBackground.color = new Color(0.4f, 0f, 0f, 0.22f);
                if (_lockBackground != null)
                    _lockBackground.color = new Color(0f, 0.28f, 0.08f, 0.22f);
            }
        }

        private static void AddFavorite(AceFavDragSource source)
        {
            string key = BanKey(source.Uid);
            foreach (string[] f in _favorites)
                if (f[0] == source.Uid || BanKey(f[0]) == key) return;
            // Ban contradicts favorite (favorite click tries to wear and
            // would be vetoed); lock coexists — wearing is allowed.
            RemoveBan(source.Uid);
            _favorites.Add(new string[] {
                source.Uid,
                source.DisplayName ?? "",
                source.CreatorName ?? "" });
            if (source.Texture != null) _favThumbs[source.Uid] = source.Texture;
            else if (source.Item != null)
            {
                DAZClothingItem item = source.Item;
                item.GetThumbnail(delegate(Texture2D tex)
                { if (tex != null) _favThumbs[item.uid] = tex; });
            }
            CaptureFavoriteState(source);
            SaveFavorites();
            // Land on the page the new entry was appended to.
            _favPage = int.MaxValue;
            _favDirty = true;
            Log("已收藏服装：" + (source.DisplayName ?? source.Uid));
        }

        private static bool RemoveFavorite(string uid, bool log)
        {
            string key = BanKey(uid);
            for (int i = 0; i < _favorites.Count; i++)
            {
                if (_favorites[i][0] != uid && BanKey(_favorites[i][0]) != key) continue;
                if (log) Log("已移除收藏：" + _favorites[i][1]);
                _favorites.RemoveAt(i);
                _favThumbs.Remove(uid);
                try { string sp = FavStatePath(uid); if (File.Exists(sp)) File.Delete(sp); }
                catch { }
                SaveFavorites();
                _favDirty = true;
                return true;
            }
            return false;
        }
    }

}
