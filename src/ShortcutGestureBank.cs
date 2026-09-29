namespace Quest3TriggerUI
{
    internal enum TemporaryShortcutGesture
    {
        LeftIndexDoubleTap, LeftGripDoubleTap, LeftIndexTripleTap, LeftGripTripleTap,
        RightIndexDoubleTap, RightGripDoubleTap, RightIndexTripleTap, RightGripTripleTap,
        RightAIndexChord, RightAGripChord, RightASingle, RightADouble,
        LeftXIndexChord, LeftXGripChord, LeftXSingle, LeftXDouble
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

    // Per-hand A/X chord detector: index+A, grip+A, single tap, double tap.
    // A press while exactly one modifier is held fires the chord on the press
    // edge and is not counted as a tap. Both modifiers held is ambiguous —
    // fires nothing. A lone tap waits out the gap so double can pre-empt it.
    internal sealed class AButtonChordChannel
    {
        private const float MaxPress = 0.28f, Gap = 0.32f;
        private readonly float _modifierHeld;
        private int _sampledFrame = -1;
        private bool _a;
        private float _aPressedAt, _aReleasedAt;
        private int _taps;
        private bool _suppress;
        internal int IndexChordFrame = -1, GripChordFrame = -1, SingleFrame = -1, DoubleFrame = -1;

        internal AButtonChordChannel(float releaseThreshold)
        {
            _modifierHeld = releaseThreshold;
        }

        internal void Advance(int frame, float now, float a, float index, float grip, bool blocked)
        {
            if (_sampledFrame == frame) return;
            _sampledFrame = frame;
            bool wasA = _a;
            _a = a >= 0.5f;
            // A pending single resolves once the double window lapses.
            if (_taps == 1 && !_a && now - _aReleasedAt > Gap)
            {
                SingleFrame = frame;
                _taps = 0;
            }
            if (blocked)
            {
                _taps = 0;
                _suppress = _a;
                return;
            }
            if (!wasA && _a)
            {
                bool indexHeld = index >= _modifierHeld;
                bool gripHeld = grip >= _modifierHeld;
                if (indexHeld != gripHeld)
                {
                    if (indexHeld) IndexChordFrame = frame;
                    else GripChordFrame = frame;
                    _suppress = true;
                    _taps = 0;
                }
                else if (indexHeld)
                {
                    _suppress = true;
                    _taps = 0;
                }
                else
                {
                    _suppress = false;
                    _aPressedAt = now;
                }
            }
            if (wasA && !_a)
            {
                if (_suppress)
                {
                    _suppress = false;
                    _taps = 0;
                }
                else if (now - _aPressedAt <= MaxPress)
                {
                    _taps++;
                    if (_taps == 2)
                    {
                        DoubleFrame = frame;
                        _taps = 0;
                    }
                    else _aReleasedAt = now;
                }
                else _taps = 0;
            }
        }
    }

    internal sealed class ShortcutGestureBank
    {
        internal const int GestureCount = 16;
        private readonly float _releaseThreshold;
        private readonly MultiTapStateMachine[] _channels = new MultiTapStateMachine[4];
        private readonly AButtonChordChannel[] _aChannels = new AButtonChordChannel[2];
        private static readonly string[] Labels = {
            "左食指 ×2", "左 Grip ×2", "左食指 ×3", "左 Grip ×3",
            "右食指 ×2", "右 Grip ×2", "右食指 ×3", "右 Grip ×3",
            "右食指+A", "右Grip+A", "右A 单击", "右A 双击",
            "左食指+X", "左Grip+X", "左X 单击", "左X 双击"
        };
        internal ShortcutGestureBank(float press, float release)
        {
            _releaseThreshold = release;
            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new MultiTapStateMachine(0.28f, 0.32f, press, release);
            _aChannels[0] = new AButtonChordChannel(release);
            _aChannels[1] = new AButtonChordChannel(release);
        }
        internal void Advance(int frame, float now, float leftIndex, float leftGrip,
            float rightIndex, float rightGrip, float rightA, float leftA, bool blocked)
        {
            _channels[0].Advance(frame, now, leftIndex, blocked || leftGrip >= _releaseThreshold);
            _channels[1].Advance(frame, now, leftGrip, blocked || leftIndex >= _releaseThreshold || rightGrip >= _releaseThreshold);
            _channels[2].Advance(frame, now, rightIndex, blocked || rightGrip >= _releaseThreshold);
            _channels[3].Advance(frame, now, rightGrip, blocked || rightIndex >= _releaseThreshold || leftGrip >= _releaseThreshold);
            _aChannels[0].Advance(frame, now, rightA, rightIndex, rightGrip, blocked);
            _aChannels[1].Advance(frame, now, leftA, leftIndex, leftGrip, blocked);
        }
        internal bool Triggered(TemporaryShortcutGesture gesture, int frame)
        {
            int id = (int)gesture;
            if (id >= 8)
            {
                AButtonChordChannel channel = _aChannels[id < 12 ? 0 : 1];
                switch (id % 4)
                {
                    case 0: return channel.IndexChordFrame == frame;
                    case 1: return channel.GripChordFrame == frame;
                    case 2: return channel.SingleFrame == frame;
                    default: return channel.DoubleFrame == frame;
                }
            }
            int tap = (id < 4 ? 0 : 2) + id % 2;
            return id % 4 < 2 ? _channels[tap].DoubleFrame == frame : _channels[tap].TripleFrame == frame;
        }
        internal static string Label(TemporaryShortcutGesture gesture) { return Labels[(int)gesture]; }
    }
}

