// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Frames;

namespace Nobara.Graphics;

/// <summary>
/// The service of nGraphics in the client context: the registry of backends,
/// the device, and the link between the events of the window and the
/// swapchain.
/// </summary>
internal sealed class GraphicsSystem : IGraphicsSystem, IGraphicsBackends
{
    private readonly ModuleContext _context;
    private readonly List<(ModuleContext Module, IGraphicsBackend Backend)> _backends = [];
    private IGraphicsBackend? _backend;
    private GraphicsDevice? _device;
    private bool _started;

    public GraphicsSystem(ModuleContext context)
    {
        _context = context;
    }

    public bool IsCreated => _device is not null;

    public string BackendName => (_backend ?? throw NotCreated()).Name;

    public GraphicsDevice Device => _device ?? throw NotCreated();

    public void Register(ModuleContext module, IGraphicsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(backend);
        if (_started)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' registers graphics backend '{backend.Name}' after nGraphics started; backends register during initialization.");
        }

        foreach ((ModuleContext other, IGraphicsBackend existing) in _backends)
        {
            if (string.Equals(existing.Name, backend.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modules '{other.Module.Id}' and '{module.Module.Id}' both register a graphics backend named '{backend.Name}'.");
            }
        }

        _backends.Add((module, backend));
        _context.Logger.Debug($"Graphics backend '{backend.Name}' registered by module '{module.Module.Id}'.");
    }

    /// <summary>Chooses the backend and creates the device on the window.</summary>
    public void Start(IWindow window)
    {
        _started = true;
        _backend = ChooseBackend();
        var options = new GraphicsDeviceOptions(
            _context.Settings.GetInt(GraphicsSettings.FramesInFlight),
            GraphicsSettings.ParseVSync(_context.Settings.GetEnum(GraphicsSettings.VSync)),
            _context.Logger);
        _device = _backend.CreateDevice(window, options);
        GraphicsDeviceInfo info = _device.Info;
        _context.Logger.Info($"Graphics device created by backend '{_backend.Name}': {info.Renderer} ({info.Vendor}), {info.Version}; features {_device.Capabilities.Features}; {_device.FramesInFlight} frames in flight; vsync {_device.Swapchain.VSync}.");
    }

    /// <summary>Destroys the device and everything left on it.</summary>
    public void Release()
    {
        if (_device is null)
        {
            return;
        }

        IReadOnlyList<GraphicsResource> left = _device.GetLiveObjects();
        if (left.Count > 0)
        {
            string more = left.Count > 10 ? ", ..." : string.Empty;
            _context.Logger.Warning($"{left.Count} graphics resources were not disposed before nGraphics was released: {string.Join(", ", left.Take(10))}{more}.");
        }

        _device.Dispose();
        _device = null;
    }

    /// <summary>Follows the window: size, minimized state, and surface.</summary>
    public void WindowChanged(WindowEvent e)
    {
        if (_device is null)
        {
            return;
        }

        Swapchain swapchain = _device.Swapchain;
        WindowSize pixels = e.Window.PixelSize;
        switch (e.Kind)
        {
            case WindowEventKind.Resized or WindowEventKind.ScaleChanged or WindowEventKind.ModeChanged:
                swapchain.Resized(pixels.Width, pixels.Height);
                break;
            case WindowEventKind.Minimized or WindowEventKind.Restored:
                swapchain.SetMinimized(e.Window.IsMinimized);
                break;
            case WindowEventKind.SurfaceLost:
                swapchain.SurfaceLost();
                break;
            case WindowEventKind.SurfaceRecreated:
                swapchain.SurfaceRecreated(pixels.Width, pixels.Height);
                break;
        }
    }

    /// <summary>Applies a change of a key of nGraphics.</summary>
    public void SettingChanged(string key)
    {
        if (_device is null)
        {
            return;
        }

        switch (key)
        {
            case GraphicsSettings.VSync:
                _device.SetVSync(GraphicsSettings.ParseVSync(_context.Settings.GetEnum(GraphicsSettings.VSync)));
                break;
            case GraphicsSettings.Backend:
                _context.Logger.Info($"'{GraphicsSettings.Backend}' changed; the graphics backend changes at the next start of the game.");
                break;
        }
    }

    private IGraphicsBackend ChooseBackend()
    {
        if (_backends.Count == 0)
        {
            throw new InvalidOperationException("No graphics backend is registered: the distribution needs a backend module of nobara.graphics, such as nobara.graphics.opengl.");
        }

        string wanted = _context.Settings.GetString(GraphicsSettings.Backend);
        if (wanted.Length > 0 && !string.Equals(wanted, GraphicsSettings.AutomaticBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach ((_, IGraphicsBackend backend) in _backends)
            {
                if (string.Equals(backend.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return backend;
                }
            }

            _context.Logger.Warning($"'{GraphicsSettings.Backend}' names the graphics backend '{wanted}', which no module registers; nGraphics uses '{_backends[0].Backend.Name}'. Registered: {string.Join(", ", _backends.Select(b => b.Backend.Name))}.");
        }

        return _backends[0].Backend;
    }

    private static InvalidOperationException NotCreated() =>
        new("The graphics device is created when nGraphics starts: use it from the start step on.");
}
