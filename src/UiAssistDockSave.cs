using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Dock save interactions are scoped to the save browser being open:
    // 保存 opens the dialog as before; while it is up, clicking a cell's
    // thumbnail shows a 拍照覆盖/仅覆盖/另存为 overlay (overwrite the .vap
    // that cell points at), and clicking its name strip opens an inline
    // rename field driven by the VR keyboard.
    internal static partial class UiAssistHudLink
    {
        // True for the lifetime of the dock-initiated save dialog.
        private static bool _pdSaveBrowsing;

        // Save mode is a session: entering it is mutually exclusive with
        // delete mode, and every other dock mode (新增/读取/删除/dock
        // teardown) must exit it so cell clicks fall back to their normal
        // meaning. The overlay and name-strip rename die with the session —
        // they only make sense while the save browser is still up.
        private static void EnterPdSaveSession()
        {
            if (_dockDeleteMode)
            {
                _dockDeleteMode = false;
                PaintDockDeleteMode();
            }
            // Re-entry (the save browser retargeted onto a new tab) must
            // not inherit an overlay bound to a cell that just got parked.
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            _pdSaveBrowsing = true;
            PaintDockSaveMode();
        }

        private static void ExitPdSaveSession()
        {
            _pdSaveBrowsing = false;
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            CancelDockRename();
            PaintDockSaveMode();
        }

        private static void PaintDockSaveMode()
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null && tag.NameHit != null)
                    tag.NameHit.raycastTarget = _pdSaveBrowsing;
            }
        }

        // ---------- thumbnail zone: overwrite save ----------

        private static GameObject _pdSaveOverlay;
        private static PdSlotTag _pdSaveTag;

        // Returns true while save mode owns the click: the cell shows the
        // overwrite overlay instead of applying/person choices.
        private static bool SavePresetOnClick(PdSlotTag tag, RectTransform cell)
        {
            if (!_pdSaveBrowsing || tag == null || cell == null) return false;
            TogglePdSaveOverlay(tag, cell);
            return true;
        }

        private static void TogglePdSaveOverlay(PdSlotTag tag, RectTransform cell)
        {
            if (_pdSaveOverlay != null && _pdSaveOverlay.activeSelf &&
                _pdSaveOverlay.transform.parent == cell && _pdSaveTag == tag)
            {
                _pdSaveOverlay.SetActive(false);
                _pdSaveTag = null;
                return;
            }
            EnsurePdSaveOverlay();
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            CancelDockRename();
            _pdSaveTag = tag;
            RectTransform rt = (RectTransform)_pdSaveOverlay.transform;
            rt.SetParent(cell, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-4f, -4f);
            rt.offsetMax = new Vector2(4f, 4f);
            rt.SetAsLastSibling();
            _pdSaveOverlay.SetActive(true);
            VrHaptics.Press();
        }

        private static void EnsurePdSaveOverlay()
        {
            if (_pdSaveOverlay != null) return;
            GameObject go = new GameObject("PdSaveOverlay", typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            go.AddComponent<PdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.78f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;
            CreatePdSaveButton(rt, "拍 照", new Vector2(-21f, 14f),
                new Vector2(40f, 30f), new Color(0.14f, 0.42f, 0.22f, 1f),
                delegate { DockOverwriteClicked(true); });
            CreatePdSaveButton(rt, "覆 盖", new Vector2(21f, 14f),
                new Vector2(40f, 30f), new Color(0.55f, 0.32f, 0.10f, 1f),
                delegate { DockOverwriteClicked(false); });
            CreatePdSaveButton(rt, "另 存", new Vector2(0f, -20f),
                new Vector2(84f, 22f), new Color(0.13f, 0.24f, 0.32f, 1f),
                delegate
                {
                    if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
                    _pdSaveTag = null;
                    // The save dialog is already what opened this window —
                    // re-point it at a fresh-name save for this tab.
                    OpenDockPresetSaver();
                });
            _pdSaveOverlay = go;
            go.SetActive(false);
        }

        private static void CreatePdSaveButton(RectTransform parent,
            string label, Vector2 center, Vector2 size, Color color,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("PdSave " + label,
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
            Image bg = go.AddComponent<Image>();
            bg.color = color;
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
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 13;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        private static void DockOverwriteClicked(bool photo)
        {
            PdSlotTag tag = _pdSaveTag;
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            if (tag == null) return;
            SuperController sc = SuperController.singleton;
            // The atom being saved must be the one the user is looking at:
            // the save browser's own dropdown pick first, then the dock's
            // target row, then the editor snapshot. The old code serialized
            // only the editor atom — a dropdown re-pick or a pooled-out
            // snapshot target silently wrote a different character's state.
            Atom atom = VrPresetBrowser.IsOpen
                ? VrPresetBrowser.LoadTarget : null;
            if (atom == null) atom = PdEffectiveTarget();
            if (atom == null)
            {
                Snapshot state = FindEditor(sc);
                if ((state == null || state.Target == null) && _presetBrowsing)
                    state = _presetState;
                atom = state == null ? null : state.Target;
            }
            // A stale snapshot can hold a pooled-out atom — serializing it
            // produces exactly the "old clothing" overwrite the user saw.
            if (atom == null || sc.GetAtomByUid(atom.uid) != atom)
            { Log("保存预设失败：未找到编辑中的角色。"); return; }
            try
            {
                if (_pdQuick == null)
                    _pdQuick = new SceneQuickActions(
                        Quest3TriggerUIPlugin.Instance);
                // 拍照 enters VaM's own aim-and-select screenshot pass;
                // its completion callback evicts our cached thumbnail.
                // 覆盖 writes the .vap only and keeps the existing jpg.
                if (!_pdQuick.DockStorePreset(atom, _pdTab, tag.Path,
                        photo, InvalidatePdThumb))
                    return;
                VrHaptics.Confirm();
            }
            catch (Exception e) { Error(e); }
        }

        // A preset's sidecar jpg moved (native screenshot, browser save,
        // rename): drop the decoded thumbnail so cells re-read the fresh
        // file. Safe to call for paths the dock never cached.
        internal static void InvalidatePdThumb(string vapPath)
        {
            if (string.IsNullOrEmpty(vapPath)) return;
            _pdThumbStamp.Remove(vapPath);
            Texture2D old;
            if (_pdThumbs.TryGetValue(vapPath, out old))
            {
                _pdThumbs.Remove(vapPath);
                if (old != null) UnityEngine.Object.Destroy(old);
            }
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag cell = _pdVisibleCells[i];
                if (cell == null || cell.Path != vapPath) continue;
                if (cell.Thumb != null) cell.Thumb.texture = null;
                ApplyPdThumb(cell);
            }
        }

        // ---------- name zone: inline rename ----------

        private static GameObject _pdRenameOverlay;
        private static InputField _pdRenameInput;
        private static string _pdRenamePath;

        private static void BeginDockRename(PdSlotTag tag)
        {
            if (!_pdSaveBrowsing || tag == null || string.IsNullOrEmpty(tag.Path))
                return;
            if (FileManager.IsPackagePath(tag.Path))
            { Log("VAR 包内预设不能改名：" + tag.Path); return; }
            EnsurePdRenameOverlay();
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            _pdRenamePath = tag.Path;
            RectTransform rt = (RectTransform)_pdRenameOverlay.transform;
            rt.SetParent(tag.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            // Slightly oversize the cell so the input stays readable.
            rt.offsetMin = new Vector2(-6f, -6f);
            rt.offsetMax = new Vector2(6f, 6f);
            rt.SetAsLastSibling();
            string leaf = tag.Path;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            _pdRenameInput.text = PresetFilenameRules.EditName(leaf);
            _pdRenameOverlay.SetActive(true);
            _pdRenameInput.ActivateInputField();
            VrTextInputBridge.Select(_pdRenameInput);
            VrHaptics.Press();
        }

        private static void EnsurePdRenameOverlay()
        {
            if (_pdRenameOverlay != null) return;
            GameObject go = new GameObject("PdRenameOverlay",
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            go.AddComponent<PdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;

            GameObject inputGo = new GameObject("Input", typeof(RectTransform));
            RectTransform ir = (RectTransform)inputGo.transform;
            ir.SetParent(rt, false);
            ir.anchorMin = new Vector2(0f, 0.5f);
            ir.anchorMax = new Vector2(1f, 1f);
            ir.offsetMin = new Vector2(2f, -4f);
            ir.offsetMax = new Vector2(-2f, -2f);
            Image inputBg = inputGo.AddComponent<Image>();
            inputBg.color = new Color(0.17f, 0.19f, 0.24f, 1f);
            Text inputText = new GameObject("Text", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform itr = (RectTransform)inputText.transform;
            itr.SetParent(ir, false);
            itr.anchorMin = Vector2.zero;
            itr.anchorMax = Vector2.one;
            itr.offsetMin = new Vector2(3f, 0f);
            itr.offsetMax = new Vector2(-3f, 0f);
            inputText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            inputText.fontSize = 13;
            inputText.alignment = TextAnchor.MiddleLeft;
            inputText.color = Color.white;
            inputText.verticalOverflow = VerticalWrapMode.Overflow;
            inputText.raycastTarget = false;
            InputField input = inputGo.AddComponent<InputField>();
            input.targetGraphic = inputBg;
            input.textComponent = inputText;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 60;
            _pdRenameInput = input;

            CreatePdSaveButton(rt, "✓", new Vector2(-20f, -14f),
                new Vector2(38f, 22f), new Color(0.14f, 0.45f, 0.20f, 1f),
                CommitDockRename);
            CreatePdSaveButton(rt, "✗", new Vector2(20f, -14f),
                new Vector2(38f, 22f), new Color(0.45f, 0.16f, 0.14f, 1f),
                CancelDockRename);
            _pdRenameOverlay = go;
            go.SetActive(false);
        }

        private static void CommitDockRename()
        {
            string path = _pdRenamePath;
            string typed = _pdRenameInput == null ? "" : _pdRenameInput.text;
            if (_pdRenameOverlay != null) _pdRenameOverlay.SetActive(false);
            _pdRenamePath = null;
            VrTextInputBridge.Clear();
            if (string.IsNullOrEmpty(path)) return;
            string leaf = PresetFilenameRules.FileName(typed);
            if (leaf.Length == 0) return;
            string dir = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0) dir = path.Substring(0, slash);
            string dst = dir + "/" + leaf;
            if (string.Equals(dst, path, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                FileManager.MoveFile(path, dst, false);
                string srcJpg = path.Substring(0, path.Length - ".vap".Length) + ".jpg";
                string dstJpg = dst.Substring(0, dst.Length - ".vap".Length) + ".jpg";
                try
                {
                    if (FileManager.FileExists(srcJpg, false, false))
                        FileManager.MoveFile(srcJpg, dstJpg, false);
                }
                catch { }
                NotifyDockPresetMoved(path, dst, false);
                VrHaptics.Confirm();
            }
            catch (Exception e)
            {
                Log("预设改名失败：" + e.Message);
            }
        }

        private static void CancelDockRename()
        {
            if (_pdRenameOverlay != null) _pdRenameOverlay.SetActive(false);
            _pdRenamePath = null;
            if (_pdRenameInput != null) VrTextInputBridge.Clear();
        }
    }
}
