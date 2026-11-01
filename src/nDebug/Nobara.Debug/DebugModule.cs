// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Frames;
using Nobara.Graphics;
using Nobara.Input;
using Nobara.Renderer;

[assembly: ModuleEntry(typeof(Nobara.Debug.DebugModule))]

namespace Nobara.Debug;

/// <summary>
/// The module of the debug user interface: it owns the context of Dear ImGui,
/// feeds it from the input layer of highest priority, draws it at the
/// insertion point of the user interface of nRenderer, and publishes
/// <see cref="IDebugUi"/>. It provides the windows of the profiler and of the
/// settings.
/// </summary>
public sealed class DebugModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.debug";

    /// <summary>The rank of the pass of nDebug at the insertion point of the user interface: after the other engines of user interface.</summary>
    public const int UiOrder = 1000;

    private DebugUi? _ui;
    private ImGuiPass? _pass;
    private RenderRegistration? _insertion;
    private DebugWindow? _profiler;
    private DebugWindow? _settings;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _ui = new DebugUi(context);
        context.Publish<IDebugUi>(_ui);
        context.Loop.SubscribeFrameStart(_ui);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _pass = new ImGuiPass(_ui!);
        _ui!.Initialize(context.Resolve<IInputSystem>(), context.Resolve<IWindowSystem>(), _pass);
        _insertion = context.Resolve<IRenderer>().Insert(context, RenderInsertionPoints.Ui, _pass, UiOrder);
        _profiler = _ui.AddWindow(context, new DebugWindowOptions { Title = "Profiler", Open = false, Size = new Vector2(760, 420) }, new ProfilerWindow(context.Profiler));
        _settings = _ui.AddWindow(context, new DebugWindowOptions { Title = "Settings", Open = false, Size = new Vector2(560, 420) }, new SettingsWindow(context));
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        _pass!.Start(context.Resolve<IGraphicsSystem>().Device, context.Resolve<IRenderResources>());
        _ui!.Start();
    }

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        _settings?.Dispose();
        _profiler?.Dispose();
        _insertion?.Dispose();
        _ui?.Stop();
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _pass?.Dispose();
        _ui?.Dispose();
    }
}
