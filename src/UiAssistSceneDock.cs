using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Scene favorites dock sharing the merged LEFT-edge slot with the
    // clothing bar and the preset dock (dock mode 2). Entries are
    // [scenePath|displayName|packName]; a favorite is a reference, never
    // a file operation (renaming/deleting here never touches the scene).
    internal sealed class SdDockTag : MonoBehaviour { }
    internal sealed class SdSlotTag : MonoBehaviour
    {
        internal string Path;
        internal RawImage Thumb;
        internal Image Bg;
        internal Image NameHit;
        internal Text Label;
    }
    internal sealed class SdTagRowTag : MonoBehaviour
    {
        internal int GroupIndex = -1;
    }
    internal sealed class SdOverlayTag : MonoBehaviour { }

    internal static partial class UiAssistHudLink
    {
        private const float SdCellH = PdCellH;   // thumb + overlaid name strip
        private const float SdGridH = PdGridH;
        private const float SdStripW = FavTagStripW;
        private const int SdBuildPerTick = 12;

        private static RectTransform _sdDock, _sdCells;
        private static Canvas _sdCanvas;
        private static Image _sdBackground;
        private static GameObject _sdList;
        private static float _sdListHeight;
        private static bool _sdDirty = true, _sdLoaded, _sdTagsLoaded;
        private static bool _sdPositionLogged;

        private static readonly List<string[]> _sdFavorites =
            new List<string[]>();
        private static readonly List<string> _sdTagNames =
            new List<string>();
        private static readonly Dictionary<string, List<string[]>>
            _sdTagItems = new Dictionary<string, List<string[]>>();
        private static string _sdTagActiveName = "";
        private static int _sdTagIndex = -1;
        private static RectTransform _sdTagStrip;
        private static int _sdTagTop;
        private static string _sdTagEditing;
        private static InputField _sdTagInput;
        private static bool _sdUserHidden, _sdCollapsedApplied;
        private static GameObject _sdShowBar;
        private static Image _sdDeleteButton;
        private static Text _sdDeleteLabel;

        private static readonly Dictionary<string, Texture2D> _sdThumbs =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _sdThumbStamp =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<SdSlotTag> _sdVisibleCells =
            new List<SdSlotTag>();
        private static readonly HashSet<SdSlotTag> _sdKeptCells =
            new HashSet<SdSlotTag>();
        private static int _sdCellPosition;
        private static GameObject _sdHintCell;
        private static List<string[]> _sdBuildSlots;
        private static int _sdBuildIdx;
        private static readonly Queue<SdSlotTag> _sdThumbQueue =
            new Queue<SdSlotTag>();
        private static readonly HashSet<SdSlotTag> _sdThumbQueued =
            new HashSet<SdSlotTag>();

        // Called from UpdatePresetButtons while the ACE editor is alive.
        private static void UpdateSceneDock(GameObject list)
        {
            if (list == null) return;
            if (_sdThumbs.Count == 0)
            {
                var prev = GenBridge.Claim("sd.thumbs") as Dictionary<string, Texture2D> ??
                    GenBridge.Take("sd.thumbs") as Dictionary<string, Texture2D>;
                if (prev != null)
                    foreach (KeyValuePair<string, Texture2D> kv in prev)
                        if (kv.Value != null) _sdThumbs[kv.Key] = kv.Value;
                var prevStamp = GenBridge.Claim("sd.thumbStamp") as Dictionary<string, long> ??
                    GenBridge.Take("sd.thumbStamp") as Dictionary<string, long>;
                if (prevStamp != null)
                    foreach (KeyValuePair<string, long> kv in prevStamp)
                        _sdThumbStamp[kv.Key] = kv.Value;
            }
            GenBridge.Publish("sd.thumbs", _sdThumbs);
            GenBridge.Publish("sd.thumbStamp", _sdThumbStamp);
            LoadSdFavorites();
            LoadSdTags();
            if (_sdList == list && _sdDock != null)
            {
                if (!_sdDock.gameObject.activeSelf) _sdDirty = true;
                return;
            }
            ClearSceneDock();
            _sdList = list;
            _sdListHeight = ((RectTransform)list.transform).rect.height;
            CreateSceneDock(list);
        }

        private static void CreateSceneDock(GameObject list)
        {
            try
            {
                GameObject go = new GameObject("Quest3 Scene Dock",
                    typeof(RectTransform));
                go.SetActive(false);
                go.AddComponent<SdDockTag>();
                go.layer = list.layer;
                _sdDock = (RectTransform)go.transform;
                _sdDock.pivot = new Vector2(0.5f, 0.5f);
                _sdCanvas = go.AddComponent<Canvas>();
                _sdCanvas.renderMode = RenderMode.WorldSpace;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas != null)
                    _sdCanvas.worldCamera = parentCanvas.worldCamera;
                go.AddComponent<GraphicRaycaster>();
                _sdBackground = go.AddComponent<Image>();
                _sdBackground.color = new Color(0f, 0f, 0f, 0.32f);
                _sdBackground.raycastTarget = true;

                // Merged left-edge slot — same layout as the other two
                // docks: tag strip at the outer left, cells to its right
                // (the side facing the ACE list).
                GameObject viewGo = new GameObject("View",
                    typeof(RectTransform));
                _sdView = (RectTransform)viewGo.transform;
                _sdView.SetParent(_sdDock, false);
                _sdView.anchorMin = new Vector2(0f, 1f);
                _sdView.anchorMax = new Vector2(0f, 1f);
                _sdView.pivot = new Vector2(0f, 1f);
                _sdView.anchoredPosition = new Vector2(
                    FavTagGap + SdStripW, -FavPad);
                _sdView.sizeDelta = new Vector2(FavColW, SdGridH);
                viewGo.AddComponent<RectMask2D>();
                GameObject cellsGo = new GameObject("Cells",
                    typeof(RectTransform));
                _sdCells = (RectTransform)cellsGo.transform;
                _sdCells.SetParent(_sdView, false);
                _sdCells.anchorMin = new Vector2(0f, 1f);
                _sdCells.anchorMax = new Vector2(0f, 1f);
                _sdCells.pivot = new Vector2(0f, 1f);
                _sdCells.anchoredPosition = Vector2.zero;
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(FavCellW, SdCellH);
                grid.spacing = new Vector2(FavSpacing, FavSpacing);
                grid.padding = new RectOffset((int)FavPad, (int)FavPad,
                    0, (int)FavPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = FavColumns;
                cellsGo.AddComponent<SdDockTag>();

                CreateSdTagStrip();
                CreateDockScrollbar(_sdDock,
                    FavColW + FavTagGap + SdStripW, -FavPad, SdGridH,
                    out _sdScrollTrack, out _sdScrollThumb);
                CreateSdShowBar();
                _sdDock.sizeDelta = new Vector2(
                    FavColW + FavTagGap + SdStripW,
                    Mathf.Max(64f, Mathf.Max(SdGridH,
                        _sdTagStrip.sizeDelta.y + FavPad * 2f)));

                SuperController.singleton.AddCanvas(_sdCanvas);
                _sdDirty = true;
                Log("场景收藏栏已创建（" + _sdFavorites.Count + " 项，" +
                    _sdTagNames.Count + " 个标签），等待定位。");
            }
            catch (Exception e) { ClearSceneDock(); Error(e); }
        }

        private static void ClearSceneDock()
        {
            _sdVisibleCells.Clear();
            _sdKeptCells.Clear();
            _sdHintCell = null;
            _sdCellPosition = 0;
            _sdBuildSlots = null;
            _sdBuildIdx = 0;
            _sdThumbQueue.Clear();
            _sdThumbQueued.Clear();
            _sdScrollY = 0f;
            _sdContentH = 0f;
            _sdUserHidden = false;
            _sdCollapsedApplied = false;
            _sdShowBar = null;
            _sdDeleteButton = null;
            _sdDeleteLabel = null;
            _sdView = null;
            _sdScrollTrack = null;
            _sdScrollThumb = null;
            if (_sdCanvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_sdCanvas);
            if (_sdDock != null) UnityEngine.Object.Destroy(_sdDock.gameObject);
            _sdDock = null;
            _sdCells = null;
            _sdBackground = null;
            _sdCanvas = null;
            _sdList = null;
            _sdTagStrip = null;
            _sdTagInput = null;
            _sdTagEditing = null;
            _sdSaveBrowsing = false;
            _sdSaveOverlay = null;
            _sdSaveTag = null;
            _sdRenameOverlay = null;
            _sdRenameInput = null;
            _sdRenamePath = null;
            _sdPositionLogged = false;
        }

        private static void TickSceneDock()
        {
            if (_sdDock == null || _sdList == null) return;
            SuperController sc = SuperController.singleton;
            bool visible = _dockMode == 2 && _sdList.activeInHierarchy &&
                sc != null && sc.MainHUDVisible && !_presetBrowsing;
            if (_sdDock.gameObject.activeSelf != visible)
                _sdDock.gameObject.SetActive(visible);
            if (!visible) return;
            if (!_sdPositionLogged)
            {
                _sdPositionLogged = true;
                Log("场景收藏栏可见，位置已锁定于列表左缘。");
            }

            RectTransform list = (RectTransform)_sdList.transform;
            list.GetWorldCorners(_dockCorners);
            _sdDock.rotation = list.rotation;
            _sdDock.localScale = list.lossyScale;
            // Same left-edge slot as the other two dock modes.
            Vector3 leftCenter = (_dockCorners[0] + _dockCorners[1]) * 0.5f;
            _sdDock.position = leftCenter - list.right *
                (24f * list.lossyScale.x +
                 _sdDock.rect.width * _sdDock.lossyScale.x * 0.5f);
            Camera viewer = sc.lookCamera;
            if (viewer != null)
            {
                Vector3 away = _sdDock.position - viewer.transform.position;
                _sdDock.position -= away.normalized *
                    (12f * list.lossyScale.x);
                if (away.sqrMagnitude > 0.0001f &&
                    Vector3.Cross(away, list.up).sqrMagnitude > 0.0001f)
                    _sdDock.rotation = Quaternion.LookRotation(away, list.up);
            }
            ApplySdCollapsed();
            if (_sdUserHidden) return;
            if (_sdDirty || _sdBuildSlots != null)
            {
                bool restart = _sdDirty;
                if (restart) _sdDirty = false;
                PumpSdCells(SdBuildPerTick, restart);
            }
            TickDockScroll(2);
            TickSdThumbnails();
        }

        // Incremental fill — same pump model as the other two docks.
        private static bool PumpSdCells(int budget, bool restart)
        {
            if (restart) _sdBuildSlots = null;
            if (_sdCells == null) { _sdBuildSlots = null; return true; }
            if (_sdBuildSlots == null)
            {
                _sdKeptCells.Clear();
                _sdCellPosition = 0;
                _sdBuildSlots = new List<string[]>(VisibleSdFavorites);
                _sdBuildIdx = 0;
            }
            int count = _sdBuildSlots.Count;
            long tickStart = Mark();
            while (_sdBuildIdx < count && budget-- > 0)
            {
                CreateSdSlot(_sdBuildSlots[_sdBuildIdx++]);
                if (_sdBuildIdx < count && ElapsedMs(tickStart) >= 2) break;
            }
            if (_sdBuildIdx < count) return false;
            _sdBuildSlots = null;
            if (count == 0) CreateSdHintSlot();
            FinishSdCells();
            int rows = Mathf.CeilToInt(count / (float)FavColumns);
            _sdContentH = rows > 0
                ? rows * SdCellH + (rows - 1) * FavSpacing + FavPad
                : FavPad;
            _sdCells.sizeDelta = new Vector2(FavColW, _sdContentH);
            ApplyDockScroll(2, _sdScrollY);
            _sdDock.sizeDelta = new Vector2(_sdDock.sizeDelta.x,
                Mathf.Max(SdGridH, RequiredSdStripHeight()));
            return true;
        }

        private static void CreateSdHintSlot()
        {
            if (_sdHintCell != null) return;
            GameObject slot = new GameObject("SdHint", typeof(RectTransform));
            _sdHintCell = slot;
            slot.transform.SetParent(_sdCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0.3f, 0.5f, 1f, 0.18f);
            bg.raycastTarget = true;
            slot.AddComponent<SdDockTag>();
            Text hint = new GameObject("Hint", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform textRect = (RectTransform)hint.transform;
            textRect.SetParent(slot.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            hint.text = "拖入场景";
            hint.alignment = TextAnchor.MiddleCenter;
            hint.fontSize = 22;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hint.raycastTarget = false;
        }

        private static void CreateSdSlot(string[] entry)
        {
            string path = entry[0];
            if (ReuseSdCell(path)) return;
            GameObject cell = new GameObject("SdSlot", typeof(RectTransform));
            RectTransform cr = (RectTransform)cell.transform;
            cr.SetParent(_sdCells, false);
            Image bg = cell.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.16f, 0.2f, 1f);

            GameObject thumbGo = new GameObject("Thumb",
                typeof(RectTransform));
            RectTransform tr = (RectTransform)thumbGo.transform;
            tr.SetParent(cr, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            RawImage thumb = thumbGo.AddComponent<RawImage>();
            thumb.raycastTarget = false;

            GameObject nameBg = new GameObject("NameBg",
                typeof(RectTransform));
            RectTransform nbr = (RectTransform)nameBg.transform;
            nbr.SetParent(cr, false);
            nbr.anchorMin = new Vector2(0f, 0f);
            nbr.anchorMax = new Vector2(1f, 0f);
            nbr.offsetMin = Vector2.zero;
            nbr.offsetMax = new Vector2(0f, PdNameH);
            Image nbgi = nameBg.AddComponent<Image>();
            nbgi.color = new Color(0f, 0f, 0f, 0.55f);
            // Name strip is clickable at all times — it renames the
            // favorite's display alias, never the scene file.
            nbgi.raycastTarget = true;
            Text name = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform ntr = (RectTransform)name.transform;
            ntr.SetParent(nbr, false);
            ntr.anchorMin = Vector2.zero;
            ntr.anchorMax = Vector2.one;
            ntr.offsetMin = new Vector2(3f, 0f);
            ntr.offsetMax = new Vector2(-3f, 0f);
            name.text = SdDisplayName(entry);
            name.alignment = TextAnchor.MiddleCenter;
            name.fontSize = 12;
            name.color = new Color(1f, 1f, 1f, 0.9f);
            name.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            name.raycastTarget = false;

            SdSlotTag tag = cell.AddComponent<SdSlotTag>();
            tag.Path = path;
            tag.Thumb = thumb;
            tag.Bg = bg;
            tag.NameHit = nbgi;
            tag.Label = name;
            _sdVisibleCells.Add(tag);
            PlaceSdCell(tag);
            Button nameBtn = nameBg.AddComponent<Button>();
            nameBtn.targetGraphic = nbgi;
            nameBtn.transition = Selectable.Transition.None;
            SdSlotTag nameTag = tag;
            nameBtn.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                BeginSdRename(nameTag);
            });
            Button button = cell.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            string captured = path;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                if (DeleteSceneOnClick(captured)) return;
                if (SaveSceneOnClick(tag, cr)) return;
                ActivateSceneFavorite(captured);
            });
            ApplySdThumb(tag);
        }

        private static bool ReuseSdCell(string path)
        {
            for (int i = 0; i < _sdVisibleCells.Count; i++)
            {
                SdSlotTag tag = _sdVisibleCells[i];
                if (tag == null || tag.Path != path ||
                    _sdKeptCells.Contains(tag)) continue;
                PlaceSdCell(tag);
                ApplySdThumb(tag);
                return true;
            }
            return false;
        }

        private static void PlaceSdCell(SdSlotTag tag)
        {
            _sdKeptCells.Add(tag);
            if (tag.transform.GetSiblingIndex() != _sdCellPosition)
                tag.transform.SetSiblingIndex(_sdCellPosition);
            _sdCellPosition++;
        }

        private static void FinishSdCells()
        {
            for (int i = _sdVisibleCells.Count - 1; i >= 0; i--)
            {
                SdSlotTag tag = _sdVisibleCells[i];
                if (tag != null && _sdKeptCells.Contains(tag)) continue;
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(tag.gameObject);
                }
                _sdVisibleCells.RemoveAt(i);
            }
            if (_sdKeptCells.Count > 0 && _sdHintCell != null)
            {
                _sdHintCell.SetActive(false);
                UnityEngine.Object.Destroy(_sdHintCell);
                _sdHintCell = null;
            }
            int pending = _sdThumbQueue.Count;
            while (pending-- > 0)
            {
                SdSlotTag tag = _sdThumbQueue.Dequeue();
                if (tag != null && _sdKeptCells.Contains(tag))
                    _sdThumbQueue.Enqueue(tag);
                else _sdThumbQueued.Remove(tag);
            }
        }

        private static void ClearSdCellsNow()
        {
            for (int i = 0; i < _sdVisibleCells.Count; i++)
            {
                SdSlotTag tag = _sdVisibleCells[i];
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(tag.gameObject);
                }
            }
            _sdVisibleCells.Clear();
            _sdKeptCells.Clear();
            _sdCellPosition = 0;
            if (_sdHintCell != null)
            {
                _sdHintCell.SetActive(false);
                UnityEngine.Object.Destroy(_sdHintCell);
                _sdHintCell = null;
            }
            _sdThumbQueue.Clear();
            _sdThumbQueued.Clear();
            _sdBuildSlots = null;
        }

        private static string SdDisplayName(string[] entry)
        {
            if (entry != null && entry.Length > 1 &&
                !string.IsNullOrEmpty(entry[1])) return entry[1];
            string file = entry == null ? "" : entry[0];
            int slash = file == null ? -1 : file.LastIndexOf('/');
            if (slash >= 0) file = file.Substring(slash + 1);
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                file = file.Substring(0, file.Length - 5);
            return file;
        }

        // ---------- thumbnails ----------

        private static void ApplySdThumb(SdSlotTag tag)
        {
            Texture2D cached;
            _sdThumbs.TryGetValue(tag.Path, out cached);
            tag.Thumb.texture = cached;
            tag.Thumb.color = cached != null
                ? Color.white : new Color(0.25f, 0.25f, 0.3f, 1f);
            if (!DockCellVisible(tag.transform.GetSiblingIndex(),
                SdCellH, _sdScrollY, SdGridH)) return;
            if (_sdThumbQueued.Add(tag)) _sdThumbQueue.Enqueue(tag);
        }

        private static void TickSdThumbnails()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int work = 0;
            while (_sdThumbQueue.Count > 0 && work < 2)
            {
                SdSlotTag tag = _sdThumbQueue.Dequeue();
                _sdThumbQueued.Remove(tag);
                work++;
                if (tag == null || !_sdKeptCells.Contains(tag)) continue;
                LoadSdThumb(tag);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                    1000.0 / System.Diagnostics.Stopwatch.Frequency >= 2.0)
                    break;
            }
        }

        // Sidecar jpg next to the scene .json, same freshness-stamp scheme
        // as the preset dock. A texture fed by a BrowserAssist sprite handoff
        // survives until a real file lands on disk.
        private static void LoadSdThumb(SdSlotTag tag)
        {
            string path = tag.Path;
            string jpg = path.Substring(0,
                path.Length - Path.GetExtension(path).Length) + ".jpg";
            long stamp = -1L;
            string full = null;
            try
            {
                full = FileManager.GetFullPath(jpg);
                if (File.Exists(full))
                    stamp = File.GetLastWriteTimeUtc(full).Ticks;
            }
            catch { }
            long cachedStamp;
            bool haveStamp = _sdThumbStamp.TryGetValue(path, out cachedStamp);
            if (haveStamp && cachedStamp != stamp)
            {
                Texture2D old;
                if (_sdThumbs.TryGetValue(path, out old))
                {
                    _sdThumbs.Remove(path);
                    if (old != null) UnityEngine.Object.Destroy(old);
                }
            }
            if (!haveStamp) _sdThumbStamp[path] = stamp;
            Texture2D tex;
            if (!_sdThumbs.TryGetValue(path, out tex))
            {
                tex = null;
                try
                {
                    byte[] bytes = null;
                    if (full != null && File.Exists(full))
                        bytes = File.ReadAllBytes(full);
                    else if (FileManager.FileExists(jpg, false, false))
                    {
                        // Package refs (UID:/...) point inside a .var —
                        // GetFullPath/File IO cannot see them, but
                        // FileManager resolves them against its registry.
                        using (var entry = FileManager.OpenStream(jpg, false))
                        {
                            if (entry != null && entry.Stream != null)
                            {
                                byte[] buf = new byte[65536];
                                using (var ms = new MemoryStream())
                                {
                                    int n;
                                    while ((n = entry.Stream.Read(buf, 0,
                                        buf.Length)) > 0)
                                        ms.Write(buf, 0, n);
                                    bytes = ms.ToArray();
                                }
                            }
                        }
                    }
                    if (bytes != null)
                    {
                        Texture2D t = new Texture2D(2, 2,
                            TextureFormat.RGBA32, false);
                        t.name = "Q3SdThumb";
                        if (t.LoadImage(bytes)) tex = t;
                        else UnityEngine.Object.Destroy(t);
                    }
                }
                catch { tex = null; }
                _sdThumbs[path] = tex;
            }
            tag.Thumb.texture = tex;
            tag.Thumb.color = tex != null
                ? Color.white : new Color(0.25f, 0.25f, 0.3f, 1f);
        }

        internal static void InvalidateSdThumb(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return;
            _sdThumbStamp.Remove(scenePath);
            Texture2D old;
            if (_sdThumbs.TryGetValue(scenePath, out old))
            {
                _sdThumbs.Remove(scenePath);
                if (old != null) UnityEngine.Object.Destroy(old);
            }
            for (int i = 0; i < _sdVisibleCells.Count; i++)
            {
                SdSlotTag cell = _sdVisibleCells[i];
                if (cell == null || cell.Path != scenePath) continue;
                if (cell.Thumb != null) cell.Thumb.texture = null;
                ApplySdThumb(cell);
            }
        }

        private static void ActivateSceneFavorite(string path)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading || string.IsNullOrEmpty(path))
                return;
            VrHaptics.Confirm();
            if (!SceneLoadAccelerator.LoadSceneFast(path))
            {
                try { sc.Load(path); }
                catch (Exception e) { Error(e); }
            }
        }
    }
}
