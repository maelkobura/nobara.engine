// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Event;

namespace Nobara.Game;

/// <summary>
/// The only place where nGame uses the event bus, its optional dependency:
/// it emits the events of the levers synchronously, when they have handlers.
/// </summary>
[UsesOptionalDependency("nobara.event")]
internal sealed class EventNotifications : IGameNotifications
{
    private readonly IEventBus _bus;

    private EventNotifications(IEventBus bus)
    {
        _bus = bus;
    }

    /// <summary>Connects to the bus of the context. Call it only when the event module is active.</summary>
    /// <returns>The notifications, or <see langword="null"/> when the bus is absent.</returns>
    public static IGameNotifications? Create(ModuleContext context) =>
        context.TryResolve(out IEventBus? bus) ? new EventNotifications(bus) : null;

    public void PauseChanged(bool paused) => Emit(new GamePauseChanged(paused));

    public void TimeScaleChanged(double previous, double current) => Emit(new Game.TimeScaleChanged(previous, current));

    public void TickRateChanged(double previous, double current) => Emit(new Game.TickRateChanged(previous, current));

    public void ClockJumped(DimensionId dimension, ulong from, ulong to) => Emit(new DimensionClockJumped(dimension, from, to));

    public void FreezeChanged(DimensionId dimension, bool frozen) => Emit(new DimensionFreezeChanged(dimension, frozen));

    private void Emit<TEvent>(TEvent e)
        where TEvent : struct
    {
        if (_bus.HasSubscribers<TEvent>())
        {
            _bus.Emit(ref e);
        }
    }
}
