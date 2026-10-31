// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Configuration;
using Nobara.Frames;

[assembly: ModuleEntry(typeof(Nobara.Graphics.GraphicsModule))]

namespace Nobara.Graphics;

/// <summary>
/// The module of the graphics device: it publishes <see cref="IGraphicsSystem"/>
/// and <see cref="IGraphicsBackends"/>, creates the device with the chosen
/// backend when it starts, and follows the window to keep the swapchain at its
/// size.
/// </summary>
public sealed class GraphicsModule : IModule
{
    private GraphicsSystem? _system;
    private WindowSubscription? _window;
    private SettingSubscription? _settings;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _system = new GraphicsSystem(context);
        context.Publish<IGraphicsSystem>(_system);
        context.Publish<IGraphicsBackends>(_system);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _window = context.Resolve<IWindowSystem>().Subscribe(context, _system!.WindowChanged);
        _settings = context.Settings.Subscribe(_system.SettingChanged);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _system!.Start(context.Resolve<IWindowSystem>().MainWindow);

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _settings?.Dispose();
        _window?.Dispose();
        _system?.Release();
    }
}
