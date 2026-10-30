// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Game;

/// <summary>
/// The service of game time: the tick number of the context, the levers on
/// the simulation (pause, time scale, tick rate), and the clocks of the
/// dimensions. Resolve it with <c>context.Resolve&lt;IGameTime&gt;()</c>;
/// there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// Game time is raw: a count of ticks per dimension, on 64 bits. It knows no
/// day, night, or season; business modules build those on the clocks.
/// </para>
/// <para>
/// At the start of each tick, before the first stage, the clock of each
/// dimension that is not frozen goes up by one. During the first tick after
/// a clock is added at 0, it reads 1.
/// </para>
/// <para>
/// The levers belong to the context that has authority over game time. Every
/// context of this version is the main context of its process and has it.
/// The changes of pause, scale, and rate take effect at the next frame. Use
/// the service on the loop thread of the context.
/// </para>
/// </remarks>
public interface IGameTime
{
    /// <summary>Gets the number of the tick that runs, or of the last tick that ran, from 1; 0 before the first tick.</summary>
    ulong Tick
    {
        [HotPath]
        get;
    }

    /// <summary>
    /// Gets the real time measured by the loop since the context started. It
    /// goes on during a pause and ignores the time scale.
    /// </summary>
    TimeSpan RealTime
    {
        [HotPath]
        get;
    }

    /// <summary>Gets or sets whether the simulation is paused: no tick runs, and no clock moves. Real time goes on.</summary>
    /// <exception cref="InvalidOperationException">On set to <see langword="true"/>: <see cref="CanPause"/> is <see langword="false"/>.</exception>
    /// <remarks>A change emits <see cref="GamePauseChanged"/> when the event bus is present.</remarks>
    bool Paused { get; set; }

    /// <summary>
    /// Gets whether the simulation may be paused. A game open to other players
    /// cannot pause; this version has no network, so it can always pause.
    /// </summary>
    bool CanPause { get; }

    /// <summary>
    /// Gets or sets how many ticks run per second of real time, as a factor of
    /// the tick rate: 2 runs twice as many ticks. The duration of a tick does
    /// not change, so the simulation stays the same; only its speed does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than 0 and at most 100.</exception>
    /// <remarks>A change emits <see cref="TimeScaleChanged"/> when the event bus is present.</remarks>
    double TimeScale { get; set; }

    /// <summary>Gets or sets the number of ticks per second at a time scale of 1. Changing it changes the duration of a tick.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not greater than 0 and at most 1000.</exception>
    /// <remarks>A change emits <see cref="TickRateChanged"/> when the event bus is present.</remarks>
    double TickRate { get; set; }

    /// <summary>Gets the duration of one tick: the inverse of <see cref="TickRate"/>.</summary>
    TimeSpan TickDuration { get; }

    /// <summary>Gets the dimensions that have a clock, in the order of their identifiers.</summary>
    IReadOnlyList<DimensionId> Dimensions { get; }

    /// <summary>Tells whether a dimension has a clock.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns><see langword="true"/> when it has one.</returns>
    [HotPath]
    bool HasDimension(DimensionId dimension);

    /// <summary>Adds the clock of a dimension.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="clock">The value the clock starts at, such as a saved value.</param>
    /// <exception cref="ArgumentException">The dimension already has a clock.</exception>
    void AddDimension(DimensionId dimension, ulong clock = 0);

    /// <summary>Removes the clock of a dimension. The tasks scheduled on it are canceled.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <exception cref="ArgumentException">The dimension has no clock, or is <see cref="DimensionId.Default"/>, whose clock is never removed.</exception>
    void RemoveDimension(DimensionId dimension);

    /// <summary>Gets the clock of a dimension: the number of ticks of game time it has counted.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The clock.</returns>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    [HotPath]
    ulong GetClock(DimensionId dimension);

    /// <summary>
    /// Moves the clock of a dimension forward at once, without simulating the
    /// ticks between, such as to skip a night. The tasks that the jump leaves
    /// behind run at the next tick.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="ticks">How many ticks to skip; 0 does nothing.</param>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    /// <exception cref="OverflowException">The clock would go beyond 2^64 - 1.</exception>
    /// <remarks>A jump emits <see cref="DimensionClockJumped"/> when the event bus is present.</remarks>
    void Jump(DimensionId dimension, ulong ticks);

    /// <summary>Tells whether the clock of a dimension is frozen.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns><see langword="true"/> when it is frozen.</returns>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    [HotPath]
    bool IsFrozen(DimensionId dimension);

    /// <summary>
    /// Freezes or releases the clock of a dimension. A frozen clock does not
    /// move, its tasks wait, and the systems that go through dimensions skip
    /// it. The tick number of the context still goes up.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="frozen">Whether the clock is frozen.</param>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    /// <remarks>A change emits <see cref="DimensionFreezeChanged"/> when the event bus is present.</remarks>
    void SetFrozen(DimensionId dimension, bool frozen);
}
