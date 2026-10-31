// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Configuration;

[assembly: ModuleEntry(typeof(Nobara.Frames.FramesModule))]

namespace Nobara.Frames;

/// <summary>
/// The module of the window: it publishes <see cref="IWindowSystem"/> and
/// <see cref="IWindowBackends"/>, creates the window with the chosen backend
/// when it starts, and pumps the events of the window at the start of each
/// frame.
/// </summary>
public sealed class FramesModule : IModule
{
    private WindowSystem? _system;
    private SettingSubscription? _settings;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _system = new WindowSystem(context);
        context.Publish<IWindowSystem>(_system);
        context.Publish<IWindowBackends>(_system);

        // The start of the frame is the start of the input phase, whatever the distribution names its phases.
        context.Loop.SubscribeFrameStart(_system);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        if (context.IsModuleActive("nobara.event"))
        {
            _system!.Notifications = EventNotifications.Create(context);
        }

        _settings = context.Settings.Subscribe(_system!.SettingChanged);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _system!.Start();

    /// <inheritdoc/>
    public void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency)
    {
        if (dependency.Id == "nobara.event")
        {
            _system!.Notifications = null;
        }
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _settings?.Dispose();
        _system?.Release();
    }
}
