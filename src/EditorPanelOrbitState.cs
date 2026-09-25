using System;
namespace Quest3TriggerUI
{
    // Right grip + horizontal stick owns yaw until grip release, so crossing
    // the deadzone does not hand the gesture back to locomotion mid-drag.
    internal sealed class EditorPanelOrbitState
    {
        internal bool Capturing { get; private set; }
        internal float Axis { get; private set; }
        internal void Update(bool available, bool grip, bool blocked, float x)
        {
            if (!available || !grip || blocked) { Reset(); return; }
            if (float.IsNaN(x) || float.IsInfinity(x)) x = 0;
            x = Math.Max(-1f, Math.Min(1f, x));
            if (Math.Abs(x) > 0.2f) Capturing = true;
            Axis = Capturing && Math.Abs(x) > 0.2f ?
                Math.Sign(x) * (Math.Abs(x) - 0.2f) / 0.8f : 0f;
        }
        internal float Degrees(float dt)
        {
            return Axis * 45f * Math.Max(0f, Math.Min(0.05f, dt));
        }
        internal void Reset() { Capturing = false; Axis = 0f; }
    }
}
