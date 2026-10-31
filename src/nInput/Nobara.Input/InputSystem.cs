// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Resources;
using Nobara.Frames;

namespace Nobara.Input;

/// <summary>
/// The service of nInput in the client context: the actions and maps, the
/// layers, the evaluation of the raw input at the start of each frame, and
/// the bindings of the player.
/// </summary>
internal sealed class InputSystem : IInputSystem, IPhaseHandler
{
    private readonly ModuleContext _context;
    private readonly List<InputLayer> _layers = [];
    private readonly Dictionary<string, InputAction> _actionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputMap> _mapsById = new(StringComparer.Ordinal);
    private InputAction[] _actions = [];
    private InputMap[] _maps = [];
    private InputLayer[] _ordered = [];
    private bool[] _wasActive = [];
    private BindingIndex _index = BindingIndex.Build([]);
    private IWindowSystem? _windows;
    private WindowSubscription? _windowEvents;
    private InputListening? _listening;
    private long _sequence;
    private double _now;
    private bool _bindingsChanged = true;
    private bool _layersChanged = true;
    private bool _mapsChanged;
    private bool _focusLost;
    private bool _relative;
    private float _sensitivity = 1;
    private bool _invertY;

    public InputSystem(ModuleContext context)
    {
        _context = context;
        Rebinding = new RebindingService(this, context);
    }

    public IReadOnlyList<InputAction> Actions => _actions;

    public IReadOnlyList<InputMap> Maps => _maps;

    public IReadOnlyList<InputLayer> Layers
    {
        get
        {
            Order();
            return _ordered;
        }
    }

    public IInputRebinding Rebinding { get; }

    public int LayoutVersion => _windows?.Input.LayoutVersion ?? 0;

    /// <summary>Gets the layer that has the text focus, or <see langword="null"/>.</summary>
    public InputLayer? TextFocus { get; private set; }

    /// <summary>Gets whether the mouse is in relative mode, as nInput set it.</summary>
    public bool RelativeMouse => _relative;

    internal RebindingService RebindingImpl => (RebindingService)Rebinding;

    public InputAction GetAction(string id) =>
        TryGetAction(id, out InputAction? action) ? action : throw new KeyNotFoundException($"No module declares the input action '{id}'.");

