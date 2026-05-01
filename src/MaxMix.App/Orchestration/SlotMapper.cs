namespace MaxMix.App.Orchestration;

using System;
using System.Collections.Generic;
using MaxMix.Core.Abstractions;

/// <summary>
/// Pure helpers that compute the firmware's 4-slot view from a flat session
/// list and a current index, plus the cycle/navigation arithmetic that fixes
/// Bug #2 from the original software (operator precedence on % vs +).
/// </summary>
public static class SlotMapper
{
    /// <summary>
    /// Maps the session list to the (current, alternate, previous, next) slots
    /// the firmware expects. Wraps around for previous/next so the user can
    /// scroll past either end. With 1 session, prev/next return the same item;
    /// alternate stays null (no second channel available).
    /// </summary>
    public static SessionSlots Map(
        IReadOnlyList<AudioSessionSnapshot> sessions,
        int currentIndex)
    {
        if (sessions is null || sessions.Count == 0)
        {
            return new SessionSlots(null, null, null, null);
        }

        int count = sessions.Count;
        // Clamp into [0, count) with wraparound — handles negative or out-of-range inputs.
        int normalised = ((currentIndex % count) + count) % count;

        var current = sessions[normalised];

        if (count == 1)
        {
            return new SessionSlots(current, null, current, current);
        }

        var next     = sessions[NextIndex(normalised, count)];
        var previous = sessions[PreviousIndex(normalised, count)];

        // Alternate slot = "the other channel" for game mode.
        // For a 2-session list, alternate = next. For larger lists, we use
        // the same convention (firmware only consumes alternate in Game mode).
        var alternate = sessions[NextIndex(normalised, count)];

        return new SessionSlots(current, alternate, previous, next);
    }

    /// <summary>
    /// Returns the next index with wraparound. Bug #2 fix:
    /// <c>(index + 1) % count</c> NOT <c>index + 1 % count</c>.
    /// The original C# code in MaxMix 1.5.6 had the parentheses missing,
    /// causing the index to never advance past the first step.
    /// </summary>
    public static int NextIndex(int currentIndex, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be positive");
        }
        return (currentIndex + 1) % count;
    }

    /// <summary>
    /// Returns the previous index with wraparound. Adds <paramref name="count"/>
    /// before the modulo so the result is always non-negative.
    /// </summary>
    public static int PreviousIndex(int currentIndex, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be positive");
        }
        return ((currentIndex - 1) % count + count) % count;
    }
}
