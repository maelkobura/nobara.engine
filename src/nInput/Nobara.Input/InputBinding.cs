// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using System.Text;

namespace Nobara.Input;

/// <summary>The kind of value an action gives.</summary>
public enum InputValueKind : byte
{
    /// <summary>Pressed or not, with the edges and the duration of the press: jump, attack, inventory.</summary>
    Button,

    /// <summary>A value from 0 to 1, read as a button above a threshold: accelerate, aim. A key gives 0 or 1.</summary>
    Float,

    /// <summary>A value from -1 to 1, or a movement for the mouse and the wheel: zoom, item wheel.</summary>
    Axis1D,

    /// <summary>Two values: move, look.</summary>
    Axis2D,
}

/// <summary>The form of a binding.</summary>
public enum BindingKind : byte
{
    /// <summary>One key or button: <c>&lt;Key&gt;Key.Space&lt;/Key&gt;</c>.</summary>
    Key,

    /// <summary>One axis: <c>&lt;Axis&gt;Mouse.Delta&lt;/Axis&gt;</c>.</summary>
    Axis,

    /// <summary>Two keys or buttons that make an axis: negative, then positive.</summary>
    Composite1D,

    /// <summary>Four keys or buttons that make a 2D axis: up, down, left, right.</summary>
    Composite2D,

    /// <summary>Keys or buttons held together; the last one triggers.</summary>
    Combo,
}

/// <summary>
/// One binding of an action: a key, an axis, a composite, or a combination.
/// Bindings are immutable and compared by value.
/// </summary>
public sealed class InputBinding : IEquatable<InputBinding>
{
    private InputBinding(BindingKind kind, ImmutableArray<InputControl> controls)
    {
        Kind = kind;
        Controls = controls;
    }

    /// <summary>Gets the form of the binding.</summary>
    public BindingKind Kind { get; }

    /// <summary>
    /// Gets the controls: the key of <see cref="BindingKind.Key"/>, the axis of
    /// <see cref="BindingKind.Axis"/>, negative then positive for
    /// <see cref="BindingKind.Composite1D"/>, up, down, left, right for
    /// <see cref="BindingKind.Composite2D"/>, the keys of a combination in order.
    /// </summary>
    public ImmutableArray<InputControl> Controls { get; }

    /// <summary>Gets the shape of the value the binding gives.</summary>
    public InputControlKind Shape => Kind switch
    {
        BindingKind.Axis => Controls[0].Kind,
        BindingKind.Composite1D => InputControlKind.Axis1D,
        BindingKind.Composite2D => InputControlKind.Axis2D,
        _ => InputControlKind.Button,
    };

    /// <summary>Creates a binding on one key or button.</summary>
    /// <param name="button">A control of kind <see cref="InputControlKind.Button"/>.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException"><paramref name="button"/> is not a button.</exception>
    public static InputBinding Key(InputControl button) => Create(BindingKind.Key, [button]);

    /// <summary>Creates a binding on one axis.</summary>
    /// <param name="axis">A control of kind <see cref="InputControlKind.Axis1D"/> or <see cref="InputControlKind.Axis2D"/>.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException"><paramref name="axis"/> is not an axis.</exception>
    public static InputBinding Axis(InputControl axis) => Create(BindingKind.Axis, [axis]);

    /// <summary>Creates an axis made of two buttons: -1 while <paramref name="negative"/> is held, 1 while <paramref name="positive"/> is.</summary>
    /// <param name="negative">A button.</param>
    /// <param name="positive">A button.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException">A control is not a button, or both are the same.</exception>
    public static InputBinding Composite1D(InputControl negative, InputControl positive) => Create(BindingKind.Composite1D, [negative, positive]);

    /// <summary>Creates a 2D axis made of four buttons; up is positive Y, right is positive X.</summary>
    /// <param name="up">A button.</param>
    /// <param name="down">A button.</param>
    /// <param name="left">A button.</param>
    /// <param name="right">A button.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException">A control is not a button, or two are the same.</exception>
    public static InputBinding Composite2D(InputControl up, InputControl down, InputControl left, InputControl right) =>
        Create(BindingKind.Composite2D, [up, down, left, right]);

