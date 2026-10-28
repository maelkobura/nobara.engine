// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using static Nobara.Event.DeferredPhase;

namespace Nobara.Event.Tests;

/// <summary>Where the deferred events go out, for the loops a distribution can declare.</summary>
public sealed class DeferredPhaseTests
{
    private static Candidate Open(string name) => new(name, false, false);

    private static Candidate Fixed(string name, bool reserved = false) => new(name, true, reserved);

    [Fact]
    public void Picks_the_phase_after_the_simulation_in_the_standard_loop()
    {
        Candidate[] loop = [Open("Input"), Fixed("Simulation"), Open("PostSimulation"), Open("Render")];

        Assert.Equal(2, Choose(loop));
    }

    [Fact]
    public void Picks_the_phase_after_the_last_fixed_phase()
    {
        Candidate[] loop = [Fixed("Physics"), Fixed("Simulation"), Open("Network"), Open("Render")];

        Assert.Equal(2, Choose(loop));
    }

    [Fact]
    public void Skips_a_phase_that_a_module_reserves()
    {
        Candidate[] loop = [Fixed("Simulation", reserved: true), new Candidate("Network", false, true), Open("Render")];

        Assert.Equal(2, Choose(loop));
    }

    [Fact]
    public void Picks_the_last_phase_when_no_phase_is_fixed()
    {
        Candidate[] loop = [Open("Input"), Open("Render")];

        Assert.Equal(1, Choose(loop));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Falls_back_to_the_start_of_the_next_frame_when_no_open_phase_follows(int variant)
    {
        Candidate[] loop = variant == 0
            ? [Open("Input"), Fixed("Simulation")]
            : [Fixed("Simulation"), new Candidate("Render", false, true)];

        Assert.Equal(-1, Choose(loop));
    }

    [Fact]
    public void Falls_back_to_the_start_of_the_frame_without_phases()
    {
        Assert.Equal(-1, Choose([]));
    }
}
