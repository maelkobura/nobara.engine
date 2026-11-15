// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Schemas;
using Nobara.Core;

namespace Nobara.Audio;

/// <summary>How the volume of a spatial sound falls between its reference and maximum distances.</summary>
public enum SoundRolloff
{
    /// <summary>The volume falls with the inverse of the distance, as in the physical world.</summary>
    Inverse,

    /// <summary>The volume falls linearly to silence at the maximum distance.</summary>
    Linear,

    /// <summary>The volume falls with a power of the distance.</summary>
    Exponential,
}

/// <summary>What keeps a real voice when there are not enough of them.</summary>
public enum SoundPriority
{
    /// <summary>Lost first.</summary>
    Low,

    /// <summary>The default.</summary>
    Normal,

    /// <summary>Kept before the others.</summary>
    High,
}

/// <summary>A factor drawn for each play: a fixed value when <see cref="Min"/> equals <see cref="Max"/>.</summary>
/// <param name="Min">The low end.</param>
/// <param name="Max">The high end.</param>
public readonly record struct SoundRange(float Min, float Max)
{
    /// <summary>Gets whether the value is fixed.</summary>
    public bool IsFixed => Min == Max;

    /// <summary>Gets the value at a position of the range.</summary>
    /// <param name="t">From 0, the low end, to 1, the high end.</param>
    /// <returns>The value.</returns>
    [HotPath]
    public float Lerp(float t) => Min + ((Max - Min) * t);
}

/// <summary>Where a spatial sound is heard.</summary>
/// <param name="ReferenceDistance">The distance under which the sound plays at full volume.</param>
/// <param name="MaxDistance">The distance beyond which it is not heard.</param>
/// <param name="Rolloff">How the volume falls between them.</param>
public readonly record struct SoundSpatial(float ReferenceDistance, float MaxDistance, SoundRolloff Rolloff);

/// <summary>
/// A sound event of one context: what plays when a module asks for
/// <c>sound:example.sounds.stonebreak</c>, with its variants and its settings.
/// </summary>
/// <remarks>
/// Its <see cref="Index"/> is the same on every side and in every load of the
/// same modules: sounds are numbered by identifier, in ordinal order, when the
/// registry freezes.
/// </remarks>
public sealed class SoundEvent
{
    private readonly float _totalWeight;

    internal SoundEvent(SoundDefinition definition, int index, AudioBus bus)
    {
        Definition = definition;
        Index = index;
        Bus = bus;
        Sound content = definition.Content;
        ObjectId = definition.Data.ObjectId;
        (float volumeMin, float volumeMax) = AudioRules.Resolve(content.Volume, 1f);
        (float pitchMin, float pitchMax) = AudioRules.Resolve(content.Pitch, 1f);
        Volume = new SoundRange(volumeMin, volumeMax);
        Pitch = new SoundRange(pitchMin, pitchMax);
        if (content.Spatial is { } spatial)
        {
            Spatial = new SoundSpatial(
                spatial.HasRefDistance ? spatial.RefDistance : AudioRules.DefaultRefDistance,
                spatial.HasMaxDistance ? spatial.MaxDistance : AudioRules.DefaultMaxDistance,
                spatial.Rolloff switch
                {
                    Rolloff.Linear => SoundRolloff.Linear,
                    Rolloff.Exponential => SoundRolloff.Exponential,
                    _ => SoundRolloff.Inverse,
                });
        }

        Priority = content.Priority switch
        {
            Schemas.Priority.Low => SoundPriority.Low,
            Schemas.Priority.High => SoundPriority.High,
            _ => SoundPriority.Normal,
        };
        MaxInstances = (int)Math.Min(content.MaxInstances, int.MaxValue);
        Loop = content.Loop;
        Streamed = content.Stream;
        Variants = definition.Variants;
        foreach (SoundVariant variant in Variants)
        {
            _totalWeight += variant.Weight;
        }
    }

    /// <summary>Gets the object identifier: <c>sound:example.sounds.stonebreak</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the numeric identifier of the event, from 0, the same on every side that loaded the same modules.</summary>
    public int Index { get; }

    /// <summary>Gets the bus the event plays on.</summary>
    public AudioBus Bus { get; }

    /// <summary>Gets the factor of the volume; 1 when the resource gives none.</summary>
    public SoundRange Volume { get; }

    /// <summary>Gets the factor of the pitch; 1 when the resource gives none.</summary>
    public SoundRange Pitch { get; }

    /// <summary>Gets where the event is heard, or <see langword="null"/> when it does not play at a position.</summary>
    public SoundSpatial? Spatial { get; }

    /// <summary>Gets the priority of the event when voices run short.</summary>
    public SoundPriority Priority { get; }

    /// <summary>Gets the most instances of the event that play at once; 0 for no limit.</summary>
    public int MaxInstances { get; }

    /// <summary>Gets whether the event loops.</summary>
    public bool Loop { get; }

    /// <summary>Gets whether the event is decoded in blocks while it plays, instead of once when it loads.</summary>
    public bool Streamed { get; }

    /// <summary>Gets the variants, in the order written.</summary>
    public IReadOnlyList<SoundVariant> Variants { get; }

    internal SoundDefinition Definition { get; }

    /// <summary>
    /// Draws a variant by weight. The same seed always gives the same variant,
    /// so that two sides that share a seed play the same file.
    /// </summary>
    /// <param name="seed">Any value; a different one for each play.</param>
    /// <returns>The variant.</returns>
    [HotPath]
    public SoundVariant PickVariant(uint seed)
    {
        float target = (float)(Mix(seed) * _totalWeight);
        for (int i = 0; i < Variants.Count - 1; i++)
        {
            target -= Variants[i].Weight;
            if (target < 0)
            {
                return Variants[i];
            }
        }

        return Variants[^1];
    }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;

    // A hash of the seed, from 0 included to 1 excluded (the finalizer of SplitMix64).
    [HotPath]
    internal static double Mix(uint seed)
    {
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}

/// <summary>
/// A bus of the mixing tree of one context: its volume multiplies the volume
/// of its children. The tree is declared in data, with the resource type
/// <c>Bus</c>; nAudio provides <c>bus:nobara.audio.master</c> and its children.
/// </summary>
public sealed class AudioBus
{
    private readonly List<AudioBus> _children = [];

    internal AudioBus(string objectId, int index, bool adjustable, string displayName)
    {
        ObjectId = objectId;
        Index = index;
        Adjustable = adjustable;
        DisplayName = displayName;
    }

    /// <summary>Gets the object identifier: <c>bus:nobara.audio.effects</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the index of the bus, from 0 for the master bus, then by identifier in ordinal order.</summary>
    public int Index { get; }

    /// <summary>Gets the parent, or <see langword="null"/> for the master bus.</summary>
    public AudioBus? Parent { get; internal set; }

    /// <summary>Gets the children, by identifier in ordinal order.</summary>
    public IReadOnlyList<AudioBus> Children => _children;

    /// <summary>Gets whether the player adjusts the volume of the bus in the settings.</summary>
    public bool Adjustable { get; }

    /// <summary>Gets the i18n key of the name shown in the settings, or an empty string.</summary>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;

    internal void AddChild(AudioBus child) => _children.Add(child);
}