    /// <summary>Creates a combination: the last control triggers it while the others are held.</summary>
    /// <param name="keys">Two or more distinct buttons; only the last may be a step of the wheel.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException">Fewer than two controls, a control that is not a button, the same control twice, or a step of the wheel that is not last.</exception>
    public static InputBinding Combo(params InputControl[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Create(BindingKind.Combo, keys);
    }

    /// <summary>Creates a binding, or tells why it is not valid.</summary>
    /// <param name="kind">The form of the binding.</param>
    /// <param name="controls">Its controls, in the order of <see cref="Controls"/>.</param>
    /// <param name="binding">The binding, or <see langword="null"/>.</param>
    /// <param name="error">Why the binding is not valid, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the binding is valid.</returns>
    public static bool TryCreate(BindingKind kind, ReadOnlySpan<InputControl> controls, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InputBinding? binding, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        binding = null;
        error = Check(kind, controls);
        if (error is not null)
        {
            return false;
        }

        binding = new InputBinding(kind, controls.ToImmutableArray());
        return true;
    }

    /// <summary>Tells whether the binding can give the value of an action of a kind.</summary>
    /// <param name="value">The kind of value of the action.</param>
    /// <returns>
    /// <see langword="true"/> for a key or a combination on a button, a float,
    /// or a 1D axis; a 1D axis on a button, a float, or a 1D axis; a
    /// <see cref="BindingKind.Composite1D"/> on a 1D axis; a 2D axis or a
    /// <see cref="BindingKind.Composite2D"/> on a 2D axis.
    /// </returns>
    public bool Accepts(InputValueKind value) => (value, Shape) switch
    {
        (InputValueKind.Axis2D, InputControlKind.Axis2D) => true,
        (InputValueKind.Axis2D, _) => false,
        (_, InputControlKind.Axis2D) => false,
        (InputValueKind.Button or InputValueKind.Float, _) => Kind != BindingKind.Composite1D,
        _ => true,
    };

    /// <summary>
    /// Tells whether two bindings can be triggered by the same input: they
    /// share a key or an axis, or are combinations of the same keys. A
    /// combination does not conflict with a single key it contains, since the
    /// most specific binding wins.
    /// </summary>
    /// <param name="other">Another binding.</param>
    /// <returns><see langword="true"/> when they conflict.</returns>
    public bool ConflictsWith(InputBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (ImmutableArray<InputControl> mine in Groups())
        {
            foreach (ImmutableArray<InputControl> theirs in other.Groups())
            {
                if (SameGroup(mine, theirs))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public bool Equals(InputBinding? other) =>
        other is not null && Kind == other.Kind && Controls.AsSpan().SequenceEqual(other.Controls.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as InputBinding);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        foreach (InputControl control in Controls)
        {
            hash.Add(control);
        }

        return hash.ToHashCode();
    }

    /// <summary>Gets a readable form: <c>Key.Space</c>, <c>Key.LeftControl+Key.Digit4</c>, <c>Composite2D(Key.W, Key.S, Key.A, Key.D)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        switch (Kind)
        {
            case BindingKind.Key or BindingKind.Axis:
                text.Append(Controls[0].Name);
                break;
            case BindingKind.Combo:
                text.AppendJoin('+', Controls.Select(c => c.Name));
                break;
            default:
                text.Append(Kind).Append('(').AppendJoin(", ", Controls.Select(c => c.Name)).Append(')');
                break;
        }

        return text.ToString();
    }

    /// <summary>Gets the groups of keys that trigger the binding: one per key of a composite, the whole combination, the axis.</summary>
    private IEnumerable<ImmutableArray<InputControl>> Groups()
    {
        if (Kind is BindingKind.Composite1D or BindingKind.Composite2D)
        {
            foreach (InputControl control in Controls)
            {
                yield return [control];
            }
        }
        else
        {
            yield return Controls;
        }
    }

    private static bool SameGroup(ImmutableArray<InputControl> a, ImmutableArray<InputControl> b)
    {
        if (a.Length != b.Length || !a[^1].Overlaps(b[^1]))
        {
            return false;
        }

        // The modifiers match in any order.
        for (int i = 0; i < a.Length - 1; i++)
        {
            bool found = false;
            for (int j = 0; j < b.Length - 1 && !found; j++)
            {
                found = a[i].Overlaps(b[j]);
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static InputBinding Create(BindingKind kind, ReadOnlySpan<InputControl> controls) =>
        TryCreate(kind, controls, out InputBinding? binding, out string? error) ? binding : throw new ArgumentException(error, nameof(controls));

    private static string? Check(BindingKind kind, ReadOnlySpan<InputControl> controls)
    {
        foreach (InputControl control in controls)
        {
            if (control.Device == InputDevice.None)
            {
                return "a control has no device";
            }
        }

        int expected = kind switch
        {
            BindingKind.Key or BindingKind.Axis => 1,
            BindingKind.Composite1D => 2,
            BindingKind.Composite2D => 4,
            _ => -1,
        };
        if (expected > 0 && controls.Length != expected)
        {
            return $"a {kind} binding has {expected} control{(expected > 1 ? "s" : string.Empty)}, not {controls.Length}";
        }

        if (kind == BindingKind.Combo && controls.Length < 2)
        {
            return "a combination holds at least two keys or buttons";
        }

        for (int i = 0; i < controls.Length; i++)
        {
            InputControl control = controls[i];
            if (kind == BindingKind.Axis)
            {
                if (control.Kind == InputControlKind.Button)
                {
                    return $"'{control.Name}' is a key or a button, not an axis; bind it with <Key>";
                }
            }
            else if (control.Kind != InputControlKind.Button)
            {
                return $"'{control.Name}' is an axis, not a key or a button; bind it with <Axis>";
            }

            if (kind == BindingKind.Combo && control.IsWheelStep && i < controls.Length - 1)
            {
                return $"'{control.Name}' cannot be held: a step of the wheel can only be the last key of a combination";
            }

            for (int j = 0; j < i; j++)
            {
                if (controls[j].Overlaps(control))
                {
                    return $"'{controls[j].Name}' and '{control.Name}' are the same key in one binding";
                }
            }
        }

        return null;
    }
}
