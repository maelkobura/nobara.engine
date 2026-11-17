// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Schemas;

namespace Nobara.Audio;

/// <summary>
/// The rules of the resources of nAudio that the schema cannot declare,
/// shared by the build plugin and by the registry, which checks them again
/// once overrides are applied.
/// </summary>
internal static class AudioRules
{
    public const string SoundType = "sound";

    public const string BusType = "bus";

    public const string MasterBus = "bus:nobara.audio.master";

    /// <summary>The Vorbis quality of a WAV or FLAC source when neither the variant nor the event gives one.</summary>
    public const float DefaultQuality = 0.4f;

    public const float DefaultRefDistance = 1f;

    public const float DefaultMaxDistance = 32f;

    /// <summary>Gets whether a file of a variant has a source format that the build reads: <c>.ogg</c>, <c>.wav</c>, or <c>.flac</c>.</summary>
    public static bool IsSupportedFile(string path) =>
        path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks a sound event; each problem names the element it is about.</summary>
    /// <returns>The message, the element, and the index of the variant, or -1 when the problem is not in a variant.</returns>
    public static IEnumerable<(string Message, string Element, int Variant)> CheckSound(Sound sound)
    {
        foreach (string problem in CheckRange(sound.Volume, "volume"))
        {
            yield return (problem, "Volume", -1);
        }

        foreach (string problem in CheckRange(sound.Pitch, "pitch"))
        {
            yield return (problem, "Pitch", -1);
        }

        if (sound.Pitch is { } pitch && ((pitch.HasValue && pitch.Value <= 0) || (pitch.HasMin && pitch.Min <= 0)))
        {
            yield return ("the pitch must be greater than 0", "Pitch", -1);
        }

        if (sound.Spatial is { } spatial)
        {
            float reference = spatial.HasRefDistance ? spatial.RefDistance : DefaultRefDistance;
            float max = spatial.HasMaxDistance ? spatial.MaxDistance : DefaultMaxDistance;
            if (reference <= 0)
            {
                yield return ("refDistance must be greater than 0", "Spatial", -1);
            }

            if (max <= reference)
            {
                yield return ($"maxDistance ({max}) must be greater than refDistance ({reference})", "Spatial", -1);
            }
        }

        if (sound.Variants is null)
        {
            yield break;
        }

        for (int i = 0; i < sound.Variants.Variant.Count; i++)
        {
            Variant variant = sound.Variants.Variant[i];
            if (variant.HasWeight && variant.Weight <= 0)
            {
                yield return ($"the weight of '{variant.File}' must be greater than 0", "Variant", i);
            }

            if (variant.HasPitch && variant.Pitch <= 0)
            {
                yield return ($"the pitch of '{variant.File}' must be greater than 0", "Variant", i);
            }
        }
    }

    /// <summary>Checks a bus, without its place in the tree.</summary>
    public static IEnumerable<(string Message, string Element)> CheckBus(string objectId, Bus bus)
    {
        if (bus.Parent == objectId)
        {
            yield return ("a bus cannot be its own parent", "Parent");
        }

        if (bus.Adjustable && bus.DisplayName.Length == 0)
        {
            yield return ("an adjustable bus needs a <DisplayName>, the i18n key shown in the settings", "Adjustable");
        }
    }

    /// <summary>Gets the low and high ends of a range, with its default when absent.</summary>
    public static (float Min, float Max) Resolve(Schemas.Range? range, float fallback)
    {
        if (range is null)
        {
            return (fallback, fallback);
        }

        if (range.HasValue)
        {
            return (range.Value, range.Value);
        }

        return (range.HasMin ? range.Min : fallback, range.HasMax ? range.Max : fallback);
    }

    private static IEnumerable<string> CheckRange(Schemas.Range? range, string name)
    {
        if (range is null)
        {
            yield break;
        }

        if (range.HasValue && (range.HasMin || range.HasMax))
        {
            yield return $"the {name} is a fixed value or a range: write value, or min and max";
        }
        else if (!range.HasValue && range.HasMin != range.HasMax)
        {
            yield return $"the {name} range needs both min and max";
        }
        else if (!range.HasValue && !range.HasMin)
        {
            yield return $"the {name} needs value, or min and max";
        }
        else if (range.HasMin && range.Min > range.Max)
        {
            yield return $"the {name} range has min ({range.Min}) greater than max ({range.Max})";
        }
    }
}
