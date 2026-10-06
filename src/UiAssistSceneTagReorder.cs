using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace Quest3TriggerUI
{
    internal static class SceneTagPageOrder
    {
        internal const int Capacity = 16;
        internal static int Top(int value, int count)
        {
            int last = count <= 0 ? 0 : ((count - 1) / Capacity) * Capacity;
            return Math.Max(0, Math.Min(last, (Math.Max(0, value) / Capacity) * Capacity));
        }
        internal static int Step(int top, int count, int delta) { return Top(top + delta * Capacity, count); }
        internal static bool Move(List<string> names, string name, int insertion)
        {
            int from = names.IndexOf(name);
            if (from < 0) return false;
            int to = Math.Max(0, Math.Min(names.Count, insertion));
            if (from < to) to--;
            if (from == to) return false;
            names.RemoveAt(from); names.Insert(to, name); return true;
        }
    }
    internal static partial class UiAssistHudLink
    {
        private static string _sdDraggingTag;
        private static RectTransform _sdPrevPage, _sdNextPage;
        private static float _sdPageHoverAt;
        private static int _sdPageHoverDirection;
        private static AceFavDragSource ResolveSdTagDrag(GameObject target)
        {
            if (_dockMode != 2 || _sdTagEditing != null || _dockDeleteMode) return null;
            SdTagRowTag tag = target.GetComponentInParent<SdTagRowTag>();
            // Only the row body owns a long press; edit/delete sub-buttons remain clickable.
            Button hit = target.GetComponentInParent<Button>();
            if (tag == null || hit == null || hit.gameObject != tag.gameObject || tag.GroupIndex < 0 || tag.GroupIndex >= _sdTagNames.Count) return null;
            return new AceFavDragSource { SceneTagName = _sdTagNames[tag.GroupIndex] };
        }
        private static bool BeginSdTagDrag(AceFavDragSource source)
        {
            _sdDraggingTag = source.SceneTagName;
            _sdPageHoverDirection = 0;
            VrHaptics.Press();
            RefreshSdTagRows();
            return true;
        }
        private static void EndSdTagDrag()
        {
            if (_sdDraggingTag == null) return;
            SaveSdTags();
            _sdDraggingTag = null; _sdPageHoverDirection = 0;
            _favoriteClickAfter = Time.unscaledTime + 0.3f;
            RefreshSdTagRows();
        }
        private static void TickSdTagDrag()
        {
            if (_sdDraggingTag == null || _sdTagStrip == null || !_sdTagStrip.gameObject.activeInHierarchy) return;
            Vector2 p;
            int dir = PointerOnRect(_sdPrevPage, out p) ? -1 : PointerOnRect(_sdNextPage, out p) ? 1 : 0;
            if (dir != 0)
            {
                if (_sdPageHoverDirection != dir)
                {
                    _sdPageHoverDirection = dir; _sdPageHoverAt = Time.unscaledTime + 0.55f;
                    return;
                }
                if (Time.unscaledTime < _sdPageHoverAt) return;
                _sdPageHoverAt = Time.unscaledTime + 0.8f;
                int next = SceneTagPageOrder.Step(_sdTagTop, _sdTagNames.Count, dir);
                if (next == _sdTagTop) return;
                // Insert at the incoming page's first slot, using full-list gap
                // coordinates so removal before that page does not displace it.
                int from = _sdTagNames.IndexOf(_sdDraggingTag);
                int gap = next + (from < next ? 1 : 0);
                MoveSdTag(gap);
                _sdTagTop = next;
                RefreshSdTagRows();
                return;
            }
            _sdPageHoverDirection = 0;
            if (!PointerOnRect(_sdTagStrip, out p)) return;
            float rowY = -p.y - (TagCaptionH + FavTagGap + FavTagRowH + FavTagGap);
            int visible = SdTagVisibleCount;
            if (rowY < 0f || rowY >= visible * (FavTagRowH + FavTagGap)) return;
            int slot = Mathf.FloorToInt(rowY / (FavTagRowH + FavTagGap));
            if (rowY % (FavTagRowH + FavTagGap) > FavTagRowH * 0.5f) slot++;
            if (MoveSdTag(_sdTagTop + slot)) RefreshSdTagRows();
        }
        private static bool MoveSdTag(int gap)
        {
            string selected = SdActiveTagName;
            if (!SceneTagPageOrder.Move(_sdTagNames, _sdDraggingTag, gap)) return false;
            _sdTagIndex = string.IsNullOrEmpty(selected) ? -1 : _sdTagNames.IndexOf(selected);
            return true;
        }
    }
}
