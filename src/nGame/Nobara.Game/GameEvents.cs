// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Game;

// The events nGame emits on the event bus when its levers are used. They do
// not depend on the bus: a module that listens to them depends on it.

/// <summary>The simulation was paused or resumed with <see cref="IGameTime.Paused"/>.</summary>
/// <param name="Paused">Whether the simulation is paused now.</param>
public readonly record struct GamePauseChanged(bool Paused);

/// <summary>The time scale changed with <see cref="IGameTime.TimeScale"/>.</summary>
/// <param name="Previous">The scale before the change.</param>
/// <param name="Current">The scale now.</param>
public readonly record struct TimeScaleChanged(double Previous, double Current);

/// <summary>The tick rate changed with <see cref="IGameTime.TickRate"/>.</summary>
/// <param name="Previous">The rate before the change, in ticks per second.</param>
/// <param name="Current">The rate now, in ticks per second.</param>
public readonly record struct TickRateChanged(double Previous, double Current);

/// <summary>The clock of a dimension jumped forward with <see cref="IGameTime.Jump"/>, without simulating the ticks between.</summary>
/// <param name="Dimension">The dimension.</param>
/// <param name="From">The clock before the jump.</param>
/// <param name="To">The clock after the jump.</param>
public readonly record struct DimensionClockJumped(DimensionId Dimension, ulong From, ulong To);

/// <summary>The clock of a dimension was frozen or released with <see cref="IGameTime.SetFrozen"/>.</summary>
/// <param name="Dimension">The dimension.</param>
/// <param name="Frozen">Whether its clock is frozen now.</param>
public readonly record struct DimensionFreezeChanged(DimensionId Dimension, bool Frozen);

/// <summary>Where nGame sends the notifications of its levers; the event bus when it is present.</summary>
internal interface IGameNotifications
{
    void PauseChanged(bool paused);

    void TimeScaleChanged(double previous, double current);

    void TickRateChanged(double previous, double current);

    void ClockJumped(DimensionId dimension, ulong from, ulong to);

    void FreezeChanged(DimensionId dimension, bool frozen);
}
