namespace MaxMix.App.Orchestration;

using MaxMix.Core.Abstractions;

/// <summary>
/// The 4-slot view the firmware understands: <c>Current</c> is what the
/// rotary knob targets right now; <c>Previous</c>/<c>Next</c> are the
/// adjacent items shown for navigation context; <c>Alternate</c> is the
/// secondary channel used in Game mode (A/B mixer).
///
/// <para>
/// Any slot may be null when the session list is empty or has fewer than
/// 4 items. The orchestrator simply skips encoding null slots.
/// </para>
/// </summary>
public readonly record struct SessionSlots(
    AudioSessionSnapshot? Current,
    AudioSessionSnapshot? Alternate,
    AudioSessionSnapshot? Previous,
    AudioSessionSnapshot? Next);
