// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Configuration;
using Nobara.Frames;

[assembly: ModuleEntry(typeof(Nobara.Input.InputModule))]

namespace Nobara.Input;

/// <summary>
/// The module of input: it defines the resource types <c>InputAction</c> and
/// <c>InputMap</c>, publishes <see cref="IInputSystem"/>, and evaluates the
/// raw input of nFrames for each layer at the start of each frame.
/// </summary>
public sealed class InputModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.input";

    /// <summary>The type prefix of the action identifiers: <c>inputaction</c>.</summary>
    public const string ActionType = InputNames.ActionType;

    /// <summary>The type prefix of the map identifiers: <c>inputmap</c>.</summary>
    public const string MapType = InputNames.MapType;

    private InputSystem? _system;
    private SettingSubscription? _settings;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _system = new InputSystem(context);
        context.Resources.RegisterDecoder(ActionType, ActionDefinition.Decode);
        context.Resources.RegisterDecoder(MapType, MapDefinition.Decode);
        context.Resources.RegisterReloadHandler(ActionType, _system.ReloadActions);
        context.Resources.RegisterReloadHandler(MapType, _system.ReloadMaps);
        context.Publish<IInputSystem>(_system);

        // nFrames, a dependency, subscribed first: its events are pumped when this handler runs.
        context.Loop.SubscribeFrameStart(_system);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _system!.ReadSettings();
        _settings = context.Settings.Subscribe(_ => _system.ReadSettings());
        _system.Load(context.Resolve<IWindowSystem>());
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _system!.Start();

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _settings?.Dispose();
        _system?.Release();
    }
}
