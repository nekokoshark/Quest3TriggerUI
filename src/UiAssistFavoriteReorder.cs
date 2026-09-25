using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        // Page-flip buttons on either dock carry one of these; a held drag
        // hovering them turns pages after a short dwell (the phone-icon
        // "drag to screen edge" equivalent). The same tag serves both docks.
        private sealed class FavoritePageTarget : MonoBehaviour
        {
            internal int Direction;
        }

        // Right favorites bar reorder state.
        private static List<string[]> _favoriteDragList;
        private static string _favoriteDragUid;
        private static int _favoriteDropIndex = -1;
        private static int _favoritePageDirection;
        private static float _favoritePageAt, _favoriteClickAfter;
        private static bool _favPtrLogged;
        private static readonly List<FavoritePageTarget> _favoritePageTargets =
            new List<FavoritePageTarget>();

        // Left preset dock reorder state — same shape as the favorites side.
        private static List<string> _pdDragList;
        private static string _pdDragPath;
        private static int _pdDropIndex = -1;
        private static int _pdPageDirection;
        private static float _pdPageAt;
        private static bool _pdPtrLogged;
        private static readonly List<FavoritePageTarget> _pdPageTargets =
            new List<FavoritePageTarget>();

        private static int FavoriteCapacity()
        {
            return FavPageSize;
        }

        // ---------- shared pointer → grid plumbing ----------

        // The pointer position that actually tracks mid-press: the laser
        // cursor's world transform IS the UI hit point, and VaM keeps moving
        // it under a held trigger — whereas lookDataRight.position freezes at
        // press time (that frozen event ray is why every Contains gate used
        // to report "outside" during drags). The pointer ray runs
        // controller→cursor so an aim that lands on a nearer/farther panel
        // still projects onto the dock plane where the user points.
        private static bool DragRight()
        {
            AceFavDragSource source = Quest3TriggerUIPlugin.ClothingDragCandidate;
            return source == null || source.RightPointer;
        }

        private static bool DragCursorWorld(out Vector3 pos)
        {
            pos = Vector3.zero;
            RectTransform cursor =
                VrPointerPresentation.CurrentCursor(DragRight());
            if (cursor == null || !cursor.gameObject.activeInHierarchy)
                return false;
            pos = cursor.position;
            return true;
        }

        private static Vector3? DockPointerWorld(RectTransform rect)
        {
            Vector3 cursor;
            if (rect == null || !rect.gameObject.activeInHierarchy ||
                !DragCursorWorld(out cursor))
                return null;
            Plane plane = new Plane(rect.forward, rect.position);
            Transform hand = VrPointerPresentation.MotionController(
                SuperController.singleton, DragRight());
            if (hand != null)
            {
                Vector3 dir = cursor - hand.position;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    float distance;
                    Ray ray = new Ray(hand.position, dir);
                    if (plane.Raycast(ray, out distance))
                        return ray.GetPoint(distance);
                }
            }
            // No hand transform: accept the cursor itself when it already
            // sits on our plane (i.e. the laser is hitting this dock).
            if (Mathf.Abs(plane.GetDistanceToPoint(cursor)) < 0.02f)
                return plane.ClosestPointOnPlane(cursor);
            return null;
        }

        private static bool DockPointerLocal(RectTransform rect, out Vector2 local)
        {
            local = Vector2.zero;
            Vector3? world = DockPointerWorld(rect);
            if (!world.HasValue) return false;
            local = rect.InverseTransformPoint(world.Value);
            return true;
        }

        // "Is the pointer actually over this rect" — nav buttons, tab strip
        // rows, dock bounds. Dead-zone-free index math uses DockPointerLocal
        // directly (the grid clamps any plane hit into the nearest slot).
        private static bool PointerOnRect(RectTransform rect, out Vector2 local)
        {
            return DockPointerLocal(rect, out local) && rect.rect.Contains(local);
        }

        // Shared grid math for both docks. Returns the insertion index into
        // the FULL list with the dragged entry still counted — i.e. the gap
        // the pointer hovers over (0..count). The local point clamps into
        // the grid bounds so anywhere inside the dock maps to the nearest
        // slot; past a cell's horizontal center inserts AFTER it, which is
        // what makes dropping at the very end possible. Indices are global:
        // pushing past the page boundary lands the entry on the next page.
        private static int GridInsertIndex(Vector2 p, RectTransform cells,
            int page, int capacity, int count)
        {
            if (cells == null || count <= 0) return -1;
            GridLayoutGroup grid = cells.GetComponent<GridLayoutGroup>();
            float left = grid != null ? grid.padding.left : FavPad;
            float right = grid != null ? grid.padding.right : FavPad;
            float top = grid != null ? grid.padding.top : 0f;
            float bottom = grid != null ? grid.padding.bottom : FavPad;
            float x = Mathf.Clamp(p.x - left, 0f,
                Mathf.Max(0f, cells.rect.width - left - right - 1f));
            float y = Mathf.Clamp(-p.y - top, 0f,
                Mathf.Max(0f, cells.rect.height - top - bottom - 1f));
            int col = Mathf.Clamp(
                Mathf.FloorToInt(x / (FavCellW + FavSpacing)), 0, FavColumns - 1);
            int row = Mathf.Max(0,
                Mathf.FloorToInt(y / (FavCellH + FavSpacing)));
            int start = page * capacity;
            int pageCount = Mathf.Clamp(count - start, 0, capacity);
            int idx = Mathf.Min(row * FavColumns + col, pageCount);
            if (idx < pageCount &&
                x - col * (FavCellW + FavSpacing) > (FavCellW + FavSpacing) * 0.5f)
                idx++;
            return Mathf.Clamp(start + idx, 0, count);
        }

        // ---------- right favorites bar ----------

        private static void BeginFavoriteReorder(AceFavDragSource source)
        {
            ClearFavoriteReorder();
            if (!source.FromBar) return;
            _favoriteDragList = VisibleFavorites;
            _favoriteDragUid = source.Uid;
            _favPtrLogged = false;
            Log("fav drag begin uid=" + source.Uid + " page=" + (_favPage + 1));
        }

        private static void ClearFavoriteReorder()
        {
            if (_favoriteDropIndex >= 0) _favPreviewDirty = true;
            _favoriteDragList = null;
            _favoriteDragUid = null;
            _favoriteDropIndex = -1;
            _favoritePageDirection = 0;
            _favoritePageAt = 0f;
        }

        private static List<string[]> FavoriteDisplayOrder()
        {
            List<string[]> items = VisibleFavorites;
            if (!ReferenceEquals(items, _favoriteDragList) || _favoriteDropIndex < 0)
                return items;
            int from = FindFavoriteIndex(items, _favoriteDragUid);
            if (from < 0) return items;
            var display = new List<string[]>(items);
            string[] entry = display[from];
            display.RemoveAt(from);
            int dst = _favoriteDropIndex - (from < _favoriteDropIndex ? 1 : 0);
            display.Insert(Mathf.Clamp(dst, 0, display.Count), entry);
            return display;
        }

        private static int FavoriteDropIndex()
        {
            List<string[]> items = VisibleFavorites;
            if (!ReferenceEquals(_favoriteDragList, items) || items.Count == 0)
                return -1;
            Vector2 p;
            if (!DockPointerLocal(_favCells, out p)) return -1;
            return GridInsertIndex(p, _favCells, _favPage, FavoriteCapacity(),
                items.Count);
        }

        private static void TickFavoriteReorder()
        {
            if (_favoriteDragList == null) return;
            int direction = 0;
            Vector2 point;
            foreach (FavoritePageTarget target in _favoritePageTargets)
                if (target != null && PointerOnRect(target.transform as RectTransform, out point))
                { direction = target.Direction; break; }
            if (direction != 0)
            {
                if (_favoritePageDirection != direction)
                {
                    _favoritePageDirection = direction;
                    _favoritePageAt = Time.unscaledTime + 0.45f;
                }
                else if (Time.unscaledTime >= _favoritePageAt)
                {
                    int old = _favPage;
                    FavPageStep(direction);
                    _favoritePageAt = Time.unscaledTime + 0.6f;
                    if (old != _favPage)
                    {
                        _favoriteDropIndex = -1;
                        Log("fav drag page=" + (_favPage + 1));
                    }
                }
                return;
            }
            _favoritePageDirection = 0;
            int index = -1;
            // Over the tag strip the drop files into that tag group, and
            // outside the dock it removes — neither shows a reorder preview.
            if (!PointerOnRect(_favTagStrip, out point) &&
                PointerOnRect(_favDock, out point))
                index = FavoriteDropIndex();
            if (!_favPtrLogged)
            {
                _favPtrLogged = true;
                Vector3 cp;
                Log("fav ptr cursor=" +
                    (DragCursorWorld(out cp) ? cp.ToString("F3") : "none") +
                    " idx=" + index);
            }
            if (index != _favoriteDropIndex)
            {
                _favoriteDropIndex = index;
                _favPreviewDirty = true;
            }
        }

        private static bool CommitFavoriteReorder(AceFavDragSource source)
        {
            List<string[]> items = VisibleFavorites;
            int from = FindFavoriteIndex(items, source.Uid);
            int to = FavoriteDropIndex();
            if (to < 0 || from < 0) return false;
            int dst = to - (from < to ? 1 : 0);
            if (dst == from) return false;
            string[] entry = items[from];
            items.RemoveAt(from);
            items.Insert(Mathf.Clamp(dst, 0, items.Count), entry);
            SaveFavoriteStores();
            _favPreviewDirty = true;
            Log("fav reorder " + from + "->" + dst + " uid=" + source.Uid);
            return true;
        }

        // ---------- left preset dock (mirrors the favorites side) ----------

        private static void BeginPdReorder(AceFavDragSource source)
        {
            ClearPdReorder();
            if (!source.FromPresetDock ||
                source.PresetTab < 0 || source.PresetTab >= PdTabCount)
                return;
            EnsurePdSlots();
            _pdDragList = _pdSlots[source.PresetTab];
            _pdDragPath = source.PresetPath;
            _pdPtrLogged = false;
            Log("pd drag begin tab=" + source.PresetTab + " " +
                source.PresetPath);
        }

        private static void ClearPdReorder()
        {
            if (_pdDropIndex >= 0) _pdPreviewDirty = true;
            _pdDragList = null;
            _pdDragPath = null;
            _pdDropIndex = -1;
            _pdPageDirection = 0;
            _pdPageAt = 0f;
        }

        private static List<string> PdDisplaySlots()
        {
            List<string> slots = _pdSlots[_pdTab];
            if (!ReferenceEquals(slots, _pdDragList) || _pdDropIndex < 0)
                return slots;
            int from = slots.IndexOf(_pdDragPath);
            if (from < 0) return slots;
            List<string> display = new List<string>(slots);
            display.RemoveAt(from);
            int dst = _pdDropIndex - (from < _pdDropIndex ? 1 : 0);
            display.Insert(Mathf.Clamp(dst, 0, display.Count), _pdDragPath);
            return display;
        }

        private static int PdDropIndex()
        {
            List<string> slots = _pdSlots[_pdTab];
            if (!ReferenceEquals(slots, _pdDragList) || slots.Count == 0)
                return -1;
            Vector2 p;
            if (!DockPointerLocal(_pdCells, out p)) return -1;
            return GridInsertIndex(p, _pdCells, _pdPage, PdPageCapacity,
                slots.Count);
        }

        private static void TickPdReorder()
        {
            if (_pdDragList == null) return;
            int direction = 0;
            Vector2 point;
            foreach (FavoritePageTarget target in _pdPageTargets)
                if (target != null && PointerOnRect(target.transform as RectTransform, out point))
                { direction = target.Direction; break; }
            if (direction != 0)
            {
                if (_pdPageDirection != direction)
                {
                    _pdPageDirection = direction;
                    _pdPageAt = Time.unscaledTime + 0.45f;
                }
                else if (Time.unscaledTime >= _pdPageAt)
                {
                    int old = _pdPage;
                    PdPageStep(direction);
                    _pdPageAt = Time.unscaledTime + 0.6f;
                    if (old != _pdPage)
                    {
                        _pdDropIndex = -1;
                        Log("pd drag page=" + (_pdPage + 1));
                    }
                }
                return;
            }
            _pdPageDirection = 0;
            int index = -1;
            // Over the tab strip the drop copies the preset into that tab,
            // and outside the dock it removes — neither shows a reorder
            // preview (mirrors the favorites side's tag-strip suppression).
            if (!PointerOnRect(_pdTabStrip, out point) &&
                PointerOnRect(_pdDock, out point))
                index = PdDropIndex();
            if (!_pdPtrLogged)
            {
                _pdPtrLogged = true;
                Vector3 cp;
                Log("pd ptr cursor=" +
                    (DragCursorWorld(out cp) ? cp.ToString("F3") : "none") +
                    " idx=" + index);
            }
            if (index != _pdDropIndex)
            {
                _pdDropIndex = index;
                _pdPreviewDirty = true;
            }
        }

        private static bool CommitPdReorder(AceFavDragSource source)
        {
            List<string> slots = _pdSlots[_pdTab];
            if (!ReferenceEquals(slots, _pdDragList) ||
                string.IsNullOrEmpty(source.PresetPath)) return false;
            int from = slots.IndexOf(source.PresetPath);
            int to = PdDropIndex();
            if (to < 0 || from < 0) return false;
            int dst = to - (from < to ? 1 : 0);
            if (dst == from) return false;
            slots.RemoveAt(from);
            slots.Insert(Mathf.Clamp(dst, 0, slots.Count), source.PresetPath);
            SavePdSlots();
            _pdPreviewDirty = true;
            Log("pd reorder " + from + "->" + dst + " " + source.PresetPath);
            return true;
        }
    }
}
