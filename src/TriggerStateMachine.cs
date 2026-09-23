namespace Quest3TriggerUI
{
	using System;

	public sealed class ShortcutBindingTarget
	{
		private readonly Action _action;

		private ShortcutBindingTarget(
			string label, ushort virtualKey, bool extended, Action action,
			string actionId)
		{
			Label = label;
			VirtualKey = virtualKey;
			Extended = extended;
			_action = action;
			ActionId = actionId;
		}

		public string Label { get; private set; }
		public ushort VirtualKey { get; private set; }
		public bool Extended { get; private set; }
		public string ActionId { get; private set; }
		public bool IsAction { get { return _action != null; } }

		public static ShortcutBindingTarget Key(
			string label, ushort virtualKey, bool extended)
		{
			return new ShortcutBindingTarget(label, virtualKey, extended, null, null);
		}

		public static ShortcutBindingTarget ActionButton(string label, Action action)
		{
			return ActionButton(label, action, null);
		}

		public static ShortcutBindingTarget ActionButton(
			string label, Action action, string actionId)
		{
			if (action == null) throw new ArgumentNullException("action");
			return new ShortcutBindingTarget(label, 0, false, action, actionId);
		}

		public void InvokeAction()
		{
			if (_action != null) _action();
		}
	}

    public sealed class SyntheticClickState
    {
        private int _downFrame = -1;
        private bool _waitingForUp;

        public void Begin(int frame)
        {
            _downFrame = frame;
            _waitingForUp = true;
        }

        public bool ConsumeUp(int frame)
        {
            if (!_waitingForUp || frame <= _downFrame)
                return false;

            _waitingForUp = false;
            return true;
        }
    }

    public sealed class TriggerStateMachine
    {
        private int _sampledFrame = -1;
        private bool _pressed;
        private float _pressedAt;
        private bool _longPressActive;

        public TriggerStateMachine(float longPressSeconds, float pressThreshold, float releaseThreshold)
        {
            LongPressSeconds = longPressSeconds;
            PressThreshold = pressThreshold;
            ReleaseThreshold = releaseThreshold;
            TapFrame = -1;
            PressedDownFrame = -1;
            ReleasedFrame = -1;
            LongPressStartFrame = -1;
            LongPressReleaseFrame = -1;
        }

        public float LongPressSeconds { get; private set; }
        public float PressThreshold { get; private set; }
        public float ReleaseThreshold { get; private set; }
        public int TapFrame { get; private set; }
        public int PressedDownFrame { get; private set; }
        public int ReleasedFrame { get; private set; }
        public int LongPressStartFrame { get; private set; }
        public int LongPressReleaseFrame { get; private set; }
        public float AnalogValue { get; private set; }
        public bool Pressed { get { return _pressed; } }
        public bool LongPressActive { get { return _longPressActive; } }

        public void Advance(int frame, float unscaledTime, float analogValue)
        {
            if (_sampledFrame == frame)
                return;

            _sampledFrame = frame;
            AnalogValue = analogValue;

            bool wasPressed = _pressed;
            bool isPressed = wasPressed
                ? analogValue >= ReleaseThreshold
                : analogValue >= PressThreshold;

            if (!wasPressed && isPressed)
            {
                _pressedAt = unscaledTime;
                _longPressActive = false;
                PressedDownFrame = frame;
            }

            if (isPressed && !_longPressActive && unscaledTime - _pressedAt >= LongPressSeconds)
            {
                _longPressActive = true;
                LongPressStartFrame = frame;
            }

            if (wasPressed && !isPressed)
            {
                ReleasedFrame = frame;
                if (_longPressActive || unscaledTime - _pressedAt >= LongPressSeconds)
                {
                    LongPressReleaseFrame = frame;
                }
                else
                {
                    TapFrame = frame;
                }

                _longPressActive = false;
            }

            _pressed = isPressed;
        }
    }

    public sealed class KeyboardChordStateMachine
    {
        private int _sampledFrame = -1;
        private bool _indexPressed;
        private bool _gripPressed;
        private bool _candidate;
        private bool _overlapped;
		private bool _longHoldActive;
        private float _startedAt;

        public KeyboardChordStateMachine(float maxTapSeconds, float pressThreshold, float releaseThreshold)
        {
            MaxTapSeconds = maxTapSeconds;
            PressThreshold = pressThreshold;
            ReleaseThreshold = releaseThreshold;
            ToggleFrame = -1;
            SuppressFrame = -1;
			LongHoldStartFrame = -1;
        }

        public float MaxTapSeconds { get; private set; }
        public float PressThreshold { get; private set; }
        public float ReleaseThreshold { get; private set; }
        public int ToggleFrame { get; private set; }
        public int SuppressFrame { get; private set; }
		public int LongHoldStartFrame { get; private set; }
		public bool LongHoldActive { get { return _longHoldActive; } }
        public bool Active { get { return _overlapped && (_indexPressed || _gripPressed); } }

        public bool SuppressInput(int frame)
        {
            return Active || SuppressFrame == frame || ToggleFrame == frame;
        }

        public void Advance(int frame, float unscaledTime, float indexValue, float gripValue)
        {
            if (_sampledFrame == frame)
                return;

            _sampledFrame = frame;
            bool wasAnyPressed = _indexPressed || _gripPressed;
            _indexPressed = ApplyHysteresis(_indexPressed, indexValue);
            _gripPressed = ApplyHysteresis(_gripPressed, gripValue);
            bool anyPressed = _indexPressed || _gripPressed;

            if (!wasAnyPressed && anyPressed)
            {
                _candidate = true;
                _overlapped = false;
                _startedAt = unscaledTime;
            }

            if (_candidate && _indexPressed && _gripPressed)
                _overlapped = true;

			if (_candidate && _overlapped && _indexPressed && _gripPressed &&
				!_longHoldActive && unscaledTime - _startedAt > MaxTapSeconds)
			{
				_longHoldActive = true;
				LongHoldStartFrame = frame;
			}

            if (_candidate && !anyPressed)
            {
				if (_overlapped && !_longHoldActive &&
					unscaledTime - _startedAt <= MaxTapSeconds)
                    ToggleFrame = frame;

                if (_overlapped)
                    SuppressFrame = frame;

                _candidate = false;
                _overlapped = false;
				_longHoldActive = false;
            }
        }

        private bool ApplyHysteresis(bool wasPressed, float value)
        {
            return wasPressed ? value >= ReleaseThreshold : value >= PressThreshold;
        }
    }

    public sealed class DoubleTapStateMachine
    {
        private readonly float _maxPressSeconds;
        private readonly float _maxGapSeconds;
        private readonly float _pressThreshold;
        private readonly float _releaseThreshold;
        private int _sampledFrame = -1;
        private bool _pressed;
        private float _pressedAt;
        private float _firstTapAt = -100f;

        public DoubleTapStateMachine(
            float maxPressSeconds,
            float maxGapSeconds,
            float pressThreshold,
            float releaseThreshold)
        {
            _maxPressSeconds = maxPressSeconds;
            _maxGapSeconds = maxGapSeconds;
            _pressThreshold = pressThreshold;
            _releaseThreshold = releaseThreshold;
            TriggerFrame = -1;
        }

        public int TriggerFrame { get; private set; }

        public void Advance(int frame, float unscaledTime, float analogValue)
        {
            if (_sampledFrame == frame)
                return;

            _sampledFrame = frame;
            bool wasPressed = _pressed;
            _pressed = wasPressed
                ? analogValue >= _releaseThreshold
                : analogValue >= _pressThreshold;

            if (!wasPressed && _pressed)
                _pressedAt = unscaledTime;

            if (!wasPressed || _pressed)
                return;

            if (unscaledTime - _pressedAt > _maxPressSeconds)
            {
                _firstTapAt = -100f;
                return;
            }

            if (unscaledTime - _firstTapAt <= _maxGapSeconds)
            {
                TriggerFrame = frame;
                _firstTapAt = -100f;
            }
            else
            {
                _firstTapAt = unscaledTime;
            }
        }

        public void Cancel()
        {
            _firstTapAt = -100f;
            _pressed = false;
        }
    }

    public sealed class GlobalPitchState
    {
        private readonly float _degreesPerSecond;
        private readonly float _maximumDegrees;
        private readonly float _deadzone;
        private readonly bool _invert;

        public GlobalPitchState(
            float degreesPerSecond, float maximumDegrees, float deadzone, bool invert)
        {
            _degreesPerSecond = Math.Max(0f, degreesPerSecond);
            _maximumDegrees = Math.Max(0f, maximumDegrees);
            _deadzone = Math.Min(0.95f, Math.Max(0f, deadzone));
            _invert = invert;
        }

        public float PitchDegrees { get; private set; }

        public float Advance(float thumbstickY, float unscaledDeltaTime)
        {
            float magnitude = Math.Abs(thumbstickY);
            if (magnitude <= _deadzone || unscaledDeltaTime <= 0f)
                return 0f;

            float normalized = (magnitude - _deadzone) / (1f - _deadzone);
            if (thumbstickY < 0f)
                normalized = -normalized;

            float direction = _invert ? 1f : -1f;
            float previous = PitchDegrees;
            PitchDegrees = Math.Min(
                _maximumDegrees,
                Math.Max(
                    -_maximumDegrees,
                    PitchDegrees + normalized * _degreesPerSecond *
                    unscaledDeltaTime * direction));
            return PitchDegrees - previous;
        }

        public void Reset()
        {
            PitchDegrees = 0f;
        }

        public void Set(float degrees)
        {
            PitchDegrees = Math.Min(_maximumDegrees,
                Math.Max(-_maximumDegrees, degrees));
        }
    }

    public sealed class GripPitchInputState
    {
        private readonly float _deadzone;
        private bool _capturingGrip;

        public GripPitchInputState(float deadzone)
        {
            _deadzone = Math.Min(0.95f, Math.Max(0f, deadzone));
        }

        public bool CapturingGrip { get { return _capturingGrip; } }
        public bool AllowAdjustment { get; private set; }
        public float ThumbstickY { get; private set; }

        public void Update(bool embodyActive, bool gripPressed,
            bool blockInput, float thumbstickY)
        {
            if (embodyActive)
            {
                _capturingGrip = false;
                AllowAdjustment = !blockInput;
                ThumbstickY = AllowAdjustment ? thumbstickY : 0f;
                return;
            }

            if (!gripPressed)
                _capturingGrip = false;
            else if (!blockInput && Math.Abs(thumbstickY) > _deadzone)
                _capturingGrip = true;

            AllowAdjustment = _capturingGrip && !blockInput;
            ThumbstickY = AllowAdjustment ? thumbstickY : 0f;
        }

        public void Reset()
        {
            _capturingGrip = false;
            AllowAdjustment = false;
            ThumbstickY = 0f;
        }
    }
}
