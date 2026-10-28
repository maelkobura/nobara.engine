// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Event;

/// <summary>Chooses the phase of the loop where the deferred events are handed out.</summary>
internal static class DeferredPhase
{
    /// <summary>A phase of the context, as the choice sees it.</summary>
    /// <param name="Name">The name of the phase.</param>
    /// <param name="IsFixed">Whether the phase runs at the fixed tick.</param>
    /// <param name="Reserved">Whether a module reserves the phase, which closes it to the others.</param>
    internal readonly record struct Candidate(string Name, bool IsFixed, bool Reserved);

    /// <summary>
    /// Picks the first open phase after the last fixed phase, since the
    /// simulation is the fixed phase and the events go out once it has run. A
    /// distribution without a fixed phase gets its last phase.
    /// </summary>
    /// <param name="phases">The phases of the context, in the order they run.</param>
    /// <returns>
    /// The index of the phase, or -1 when no open phase fits: the events then
    /// go out at the start of the next frame, which follows the same tick.
    /// </returns>
    public static int Choose(IReadOnlyList<Candidate> phases)
    {
        int lastFixed = -1;
        for (int i = 0; i < phases.Count; i++)
        {
            if (phases[i].IsFixed)
            {
                lastFixed = i;
            }
        }

        int first = lastFixed < 0 ? phases.Count - 1 : lastFixed + 1;
        for (int i = Math.Max(first, 0); i < phases.Count; i++)
        {
            if (!phases[i].Reserved)
            {
                return i;
            }
        }

        return -1;
    }
}
