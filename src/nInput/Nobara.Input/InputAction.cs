// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Input;

/// <summary>
/// An input action, as a module declares it in a <c>.nof</c> file: what the
/// game reads instead of keys. Its bindings are the defaults of the resource,
/// unless the player changed them.
/// </summary>
/// <remarks>
/// Each layer reads the state of an action with <see cref="InputLayer.Read"/>.
/// Use it on the loop thread of the client context.
/// </remarks>
public sealed class InputAction
{
    private readonly InputSystem _system;

    internal InputAction(InputSystem system, int index, string id, ActionDefinition definition)
    {
        _system = system;
        Index = index;
        Id = id;
        Definition = definition;
        Bindings = definition.Bindings;
        Interaction = definition.Interaction;
    }

    /// <summary>Gets the object identifier: <c>inputaction:example.input.jump</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the localization key of the name.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>Gets the kind of value of the action.</summary>
    public InputValueKind ValueKind => Definition.ValueKind;

    /// <summary>Gets the map of the action, or <see langword="null"/> for an action that is always active.</summary>
    public InputMap? Map { get; internal set; }

    /// <summary>Gets whether the action is evaluated: it has no map, or its map is active.</summary>
    public bool IsActive => Map is null || Map.IsActive;

    /// <summary>Gets the value from which a float, or an axis bound to a button, reads as pressed, from 0 to 1.</summary>
    public float Threshold => Definition.Threshold;

    /// <summary>Gets the dead zone of the analog inputs of a gamepad, from 0 to 0.95.</summary>
    public float DeadZone => Definition.DeadZone;

    /// <summary>Gets the bindings that the resource declares.</summary>
    public IReadOnlyList<InputBinding> DefaultBindings => Definition.Bindings;

    /// <summary>Gets the interaction that the resource declares.</summary>
    public InputInteraction DefaultInteraction => Definition.Interaction;

    /// <summary>Gets the bindings in use: those of the player, or the defaults.</summary>
    public IReadOnlyList<InputBinding> Bindings { get; private set; }

    /// <summary>Gets the interaction in use: the one the player chose, or the default.</summary>
    public InputInteraction Interaction { get; private set; }

    /// <summary>Gets whether the player changed the bindings.</summary>
    public bool HasCustomBindings { get; private set; }

    /// <summary>Gets whether the player changed the interaction.</summary>
    public bool HasCustomInteraction { get; private set; }

    internal int Index { get; }

    internal ActionDefinition Definition { get; private set; }

    /// <summary>Gets the bindings the player chose, or <see langword="null"/>.</summary>
    internal IReadOnlyList<InputBinding>? CustomBindings => HasCustomBindings ? Bindings : null;

    /// <summary>Gets the interaction the player chose, or <see langword="null"/>.</summary>
    internal InputInteraction? CustomInteraction => HasCustomInteraction ? Interaction : null;

    /// <inheritdoc/>
    public override string ToString() => Id;

    /// <summary>Replaces the definition after a reload; the choices of the player stay.</summary>
    internal void Redefine(ActionDefinition definition)
    {
        Definition = definition;
        if (!HasCustomBindings)
        {
            Bindings = definition.Bindings;
        }

        if (!HasCustomInteraction)
        {
            Interaction = definition.Interaction;
        }
    }

    /// <summary>Sets the bindings of the player, or goes back to the defaults with <see langword="null"/>.</summary>
    internal void SetCustomBindings(IReadOnlyList<InputBinding>? bindings)
    {
        if (bindings is null || bindings.SequenceEqual(Definition.Bindings))
        {
            HasCustomBindings = false;
            Bindings = Definition.Bindings;
        }
        else
        {
            HasCustomBindings = true;
            Bindings = bindings.ToArray();
        }

        _system.BindingsChanged();
    }

    /// <summary>Sets the interaction of the player, or goes back to the default with <see langword="null"/>.</summary>
    internal void SetCustomInteraction(InputInteraction? interaction)
    {
        HasCustomInteraction = interaction is { } chosen && chosen != Definition.Interaction;
        Interaction = HasCustomInteraction ? interaction!.Value : Definition.Interaction;
        _system.BindingsChanged();
    }
}

/// <summary>
/// A map of actions, which the game turns on and off together: on foot, in a
/// vehicle, in a menu. An action of an inactive map is not evaluated.
/// </summary>
public sealed class InputMap
{
    private readonly InputSystem _system;
    private bool _active;

    internal InputMap(InputSystem system, string id, string displayName, bool activeByDefault)
    {
        _system = system;
        Id = id;
        DisplayName = displayName;
        ActiveByDefault = activeByDefault;
        _active = activeByDefault;
    }

    /// <summary>Gets the object identifier: <c>inputmap:example.input.on_foot</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the localization key of the name.</summary>
    public string DisplayName { get; internal set; }

    /// <summary>Gets whether the map is active when the game starts.</summary>
    public bool ActiveByDefault { get; }

    /// <summary>
    /// Gets or sets whether the actions of the map are evaluated. The game
    /// turns maps on and off, such as the map of a vehicle when the player
    /// gets in.
    /// </summary>
    /// <remarks>
    /// An action whose map turns off reads as released from the next frame
    /// on; one whose map turns on waits for a new press of its bindings.
    /// </remarks>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active != value)
            {
                _active = value;
                _system.MapsChanged();
            }
        }
    }

    /// <summary>Gets the actions of the map, by identifier.</summary>
    public IReadOnlyList<InputAction> Actions { get; internal set; } = [];

    /// <inheritdoc/>
    public override string ToString() => Id;
}
