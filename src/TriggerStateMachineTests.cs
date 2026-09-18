using System;
using Quest3TriggerUI;

internal static class TriggerStateMachineTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    public static int Main()
    {
        TriggerStateMachine tap = new TriggerStateMachine(0.35f, 0.55f, 0.45f);
        tap.Advance(1, 0.00f, 0.80f);
        tap.Advance(2, 0.10f, 0.00f);
        Assert(tap.PressedDownFrame == 1, "Trigger down edge was not recorded.");
        Assert(tap.ReleasedFrame == 2, "Trigger up edge was not recorded.");
        Assert(tap.TapFrame == 2, "Short press did not emit a tap.");
        Assert(tap.LongPressStartFrame == -1, "Short press emitted Grab.");

        TriggerStateMachine hold = new TriggerStateMachine(0.35f, 0.55f, 0.45f);
        hold.Advance(10, 1.00f, 0.80f);
        hold.Advance(11, 1.20f, 0.80f);
        Assert(hold.LongPressStartFrame == -1, "Grab started too early.");
        hold.Advance(12, 1.36f, 0.80f);
        Assert(hold.LongPressStartFrame == 12, "Long press did not emit the radial-menu gesture.");
        Assert(hold.LongPressActive, "Long press was not active.");
        hold.Advance(13, 1.50f, 0.00f);
        Assert(hold.LongPressReleaseFrame == 13, "Long press release was not recorded.");
        Assert(hold.TapFrame == -1, "Long press also emitted a tap.");

        TriggerStateMachine hysteresis = new TriggerStateMachine(0.35f, 0.55f, 0.45f);
        hysteresis.Advance(20, 2.00f, 0.80f);
        hysteresis.Advance(21, 2.10f, 0.50f);
        Assert(hysteresis.TapFrame == -1, "Hysteresis released too early.");
        hysteresis.Advance(22, 2.20f, 0.40f);
        Assert(hysteresis.TapFrame == 22, "Hysteresis did not release below threshold.");

        SyntheticClickState click = new SyntheticClickState();
        click.Begin(30);
        Assert(!click.ConsumeUp(30), "UI pointer up occurred in the down frame.");
        Assert(click.ConsumeUp(31), "UI pointer up was not emitted on the next frame.");
        Assert(!click.ConsumeUp(32), "UI pointer up was emitted more than once.");

        KeyboardChordStateMachine chord = new KeyboardChordStateMachine(0.45f, 0.55f, 0.45f);
        chord.Advance(40, 4.00f, 0.80f, 0.00f);
        chord.Advance(41, 4.05f, 0.80f, 0.80f);
        Assert(chord.Active, "Overlapping index+grip press did not claim the chord.");
        Assert(chord.SuppressInput(41), "Active chord did not suppress scene input.");
        chord.Advance(42, 4.20f, 0.00f, 0.00f);
        Assert(chord.ToggleFrame == 42, "Short index+grip chord did not toggle.");
        Assert(chord.SuppressInput(42), "Chord release frame was not suppressed.");

        chord.Advance(43, 5.00f, 0.80f, 0.80f);
        chord.Advance(44, 5.15f, 0.00f, 0.00f);
        Assert(chord.ToggleFrame == 44, "Second short chord did not toggle off.");

        KeyboardChordStateMachine indexOnly = new KeyboardChordStateMachine(0.45f, 0.55f, 0.45f);
        indexOnly.Advance(50, 6.00f, 0.80f, 0.00f);
        indexOnly.Advance(51, 6.10f, 0.00f, 0.00f);
        Assert(indexOnly.ToggleFrame == -1, "Index-only tap incorrectly toggled the keyboard.");

        KeyboardChordStateMachine longChord = new KeyboardChordStateMachine(0.45f, 0.55f, 0.45f);
        longChord.Advance(60, 7.00f, 0.80f, 0.80f);
		longChord.Advance(61, 7.46f, 0.80f, 0.80f);
		Assert(longChord.LongHoldStartFrame == 61,
			"Long right index+grip hold did not emit Grab.");
		longChord.Advance(62, 7.60f, 0.00f, 0.00f);
        Assert(longChord.ToggleFrame == -1, "Long chord incorrectly toggled the keyboard.");

		int actionCount = 0;
		ShortcutBindingTarget actionTarget = ShortcutBindingTarget.ActionButton(
			"Embody", delegate { actionCount++; });
		ShortcutBindingTarget keyTarget = ShortcutBindingTarget.Key("F2", 0x71, false);
		Assert(actionTarget.IsAction && actionTarget.Label == "Embody",
			"Function action was not represented as a bindable shortcut target.");
		actionTarget.InvokeAction();
		Assert(actionCount == 1, "Bound function action did not execute exactly once.");
		Assert(!keyTarget.IsAction && keyTarget.VirtualKey == 0x71,
			"Keyboard shortcut target regressed.");

        KeyboardChordStateMachine recenterChord =
            new KeyboardChordStateMachine(0.45f, 0.55f, 0.45f);
        recenterChord.Advance(70, 8.00f, 0.80f, 0.00f);
        recenterChord.Advance(71, 8.05f, 0.80f, 0.80f);
        recenterChord.Advance(72, 8.20f, 0.00f, 0.00f);
        Assert(recenterChord.ToggleFrame == 72,
            "Left+right grip tap did not emit the recenter action.");

        DoubleTapStateMachine doubleTap =
            new DoubleTapStateMachine(0.28f, 0.38f, 0.55f, 0.45f);
        doubleTap.Advance(80, 9.00f, 0.80f);
        doubleTap.Advance(81, 9.08f, 0.00f);
        doubleTap.Advance(82, 9.22f, 0.80f);
        doubleTap.Advance(83, 9.30f, 0.00f);
        Assert(doubleTap.TriggerFrame == 83, "Two quick taps did not emit a shortcut gesture.");

        DoubleTapStateMachine slowDoubleTap =
            new DoubleTapStateMachine(0.28f, 0.38f, 0.55f, 0.45f);
        slowDoubleTap.Advance(90, 10.00f, 0.80f);
        slowDoubleTap.Advance(91, 10.08f, 0.00f);
        slowDoubleTap.Advance(92, 10.60f, 0.80f);
        slowDoubleTap.Advance(93, 10.68f, 0.00f);
        Assert(slowDoubleTap.TriggerFrame == -1, "Slow taps incorrectly emitted a shortcut gesture.");

        KeyboardChordStateMachine indexAChord =
            new KeyboardChordStateMachine(0.45f, 0.55f, 0.45f);
        indexAChord.Advance(100, 11.00f, 0.80f, 0.00f);
        indexAChord.Advance(101, 11.04f, 0.80f, 1.00f);
        indexAChord.Advance(102, 11.16f, 0.00f, 0.00f);
        Assert(indexAChord.ToggleFrame == 102,
            "Right index+A chord did not emit a shortcut gesture.");

        GlobalPitchState pitch = new GlobalPitchState(45f, 80f, 0.18f, false);
        Assert(pitch.Advance(0.17f, 1f) == 0f,
            "Pitch moved inside the thumbstick deadzone.");
        float firstPitch = pitch.Advance(1f, 1f);
        Assert(Math.Abs(firstPitch + 45f) < 0.001f &&
               Math.Abs(pitch.PitchDegrees + 45f) < 0.001f,
            "Right thumbstick up did not pitch the view upward.");
        pitch.Advance(1f, 2f);
        Assert(Math.Abs(pitch.PitchDegrees + 80f) < 0.001f,
            "Pitch did not clamp at the upper limit.");
        pitch.Advance(-1f, 4f);
        Assert(Math.Abs(pitch.PitchDegrees - 80f) < 0.001f,
            "Pitch did not clamp at the lower limit.");
        pitch.Reset();
        Assert(pitch.PitchDegrees == 0f, "Pitch reset did not restore the horizon.");

        GlobalPitchState invertedPitch = new GlobalPitchState(45f, 80f, 0.18f, true);
        invertedPitch.Advance(1f, 1f);
        Assert(Math.Abs(invertedPitch.PitchDegrees - 45f) < 0.001f,
            "Pitch inversion was not applied.");

            Console.WriteLine("MODIFIED RESULT=PASS; Version=4.0.1; RadialHover=EventDrivenColorTint; AppearanceLoad=SelectedClothingEmpty+OriginalClothingRestore; ClothingOverlay=False; Standby=CoordinatedPause+5FPSMainThreadLimiter; CharacterTwistRegression=False; HotUpdate=PayloadSwapWithoutGameRestart; ExistingFeatures=Preserved");
        return 0;
    }
}
