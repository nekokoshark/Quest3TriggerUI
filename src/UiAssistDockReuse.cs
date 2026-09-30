using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        // Retain only the current page. A reorder moves existing objects;
        // it must not rebuild 56 buttons or re-read 56 thumbnail files.
        private static readonly List<AceFavSlotTag> _favVisibleCells = new List<AceFavSlotTag>();
        private static readonly HashSet<AceFavSlotTag> _favKeptCells = new HashSet<AceFavSlotTag>();
        private static readonly List<PdSlotTag> _pdVisibleCells = new List<PdSlotTag>();
        private static readonly HashSet<PdSlotTag> _pdKeptCells = new HashSet<PdSlotTag>();
        private static int _favCellPosition, _pdCellPosition;
        private static GameObject _favHintCell, _pdHintCell;
        private static bool _favPreviewDirty, _pdPreviewDirty;

        private static bool ReuseFavoriteCell(string uid)
        {
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag == null || tag.Uid != uid || _favKeptCells.Contains(tag)) continue;
                PlaceFavoriteCell(tag);
                if (_favBuildVisual) QueueFavoriteVisual(tag);
                return true;
            }
            return false;
        }

        private static void PlaceFavoriteCell(AceFavSlotTag tag)
        {
            _favKeptCells.Add(tag);
            if (tag.transform.GetSiblingIndex() != _favCellPosition)
                tag.transform.SetSiblingIndex(_favCellPosition);
            _favCellPosition++;
        }

        private static void FinishFavoriteCells()
        {
            for (int i = _favVisibleCells.Count - 1; i >= 0; i--)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag != null && _favKeptCells.Contains(tag)) continue;
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
                _favVisibleCells.RemoveAt(i);
            }
            if (_favKeptCells.Count > 0 && _favHintCell != null)
            {
                _favHintCell.SetActive(false);
                Object.Destroy(_favHintCell);
                _favHintCell = null;
            }
            _favPreviewDirty = false;
            int pending = _favVisualQueue.Count;
            while (pending-- > 0)
            {
                AceFavSlotTag tag = _favVisualQueue.Dequeue();
                if (tag != null && _favKeptCells.Contains(tag)) _favVisualQueue.Enqueue(tag);
                else _favVisualQueued.Remove(tag);
            }
        }

        // Tab switch parks the outgoing grid instead of destroying it:
        // inactive children are ignored by the GridLayoutGroup, so the
        // switch-back path reactivates them with thumbnails still bound
        // and no refill animation. A dirty or mid-build grid is not
        // parkable — its cells die outright as before. The decoded-texture
        // cache (_pdThumbs) survives either way.
        private static void ParkPdTabCells(int tab)
        {
            _pdParkedCells[tab] = null;
            _pdParkedHints[tab] = null;
            _pdParkedFresh[tab] = false;
            if (_pdDirty || _pdBuildSlots != null ||
                (_pdVisibleCells.Count == 0 && _pdHintCell == null))
            {
                ClearPdCellsNow();
                return;
            }
            if (_pdVisibleCells.Count > 0)
                _pdParkedCells[tab] = new List<PdSlotTag>(_pdVisibleCells);
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null) tag.gameObject.SetActive(false);
            }
            if (_pdHintCell != null)
            {
                _pdParkedHints[tab] = _pdHintCell;
                _pdHintCell.SetActive(false);
                _pdHintCell = null;
            }
            _pdParkedFresh[tab] = true;
            _pdVisibleCells.Clear();
            _pdKeptCells.Clear();
            _pdCellPosition = 0;
            TeardownPdCellInteraction();
        }

        // Hard wipe of the visible grid — kept for teardown paths and for
        // unparkable (dirty/mid-build) tab switches.
        private static void ClearPdCellsNow()
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
            }
            _pdVisibleCells.Clear();
            _pdKeptCells.Clear();
            _pdCellPosition = 0;
            if (_pdHintCell != null)
            {
                _pdHintCell.SetActive(false);
                Object.Destroy(_pdHintCell);
                _pdHintCell = null;
            }
            TeardownPdCellInteraction();
        }

        // Interaction state that must die with a grid swap regardless of
        // whether the cells were parked or destroyed.
        private static void TeardownPdCellInteraction()
        {
            _pdThumbQueue.Clear();
            _pdThumbQueued.Clear();
            _pdBuildSlots = null;
            _pdHzTag = null;
            if (_pdHzPanel != null) _pdHzPanel.gameObject.SetActive(false);
            if (_pdPersonOverlay != null)
            { Object.Destroy(_pdPersonOverlay); _pdPersonOverlay = null; }
            if (_pdSaveOverlay != null)
            { Object.Destroy(_pdSaveOverlay); _pdSaveOverlay = null; _pdSaveTag = null; }
            if (_pdRenameOverlay != null)
            {
                Object.Destroy(_pdRenameOverlay);
                _pdRenameOverlay = null; _pdRenameInput = null;
                _pdRenamePath = null;
                VrTextInputBridge.Clear();
            }
        }

        private static bool ReusePdCell(string path)
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag == null || tag.Path != path || _pdKeptCells.Contains(tag)) continue;
                PlacePdCell(tag);
                if (_pdBuildThumbs) ApplyPdThumb(tag);
                return true;
            }
            return false;
        }

        private static void PlacePdCell(PdSlotTag tag)
        {
            _pdKeptCells.Add(tag);
            if (tag.transform.GetSiblingIndex() != _pdCellPosition)
                tag.transform.SetSiblingIndex(_pdCellPosition);
            _pdCellPosition++;
        }

        private static void FinishPdCells()
        {
            for (int i = _pdVisibleCells.Count - 1; i >= 0; i--)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null && _pdKeptCells.Contains(tag)) continue;
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
                _pdVisibleCells.RemoveAt(i);
            }
            if (_pdKeptCells.Count > 0 && _pdHintCell != null)
            {
                _pdHintCell.SetActive(false);
                Object.Destroy(_pdHintCell);
                _pdHintCell = null;
            }
            _pdPreviewDirty = false;
            // Keep pending work bounded to the current page even when users
            // flip faster than thumbnails can be decoded.
            int pending = _pdThumbQueue.Count;
            while (pending-- > 0)
            {
                PdSlotTag tag = _pdThumbQueue.Dequeue();
                if (tag != null && _pdKeptCells.Contains(tag)) _pdThumbQueue.Enqueue(tag);
                else _pdThumbQueued.Remove(tag);
            }
        }

        // ---- favorites-bar parked views ---------------------------------
        // Same contract as _pdParkedCells but keyed by the backing list
        // object instead of a tab index: the favorites bar has a dynamic
        // tag set, and a tag rename re-keys the name -> list mapping while
        // the List<string[]> instance (and therefore its parked cells)
        // survives. Any mutation of a list must InvalidateFavParked it.

        private static readonly Dictionary<List<string[]>, List<AceFavSlotTag>>
            _favParkedCells = new Dictionary<List<string[]>, List<AceFavSlotTag>>();
        private static readonly Dictionary<List<string[]>, GameObject>
            _favParkedHints = new Dictionary<List<string[]>, GameObject>();

        private static void ParkFavViewCells(List<string[]> list)
        {
            if (list == null) return;
            InvalidateFavParked(list);
            if (_favCells == null) return;
            // A dirty or mid-build grid is not parkable — its cells no
            // longer mirror the list and die outright as before. A live
            // reorder preview has the same mismatch (visual order ahead
            // of list order), so it is unparkable too.
            if (_favDirty || _favPreviewDirty || _favBuildSlots != null ||
                (_favVisibleCells.Count == 0 && _favHintCell == null))
            {
                ClearFavCellsNow();
                return;
            }
            if (_favVisibleCells.Count > 0)
                _favParkedCells[list] = new List<AceFavSlotTag>(_favVisibleCells);
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag != null) tag.gameObject.SetActive(false);
            }
            if (_favHintCell != null)
            {
                _favParkedHints[list] = _favHintCell;
                _favHintCell.SetActive(false);
                _favHintCell = null;
            }
            _favVisibleCells.Clear();
            _favKeptCells.Clear();
            _favCellPosition = 0;
            _favVisualQueue.Clear();
            _favVisualQueued.Clear();
        }

        private static bool RestoreFavViewCells(List<string[]> list)
        {
            List<AceFavSlotTag> parked;
            GameObject hint;
            _favParkedCells.TryGetValue(list, out parked);
            _favParkedHints.TryGetValue(list, out hint);
            _favParkedCells.Remove(list);
            _favParkedHints.Remove(list);
            if (_favCells == null || (parked == null && hint == null))
            {
                // Stale leftovers die here rather than surfacing one
                // switch later with wrong thumbnails.
                if (parked != null)
                    for (int i = 0; i < parked.Count; i++)
                        if (parked[i] != null)
                            Object.Destroy(parked[i].gameObject);
                if (hint != null) Object.Destroy(hint);
                return false;
            }
            if (parked != null)
            {
                for (int i = 0; i < parked.Count; i++)
                {
                    AceFavSlotTag tag = parked[i];
                    if (tag == null) continue;
                    tag.gameObject.SetActive(true);
                    _favVisibleCells.Add(tag);
                    _favKeptCells.Add(tag);
                    // Re-queue visuals: _favThumbs hits bind instantly, so
                    // this only refreshes Dim state after an atom switch —
                    // never a texture re-decode.
                    QueueFavoriteVisual(tag);
                }
            }
            if (hint != null)
            {
                if (_favVisibleCells.Count == 0)
                { _favHintCell = hint; hint.SetActive(true); }
                else Object.Destroy(hint);
            }
            _favCellPosition = _favVisibleCells.Count;
            int rows = Mathf.CeilToInt(_favVisibleCells.Count / (float)FavColumns);
            _favContentH = rows > 0
                ? rows * FavCellH + (rows - 1) * FavSpacing + FavPad
                : FavPad;
            _favCells.sizeDelta = new Vector2(FavColW, _favContentH);
            ApplyDockScroll(0, _favScrollY);
            ApplyFavoritesDockWidth();
            return true;
        }

        // List contents changed (add/remove/move/reorder) while its cells
        // were parked — the parked set is stale, destroy it.
        private static void InvalidateFavParked(List<string[]> list)
        {
            if (list == null) return;
            List<AceFavSlotTag> parked;
            if (_favParkedCells.TryGetValue(list, out parked))
            {
                for (int i = 0; i < parked.Count; i++)
                    if (parked[i] != null)
                        Object.Destroy(parked[i].gameObject);
                _favParkedCells.Remove(list);
            }
            GameObject hint;
            if (_favParkedHints.TryGetValue(list, out hint))
            {
                if (hint != null) Object.Destroy(hint);
                _favParkedHints.Remove(list);
            }
        }

        // Wholesale teardown — the tag-item dictionary was rebuilt, so old
        // list keys can never be looked up again; their cells would leak.
        private static void ClearFavParkedAll()
        {
            foreach (List<AceFavSlotTag> parked in _favParkedCells.Values)
                if (parked != null)
                    for (int i = 0; i < parked.Count; i++)
                        if (parked[i] != null)
                            Object.Destroy(parked[i].gameObject);
            foreach (GameObject hint in _favParkedHints.Values)
                if (hint != null) Object.Destroy(hint);
            _favParkedCells.Clear();
            _favParkedHints.Clear();
        }

        // Hard wipe of the visible grid — teardown paths and unparkable
        // (dirty/mid-build) view switches.
        private static void ClearFavCellsNow()
        {
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
            }
            _favVisibleCells.Clear();
            _favKeptCells.Clear();
            _favCellPosition = 0;
            if (_favHintCell != null)
            {
                _favHintCell.SetActive(false);
                Object.Destroy(_favHintCell);
                _favHintCell = null;
            }
            _favVisualQueue.Clear();
            _favVisualQueued.Clear();
            _favBuildSlots = null;
        }

        private static readonly Queue<AceFavSlotTag> _favVisualQueue = new Queue<AceFavSlotTag>();
        private static readonly HashSet<AceFavSlotTag> _favVisualQueued = new HashSet<AceFavSlotTag>();

        private static void QueueFavoriteVisual(AceFavSlotTag tag)
        {
            Texture2D cached;
            if (_favThumbs.TryGetValue(tag.Uid, out cached) && cached != null)
            {
                tag.Thumb.texture = cached;
                tag.Thumb.color = Color.white;
            }
            // Cells outside the masked viewport stay unqueued — scrolling
            // pumps them in via PumpVisibleFavVisuals, so a long list only
            // ever decodes the rows the user can actually see.
            if (!DockCellVisible(tag.transform.GetSiblingIndex(),
                FavCellH, _favScrollY, FavGridH)) return;
            if (_favVisualQueued.Add(tag)) _favVisualQueue.Enqueue(tag);
        }

        private static void TickFavoriteVisuals()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int work = 0;
            while (_favVisualQueue.Count > 0 && work < 2)
            {
                AceFavSlotTag tag = _favVisualQueue.Dequeue();
                _favVisualQueued.Remove(tag);
                work++;
                if (tag == null || !_favKeptCells.Contains(tag)) continue;
                RefreshSlotVisual(tag);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency >= 2.0) break;
            }
        }

        private static readonly Queue<PdSlotTag> _pdThumbQueue = new Queue<PdSlotTag>();
        private static readonly HashSet<PdSlotTag> _pdThumbQueued = new HashSet<PdSlotTag>();

        // Decoded thumbnails arrive at the sidecar jpg's native size - 512x512
        // for a cell that draws at 80x76 (240x228 once the hover zoom is
        // applied), so a full-size RGBA32 cost 1MiB per slot and a dock that
        // had visited all five tabs sat at ~112MiB of uploads for content the
        // panel can never resolve. Bigger sources are resampled down once, on
        // this pump's own 2ms budget.
        private const int ThumbMaxEdge = 256;

        // GPU-side downscale: one blit plus a <=256x256 readback, so capping
        // the dock costs no managed pixel arrays (a GetPixels32/SetPixels32
        // pass would allocate 1MiB of garbage per thumbnail and undo the
        // saving). Any failure returns the original texture untouched - a
        // thumbnail must never be lost to a resize.
        private static Texture2D CapThumbEdge(Texture2D src, string name)
        {
            if (src == null) return null;
            int sw = src.width, sh = src.height;
            if (sw <= ThumbMaxEdge && sh <= ThumbMaxEdge) return src;
            int nw, nh;
            if (sw >= sh)
            {
                nw = ThumbMaxEdge;
                nh = Mathf.Max(1, Mathf.RoundToInt(sh * (ThumbMaxEdge / (float)sw)));
            }
            else
            {
                nh = ThumbMaxEdge;
                nw = Mathf.Max(1, Mathf.RoundToInt(sw * (ThumbMaxEdge / (float)sh)));
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D dst = null;
            try
            {
                rt = RenderTexture.GetTemporary(nw, nh, 0);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                dst = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                dst.name = name;
                dst.ReadPixels(new Rect(0f, 0f, nw, nh), 0, 0);
                dst.Apply(false, false);
            }
            catch
            {
                if (dst != null) UnityEngine.Object.Destroy(dst);
                return src;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
            UnityEngine.Object.Destroy(src);
            return dst;
        }

        private static bool ThumbOversized(Texture2D t)
        {
            return t != null && (t.width > ThumbMaxEdge || t.height > ThumbMaxEdge);
        }

        private static void ApplyPdThumb(PdSlotTag tag)
        {
            Texture2D cached;
            _pdThumbs.TryGetValue(tag.Path, out cached);
            tag.Thumb.texture = cached;
            tag.Thumb.color = cached != null ? Color.white : new Color(0.25f, 0.25f, 0.3f, 1f);
            if (!DockCellVisible(tag.transform.GetSiblingIndex(),
                PdCellH, _pdScrollY, PdGridH)) return;
            if (_pdThumbQueued.Add(tag)) _pdThumbQueue.Enqueue(tag);
        }

        private static void TickPdThumbnails()
        {
            // Unity texture upload stays on the main thread. At most two
            // files per frame, stop after ~2ms; a single decode is indivisible.
            // Keep thumbnail decodes out of the character's decode/upload peak.
            // Requests stay queued and resume once the existing load window settles.
            if (LoadWindow.PresetBusy || SceneLoadAccelerator.SceneLoadActive ||
                WardrobeJanitor.ImagesBusy()) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int work = 0;
            while (_pdThumbQueue.Count > 0 && work < 2)
            {
                PdSlotTag tag = _pdThumbQueue.Dequeue();
                _pdThumbQueued.Remove(tag);
                work++;
                if (tag == null || !_pdKeptCells.Contains(tag)) continue;
                LoadPdThumb(tag);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency >= 2.0) break;
            }
        }
    }
}
