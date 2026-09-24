using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    // Marks a favorites tag row so a dropped thumbnail is filed under that tag.
    internal sealed class AceFavTagRowTag : MonoBehaviour
    {
        internal int GroupIndex = -1;
    }

    internal sealed class AceFavDragSource
    {
        internal bool FromBar;
        internal bool FromBan;
        internal bool FromLock;
        // Preset-dock slot drags: PresetPath is the .vap filed under
        // PresetTab (the tab it was picked up from).
        internal bool FromPresetDock;
        internal string PresetPath;
        internal int PresetTab;
        internal string Uid;
        internal string DisplayName;
        internal string CreatorName;
        internal DAZClothingItem Item;
        internal Texture2D Texture;
        // Which hand's laser pressed this slot — the drag then tracks that
        // pointer's cursor (cursorRight vs cursor).
        internal bool RightPointer = true;
    }

    internal static partial class UiAssistHudLink
    {
        // Transparent two-column strip docked to the right edge of the ACE list.
        // Entries are DAZClothingItem uids, so the bar is character-independent.
        private const float FavCellW = 96f;
        private const float FavCellH = 112f;
        private const float FavSpacing = 6f;
        private const float FavPad = 8f;
        private const float FavColW = FavCellW * 2f + FavSpacing + FavPad * 2f;
        private const float FavTagStripW = 176f;
        private const float FavTagBackW = 92f;
        private const float FavTagRowH = 34f;
        private const float FavTagGap = 4f;
        private const float TagCaptionH = 26f;

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
        // Shared scratch buffer for dock placement (favorites bar, preset
        // dock, ban/lock bars all position against the ACE list's corners).
        private static readonly Vector3[] _dockCorners = new Vector3[4];
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
        // Named clothing tag groups. The untagged default list stays in the
        // original favorites file; every tag owns its own list.
        private static readonly List<string> _favTagNames = new List<string>();
        private static readonly Dictionary<string, List<string[]>> _favTagItems =
            new Dictionary<string, List<string[]>>();
        private static string _favTagActiveName = "";
        private static int _favTagIndex = -1;
        private static bool _favTagView = true;
        private static bool _favTagsLoaded;
        private static RectTransform _favTagStrip;
        private static int _favTagTop;
        private static string _favTagEditing;
        private static InputField _favTagInput;

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

        // Wearing makes VaM load the preset named by the item's
        // storePresetName storable a few frames later — an immediate restore
        // is overwritten by that load, which is why favorited items used to
        // come back with the default preset. Restore in two phases instead:
        // phase 1 applies only the Preset storables (selects the drag-time
        // preset), phase 2 layers every other storable on top after the
        // preset load settles.
        private static IEnumerator ApplyFavoriteStateDeferred(DAZClothingItem item)
        {
            yield return new WaitForSecondsRealtime(0.15f);
            if (item == null || !item.active) yield break;
            ApplyFavoriteState(item, true);
            yield return new WaitForSecondsRealtime(0.5f);
            if (item == null || !item.active) yield break;
            ApplyFavoriteState(item, false);
        }

        private static void ApplyFavoriteState(DAZClothingItem item,
            bool presetPhase)
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
                    bool isPreset = storable.storeId.EndsWith("Preset",
                        StringComparison.OrdinalIgnoreCase);
                    if (isPreset != presetPhase) continue;
                    JSONClass sub = json[storable.storeId] as JSONClass;
                    if (sub == null) continue;
                    storable.RestoreFromJSON(sub, true, true, new JSONArray(), false);
                    applied++;
                }
                if (!presetPhase && applied > 0)
                    Log("已恢复收藏状态：" + (item.displayName ?? item.uid) +
                        "（" + applied + " 组参数）");
            }
            catch (Exception e) { Error(e); }
        }

        private static void ClearFavoritesBar()
        {
            ClearFavoriteReorder();
            _favoritePageTargets.Clear();
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
            _favTagStrip = null;
            _favTagInput = null;
            _favTagEditing = null;
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

        private static string FavTagsPath
        {
            get { return Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.clothing-favtags.txt"); }
        }

        // Pure text codec for the tag store: "#name" opens a group,
        // "uid|display|creator" adds an entry to the open group, "!key=value"
        // carries the remembered view. The untagged default list keeps using
        // the original favorites file, so old installs load unchanged.
        internal static void ParseFavoriteTagLines(string[] lines)
        {
            _favTagNames.Clear();
            _favTagItems.Clear();
            _favTagActiveName = "";
            _favTagView = true;
            List<string[]> current = null;
            if (lines != null)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.TrimEnd('\r', '\n', ' ', '\t');
                    if (line.Length == 0) continue;
                    if (line[0] == '!')
                    {
                        const string viewKey = "!view=";
                        const string activeKey = "!active=";
                        if (line.StartsWith(viewKey, StringComparison.Ordinal))
                            _favTagView = line.Substring(viewKey.Length).Trim() != "tag";
                        else if (line.StartsWith(activeKey, StringComparison.Ordinal))
                            _favTagActiveName = line.Substring(activeKey.Length).Trim();
                        continue;
                    }
                    if (line[0] == '#')
                    {
                        string name = SanitizeTagName(line.Substring(1));
                        if (name.Length == 0) { current = null; continue; }
                        List<string[]> existing;
                        if (_favTagItems.TryGetValue(name, out existing))
                            current = existing;   // repeated header merges
                        else
                        {
                            current = new List<string[]>();
                            _favTagNames.Add(name);
                            _favTagItems[name] = current;
                        }
                        continue;
                    }
                    if (current == null) continue;
                    string[] parts = line.Split('|');
                    for (int p = 0; p < parts.Length; p++) parts[p] = parts[p].Trim();
                    if (parts[0].Length == 0) continue;
                    current.Add(new string[] {
                        parts[0],
                        parts.Length > 1 ? parts[1] : "",
                        parts.Length > 2 ? parts[2] : "" });
                }
            }
            _favTagIndex = _favTagNames.IndexOf(_favTagActiveName);
            if (!_favTagView && _favTagIndex < 0)
                _favTagView = true;   // a view needs a tag that still exists
        }

        internal static string[] FormatFavoriteTagLines()
        {
            List<string> lines = new List<string>();
            lines.Add("!active=" + (_favTagActiveName ?? ""));
            lines.Add("!view=" + (_favTagView ? "root" : "tag"));
            for (int i = 0; i < _favTagNames.Count; i++)
            {
                string name = _favTagNames[i];
                lines.Add("#" + name);
                List<string[]> items;
                if (!_favTagItems.TryGetValue(name, out items) || items == null)
                    continue;
                for (int j = 0; j < items.Count; j++)
                    lines.Add(items[j][0] + "|" + items[j][1] + "|" + items[j][2]);
            }
            return lines.ToArray();
        }

        // Tag names live on a single line and open a group, so the reserved
        // markers are stripped rather than escaped.
        private static string SanitizeTagName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            s = s.Replace('|', '-').Replace('#', '-').Replace('!', '-');
            s = s.Replace('\r', '-').Replace('\n', '-');
            if (s.Length > 16) s = s.Substring(0, 16);
            return s.Trim();
        }

        private static void LoadFavoriteTags()
        {
            if (_favTagsLoaded) return;
            _favTagsLoaded = true;
            try
            {
                ParseFavoriteTagLines(File.Exists(FavTagsPath)
                    ? File.ReadAllLines(FavTagsPath)
                    : new string[0]);
            }
            catch (Exception e) { Error(e); }
        }

        private static void SaveFavoriteTags()
        {
            try
            {
                _favTagActiveName = ActiveTagName ?? "";
                File.WriteAllLines(FavTagsPath, FormatFavoriteTagLines());
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning(
                        "Clothing favorite tags save failed: " + e.Message);
            }
        }

        private static void SaveFavoriteStores()
        {
            SaveFavorites();
            SaveFavoriteTags();
        }

        private static string ActiveTagName
        {
            get
            {
                return _favTagIndex >= 0 && _favTagIndex < _favTagNames.Count
                    ? _favTagNames[_favTagIndex] : null;
            }
        }

        private static List<string[]> TagFavorites(int index)
        {
            if (index < 0 || index >= _favTagNames.Count) return _favorites;
            string name = _favTagNames[index];
            List<string[]> items;
            if (!_favTagItems.TryGetValue(name, out items) || items == null)
            {
                items = new List<string[]>();
                _favTagItems[name] = items;
            }
            return items;
        }

        // Column contents: the tag view roots the default (untagged) list, a
        // tag view shows that tag's own list.
        private static List<string[]> VisibleFavorites
        {
            get { return _favTagView ? _favorites : TagFavorites(_favTagIndex); }
        }

        private static int FindFavoriteIndex(List<string[]> items, string uid)
        {
            if (items == null || string.IsNullOrEmpty(uid)) return -1;
            string key = BanKey(uid);
            for (int i = 0; i < items.Count; i++)
                if (items[i][0] == uid || BanKey(items[i][0]) == key) return i;
            return -1;
        }

        private static bool IsFavoriteAnywhere(string uid)
        {
            if (FindFavoriteIndex(_favorites, uid) >= 0) return true;
            foreach (List<string[]> items in _favTagItems.Values)
                if (FindFavoriteIndex(items, uid) >= 0) return true;
            return false;
        }

        private static bool RemoveFavoriteFrom(List<string[]> items, string uid)
        {
            int index = FindFavoriteIndex(items, uid);
            if (index < 0) return false;
            items.RemoveAt(index);
            return true;
        }

        // Tag rows that fit above the create row, leaving room for the pager.
        private static int FavTagCapacity
        {
            get
            {
                float listH = _favListHeight > 0f ? _favListHeight : 400f;
                float usable = listH - FavPad * 2f - FavTagRowH - TagCaptionH;
                int cap = Mathf.FloorToInt(usable / (FavTagRowH + FavTagGap));
                if (_favTagNames.Count > cap) cap -= 1;
                return Mathf.Max(1, cap);
            }
        }

        private static int FavTagVisibleCount
        {
            get { return Mathf.Min(_favTagNames.Count, FavTagCapacity); }
        }

        private static bool FavTagPager
        {
            get { return _favTagNames.Count > FavTagCapacity; }
        }

        private static float CurrentTagStripWidth
        {
            get { return _favTagView ? FavTagStripW : FavTagBackW; }
        }

        private static float RequiredTagStripHeight()
        {
            if (!_favTagView)
                return FavPad * 2f + FavTagRowH + FavTagGap + TagCaptionH;
            int rows = 1 + FavTagVisibleCount + 1 + (FavTagPager ? 1 : 0);
            return FavPad * 2f + rows * (FavTagRowH + FavTagGap);
        }

        private static void ApplyFavoritesDockWidth()
        {
            if (_favDock == null) return;
            _favDock.sizeDelta = new Vector2(
                FavColW + FavTagGap + CurrentTagStripWidth, _favDock.sizeDelta.y);
        }

        private static void SetFavoriteTagView(bool root)
        {
            if (_favTagView == root) return;
            _favTagView = root;
            _favPage = 0;
            _favDirty = true;
            SaveFavoriteTags();
            RefreshTagRows();
        }

        private static void EnterFavoriteTag(int index)
        {
            if (index < 0 || index >= _favTagNames.Count) return;
            if (_favTagEditing != null) CommitTagRename();
            _favTagIndex = index;
            SetFavoriteTagView(false);
            Log("收藏标签：" + _favTagNames[index]);
        }

        private static void ExitFavoriteTagView()
        {
            SetFavoriteTagView(true);
            Log("已返回标签视图。");
        }

        private static void CreateFavoriteTag()
        {
            LoadFavoriteTags();
            if (_favTagEditing != null) CommitTagRename();
            int n = _favTagNames.Count + 1;
            string name = "标签" + n;
            while (_favTagItems.ContainsKey(name))
                name = "标签" + n + "-" + (++n);
            _favTagItems[name] = new List<string[]>();
            _favTagNames.Add(name);
            _favTagTop = Mathf.Max(0, _favTagNames.Count - FavTagCapacity);
            _favTagEditing = name;
            _favTagView = true;
            _favPage = 0;
            _favDirty = true;
            SaveFavoriteTags();
            RefreshTagRows();
            Log("已新建收藏标签 " + name + "：输入名称后点 ✓ 确认。");
        }

        private static void BeginTagRename(int index)
        {
            if (index < 0 || index >= _favTagNames.Count) return;
            if (_favTagEditing != null) CommitTagRename();
            _favTagEditing = _favTagNames[index];
            RefreshTagRows();
        }

        private static void CommitTagRename()
        {
            if (_favTagEditing == null) return;
            string old = _favTagEditing;
            InputField input = _favTagInput;
            _favTagEditing = null;
            _favTagInput = null;
            int index = _favTagNames.IndexOf(old);
            if (index >= 0)
            {
                string name = SanitizeTagName(input == null ? "" : input.text);
                if (name.Length > 0 && name != old && !_favTagItems.ContainsKey(name))
                {
                    List<string[]> items = _favTagItems[old];
                    _favTagItems.Remove(old);
                    _favTagItems[name] = items;
                    _favTagNames[index] = name;
                    if (_favTagIndex == index) _favTagActiveName = name;
                    Log("收藏标签已命名为：" + name);
                }
            }
            SaveFavoriteTags();
            RefreshTagRows();
        }

        private static void DeleteFavoriteTag(int index)
        {
            if (index < 0 || index >= _favTagNames.Count) return;
            string name = _favTagNames[index];
            List<string[]> items = _favTagItems.ContainsKey(name)
                ? _favTagItems[name] : new List<string[]>();
            // Deleting a tag never destroys curated entries: they return to the
            // default list instead.
            int moved = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (FindFavoriteIndex(_favorites, items[i][0]) >= 0) continue;
                _favorites.Add(items[i]);
                moved++;
            }
            _favTagItems.Remove(name);
            _favTagNames.RemoveAt(index);
            if (_favTagEditing == name) { _favTagEditing = null; _favTagInput = null; }
            if (_favTagIndex == index) { _favTagIndex = -1; _favTagView = true; }
            else if (_favTagIndex > index) _favTagIndex--;
            _favPage = 0;
            _favDirty = true;
            SaveFavoriteStores();
            RefreshTagRows();
            Log("已删除收藏标签：" + name +
                (moved > 0 ? "（" + moved + " 项已移回默认）" : ""));
        }
        private static void CreateFavTagStrip()
        {
            GameObject strip = new GameObject("FavTagStrip", typeof(RectTransform));
            _favTagStrip = (RectTransform)strip.transform;
            _favTagStrip.SetParent(_favDock, false);
            _favTagStrip.anchorMin = new Vector2(1f, 1f);
            _favTagStrip.anchorMax = new Vector2(1f, 1f);
            _favTagStrip.pivot = new Vector2(1f, 1f);
            _favTagStrip.anchoredPosition = new Vector2(-FavTagGap, -FavPad);
            _favTagStrip.sizeDelta = new Vector2(CurrentTagStripWidth, 64f);
            RefreshTagRows();
        }

        // The tag view lists the tags; a tag view collapses the strip down to a
        // small back button, the way a submenu replaces its parent list.
        private static void RefreshTagRows()
        {
            ApplyFavoritesDockWidth();
            if (_favTagStrip == null) return;
            for (int i = _favTagStrip.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_favTagStrip.GetChild(i).gameObject);
            if (!_favTagView) { BuildTagBackRows(); return; }
            _favTagTop = Mathf.Clamp(_favTagTop, 0,
                Mathf.Max(0, _favTagNames.Count - FavTagCapacity));
            float y = 0f;
            AddTagCaption("收藏标签", y);
            y += TagCaptionH + FavTagGap;
            int visible = FavTagVisibleCount;
            for (int i = 0; i < visible; i++)
            {
                CreateTagRow(_favTagNames[_favTagTop + i], _favTagTop + i, y);
                y += FavTagRowH + FavTagGap;
            }
            CreateTagCreateRow(y);
            y += FavTagRowH + FavTagGap;
            if (FavTagPager) { CreateTagPagerRow(y); y += FavTagRowH + FavTagGap; }
            _favTagStrip.sizeDelta = new Vector2(FavTagStripW, y);
        }

        private static void BuildTagBackRows()
        {
            GameObject back = new GameObject("TagBack", typeof(RectTransform));
            RectTransform rect = (RectTransform)back.transform;
            rect.SetParent(_favTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(FavTagBackW, FavTagRowH);
            Image bg = back.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            bg.raycastTarget = true;
            Button button = back.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { VrHaptics.Press(); ExitFavoriteTagView(); });
            AddTagLabel(rect, "◀ 返回", 14, TextAnchor.MiddleCenter, Color.white);
            AddTagCaption(ActiveTagName ?? "默认", FavTagRowH + FavTagGap);
            _favTagStrip.sizeDelta = new Vector2(FavTagBackW,
                FavTagRowH + FavTagGap + TagCaptionH);
        }

        private static void CreateTagRow(string label, int index, float y)
        {
            bool editing = _favTagEditing != null && _favTagNames[index] == _favTagEditing;
            GameObject row = new GameObject("Tag " + label, typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_favTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(FavTagStripW, FavTagRowH);
            row.AddComponent<AceFavTagRowTag>().GroupIndex = index;

            float nameWidth = editing
                ? FavTagStripW - FavTagRowH - FavTagGap
                : FavTagStripW - 2f * (FavTagRowH + FavTagGap);
            GameObject nameGo = new GameObject("Name", typeof(RectTransform));
            RectTransform nameRect = (RectTransform)nameGo.transform;
            nameRect.SetParent(rect, false);
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(0f, 1f);
            nameRect.pivot = new Vector2(0f, 0.5f);
            nameRect.anchoredPosition = Vector2.zero;
            nameRect.sizeDelta = new Vector2(nameWidth, -4f);
            Image bg = nameGo.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.20f, 0.27f, 1f);
            bg.raycastTarget = true;
            Button button = nameGo.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            int capturedIndex = index;
            button.onClick.AddListener(delegate { VrHaptics.Press(); EnterFavoriteTag(capturedIndex); });
            AddTagLabel(nameRect, label, 14, TextAnchor.MiddleLeft, Color.white);
            if (editing)
            {
                button.interactable = false;
                bg.color = new Color(0.08f, 0.10f, 0.14f, 1f);
                InputField input = CreateTagNameInput(rect, nameWidth);
                _favTagInput = input;
                input.text = label;
                input.ActivateInputField();
                input.Select();
                VrTextInputBridge.Select(input);
                // No onEndEdit auto-commit: opening the VR keyboard steals
                // focus, which would commit and destroy the field before a
                // single keystroke arrives. Commit lives on ✓ only.
                AddTagAction(rect, "✓", FavTagStripW - FavTagRowH,
                    new Color(0.12f, 0.42f, 0.24f, 1f),
                    delegate { CommitTagRename(); });
            }
            else
            {
                AddTagAction(rect, "✎", FavTagStripW - 2f * FavTagRowH - FavTagGap,
                    new Color(0.16f, 0.24f, 0.36f, 1f),
                    delegate { BeginTagRename(capturedIndex); });
                AddTagAction(rect, "✕", FavTagStripW - FavTagRowH,
                    new Color(0.30f, 0.14f, 0.14f, 1f),
                    delegate { DeleteFavoriteTag(capturedIndex); });
            }
        }

        private static void CreateTagCreateRow(float y)
        {
            GameObject row = new GameObject("TagCreate", typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_favTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(FavTagStripW, FavTagRowH);
            Image bg = row.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.30f, 0.20f, 1f);
            bg.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { VrHaptics.Press(); CreateFavoriteTag(); });
            AddTagLabel(rect, "＋ 新建标签", 14, TextAnchor.MiddleCenter, Color.white);
        }

        private static void CreateTagPagerRow(float y)
        {
            GameObject row = new GameObject("TagPager", typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_favTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(FavTagStripW, FavTagRowH);
            float x = FavTagStripW * 0.25f;
            CreateFavNavButton(rect, "▲", -x, delegate { FavTagPageStep(-1); });
            CreateFavNavButton(rect, "▼", x, delegate { FavTagPageStep(1); });
        }

        private static InputField CreateTagNameInput(RectTransform row, float width)
        {
            GameObject go = new GameObject("TagNameInput", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(row, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, -4f);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.18f, 0.24f, 1f);
            InputField input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 16;
            GameObject textGo = new GameObject("Text", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 1f);
            textRect.offsetMax = new Vector2(-6f, -1f);
            Text text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.supportRichText = false;
            text.raycastTarget = false;
            input.textComponent = text;
            return input;
        }

        private static Text AddTagLabel(RectTransform parent, string value,
            int fontSize, TextAnchor align, Color color)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 1f);
            rect.offsetMax = new Vector2(-6f, -1f);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = color;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static Text AddTagCaption(string value, float y)
        {
            GameObject go = new GameObject("TagCaption", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(_favTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(CurrentTagStripWidth, TagCaptionH);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(1f, 1f, 1f, 0.55f);
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static void AddTagAction(RectTransform row, string label, float x,
            Color color, UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("TagAction " + label, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(row, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(FavTagRowH, -4f);
            Image bg = go.AddComponent<Image>();
            bg.color = color;
            bg.raycastTarget = true;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate { VrHaptics.Press(); action(); });
            AddTagLabel(rect, label, 15, TextAnchor.MiddleCenter, Color.white);
        }

        private static void FavTagPageStep(int delta)
        {
            int cap = FavTagCapacity;
            int max = Mathf.Max(0, _favTagNames.Count - cap);
            int next = Mathf.Clamp(_favTagTop + delta * cap, 0, max);
            if (next == _favTagTop) return;
            _favTagTop = next;
            RefreshTagRows();
        }

        // Drag routing: the tag row under the pointer names the group a dropped
        // thumbnail is filed under.
        private static bool TryGetTagRowGroup(out int group)
        {
            group = -1;
            // Cursor first: the laser point tracks mid-drag even when the
            // look target is stale after a press.
            if (_favTagStrip != null)
            {
                Vector2 local;
                foreach (AceFavTagRowTag row in _favTagStrip
                    .GetComponentsInChildren<AceFavTagRowTag>())
                {
                    if (row != null && PointerOnRect(
                        row.transform as RectTransform, out local))
                    {
                        group = row.GroupIndex;
                        return true;
                    }
                }
            }
            GameObject target = VrPointerPresentation.CurrentLookTarget(true);
            AceFavTagRowTag look = target == null
                ? null : target.GetComponentInParent<AceFavTagRowTag>();
            if (look == null)
            {
                target = VrPointerPresentation.CurrentLookTarget(false);
                look = target == null ? null : target.GetComponentInParent<AceFavTagRowTag>();
            }
            if (look == null) return false;
            group = look.GroupIndex;
            return true;
        }

        private static bool MoveFavorite(AceFavDragSource source, int group)
        {
            if (source == null || group < 0 || group >= _favTagNames.Count) return false;
            List<string[]> from = VisibleFavorites;
            List<string[]> to = TagFavorites(group);
            if (ReferenceEquals(from, to)) return false;
            if (FindFavoriteIndex(to, source.Uid) >= 0)
                return RemoveFavoriteFrom(from, source.Uid);
            int at = FindFavoriteIndex(from, source.Uid);
            if (at < 0) return false;
            string[] entry = from[at];
            from.RemoveAt(at);
            to.Add(entry);
            SaveFavoriteStores();
            _favDirty = true;
            Log("已移动到标签 " + _favTagNames[group] + "：" + entry[1]);
            return true;
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
            LoadFavoriteTags();
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
                _favDock.sizeDelta = new Vector2(
                    FavColW + FavTagGap + CurrentTagStripWidth, 64f);
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
                _favCells.anchorMax = new Vector2(0f, 1f);
                _favCells.pivot = new Vector2(0f, 1f);
                _favCells.anchoredPosition = new Vector2(0f, -FavPad);
                _favCells.sizeDelta = new Vector2(FavColW, 0f);
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(FavCellW, FavCellH);
                grid.spacing = new Vector2(FavSpacing, FavSpacing);
                grid.padding = new RectOffset((int)FavPad, (int)FavPad, 0, (int)FavPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                ContentSizeFitter fitter = cellsGo.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

                _favListHeight = ((RectTransform)list.transform).rect.height;
                LoadFavoriteTags();
                CreateFavNav();
                CreateFavTagStrip();
                ApplyFavoritesDockWidth();

                SuperController.singleton.AddCanvas(_favCanvas);
                _favDirty = true;
                Log("服装收藏栏已创建（默认 " + _favorites.Count + " 项，" +
                    _favTagNames.Count + " 个标签），等待定位。");
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
            navRect.anchorMin = new Vector2(0f, 0f);
            navRect.anchorMax = new Vector2(0f, 0f);
            navRect.pivot = new Vector2(0.5f, 0f);
            navRect.anchoredPosition = new Vector2(FavColW * 0.5f, 4f);
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
            if (parent.gameObject.name == "FavNav")
            {
                FavoritePageTarget target = go.AddComponent<FavoritePageTarget>();
                target.Direction = x < 0f ? -1 : 1;
                _favoritePageTargets.Add(target);
            }
            button.onClick.AddListener(delegate {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press(); action();
            });
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
            List<string[]> favorites = FavoriteDisplayOrder();
            int count = favorites.Count;
            // Page capacity is always rowsNoNav*2 — the nav row sits BELOW
            // the cells (the dock grows one nav-row taller than the list)
            // instead of stealing a cell row, so every page stays 4x2.
            float listH = _favListHeight > 0f ? _favListHeight : 400f;
            int rowsNoNav = Mathf.Max(1, Mathf.FloorToInt(
                (listH - FavPad * 2f + FavSpacing) / (FavCellH + FavSpacing)));
            bool nav = count > rowsNoNav * 2;
            int capacity = Mathf.Max(2, rowsNoNav * 2);
            _favPages = Mathf.Max(1, Mathf.CeilToInt(count / (float)capacity));
            _favPage = Mathf.Clamp(_favPage, 0, _favPages - 1);
            int start = _favPage * capacity;
            int end = Mathf.Min(count, start + capacity);
            for (int i = start; i < end; i++)
                CreateFavoriteSlot(favorites[i][0]);
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
            ApplyFavoritesDockWidth();
            // The dock may grow past the list height by exactly the nav row
            // so paging never shrinks the 4x2 cell grid.
            float cap = listH + (nav ? FavNavH + 4f : 0f);
            _favDock.sizeDelta = new Vector2(_favDock.sizeDelta.x,
                Mathf.Min(cap, Mathf.Max(64f,
                    Mathf.Max(height, RequiredTagStripHeight()))));
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
            button.onClick.AddListener(delegate {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                WearFavorite(captured);
            });
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
                if (item.active && Quest3TriggerUIPlugin.Instance != null)
                    Quest3TriggerUIPlugin.Instance.StartCoroutine(
                        ApplyFavoriteStateDeferred(item));
            }
            catch (Exception e) { Error(e); }
        }

        // ---- drag plumbing ----

        internal static AceFavDragSource BeginFavoriteCandidate(
            GameObject target, bool right)
        {
            AceFavDragSource source = ResolveFavoriteCandidate(target);
            if (source != null) source.RightPointer = right;
            return source;
        }

        private static AceFavDragSource ResolveFavoriteCandidate(GameObject target)
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
                PdSlotTag pdSlot = target.GetComponentInParent<PdSlotTag>();
                if (pdSlot != null)
                {
                    return new AceFavDragSource {
                        FromPresetDock = true, PresetPath = pdSlot.Path,
                        PresetTab = _pdTab,
                        Texture = pdSlot.Thumb == null ? null : pdSlot.Thumb.texture as Texture2D };
                }
                // UIAssist's scroll-list rows are the authoritative source for
                // the clothing editor.  ACEIDPlus rows bind a
                // DCIButtonPointerBehaviour holding the exact DAZClothingItem
                // — read it before any text matching: the name label shows a
                // BrowserAssist alias (GetDisplayNameWithAvailableBAAlias),
                // not item.displayName, so text probes miss at row level and
                // the shared content root then resolves every drag to the
                // same first-scoring item.
                if (_favList != null && target.transform.IsChildOf(_favList.transform))
                {
                    AceFavDragSource bound = ResolvePointerBehaviour(target);
                    if (bound != null) return bound;
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

        // ACEIDPlus rows attach DCIButtonPointerBehaviour to both the
        // thumbnail button and the name button; its private 'dci' field IS
        // the row's DAZClothingItem — the authoritative binding. When the
        // behaviour sits on the image button, that button's sprite is the
        // exact thumbnail the user sees, so the drag ghost matches what was
        // grabbed instead of a re-fetched texture.
        private static AceFavDragSource ResolvePointerBehaviour(GameObject target)
        {
            try
            {
                foreach (Component c in target.GetComponentsInParent<Component>())
                {
                    if (c == null || c.GetType().Name != "DCIButtonPointerBehaviour") continue;
                    FieldInfo f = c.GetType().GetField("dci",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    DAZClothingItem item = f == null
                        ? null : f.GetValue(c) as DAZClothingItem;
                    if (item == null) continue;
                    Texture2D tex = null;
                    UIDynamicButton dyn = c.GetComponent<UIDynamicButton>();
                    if (dyn != null && dyn.button != null &&
                        dyn.button.image != null &&
                        dyn.button.image.sprite != null)
                        tex = dyn.button.image.sprite.texture as Texture2D;
                    return new AceFavDragSource {
                        FromBar = false, Uid = item.uid, Item = item,
                        Texture = tex,
                        DisplayName = item.displayName,
                        CreatorName = item.creatorName };
                }
            }
            catch (Exception e) { Error(e); }
            return null;
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
            if (source == null || (_favDock == null && _pdDock == null))
                return false;
            BeginFavoriteReorder(source);
            BeginPdReorder(source);
            VrHaptics.Press();
            try
            {
                // The ghost needs its own world-space canvas: a RawImage with
                // no Canvas ancestor never renders, so it cannot just be a
                // detached child of the bar.
                Canvas srcCanvas = _favCanvas != null ? _favCanvas : _pdCanvas;
                GameObject go = new GameObject("AceFavDragGhost", typeof(RectTransform));
                Canvas ghostCanvas = go.AddComponent<Canvas>();
                ghostCanvas.renderMode = RenderMode.WorldSpace;
                if (srcCanvas != null)
                {
                    ghostCanvas.worldCamera = srcCanvas.worldCamera;
                    go.transform.localScale = srcCanvas.transform.localScale;
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
            Transform root = _favGhostRoot.transform;
            // The ghost rides the laser cursor's world position — the exact
            // UI hit point that keeps tracking under a held press (the old
            // event ray froze mid-drag, which parked the ghost off-view).
            Vector3 cursorPos;
            if (DragCursorWorld(out cursorPos))
            {
                Vector3 away = viewer == null
                    ? Vector3.back
                    : (cursorPos - viewer.transform.position).normalized;
                root.position = cursorPos + away * 0.03f;
            }
            else
            {
                Transform hand = VrPointerPresentation.MotionController(
                    sc, DragRight());
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
            // Live reorder preview (phone-icon reflow) on both docks.
            TickPdReorder();
            TickFavoriteReorder();
        }

        private static bool PointerOverFavoritesBar()
        {
            GameObject target = VrPointerPresentation.CurrentLookTarget(true);
            if (target != null && target.GetComponentInParent<AceFavBarTag>() != null)
                return true;
            target = VrPointerPresentation.CurrentLookTarget(false);
            if (target != null && target.GetComponentInParent<AceFavBarTag>() != null)
                return true;
            // Preview rebuilds swap slot objects under the pointer — test the
            // persistent dock rect so a stale look target cannot misread.
            Vector2 local;
            return PointerOnRect(_favDock, out local);
        }

        internal static void EndFavoriteDrag(AceFavDragSource source)
        {
            try
            {
                if (source == null) return;
                if (source.FromPresetDock)
                {
                    // Preset-dock slot: drop on a different tab copies the
                    // preset into that tab (acceptance rules still apply,
                    // silently); drop on the dock's cell area commits the
                    // previewed reorder; drop outside removes it.
                    int dropTab = PresetDockTabUnderPointer();
                    bool overDock = PointerOverPresetDock();
                    bool pdActed = false;
                    if (dropTab >= 0 && dropTab != source.PresetTab)
                    {
                        if (FileDockPreset(dropTab, source.PresetPath))
                        {
                            PdSelectTab(dropTab);
                            _pdPage = Mathf.Max(0,
                                Mathf.CeilToInt(_pdSlots[dropTab].Count /
                                    (float)PdPageCapacity) - 1);
                            _pdDirty = true;
                        }
                        pdActed = true;
                    }
                    else if (overDock && source.PresetTab == _pdTab)
                    {
                        pdActed = CommitPdReorder(source);
                    }
                    else if (!overDock)
                    {
                        pdActed = RemoveDockSlot(source.PresetTab,
                            source.PresetPath);
                    }
                    Log("pd drop tab=" + dropTab + " over=" + overDock +
                        " idx=" + _pdDropIndex + " acted=" + pdActed);
                    if (pdActed) VrHaptics.Confirm();
                    return;
                }
                bool overFav = PointerOverFavoritesBar();
                bool overBan = PointerOverBanBar();
                bool overLock = PointerOverLockBar();
                Vector2 favoriteDropPoint;
                // Preview rebuilds replace slot objects. Hit the persistent
                // dock plane so a stale LookTarget cannot turn a reorder into deletion.
                if (source.FromBar && PointerOnRect(_favDock, out favoriteDropPoint))
                    overFav = true;
                bool acted = false;
                int dropGroup;
                bool onTagRow = TryGetTagRowGroup(out dropGroup);
                if (!source.FromBar && !source.FromBan && !source.FromLock)
                {
                    // Dragged out of the editor list.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overBan)
                        acted = AddBan(source);
                    else if (overFav)
                    {
                        if (onTagRow) EnterFavoriteTag(dropGroup);
                        acted = AddFavorite(source);
                    }
                }
                else if (source.FromBar)
                {
                    // Favorites-bar slot: dropping on a tag row files it under
                    // that tag, back on its own bar it stays, anywhere else it
                    // is removed.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overBan)
                        acted = AddBan(source);
                    else if (overFav)
                    {
                        if (onTagRow) acted = MoveFavorite(source, dropGroup);
                        else acted = CommitFavoriteReorder(source);
                    }
                    else
                        acted = RemoveFavorite(source.Uid, true);
                }
                else if (source.FromBan)
                {
                    // Ban-bar slot: same rules mirrored.
                    if (overLock)
                        acted = AddLock(source);
                    else if (overFav)
                    {
                        if (onTagRow) EnterFavoriteTag(dropGroup);
                        acted = AddFavorite(source);
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
                        if (onTagRow) EnterFavoriteTag(dropGroup);
                        acted = AddFavorite(source);
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
                _favoriteClickAfter = Time.unscaledTime + 0.2f;
                ClearFavoriteReorder();
                ClearPdReorder();
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

        private static bool AddFavorite(AceFavDragSource source)
        {
            if (source == null) return false;
            List<string[]> items = VisibleFavorites;
            if (FindFavoriteIndex(items, source.Uid) >= 0) return true;
            // Ban contradicts favorite (a favorite click tries to wear and
            // would be vetoed); lock coexists — wearing is allowed.
            RemoveBan(source.Uid);
            items.Add(new string[] {
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
            SaveFavoriteStores();
            // Land on the page the new entry was appended to.
            _favPage = int.MaxValue;
            _favDirty = true;
            Log("已收藏服装：" + (source.DisplayName ?? source.Uid) +
                " → " + (ActiveTagName ?? "默认"));
            return true;
        }

        private static bool RemoveFavorite(string uid, bool log)
        {
            List<string[]> items = VisibleFavorites;
            int index = FindFavoriteIndex(items, uid);
            if (index < 0) return false;
            if (log) Log("已移除收藏：" + items[index][1]);
            items.RemoveAt(index);
            // The cached thumbnail and the saved wear-state are dropped only
            // once no group references the item any more.
            if (!IsFavoriteAnywhere(uid))
            {
                _favThumbs.Remove(uid);
                try { string sp = FavStatePath(uid); if (File.Exists(sp)) File.Delete(sp); }
                catch { }
            }
            SaveFavoriteStores();
            _favDirty = true;
            return true;
        }
    }

}
