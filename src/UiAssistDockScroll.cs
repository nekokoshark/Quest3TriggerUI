using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        // ---------- dock vertical scrolling ----------
        // Both side bars keep their full item list live inside a RectMask2D
        // viewport; content slides by setting anchoredPosition.y directly.
        // Unity's ScrollRect gets no usable input from VaM's LookInputModule,
        // so scrolling is driven manually: thumbstick or mouse wheel while
        // the pointer rests on the dock, plus edge auto-scroll while a
        // thumbnail drag hovers the viewport's top/bottom strip.
        private const float DockScrollSpeed = 700f;   // px/s at full deflection
        private const float DockEdgeZone = 30f;       // px edge strip during drag
        private const float DockScrollBarW = 8f;
        private const float DockWheelStep = 200f;     // px per wheel notch
        private const float DockStickDeadzone = 0.15f;

        private static float _favScrollY, _favContentH;
        private static RectTransform _favView;
        private static RectTransform _favScrollTrack, _favScrollThumb;
        private static int _favScrollFrame = -1;
        private static float _pdScrollY, _pdContentH;
        private static RectTransform _pdView;
        private static RectTransform _pdScrollTrack, _pdScrollThumb;
        private static int _pdScrollFrame = -1;

        // While our scroll owns the stick, VaM scene navigation must not
        // also react to it (same suppression the preset browser uses).
        private static bool _dockNavSuppressed;
        private static bool _dockNavPrev;
        private static float _dockNavDemandAt = -10f;

        private static float DockMaxScroll(float contentH, float viewH)
        {
            return Mathf.Max(0f, contentH - viewH);
        }

        // A cell row is "on screen" when it intersects the viewport plus one
        // row of prefetch margin. Row order equals sibling order: the grid
        // lays cells 0..n and reuse keeps sibling indices == display index.
        private static bool DockCellVisible(int siblingIndex, float cellH,
            float scrollY, float viewH)
        {
            float stride = cellH + FavSpacing;
            float top = (siblingIndex / FavColumns) * stride;
            return top < scrollY + viewH + stride &&
                top + cellH > scrollY - stride;
        }

        private static void ApplyDockScroll(bool fav, float scrollY)
        {
            if (fav)
            {
                _favScrollY = Mathf.Clamp(scrollY, 0f,
                    DockMaxScroll(_favContentH, FavGridH));
                if (_favCells != null)
                    _favCells.anchoredPosition = new Vector2(0f, _favScrollY);
                UpdateDockScrollbar(true);
                PumpVisibleFavVisuals();
                UpdateFavCellRaycasts();
            }
            else
            {
                float clamped = Mathf.Clamp(scrollY, 0f,
                    DockMaxScroll(_pdContentH, PdGridH));
                bool moved = clamped != _pdScrollY;
                _pdScrollY = clamped;
                if (_pdCells != null)
                    _pdCells.anchoredPosition = new Vector2(0f, _pdScrollY);
                UpdateDockScrollbar(false);
                PumpVisiblePdVisuals();
                UpdatePdCellRaycasts();
                // A scrolled-off cell's 替换/外观 overlay would stay hittable
                // but invisible — collapse it when the list actually moves.
                if (moved && _pdPersonOverlay != null &&
                    _pdPersonOverlay.activeSelf)
                    _pdPersonOverlay.SetActive(false);
            }
        }

        private static void UpdateDockScrollbar(bool fav)
        {
            RectTransform track = fav ? _favScrollTrack : _pdScrollTrack;
            RectTransform thumb = fav ? _favScrollThumb : _pdScrollThumb;
            if (track == null || thumb == null) return;
            float viewH = fav ? FavGridH : PdGridH;
            float contentH = fav ? _favContentH : _pdContentH;
            float scrollY = fav ? _favScrollY : _pdScrollY;
            bool need = contentH > viewH + 0.5f;
            if (track.gameObject.activeSelf != need)
                track.gameObject.SetActive(need);
            if (!need) return;
            float thumbH = Mathf.Max(28f, viewH * viewH / contentH);
            float travel = viewH - thumbH;
            float maxScroll = DockMaxScroll(contentH, viewH);
            float t = maxScroll > 0.01f ? scrollY / maxScroll : 0f;
            thumb.sizeDelta = new Vector2(DockScrollBarW, thumbH);
            thumb.anchoredPosition = new Vector2(0f, -travel * t);
        }

        // Per-frame driver, shared by the bar tick (hover scroll) and the
        // drag tick (stick + edge scroll). Deduped per frame: whichever path
        // runs first handles the frame — both resolve drag state the same.
        private static void TickDockScroll(bool fav)
        {
            int frame = Time.frameCount;
            if (fav)
            {
                if (_favScrollFrame == frame) return;
                _favScrollFrame = frame;
            }
            else
            {
                if (_pdScrollFrame == frame) return;
                _pdScrollFrame = frame;
            }
            RectTransform view = fav ? _favView : _pdView;
            RectTransform content = fav ? _favCells : _pdCells;
            RectTransform dock = fav ? _favDock : _pdDock;
            if (view == null || content == null || dock == null) return;
            float viewH = fav ? FavGridH : PdGridH;
            float contentH = fav ? _favContentH : _pdContentH;
            float scrollY = fav ? _favScrollY : _pdScrollY;
            float maxScroll = DockMaxScroll(contentH, viewH);
            if (scrollY > maxScroll || scrollY < 0f)
            {
                ApplyDockScroll(fav, scrollY);
                scrollY = fav ? _favScrollY : _pdScrollY;
            }
            if (maxScroll <= 0.01f) return;

            float delta = 0f;
            Vector2 stick = Quest3TriggerUIPlugin.SampledRightStick;
            bool dragging = Quest3TriggerUIPlugin.ClothingDragActive;
            if (dragging)
            {
                // The dragging hand's cursor is the live pointer. The dock
                // rect is a hard gate — the plane projection used below hits
                // for ANY ray, so without it a distant cursor could land in
                // an edge zone and phantom-scroll the list.
                Vector2 lp;
                bool overDock = PointerOnRect(dock, out lp);
                if (overDock && Mathf.Abs(stick.y) > DockStickDeadzone)
                    delta -= stick.y * DockScrollSpeed * Time.unscaledDeltaTime;
                Vector2 lv;
                if (overDock && DockPointerLocal(view, out lv) &&
                    lv.x > -20f && lv.x < view.rect.width + 20f)
                {
                    // Depth = how far past the zone's inner boundary the
                    // cursor sits; 0 at the boundary, 1 at the edge and
                    // beyond (dragging slightly past the edge scrolls at
                    // full speed, like a phone).
                    if (lv.y > -DockEdgeZone)
                    {
                        float depth = Mathf.Max(
                            1f - Mathf.Clamp01(-lv.y / DockEdgeZone), 0.25f);
                        delta -= DockScrollSpeed * depth *
                            Time.unscaledDeltaTime;
                    }
                    else if (lv.y < -viewH + DockEdgeZone)
                    {
                        float depth = Mathf.Max(
                            1f - Mathf.Clamp01(
                                (lv.y + viewH) / DockEdgeZone), 0.25f);
                        delta += DockScrollSpeed * depth *
                            Time.unscaledDeltaTime;
                    }
                }
            }
            else if (DockPointerRestsOn(dock))
            {
                if (Mathf.Abs(stick.y) > DockStickDeadzone)
                    delta -= stick.y * DockScrollSpeed * Time.unscaledDeltaTime;
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.001f)
                    delta -= wheel * DockWheelStep;
            }
            if (delta == 0f) return;
            _dockNavDemandAt = Time.unscaledTime;
            ApplyDockScroll(fav, scrollY + delta);
        }

        // Hover test for the non-drag path: look targets track reliably
        // without a press (the cursor-based path is reserved for drags).
        private static bool DockPointerRestsOn(RectTransform dock)
        {
            if (dock == null) return false;
            for (int h = 0; h < 2; h++)
            {
                GameObject look =
                    VrPointerPresentation.CurrentLookTarget(h == 0);
                if (look != null && look.transform.IsChildOf(dock.transform))
                    return true;
            }
            return false;
        }

        private static void UpdateFavCellRaycasts()
        {
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag == null || tag.Bg == null) continue;
                bool vis = DockCellVisible(tag.transform.GetSiblingIndex(),
                    FavCellH, _favScrollY, FavGridH);
                if (tag.Bg.raycastTarget != vis) tag.Bg.raycastTarget = vis;
            }
        }

        private static void UpdatePdCellRaycasts()
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag == null || tag.Bg == null) continue;
                bool vis = DockCellVisible(tag.transform.GetSiblingIndex(),
                    PdCellH, _pdScrollY, PdGridH);
                if (tag.Bg.raycastTarget != vis) tag.Bg.raycastTarget = vis;
            }
        }

        // Thumbnails stay lazy: only cells near the viewport enter the
        // 2-per-frame decode queue — a long list no longer decodes hundreds
        // of offscreen images just because the rebuild ran.
        private static void PumpVisibleFavVisuals()
        {
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag != null && DockCellVisible(
                    tag.transform.GetSiblingIndex(),
                    FavCellH, _favScrollY, FavGridH))
                    QueueFavoriteVisual(tag);
            }
        }

        private static void PumpVisiblePdVisuals()
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null && DockCellVisible(
                    tag.transform.GetSiblingIndex(),
                    PdCellH, _pdScrollY, PdGridH))
                    ApplyPdThumb(tag);
            }
        }

        // Scroll consumption must not double as scene navigation. Demand is
        // timestamped; the Observe tick releases suppression 0.25s after the
        // last scroll so a hidden dock can never leave nav locked on.
        private static void TickDockNavSuppression()
        {
            bool want = Time.unscaledTime - _dockNavDemandAt < 0.25f;
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            if (want && !_dockNavSuppressed)
            {
                _dockNavPrev = sc.disableAllNavigation;
                sc.disableAllNavigation = true;
                _dockNavSuppressed = true;
            }
            else if (!want && _dockNavSuppressed)
            {
                sc.disableAllNavigation = _dockNavPrev;
                _dockNavSuppressed = false;
            }
        }

        private static void ReleaseDockNavSuppression()
        {
            _dockNavDemandAt = -10f;
            if (!_dockNavSuppressed) return;
            _dockNavSuppressed = false;
            SuperController sc = SuperController.singleton;
            if (sc != null) sc.disableAllNavigation = _dockNavPrev;
        }

        // Thin indicator bar docked to the viewport's right edge — a visual
        // position cue like the browser's VScroll; input stays stick/wheel.
        private static void CreateDockScrollbar(
            Transform parent, float rightEdgeX, float topY, float viewH,
            out RectTransform trackRect, out RectTransform thumbRect)
        {
            GameObject track = new GameObject("VScroll", typeof(RectTransform));
            trackRect = (RectTransform)track.transform;
            trackRect.SetParent(parent, false);
            trackRect.anchorMin = new Vector2(0f, 1f);
            trackRect.anchorMax = new Vector2(0f, 1f);
            trackRect.pivot = new Vector2(0f, 1f);
            trackRect.anchoredPosition = new Vector2(
                rightEdgeX - DockScrollBarW - 2f, topY);
            trackRect.sizeDelta = new Vector2(DockScrollBarW, viewH);
            Image trackBg = track.AddComponent<Image>();
            trackBg.color = new Color(0f, 0f, 0f, 0.30f);
            trackBg.raycastTarget = false;

            GameObject thumb = new GameObject("Thumb", typeof(RectTransform));
            thumbRect = (RectTransform)thumb.transform;
            thumbRect.SetParent(trackRect, false);
            thumbRect.anchorMin = new Vector2(0f, 1f);
            thumbRect.anchorMax = new Vector2(0f, 1f);
            thumbRect.pivot = new Vector2(0f, 1f);
            thumbRect.anchoredPosition = Vector2.zero;
            thumbRect.sizeDelta = new Vector2(DockScrollBarW, 28f);
            Image thumbImg = thumb.AddComponent<Image>();
            thumbImg.color = new Color(0.55f, 0.62f, 0.72f, 0.9f);
            thumbImg.raycastTarget = false;
            track.SetActive(false);
        }
    }
}
