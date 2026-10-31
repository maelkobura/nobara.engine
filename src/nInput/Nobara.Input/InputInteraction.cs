// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;

namespace Nobara.Input;

/// <summary>How a press of the bindings of an action becomes a trigger of the action.</summary>
public enum InteractionKind : byte
{
    /// <summary>The action is pressed while a binding is held: the default.</summary>
    Press,

    /// <summary>The action is pressed and released at once when the binding is released.</summary>
    Release,

    /// <summary>The action is pressed once the binding has been held for <see cref="InputInteraction.Seconds"/>, and released with it.</summary>
    Hold,

    /// <summary>The action is pressed and released at once when a press shorter than <see cref="InputInteraction.Seconds"/> ends.</summary>
    Tap,

    /// <summary>The action is pressed by a second press that starts at most <see cref="InputInteraction.Seconds"/> after the first, and released with it.</summary>
    DoubleTap,

    /// <summary>A press turns the action on, the next one turns it off: the action is held in between.</summary>
    Toggle,
}

/// <summary>An interaction and its duration, as an action declares it or as the player chooses it.</summary>
public readonly record struct InputInteraction
{
    private InputInteraction(InteractionKind kind, double seconds)
    {
        Kind = kind;
        Seconds = seconds;
    }

    /// <summary>Gets the interaction <see cref="InteractionKind.Press"/>, the default.</summary>
    public static InputInteraction Press => default;

    /// <summary>Gets the interaction <see cref="InteractionKind.Release"/>.</summary>
    public static InputInteraction Release => new(InteractionKind.Release, 0);

    /// <summary>Gets the interaction <see cref="InteractionKind.Toggle"/>.</summary>
    public static InputInteraction Toggle => new(InteractionKind.Toggle, 0);

    /// <summary>Gets the interaction.</summary>
    public InteractionKind Kind { get; }

    /// <summary>Gets the duration of <see cref="InteractionKind.Hold"/>, the longest press of <see cref="InteractionKind.Tap"/>, or the window of <see cref="InteractionKind.DoubleTap"/>, in seconds; 0 for the others.</summary>
    public double Seconds { get; }

    /// <summary>Gets the interaction <see cref="InteractionKind.Hold"/>.</summary>
    /// <param name="duration">How long the binding is held before the action is pressed, in seconds, more than 0.</param>
    /// <returns>The interaction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is not more than 0.</exception>
    public static InputInteraction Hold(double duration) => new(InteractionKind.Hold, Positive(duration, nameof(duration)));

    /// <summary>Gets the interaction <see cref="InteractionKind.Tap"/>.</summary>
    /// <param name="max">The longest press that counts as a tap, in seconds, more than 0.</param>
    /// <returns>The interaction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is not more than 0.</exception>
    public static InputInteraction Tap(double max) => new(InteractionKind.Tap, Positive(max, nameof(max)));

    /// <summary>Gets the interaction <see cref="InteractionKind.DoubleTap"/>.</summary>
    /// <param name="window">The longest time between the two presses, in seconds, more than 0.</param>
    /// <returns>The interaction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not more than 0.</exception>
    public static InputInteraction DoubleTap(double window) => new(InteractionKind.DoubleTap, Positive(window, nameof(window)));

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        InteractionKind.Hold => $"Hold(duration={Seconds.ToString(CultureInfo.InvariantCulture)})",
        InteractionKind.Tap => $"Tap(max={Seconds.ToString(CultureInfo.InvariantCulture)})",
        InteractionKind.DoubleTap => $"DoubleTap(window={Seconds.ToString(CultureInfo.InvariantCulture)})",
        _ => Kind.ToString(),
    };

    private static double Positive(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The duration is a number of seconds above 0.");
        }

        return value;
    }
}
