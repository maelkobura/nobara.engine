// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Event;

namespace Nobara.Frames;

/// <summary>What nFrames relays to the game besides its own service.</summary>
internal interface IFramesNotifications
{
    void FocusChanged(bool hasFocus);

    void CloseRequested();
}

/// <summary>
/// The only place where nFrames uses the event bus, its optional dependency:
/// it emits the events useful to the game synchronously, when they have
/// handlers.
/// </summary>
[UsesOptionalDependency("nobara.event")]
internal sealed class EventNotifications : IFramesNotifications
{
    private readonly IEventBus _bus;

    private EventNotifications(IEventBus bus)
    {
        _bus = bus;
    }

    /// <summary>Connects to the bus of the context. Call it only when the event module is active.</summary>
    /// <returns>The notifications, or <see langword="null"/> when the bus is absent.</returns>
    public static IFramesNotifications? Create(ModuleContext context) =>
        context.TryResolve(out IEventBus? bus) ? new EventNotifications(bus) : null;

    public void FocusChanged(bool hasFocus) => Emit(new WindowFocusChanged(hasFocus));

    public void CloseRequested() => Emit(default(WindowCloseRequested));

    private void Emit<TEvent>(TEvent e)
        where TEvent : struct
    {
        if (_bus.HasSubscribers<TEvent>())
        {
            _bus.Emit(ref e);
        }
    }
}
