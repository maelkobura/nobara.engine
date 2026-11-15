// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Nobara.Audio.Schemas;
using Nobara.AudioToolkit;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.Audio.Build.AudioPlugin))]

namespace Nobara.Audio.Build;

/// <summary>
/// The resource plugin of the types <c>Sound</c> and <c>Bus</c>: it checks
/// the rules that the schema cannot declare, converts WAV and FLAC sources to
/// Ogg Vorbis, mixes stereo to mono when a sound asks for it, and reports a
/// stereo file that a spatial sound uses.
/// </summary>
/// <remarks>
/// The schema already checks the form of the fields, the references to buses,
/// and that the files exist. The tree of buses is checked when nAudio loads
/// it, since its parents may come from other modules.
/// </remarks>
public sealed class AudioPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a sound whose settings are not valid.</summary>
    public const string InvalidSound = "NAUD001";

    /// <summary>The diagnostic of a file in a format that the build does not read, or that is malformed.</summary>
    public const string UnsupportedFile = "NAUD002";

    /// <summary>The warning of a stereo file used by a spatial sound, which a backend plays without position.</summary>
    public const string StereoSpatial = "NAUD003";

    /// <summary>The diagnostic of a bus whose settings are not valid.</summary>
    public const string InvalidBus = "NAUD004";

    /// <summary>The diagnostic of a file with more than two channels.</summary>
    public const string TooManyChannels = "NAUD005";

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = ["Sound", "Bus"];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An override changes some fields only; nAudio checks the result when it loads it.
        if (string.Equals((string?)context.Element.Attribute("override"), "true", StringComparison.Ordinal))
        {
            return;
        }

        switch (context.Message.Name)
        {
            case "Sound":
                ValidateSound(context);
                break;
            case "Bus":
                ValidateBus(context);
                break;
        }
    }

    /// <inheritdoc/>
    public override byte[] ProcessFile(FileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var uses = new List<(string Sound, Sound Content, Variant Variant)>();
        foreach (FileReference reference in context.References)
        {
            if (reference.Element != "Sound")
            {
                continue;
            }

            Sound sound = reference.Parse(Sound.Parser);
            foreach (Variant variant in sound.Variants?.Variant ?? [])
            {
                if (variant.File == context.Path)
                {
                    uses.Add((reference.ObjectId, sound, variant));
                }
            }
        }

        return uses.Count == 0 ? context.Content : SoundConversion.Convert(context, uses);
    }

    private static void ValidateSound(ResourceContext context)
    {
        Sound sound = context.Parse(Sound.Parser);
        List<XElement> variants = context.Content.Element("Variants")?.Elements("Variant").ToList() ?? [];
        foreach ((string message, string element, int variant) in AudioRules.CheckSound(sound))
        {
            XElement? at = variant >= 0 && variant < variants.Count ? variants[variant] : context.Content.Element(element);
            context.Error(InvalidSound, message, at);
        }

        for (int i = 0; i < sound.Variants.Variant.Count; i++)
        {
            string file = sound.Variants.Variant[i].File;
            if (!AudioRules.IsSupportedFile(file))
            {
                XElement? at = i < variants.Count ? variants[i] : null;
                context.Error(UnsupportedFile, $"'{file}' is not a sound file that the build reads: write an .ogg, .wav, or .flac file", at);
            }
        }
    }

    private static void ValidateBus(ResourceContext context)
    {
        Bus bus = context.Parse(Bus.Parser);
        foreach ((string message, string element) in AudioRules.CheckBus(context.ObjectId, bus))
        {
            context.Error(InvalidBus, message, context.Content.Element(element));
        }

        // nAudio provides the root; the buses of other modules hang below it.
        if (bus.Parent.Length == 0)
        {
            context.Error(InvalidBus, $"a bus needs a <Parent>: only {AudioRules.MasterBus} is a root", context.Content);
        }
    }
}
