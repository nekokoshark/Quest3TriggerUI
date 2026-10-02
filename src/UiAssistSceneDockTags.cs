using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Scene dock tag strip + persistence. Same codec as the clothing bar:
    // "#name" opens a tag group, "path|display|pack" is an entry,
    // "!active=name" remembers the selected tag; the untagged default
    // list lives in its own file so old installs load unchanged.
    internal static partial class UiAssistHudLink
    {
        private static string SdFavPath
        {
            get
            {
                return Path.Combine(Paths.ConfigPath,
                    "Quest3TriggerUI.scene-favorites.txt");
            }
        }

        private static string SdTagsPath
        {
            get
            {
                return Path.Combine(Paths.ConfigPath,
                    "Quest3TriggerUI.scene-favtags.txt");
            }
        }

        private static void LoadSdFavorites()
        {
            if (_sdLoaded) return;
            _sdLoaded = true;
            try
            {
                if (!File.Exists(SdFavPath)) return;
                foreach (string line in File.ReadAllLines(SdFavPath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 1 && parts[0].Length > 0)
                        _sdFavorites.Add(new string[] {
                            parts[0],
                            parts.Length > 1 ? parts[1] : "",
                            parts.Length > 2 ? parts[2] : "" });
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void SaveSdFavorites()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (string[] f in _sdFavorites)
                    lines.Add(f[0] + "|" + f[1] + "|" + f[2]);
                File.WriteAllLines(SdFavPath, lines.ToArray());
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning(
                        "Scene favorites save failed: " + e.Message);
            }
        }

        internal static void ParseSdTagLines(string[] lines)
        {
            _sdTagNames.Clear();
            _sdTagItems.Clear();
            _sdTagActiveName = "";
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
                        const string activeKey = "!active=";
                        if (line.StartsWith(activeKey,
                            StringComparison.Ordinal))
                            _sdTagActiveName =
                                line.Substring(activeKey.Length).Trim();
                        continue;
                    }
                    if (line[0] == '#')
                    {
                        string name = SanitizeTagName(line.Substring(1));
                        if (name.Length == 0) { current = null; continue; }
                        List<string[]> existing;
                        if (_sdTagItems.TryGetValue(name, out existing))
                            current = existing;
                        else
                        {
                            current = new List<string[]>();
                            _sdTagNames.Add(name);
                            _sdTagItems[name] = current;
                        }
                        continue;
                    }
                    if (current == null) continue;
                    string[] parts = line.Split('|');
                    for (int p = 0; p < parts.Length; p++)
                        parts[p] = parts[p].Trim();
                    if (parts[0].Length == 0) continue;
                    current.Add(new string[] {
                        parts[0],
                        parts.Length > 1 ? parts[1] : "",
                        parts.Length > 2 ? parts[2] : "" });
                }
            }
            _sdTagIndex = _sdTagNames.IndexOf(_sdTagActiveName);
        }

        internal static string[] FormatSdTagLines()
        {
            List<string> lines = new List<string>();
            lines.Add("!active=" + (_sdTagActiveName ?? ""));
            for (int i = 0; i < _sdTagNames.Count; i++)
            {
                string name = _sdTagNames[i];
                lines.Add("#" + name);
                List<string[]> items;
                if (!_sdTagItems.TryGetValue(name, out items) ||
                    items == null) continue;
                for (int j = 0; j < items.Count; j++)
                    lines.Add(items[j][0] + "|" + items[j][1] + "|" +
                        items[j][2]);
            }
            return lines.ToArray();
        }

        private static void LoadSdTags()
        {
            if (_sdTagsLoaded) return;
            _sdTagsLoaded = true;
            try
            {
                ParseSdTagLines(File.Exists(SdTagsPath)
                    ? File.ReadAllLines(SdTagsPath) : new string[0]);
            }
            catch (Exception e) { Error(e); }
        }

        private static void SaveSdTags()
        {
            try
            {
                _sdTagActiveName = SdActiveTagName ?? "";
                File.WriteAllLines(SdTagsPath, FormatSdTagLines());
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning(
                        "Scene favorite tags save failed: " + e.Message);
            }
        }

        private static void SaveSdStores()
        {
            SaveSdFavorites();
            SaveSdTags();
        }

        private static string SdActiveTagName
        {
            get
            {
                return _sdTagIndex >= 0 && _sdTagIndex < _sdTagNames.Count
                    ? _sdTagNames[_sdTagIndex] : null;
            }
        }

        private static List<string[]> SdTagFavorites(int index)
        {
            if (index < 0 || index >= _sdTagNames.Count) return _sdFavorites;
            string name = _sdTagNames[index];
            List<string[]> items;
            if (!_sdTagItems.TryGetValue(name, out items) || items == null)
            {
                items = new List<string[]>();
                _sdTagItems[name] = items;
            }
            return items;
        }

        private static List<string[]> VisibleSdFavorites
        {
            get
            {
                return _sdTagIndex < 0 ? _sdFavorites : SdTagFavorites(_sdTagIndex);
            }
        }

        private static int FindSdIndex(List<string[]> items, string path)
        {
            if (items == null || string.IsNullOrEmpty(path)) return -1;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i][0], path,
                    StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Strip metrics: tag capacity matches the clothing bar's budget
        // (默认+新建+pager+服装+预设+删除+隐藏 = 7 rows). The two extra
        // management rows (存新/保存) extend the dock below the list
        // height — RequiredSdStripHeight already grows the dock to fit.
        private static int SdTagCapacity
        {
            get
            {
                float listH = _sdListHeight > 0f ? _sdListHeight : 400f;
                float usable = listH - FavPad * 2f - TagCaptionH -
                    7f * FavTagRowH - 8f * FavTagGap;
                return Mathf.Max(1,
                    Mathf.FloorToInt(usable / (FavTagRowH + FavTagGap)));
            }
        }

        private static int SdTagVisibleCount
        {
            get { return Mathf.Min(_sdTagNames.Count, SdTagCapacity); }
        }

        private static bool SdTagPager
        {
            get { return _sdTagNames.Count > SdTagCapacity; }
        }

        private static float RequiredSdStripHeight()
        {
            int rows = 1 + SdTagVisibleCount + 1 +
                (SdTagPager ? 1 : 0) + 6;
            return FavPad * 2f + rows * (FavTagRowH + FavTagGap) +
                TagCaptionH;
        }

        private static void ApplySdDockWidth()
        {
            if (_sdDock == null) return;
            _sdDock.sizeDelta = new Vector2(
                FavColW + FavTagGap + SdStripW, _sdDock.sizeDelta.y);
        }

        private static void CreateSdTagStrip()
        {
            GameObject strip = new GameObject("SdTagStrip",
                typeof(RectTransform));
            _sdTagStrip = (RectTransform)strip.transform;
            _sdTagStrip.SetParent(_sdDock, false);
            _sdTagStrip.anchorMin = new Vector2(0f, 1f);
            _sdTagStrip.anchorMax = new Vector2(0f, 1f);
            _sdTagStrip.pivot = new Vector2(0f, 1f);
            _sdTagStrip.anchoredPosition = new Vector2(
                FavTagGap, -FavPad);
            _sdTagStrip.sizeDelta = new Vector2(SdStripW, 64f);
            RefreshSdTagRows();
        }

        // Flat strip like the favorites bar: caption, 默认 + one row per
        // tag, then management rows (存新/保存/删除/隐藏).
        private static void RefreshSdTagRows()
        {
            ApplySdDockWidth();
            if (_sdTagStrip == null) return;
            for (int i = _sdTagStrip.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(
                    _sdTagStrip.GetChild(i).gameObject);
            _sdTagTop = Mathf.Clamp(_sdTagTop, 0,
                Mathf.Max(0, _sdTagNames.Count - SdTagCapacity));
            float y = 0f;
            AddSdCaption("场景收藏", y);
            y += TagCaptionH + FavTagGap;
            CreateSdDefaultTagRow(y);
            y += FavTagRowH + FavTagGap;
            int visible = SdTagVisibleCount;
            for (int i = 0; i < visible; i++)
            {
                CreateSdTagRow(_sdTagNames[_sdTagTop + i],
                    _sdTagTop + i, y);
                y += FavTagRowH + FavTagGap;
            }
            CreateSdCreateRow(y);
            y += FavTagRowH + FavTagGap;
            if (SdTagPager)
            {
                CreateSdPagerRow(y);
                y += FavTagRowH + FavTagGap;
            }
            CreateSdStripRow(y, "SdSaveNew", "存 新",
                new Color(0.13f, 0.30f, 0.20f, 1f),
                delegate { BeginSdSaveAs(null); });
            y += FavTagRowH + FavTagGap;
            CreateSdStripRow(y, "SdSave",
                _sdSaveBrowsing ? "结束保存" : "保 存",
                new Color(0.45f, 0.28f, 0.10f, 1f),
                delegate
                {
                    if (_sdSaveBrowsing) ExitSdSaveSession();
                    else EnterSdSaveSession();
                });
            y += FavTagRowH + FavTagGap;
            CreateSdDeleteRow(y);
            y += FavTagRowH + FavTagGap;
            CreateSdStripRow(y, "SdHide", "隐 藏",
                new Color(0.16f, 0.22f, 0.30f, 1f),
                delegate { SetSdHidden(true); });
            y += FavTagRowH + FavTagGap;
            // Mode switch rows — same shared left-edge slot as the other
            // two docks; all trees stay loaded, only visibility flips.
            CreateSdStripRow(y, "SdModeFav", "服 装",
                new Color(0.14f, 0.30f, 0.24f, 1f),
                delegate { SetDockMode(0); });
            y += FavTagRowH + FavTagGap;
            CreateSdStripRow(y, "SdModePd", "预 设",
                new Color(0.20f, 0.24f, 0.34f, 1f),
                delegate { SetDockMode(1); });
            y += FavTagRowH + FavTagGap;
            _sdTagStrip.sizeDelta = new Vector2(SdStripW, y);
        }

        private static void CreateSdStripRow(float y, string name,
            string label, Color color,
            UnityEngine.Events.UnityAction action)
        {
            Image bg; Text labelText;
            CreateSdStripRow(y, name, label, color, action,
                out bg, out labelText);
        }

        private static void CreateSdStripRow(float y, string name,
            string label, Color color,
            UnityEngine.Events.UnityAction action,
            out Image bgOut, out Text labelOut)
        {
            GameObject row = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            Image bg = row.AddComponent<Image>();
            bg.color = color;
            bg.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive) return;
                VrHaptics.Press();
                action();
            });
            labelOut = AddTagLabel(rect, label, 14,
                TextAnchor.MiddleCenter, Color.white);
            bgOut = bg;
        }

        private static void CreateSdDeleteRow(float y)
        {
            Image bg; Text label;
            CreateSdStripRow(y, "SdDelete",
                _dockDeleteMode ? "结束删除" : "删 除",
                new Color(0.28f, 0.15f, 0.15f, 1f),
                delegate { ToggleDockDeleteMode(); },
                out bg, out label);
            _sdDeleteButton = bg;
            _sdDeleteLabel = label;
            PaintDockDeleteMode();
        }

        private static void AddSdCaption(string value, float y)
        {
            GameObject go = new GameObject("SdCaption",
                typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, TagCaptionH);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(1f, 1f, 1f, 0.55f);
            text.raycastTarget = false;
            text.text = value;
        }

        private static void CreateSdDefaultTagRow(float y)
        {
            GameObject row = new GameObject("SdTag 默认",
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            row.AddComponent<SdTagRowTag>().GroupIndex = -1;
            Image bg = row.AddComponent<Image>();
            bg.color = _sdTagIndex < 0
                ? new Color(0.11f, 0.38f, 0.48f, 1f)
                : new Color(0.11f, 0.20f, 0.27f, 1f);
            bg.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive) return;
                VrHaptics.Press();
                SelectSdTag(-1);
            });
            AddTagLabel(rect, "默认（" + _sdFavorites.Count + "）", 14,
                TextAnchor.MiddleLeft, Color.white);
        }

        private static void CreateSdTagRow(string name, int index, float y)
        {
            GameObject row = new GameObject("SdTag " + name,
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            row.AddComponent<SdTagRowTag>().GroupIndex = index;
            Image bg = row.AddComponent<Image>();
            bg.color = _sdTagIndex == index
                ? new Color(0.11f, 0.38f, 0.48f, 1f)
                : new Color(0.16f, 0.16f, 0.2f, 1f);
            bg.raycastTarget = true;
            int capturedIndex = index;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive) return;
                if (_sdTagEditing != null) return;
                VrHaptics.Press();
                SelectSdTag(capturedIndex);
            });
            if (_sdTagEditing == name)
            {
                _sdTagInput = CreateTagNameInput(rect,
                    SdStripW - FavTagRowH - FavTagGap);
                _sdTagInput.text = name;
                _sdTagInput.ActivateInputField();
                VrTextInputBridge.Select(_sdTagInput);
                AddTagAction(rect, "✓", SdStripW - FavTagRowH,
                    new Color(0.12f, 0.42f, 0.24f, 1f),
                    delegate { CommitSdTagRename(); });
            }
            else
            {
                List<string[]> items;
                int count = _sdTagItems.TryGetValue(name, out items) &&
                    items != null ? items.Count : 0;
                AddTagLabel(rect, name + "（" + count + "）", 14,
                    TextAnchor.MiddleLeft, Color.white);
                AddTagAction(rect, "✎",
                    SdStripW - 2f * FavTagRowH - FavTagGap,
                    new Color(0.16f, 0.24f, 0.36f, 1f),
                    delegate { BeginSdTagRename(capturedIndex); });
                AddTagAction(rect, "✕", SdStripW - FavTagRowH,
                    new Color(0.30f, 0.14f, 0.14f, 1f),
                    delegate { DeleteSdTag(capturedIndex); });
            }
        }

        private static void CreateSdCreateRow(float y)
        {
            GameObject row = new GameObject("SdTagCreate",
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            Image bg = row.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.30f, 0.20f, 1f);
            bg.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive) return;
                VrHaptics.Press();
                CreateSdTag();
            });
            AddTagLabel(rect, "＋ 新建标签", 14,
                TextAnchor.MiddleCenter, Color.white);
        }

        private static void CreateSdPagerRow(float y)
        {
            GameObject row = new GameObject("SdTagPager",
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_sdTagStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            float x = SdStripW * 0.25f;
            CreateSdNavButton(rect, "▲", -x,
                delegate { SdTagPageStep(-1); });
            CreateSdNavButton(rect, "▼", x,
                delegate { SdTagPageStep(1); });
        }

        private static void CreateSdNavButton(RectTransform parent,
            string label, float x, UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("SdNav " + label,
                typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(46f, FavNavH - 4f);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            bg.raycastTarget = true;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                action();
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
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

        private static void SdTagPageStep(int delta)
        {
            int cap = SdTagCapacity;
            int max = Mathf.Max(0, _sdTagNames.Count - cap);
            int next = Mathf.Clamp(_sdTagTop + delta * cap, 0, max);
            if (next == _sdTagTop) return;
            _sdTagTop = next;
            RefreshSdTagRows();
        }

        // Flat tab switch: -1 selects the untagged default list.
        private static void SelectSdTag(int index)
        {
            if (index < -1 || index >= _sdTagNames.Count) return;
            if (_sdTagEditing != null) CommitSdTagRename();
            if (_sdTagIndex == index) return;
            ExitSdSaveSession();
            _sdTagIndex = index;
            _sdTagActiveName = SdActiveTagName ?? "";
            _sdScrollY = 0f;
            ClearSdCellsNow();
            _sdDirty = true;
            SaveSdTags();
            RefreshSdTagRows();
            Log("场景收藏标签：" + (SdActiveTagName ?? "默认"));
        }

        private static void CreateSdTag()
        {
            LoadSdTags();
            if (_sdTagEditing != null) CommitSdTagRename();
            int n = _sdTagNames.Count + 1;
            string name = "标签" + n;
            while (_sdTagItems.ContainsKey(name))
                name = "标签" + n + "-" + (++n);
            _sdTagItems[name] = new List<string[]>();
            _sdTagNames.Add(name);
            _sdTagTop = Mathf.Max(0, _sdTagNames.Count - SdTagCapacity);
            _sdTagEditing = name;
            SaveSdTags();
            RefreshSdTagRows();
            Log("已新建场景收藏标签 " + name + "：输入名称后点 ✓ 确认。");
        }

        private static void BeginSdTagRename(int index)
        {
            if (index < 0 || index >= _sdTagNames.Count) return;
            if (_sdTagEditing != null) CommitSdTagRename();
            _sdTagEditing = _sdTagNames[index];
            RefreshSdTagRows();
        }

        private static void CommitSdTagRename()
        {
            if (_sdTagEditing == null) return;
            string old = _sdTagEditing;
            InputField input = _sdTagInput;
            _sdTagEditing = null;
            _sdTagInput = null;
            int index = _sdTagNames.IndexOf(old);
            if (index >= 0)
            {
                string name = SanitizeTagName(
                    input == null ? "" : input.text);
                if (name.Length > 0 && name != old &&
                    !_sdTagItems.ContainsKey(name))
                {
                    List<string[]> items = _sdTagItems[old];
                    _sdTagItems.Remove(old);
                    _sdTagItems[name] = items;
                    _sdTagNames[index] = name;
                    if (_sdTagIndex == index) _sdTagActiveName = name;
                    Log("场景收藏标签已命名为：" + name);
                }
            }
            SaveSdTags();
            RefreshSdTagRows();
        }

        private static void DeleteSdTag(int index)
        {
            if (index < 0 || index >= _sdTagNames.Count) return;
            string name = _sdTagNames[index];
            List<string[]> items = _sdTagItems.ContainsKey(name)
                ? _sdTagItems[name] : new List<string[]>();
            int moved = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (FindSdIndex(_sdFavorites, items[i][0]) >= 0) continue;
                _sdFavorites.Add(items[i]);
                moved++;
            }
            _sdTagItems.Remove(name);
            _sdTagNames.RemoveAt(index);
            if (_sdTagEditing == name)
            { _sdTagEditing = null; _sdTagInput = null; }
            if (_sdTagIndex == index)
            {
                _sdTagIndex = -1;
                _sdTagActiveName = "";
                ClearSdCellsNow();
                _sdDirty = true;
            }
            else if (_sdTagIndex > index) _sdTagIndex--;
            _sdScrollY = 0f;
            _sdDirty = true;
            SaveSdStores();
            RefreshSdTagRows();
            Log("已删除场景收藏标签：" + name +
                (moved > 0 ? "（" + moved + " 项已移回默认）" : ""));
        }

        // ---------- drop routing ----------

        private static bool TryGetSdTagRowGroup(out int group)
        {
            group = -1;
            if (_sdTagStrip == null ||
                !_sdTagStrip.gameObject.activeInHierarchy) return false;
            Vector2 local;
            foreach (SdTagRowTag row in _sdTagStrip
                .GetComponentsInChildren<SdTagRowTag>())
            {
                if (row != null && PointerOnRect(
                    row.transform as RectTransform, out local))
                {
                    group = row.GroupIndex;
                    return true;
                }
            }
            GameObject target =
                VrPointerPresentation.CurrentLookTarget(DragRight());
            SdTagRowTag look = target == null
                ? null : target.GetComponentInParent<SdTagRowTag>();
            if (look == null) return false;
            group = look.GroupIndex;
            return true;
        }

        private static bool PointerOverSceneDock()
        {
            return PointerOverSceneDock(DragRight());
        }

        private static bool PointerOverSceneDock(bool right)
        {
            if (_sdDock == null || !_sdDock.gameObject.activeInHierarchy ||
                _sdUserHidden)
                return false;
            GameObject t = VrPointerPresentation.CurrentLookTarget(right);
            if (t != null && t.GetComponentInParent<SdDockTag>() != null)
                return true;
            Vector2 p;
            return PointerOnRect(_sdDock, out p);
        }

        // Drop a scene source onto a tag group (preset-dock parity: COPY —
        // the source list keeps its entry). group -1 = default list.
        private static bool FileSceneFavorite(AceFavDragSource source,
            int group)
        {
            if (source == null || string.IsNullOrEmpty(source.ScenePath))
                return false;
            List<string[]> to = group < 0
                ? _sdFavorites : SdTagFavorites(group);
            if (source.FromSceneDock &&
                ReferenceEquals(to, VisibleSdFavorites)) return false;
            if (FindSdIndex(to, source.ScenePath) >= 0) return false;
            int at = FindSdIndex(VisibleSdFavorites, source.ScenePath);
            string[] entry = at >= 0 && source.FromSceneDock
                ? VisibleSdFavorites[at]
                : new string[] {
                    source.ScenePath,
                    source.DisplayName ?? "",
                    source.CreatorName ?? "" };
            to.Add(entry);
            if (source.Texture != null && !_sdThumbs.ContainsKey(entry[0]))
                _sdThumbs[entry[0]] = source.Texture;
            SaveSdStores();
            SelectSdTag(group);
            _sdScrollY = float.MaxValue;
            _sdDirty = true;
            Log("已收藏场景到 " + (group >= 0 ? _sdTagNames[group] : "默认") +
                "：" + entry[1]);
            return true;
        }

        private static bool AddSceneFavorite(AceFavDragSource source)
        {
            if (source == null || string.IsNullOrEmpty(source.ScenePath))
                return false;
            List<string[]> items = VisibleSdFavorites;
            if (FindSdIndex(items, source.ScenePath) >= 0) return true;
            items.Add(new string[] {
                source.ScenePath,
                source.DisplayName ?? "",
                source.CreatorName ?? "" });
            if (source.Texture != null &&
                !_sdThumbs.ContainsKey(source.ScenePath))
                _sdThumbs[source.ScenePath] = source.Texture;
            SaveSdStores();
            _sdScrollY = float.MaxValue;
            _sdDirty = true;
            Log("已收藏场景：" + (source.DisplayName ?? source.ScenePath));
            return true;
        }

        private static bool DeleteSceneOnClick(string path)
        {
            if (!_dockDeleteMode) return false;
            List<string[]> items = VisibleSdFavorites;
            int at = FindSdIndex(items, path);
            if (at >= 0)
            {
                items.RemoveAt(at);
                SaveSdStores();
                _sdDirty = true;
                VrHaptics.Confirm();
                Log("已从收藏移除场景：" + path + "（场景文件未动）");
            }
            return true; // Never fall through to a scene load in this mode.
        }

        // ---------- collapsed state ----------

        private static void CreateSdShowBar()
        {
            GameObject go = new GameObject("SdShow", typeof(RectTransform));
            _sdShowBar = go;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(_sdDock, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(SdStripW, FavTagRowH);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.22f, 0.30f, 1f);
            bg.raycastTarget = true;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                SetSdHidden(false);
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = "显 示";
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 14;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
            go.SetActive(false);
        }

        private static void SetSdHidden(bool hidden)
        {
            if (_sdUserHidden == hidden) return;
            _sdUserHidden = hidden;
            if (!hidden) return;
            ExitSdSaveSession();
            if (_sdTagEditing != null) CommitSdTagRename();
        }

        private static void ApplySdCollapsed()
        {
            bool expanded = !_sdUserHidden;
            if (_sdCollapsedApplied == expanded) return;
            _sdCollapsedApplied = expanded;
            if (_sdTagStrip != null)
                _sdTagStrip.gameObject.SetActive(expanded);
            if (_sdView != null)
                _sdView.gameObject.SetActive(expanded);
            if (_sdScrollTrack != null)
                _sdScrollTrack.gameObject.SetActive(expanded);
            if (_sdShowBar != null)
                _sdShowBar.SetActive(!expanded);
            if (_sdDock == null) return;
            if (expanded)
                _sdDock.sizeDelta = new Vector2(
                    FavColW + FavTagGap + SdStripW,
                    Mathf.Max(SdGridH, RequiredSdStripHeight()));
            else
                _sdDock.sizeDelta = new Vector2(
                    SdStripW + FavPad * 2f, FavTagRowH + FavPad * 2f);
        }
    }
}
