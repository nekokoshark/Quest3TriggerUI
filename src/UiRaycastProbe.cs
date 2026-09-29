using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // One-shot canvas/raycast census, armed via cfg flag UiRaycastCensusOnce.
    // For every Canvas: render mode, raycaster settings (incl. the
    // ignoreReversedGraphics flag that culls back-facing world-space UI),
    // worldCamera and the facing dot vs the head. Answers whether a button
    // that "the laser passes through" is missing a raycaster, reversed, or
    // simply inactive — without needing a VR aim-and-shoot session.
    internal static class UiRaycastProbe
    {
        internal static void Dump()
        {
            try
            {
                var log = Quest3TriggerUIPlugin.Log;
                SuperController sc = SuperController.singleton;
                Transform head = null;
                if (sc != null)
                {
                    if (sc.centerCameraTarget != null) head = sc.centerCameraTarget.transform;
                    else if (sc.lookCamera != null) head = sc.lookCamera.transform;
                }
                Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                log.LogInfo("[uiprobe] canvases=" + canvases.Length +
                    " head=" + (head != null ? head.position.ToString("F2") : "null"));
                foreach (Canvas c in canvases)
                {
                    if (c == null) continue;
                    GraphicRaycaster gr = c.GetComponent<GraphicRaycaster>();
                    string face = "-";
                    if (c.renderMode == RenderMode.WorldSpace && head != null)
                    {
                        // Positive = camera sits in the canvas's FRONT
                        // hemisphere (visible side); negative = back side,
                        // where ignoreReversedGraphics culls every graphic.
                        Vector3 toCam = (head.position - c.transform.position).normalized;
                        face = Vector3.Dot(c.transform.forward, toCam).ToString("0.00");
                    }
                    int selectables = 0;
                    foreach (Selectable s in c.GetComponentsInChildren<Selectable>(true))
                        if (s != null && s.IsInteractable()) selectables++;
                    var sb = new StringBuilder(c.name);
                    Transform p = c.transform.parent;
                    int depth = 0;
                    while (p != null && depth < 8) { sb.Insert(0, p.name + "/"); p = p.parent; depth++; }
                    log.LogInfo(string.Format(
                        "[uiprobe] {0} mode={1} sort={2} ray={3} rev={4} blk={5} cam={6} face={7} active={8} sel={9}",
                        sb, c.renderMode, c.sortingOrder,
                        gr == null ? "none" : (gr.enabled ? "on" : "off"),
                        gr == null ? "-" : gr.ignoreReversedGraphics.ToString(),
                        gr == null ? "-" : gr.blockingObjects.ToString(),
                        c.worldCamera == null ? "-" : c.worldCamera.name,
                        face, c.gameObject.activeInHierarchy, selectables));
                }
            }
            catch (Exception e)
            {
                Quest3TriggerUIPlugin.Log.LogError("[uiprobe] " + e);
            }
        }
    }
}