    public bool TryGetAction(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InputAction? action)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _actionsById.TryGetValue(id, out action);
    }

    public InputMap GetMap(string id) =>
        TryGetMap(id, out InputMap? map) ? map : throw new KeyNotFoundException($"No module declares the input map '{id}'.");

    public bool TryGetMap(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InputMap? map)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _mapsById.TryGetValue(id, out map);
    }

    public InputLayer AddLayer(ModuleContext module, InputLayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name, nameof(options));
        var layer = new InputLayer(this, module, options, ++_sequence);
        layer.State.Reset(_index, _actions);
        _layers.Add(layer);
        _layersChanged = true;
        _context.Logger.Debug($"Input layer '{layer.Name}' added by module '{module.Module.Id}' with priority {layer.Priority}.");
        return layer;
    }

    public string GetLayoutName(InputControl control)
    {
        IWindowSystem windows = _windows ?? throw new InvalidOperationException("Key names come from the window, which exists from the start step on.");
        return control.Device == InputDevice.Keyboard && !control.IsEitherSide ? windows.Input.GetKeyName((Scancode)control.Code) : string.Empty;
    }

    /// <summary>Reads the actions and maps of every module, and the bindings of the player.</summary>
    public void Load(IWindowSystem windows)
    {
        _windows = windows;
        _windowEvents = windows.Subscribe(_context, e =>
        {
            if (e.Kind == WindowEventKind.FocusLost)
            {
                _focusLost = true;
            }
        });

        var maps = _context.Resources.GetAll<MapDefinition>(InputNames.MapType).OrderBy(m => m.Data.ObjectId, StringComparer.Ordinal).ToArray();
        _maps = maps.Select(m => new InputMap(this, m.Data.ObjectId, m.DisplayName, m.Active)).ToArray();
        foreach (InputMap map in _maps)
        {
            _mapsById.Add(map.Id, map);
        }

        var actions = _context.Resources.GetAll<ActionDefinition>(InputNames.ActionType).OrderBy(a => a.Data.ObjectId, StringComparer.Ordinal).ToArray();
        _actions = new InputAction[actions.Length];
        for (int i = 0; i < actions.Length; i++)
        {
            var action = new InputAction(this, i, actions[i].Data.ObjectId, actions[i]);
            action.Map = MapOf(actions[i]);
            _actions[i] = action;
            _actionsById.Add(action.Id, action);
        }

        _wasActive = new bool[_actions.Length];
        AssignMapActions();
        RebindingImpl.Load();
        BindingsChanged();
        _context.Logger.Info($"Input holds {_actions.Length} actions and {_maps.Length} maps.");
    }

    /// <summary>Applies what waits for the window: the text input of a focus taken before it existed.</summary>
    public void Start()
    {
        if (TextFocus is not null && _windows?.IsCreated == true)
        {
            _windows.MainWindow.StartTextInput();
        }
    }

    public void Release()
    {
        _windowEvents?.Dispose();
        if (_relative && _windows?.IsCreated == true)
        {
            _windows.MainWindow.RelativeMouseMode = false;
        }

        _relative = false;
    }

    public void ReadSettings()
    {
        _sensitivity = (float)_context.Settings.GetFloat(InputSettings.MouseSensitivity);
        _invertY = _context.Settings.GetBool(InputSettings.InvertY);
    }

    public void BindingsChanged() => _bindingsChanged = true;

    public void MapsChanged() => _mapsChanged = true;

    public void LayersChanged() => _layersChanged = true;

    public void RemoveLayer(InputLayer layer)
    {
        _layers.Remove(layer);
        foreach (InputSampler sampler in layer.State.Samplers.ToArray())
        {
            sampler.Dispose();
        }

        _layersChanged = true;
    }

    public void SetTextFocus(InputLayer? layer)
    {
        if (TextFocus == layer)
        {
            return;
        }

        TextFocus = layer;
        _layersChanged = true;
        if (_windows?.IsCreated == true)
        {
            if (layer is null)
            {
                _windows.MainWindow.StopTextInput();
            }
            else
            {
                _windows.MainWindow.StartTextInput();
            }
        }
    }

    public void SetListening(InputListening? listening)
    {
        _listening?.Dispose();
        _listening = listening;
    }

    /// <summary>Applies a reload of actions; the choices of the player stay.</summary>
    public void ReloadActions(ResourceReload reload)
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new input action needs a restart of the game");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed input action needs a restart of the game");
        }

        var changed = reload.Changed.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        foreach (ActionDefinition definition in _context.Resources.GetAll<ActionDefinition>(InputNames.ActionType))
        {
            if (!changed.Contains(definition.Data.ObjectId) || !_actionsById.TryGetValue(definition.Data.ObjectId, out InputAction? action))
            {
                continue;
            }

            if (definition.ValueKind != action.ValueKind)
            {
                reload.Refuse(action.Id, $"the kind of value changes from {action.ValueKind} to {definition.ValueKind}, which needs a restart of the game");
                continue;
            }

            action.Redefine(definition);
            action.Map = MapOf(definition);
        }

        AssignMapActions();
        BindingsChanged();
    }

    /// <summary>Applies a reload of maps: their names change in place.</summary>
    public void ReloadMaps(ResourceReload reload)
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new input map needs a restart of the game");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed input map needs a restart of the game");
        }

        foreach (MapDefinition definition in _context.Resources.GetAll<MapDefinition>(InputNames.MapType))
        {
            if (_mapsById.TryGetValue(definition.Data.ObjectId, out InputMap? map))
            {
                map.DisplayName = definition.DisplayName;
                if (definition.Active != map.ActiveByDefault)
                {
                    reload.Refuse(map.Id, "whether the map is active when the game starts changes at the next start");
                }
            }
        }
    }

    /// <summary>The start of each frame, after nFrames pumped the events: evaluates the actions of every layer.</summary>
    public void Run(in PhaseTime time)
    {
        _now += time.Delta.TotalSeconds;
        if (_windows is null)
        {
            return;
        }

        if (_bindingsChanged)
        {
            _bindingsChanged = false;
            _index = BindingIndex.Build(_actions);
            foreach (InputLayer layer in _layers)
            {
                layer.State.Reset(_index, _actions);
            }
        }

        Order();
        foreach (InputLayer layer in _ordered)
        {
            layer.State.BeginFrame(_now);
        }

        if (_mapsChanged)
        {
            _mapsChanged = false;
            for (int a = 0; a < _actions.Length; a++)
            {
                bool active = _actions[a].IsActive;
                if (_wasActive[a] && !active)
                {
                    foreach (InputLayer layer in _ordered)
                    {
                        layer.State.ResetAction(a);
                    }
                }

                _wasActive[a] = active;
            }
        }

        if (_focusLost)
        {
            _focusLost = false;
            foreach (InputLayer layer in _ordered)
            {
                layer.State.ReleaseDevice(keyboard: true, forget: true);
                layer.State.ReleaseDevice(keyboard: false, forget: true);
            }
        }

        UpdateReception();
        IRawInput input = _windows.Input;
        foreach (RawInputEvent e in input.Events)
        {
            Dispatch(e, input);
        }

        foreach (InputLayer layer in _ordered)
        {
            if (IsLive(layer))
            {
                layer.State.EndFrame(_sensitivity, _invertY);
            }
        }

        ApplyCursor();
    }

    /// <summary>Releases what no longer reaches each layer: a device captured above it, the text focus, a disabled layer.</summary>
    private void UpdateReception()
    {
        bool keyboard = true;
        bool mouse = true;
        foreach (InputLayer layer in _ordered)
        {
            LayerState state = layer.State;
            if (!IsLive(layer))
            {
                state.ReleaseDevice(keyboard: true, forget: true);
                state.ReleaseDevice(keyboard: false, forget: true);
                continue;
            }

            state.KeyboardActions = keyboard && TextFocus is null;
            if (!keyboard)
            {
                state.ReleaseDevice(keyboard: true, forget: true);
            }
            else if (!state.KeyboardActions)
            {
                state.ReleaseDevice(keyboard: true, forget: false);
            }

            if (!mouse)
            {
                state.ReleaseDevice(keyboard: false, forget: true);
            }

            keyboard &= (layer.Capture & InputCapture.Keyboard) == 0 && TextFocus != layer;
            mouse &= (layer.Capture & InputCapture.Mouse) == 0;
        }
    }

    private void Dispatch(in RawInputEvent e, IRawInput input)
    {
        if (_listening is not null && RebindingImpl.Listen(_listening, e))
        {
            if (!_listening.IsListening)
            {
                _listening = null;
            }

            // A release still reaches the layers that saw the press.
            if (e.Kind is not (RawInputKind.KeyUp or RawInputKind.MouseButtonUp))
            {
                return;
            }
        }

        switch (e.Kind)
        {
            case RawInputKind.KeyDown:
                DeliverDown(e, input, BindingIndex.KeySlot(e.Scancode), keyboard: true);
                break;
            case RawInputKind.KeyUp:
                DeliverUp(e, input, BindingIndex.KeySlot(e.Scancode));
                break;
            case RawInputKind.MouseButtonDown:
                DeliverDown(e, input, BindingIndex.MouseSlot(e.Button), keyboard: false);
                break;
            case RawInputKind.MouseButtonUp:
                DeliverUp(e, input, BindingIndex.MouseSlot(e.Button));
                break;
            case RawInputKind.MouseMotion or RawInputKind.MouseWheel:
                DeliverMouse(e, input);
                break;
            default:
                if (TextFocus is { } focus && IsLive(focus) && focus.Handler is { } handler)
                {
                    try
                    {
                        handler.OnText(e, input.GetText(e));
                    }
                    catch (Exception exception) when (exception is not EngineStopException)
                    {
                        focus.Module.ReportFault(exception);
                    }
                }

                break;
        }
    }

    private void DeliverDown(in RawInputEvent e, IRawInput input, int slot, bool keyboard)
    {
        foreach (InputLayer layer in _ordered)
        {
            if (!IsLive(layer))
            {
                continue;
            }

            if (slot >= 0 && !e.IsRepeat)
            {
                layer.State.MarkDown(slot);
            }

            if (Raw(layer, e, input) == InputResult.Consume)
            {
                return;
            }

            if (slot >= 0 && !e.IsRepeat && (!keyboard || TextFocus is null) && layer.State.ButtonDown(slot))
            {
                return;
            }

            InputCapture device = keyboard ? InputCapture.Keyboard : InputCapture.Mouse;
            if ((layer.Capture & device) != 0 || (keyboard && TextFocus == layer))
            {
                return;
            }
        }
    }

    private void DeliverUp(in RawInputEvent e, IRawInput input, int slot)
    {
        foreach (InputLayer layer in _ordered)
        {
            if (IsLive(layer) && slot >= 0 && layer.State.IsDown(slot))
            {
                Raw(layer, e, input);
                layer.State.ButtonUp(slot);
            }
        }
    }

    private void DeliverMouse(in RawInputEvent e, IRawInput input)
    {
        foreach (InputLayer layer in _ordered)
        {
            if (!IsLive(layer))
            {
                continue;
            }

            if (Raw(layer, e, input) == InputResult.Consume)
            {
                return;
            }

            if (e.Kind == RawInputKind.MouseMotion)
            {
                layer.State.Motion(e.DeltaX, e.DeltaY);
            }
            else
            {
                layer.State.Wheel(e.DeltaX, e.DeltaY);
                if (WheelSteps(layer.State, e))
                {
                    return;
                }
            }

            if ((layer.Capture & InputCapture.Mouse) != 0)
            {
                return;
            }
        }
    }

    /// <summary>Presses and releases the steps of the wheel at once.</summary>
    private static bool WheelSteps(LayerState state, in RawInputEvent e)
    {
        bool consumed = false;
        if (e.DeltaY != 0)
        {
            consumed |= Step(state, e.DeltaY > 0 ? MouseControl.WheelUp : MouseControl.WheelDown);
        }

        if (e.DeltaX != 0)
        {
            consumed |= Step(state, e.DeltaX > 0 ? MouseControl.WheelRight : MouseControl.WheelLeft);
        }

        return consumed;

        static bool Step(LayerState state, MouseControl control)
        {
            int slot = BindingIndex.MouseSlot(control);
            bool consumed = state.ButtonDown(slot);
            state.ButtonUp(slot);
            return consumed;
        }
    }

    private static InputResult Raw(InputLayer layer, in RawInputEvent e, IRawInput input)
    {
        if (!layer.ReceivesRawInput || layer.Handler is not { } handler)
        {
            return InputResult.Pass;
        }

        try
        {
            return handler.OnRawInput(e, input);
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            layer.Module.ReportFault(exception);
            return InputResult.Pass;
        }
    }

    /// <summary>Puts the mouse in relative mode when the highest layer that receives the mouse asks for it.</summary>
    private void ApplyCursor()
    {
        bool relative = false;
        foreach (InputLayer layer in _ordered)
        {
            if (!IsLive(layer))
            {
                continue;
            }

            if (layer.Cursor == CursorRequest.Relative)
            {
                relative = true;
                break;
            }

            if (layer.Cursor == CursorRequest.Cursor || (layer.Capture & InputCapture.Mouse) != 0)
            {
                break;
            }
        }

        if (relative != _relative && _windows!.IsCreated)
        {
            _relative = relative;
            _windows.MainWindow.RelativeMouseMode = relative;
        }
    }

    private static bool IsLive(InputLayer layer) => layer.Enabled && !layer.IsRemoved && layer.Module.IsModuleActive(layer.Module.Module.Id);

    private void Order()
    {
        if (!_layersChanged)
        {
            return;
        }

        _layersChanged = false;
        _ordered = _layers.OrderByDescending(l => l.Priority).ThenByDescending(l => l.Sequence).ToArray();
    }

    private InputMap? MapOf(ActionDefinition definition)
    {
        if (definition.MapId.Length == 0)
        {
            return null;
        }

        if (_mapsById.TryGetValue(definition.MapId, out InputMap? map))
        {
            return map;
        }

        _context.Logger.Warning($"Input action '{definition.Data.ObjectId}' names the map '{definition.MapId}', which is not loaded; the action has no map.");
        return null;
    }

    private void AssignMapActions()
    {
        foreach (InputMap map in _maps)
        {
            map.Actions = _actions.Where(a => a.Map == map).ToArray();
        }

        for (int a = 0; a < _wasActive.Length; a++)
        {
            _wasActive[a] = _actions[a].IsActive;
        }
    }
}

/// <summary>The names of the configuration keys of nInput.</summary>
public static class InputSettings
{
    /// <summary>The multiplier of the movements of the mouse: <c>input.mouseSensitivity</c>, a float from 0.01 to 100, 1 by default.</summary>
    public const string MouseSensitivity = "input.mouseSensitivity";

    /// <summary>Whether a movement of the mouse up reads as down: <c>input.invertY</c>, a bool, false by default.</summary>
    public const string InvertY = "input.invertY";
}
