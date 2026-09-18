namespace Quest3TriggerUI
{
    internal enum TemporaryShortcutGesture
    {
        LeftIndexDoubleTap, LeftGripDoubleTap, LeftIndexTripleTap, LeftGripTripleTap,
        RightIndexDoubleTap, RightGripDoubleTap, RightIndexTripleTap, RightGripTripleTap
    }

    // A double tap waits out the third-tap window. A triple never emits its double prefix.
    internal sealed class MultiTapStateMachine
    {
        private readonly float _maxPress, _gap, _press, _release;
        private int _sampledFrame = -1, _count;
        private bool _pressed, _invalidPress;
        private float _pressedAt, _releasedAt;
        internal int DoubleFrame { get; private set; }
        internal int TripleFrame { get; private set; }
        internal MultiTapStateMachine(float maxPress, float gap, float press, float release)
        {
            _maxPress = maxPress; _gap = gap; _press = press; _release = release;
            DoubleFrame = TripleFrame = -1;
        }
        internal void Advance(int frame, float now, float value, bool blocked)
        {
            if (_sampledFrame == frame) return;
            _sampledFrame = frame;
            bool wasPressed = _pressed;
            _pressed = value >= (wasPressed ? _release : _press);
            if (blocked)
            {
                _count = 0;
                // Keep the actual button state: releasing a blocked chord is not a new tap.
                _invalidPress = _pressed;
                return;
            }
            if (!wasPressed && _count > 0 && now - _releasedAt > _gap)
            {
                if (_count == 2) DoubleFrame = frame;
                _count = 0;
            }
            if (!wasPressed && _pressed)
            {
                _pressedAt = now;
                _invalidPress = false;
            }
            if (wasPressed && _pressed && now - _pressedAt > _maxPress)
            {
                _count = 0;
                _invalidPress = true;
            }
            if (!wasPressed || _pressed) return;
            if (_invalidPress || now - _pressedAt > _maxPress)
            {
                _count = 0;
                _invalidPress = false;
                return;
            }
            _releasedAt = now;
            _count++;
            if (_count == 3)
            {
                TripleFrame = frame;
                _count = 0;
            }
        }
    }

    internal sealed class ShortcutGestureBank
    {
        internal const int GestureCount = 8;
        private readonly float _releaseThreshold;
        private readonly MultiTapStateMachine[] _channels = new MultiTapStateMachine[4];
        private static readonly string[] Labels = {
            "左食指 ×2", "左 Grip ×2", "左食指 ×3", "左 Grip ×3",
            "右食指 ×2", "右 Grip ×2", "右食指 ×3", "右 Grip ×3"
        };
        internal ShortcutGestureBank(float press, float release)
        {
            _releaseThreshold = release;
            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new MultiTapStateMachine(0.28f, 0.32f, press, release);
        }
        internal void Advance(int frame, float now, float leftIndex, float leftGrip,
            float rightIndex, float rightGrip, bool blocked)
        {
            _channels[0].Advance(frame, now, leftIndex, blocked || leftGrip >= _releaseThreshold);
            _channels[1].Advance(frame, now, leftGrip, blocked || leftIndex >= _releaseThreshold || rightGrip >= _releaseThreshold);
            _channels[2].Advance(frame, now, rightIndex, blocked || rightGrip >= _releaseThreshold);
            _channels[3].Advance(frame, now, rightGrip, blocked || rightIndex >= _releaseThreshold || leftGrip >= _releaseThreshold);
        }
        internal bool Triggered(TemporaryShortcutGesture gesture, int frame)
        {
            int id = (int)gesture;
            int channel = (id < 4 ? 0 : 2) + id % 2;
            return id % 4 < 2 ? _channels[channel].DoubleFrame == frame : _channels[channel].TripleFrame == frame;
        }
        internal static string Label(TemporaryShortcutGesture gesture) { return Labels[(int)gesture]; }
    }
}

