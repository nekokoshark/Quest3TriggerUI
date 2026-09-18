using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed partial class VrKeyboardOverlay
    {
        private const int ImeCandidateSlots = 9;
        private GameObject _imePanel;
        private Text _imeCompositionText;
        private Image _imeModeImage;
        private Text _imeModeText;
        private readonly GameObject[] _imeCandidateObjects = new GameObject[ImeCandidateSlots];
        private readonly Text[] _imeCandidateTexts = new Text[ImeCandidateSlots];
        private readonly Image[] _imeCandidateImages = new Image[ImeCandidateSlots];
        private int[] _imeCandidateAbsoluteIndices = new int[ImeCandidateSlots];
        private string _imeUiSignature = string.Empty;
        // Doubao bridge: the desktop candidate bar rendered as an image strip
        // hanging below the keyboard's bottom-left corner.
        private GameObject _imeImagePanel;
        private RawImage _imeImage;
        private GameObject _imePagePrev, _imePageNext;

        private void CreateImeUi(RectTransform parent)
        {
            GameObject mode = CreateUiObject("Chinese IME mode", parent);
            RectTransform modeRect = mode.GetComponent<RectTransform>();
            SetTopLeft(modeRect, 1640f, 606f, 190f, 58f);
            _imeModeImage = mode.AddComponent<Image>();
            Button modeButton = mode.AddComponent<Button>();
            modeButton.targetGraphic = _imeModeImage;
            modeButton.onClick.AddListener(ToggleImeMode);
            _imeModeText = AddText(mode.transform, "中/英：英文", 25,
                TextAnchor.MiddleCenter, Color.white, 4f);

            // Left-wing vertical candidate window — docks to the keyboard's
            // left edge (negative x sits outside the canvas rect, same trick
            // as the old above-keyboard strip) so it never collides with the
            // file browser panel overlapping the top edge.
            _imePanel = CreateUiObject("VR Chinese IME candidates", parent);
            RectTransform panelRect = _imePanel.GetComponent<RectTransform>();
            SetTopLeft(panelRect, -320f, 150f, 304f, 442f);
            Image background = _imePanel.AddComponent<Image>();
            background.color = new Color(0.035f, 0.055f, 0.075f, 0.98f);
            background.raycastTarget = false;

            GameObject composition = CreateUiObject("IME composition", panelRect);
            RectTransform compositionRect = composition.GetComponent<RectTransform>();
            SetTopLeft(compositionRect, 10f, 6f, 284f, 40f);
            _imeCompositionText = AddText(composition.transform,
                "中文输入", 25,
                TextAnchor.MiddleLeft, new Color(0.88f, 0.95f, 1f, 1f), 8f);

            const float rowHeight = 42f;
            const float gap = 4f;
            _imePagePrev = CreateImePageButton(panelRect, "‹", 10f, 50f, 62f, 34f, false);
            _imePageNext = CreateImePageButton(panelRect, "›", 232f, 50f, 62f, 34f, true);
            float y = 50f + 34f + gap;
            for (int i = 0; i < ImeCandidateSlots; i++)
            {
                int slot = i;
                GameObject item = CreateUiObject("IME candidate " + (i + 1), panelRect);
                RectTransform itemRect = item.GetComponent<RectTransform>();
                SetTopLeft(itemRect, 10f, y, 284f, rowHeight);
                _imeCandidateImages[i] = item.AddComponent<Image>();
                _imeCandidateImages[i].color = new Color(0.15f, 0.19f, 0.24f, 1f);
                Button button = item.AddComponent<Button>();
                button.targetGraphic = _imeCandidateImages[i];
                button.onClick.AddListener(delegate { ChooseImeCandidate(slot); });
                _imeCandidateTexts[i] = AddText(item.transform, string.Empty, 24,
                    TextAnchor.MiddleLeft, Color.white, 8f);
                _imeCandidateObjects[i] = item;
                y += rowHeight + gap;
            }

            // Doubao image strip — hangs below the keyboard bottom edge,
            // same off-canvas trick as the wing. Height fixed, width follows
            // the captured bar's aspect.
            _imeImagePanel = CreateUiObject("VR IME candidate bar image", parent);
            RectTransform imagePanelRect = _imeImagePanel.GetComponent<RectTransform>();
            SetTopLeft(imagePanelRect, -320f, 776f, 1100f, 170f);
            Image stripBg = _imeImagePanel.AddComponent<Image>();
            stripBg.color = new Color(0.035f, 0.055f, 0.075f, 0.96f);
            GameObject imageObj = CreateUiObject("image", imagePanelRect);
            _imeImage = imageObj.AddComponent<RawImage>();
            _imeImage.color = Color.white;
            imageObj.AddComponent<DoubaoCandClick>();
            RectTransform imageRect = imageObj.GetComponent<RectTransform>();
            SetTopLeft(imageRect, 0f, 0f, 1100f, 170f);
            _imeImagePanel.SetActive(false);

            SyncImeUi(true);
        }

        private GameObject CreateImePageButton(RectTransform parent, string label,
            float x, float y, float width, float height, bool next)
        {
            GameObject item = CreateUiObject("IME page " + label, parent);
            RectTransform rect = item.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, width, height);
            Image image = item.AddComponent<Image>();
            image.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            Button button = item.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(delegate { VrTextInputBridge.ImePage(next); });
            AddText(item.transform, label, 30, TextAnchor.MiddleCenter, Color.white, 2f);
            return item;
        }

        private void ToggleImeMode()
        {
            VrTextInputBridge.ToggleIme();
            SyncImeUi(true);
            UpdateHelpText(VrTextInputBridge.ImeEnabled
                ? "中文输入：点字母打拼音，点候选词选字；Shift 轻拍切英文；豆包语音 RAlt 仍可用。"
                : "英文输入：字母直接上屏；Shift 轻拍切回中文。");
        }

        private void TickImeUi()
        {
            if (_canvas == null) return;
            SyncImeUi(false);
        }

        private void SyncImeUi(bool force)
        {
            if (_imePanel == null) return;
            bool enabled = VrTextInputBridge.ImeEnabled;
            bool bridged = DoubaoImeBridge.Active;
            ImeCandidateSnapshot snapshot = bridged
                ? DoubaoImeBridge.Snapshot : PinyinEngine.Snapshot;
            string signature = BuildImeSignature(enabled, snapshot);
            if (!force && signature == _imeUiSignature) return;
            _imeUiSignature = signature;

            // Show the IME's real conversion mode (its internal Shift
            // toggle), not just whether the bridge is engaged.
            bool chinese = enabled && snapshot.ChineseMode;
            _imeModeImage.color = chinese
                ? new Color(0.08f, 0.68f, 0.36f, 1f)
                : new Color(0.11f, 0.38f, 0.48f, 1f);
            _imeModeText.text = chinese ? "中/英：中文" : "中/英：英文";
            _imePanel.SetActive(enabled && VrTextInputBridge.Active);
            if (!enabled) { _imeImagePanel.SetActive(false); return; }

            _imeCompositionText.text = string.IsNullOrEmpty(snapshot.Composition)
                ? (snapshot.ChineseMode
                    ? (bridged ? "豆包输入：打字后候选栏出现在下方"
                               : "中文输入：请输入拼音；点候选词完成选字")
                    : "英文模式：字母直接上屏，Shift 轻拍切回中文")
                : "正在输入：" + snapshot.Composition;

            // Bridged mode — desktop candidate bar as an image strip; the
            // text rows stay parked. Click maps back onto the real window.
            bool imageOn = bridged && snapshot.ImageMode &&
                snapshot.Image != null;
            _imeImagePanel.SetActive(imageOn);
            if (imageOn)
            {
                _imeImage.texture = snapshot.Image;
                float aspect = snapshot.ImageH > 0
                    ? (float)snapshot.ImageW / snapshot.ImageH : 6f;
                RectTransform rp = _imeImagePanel.GetComponent<RectTransform>();
                RectTransform ri = _imeImage.GetComponent<RectTransform>();
                float hh = 170f, ww = Mathf.Clamp(hh * aspect, 200f, 1400f);
                rp.sizeDelta = new Vector2(ww, hh);
                ri.sizeDelta = new Vector2(ww, hh);
                for (int i = 0; i < ImeCandidateSlots; i++)
                    _imeCandidateObjects[i].SetActive(false);
                _imePagePrev.SetActive(false);
                _imePageNext.SetActive(false);
                return;
            }
            _imePagePrev.SetActive(true);
            _imePageNext.SetActive(true);

            int start = Math.Max(0, snapshot.PageStart);
            int count = snapshot.Candidates == null ? 0 : snapshot.Candidates.Count;
            int pageSize = snapshot.PageSize > 0 ? snapshot.PageSize : ImeCandidateSlots;
            for (int i = 0; i < ImeCandidateSlots; i++)
            {
                int absolute = start + i;
                bool visible = i < pageSize && absolute < count;
                _imeCandidateObjects[i].SetActive(visible);
                _imeCandidateAbsoluteIndices[i] = absolute;
                if (!visible) continue;
                _imeCandidateTexts[i].text = (i + 1) + "  " + snapshot.Candidates[absolute];
                _imeCandidateImages[i].color = absolute == snapshot.Selection
                    ? new Color(0.84f, 0.52f, 0.08f, 1f)
                    : new Color(0.15f, 0.19f, 0.24f, 1f);
            }
        }

        private void ChooseImeCandidate(int slot)
        {
            if (slot < 0 || slot >= ImeCandidateSlots ||
                !_imeCandidateObjects[slot].activeSelf) return;
            VrTextInputBridge.SelectImeCandidate(slot, _imeCandidateAbsoluteIndices[slot]);
        }

        private static readonly StringBuilder SignatureBuilder = new StringBuilder();
        private static string BuildImeSignature(bool enabled, ImeCandidateSnapshot snapshot)
        {
            StringBuilder value = SignatureBuilder;
            value.Length = 0;
            value.Append(enabled ? '1' : '0').Append('|')
                .Append(VrTextInputBridge.Active ? '1' : '0').Append('|')
                .Append(snapshot.ChineseMode ? '1' : '0').Append('|')
                .Append(snapshot.ImageMode ? '1' : '0').Append('|')
                .Append(snapshot.ImageStamp).Append('|')
                .Append(snapshot.Composition).Append('|')
                .Append(snapshot.Selection).Append('|')
                .Append(snapshot.PageStart).Append('|').Append(snapshot.PageSize);
            for (int i = 0; i < snapshot.Candidates.Count; i++)
                value.Append('|').Append(snapshot.Candidates[i]);
            return value.ToString();
        }
    }

    // Click on the captured candidate image maps onto the real DirectUI
    // candidate window at the same client position — Doubao handles the
    // hit-test itself, so we never need the candidate text.
    internal sealed class DoubaoCandClick : MonoBehaviour, IPointerClickHandler
    {
        private RectTransform _rect;

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_rect == null) return;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _rect, eventData.position, eventData.pressEventCamera,
                    out local)) return;
            Rect r = _rect.rect;
            float u = (local.x - r.x) / r.width;
            float v = (local.y - r.y) / r.height;
            DoubaoImeBridge.ClickCandidate(u, v);
        }
    }
}
