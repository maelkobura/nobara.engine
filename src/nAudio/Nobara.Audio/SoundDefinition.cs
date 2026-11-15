// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Schemas;
using Nobara.Core.Resources;

namespace Nobara.Audio;

/// <summary>
/// A sound resource decoded once per process and shared by every context:
/// its content, and the Ogg Vorbis file of each variant with what its headers
/// tell. Each context builds its own <see cref="SoundEvent"/> from it.
/// </summary>
internal sealed class SoundDefinition
{
    private SoundDefinition(ResourceData data, Sound content, IReadOnlyList<SoundVariant> variants)
    {
        Data = data;
        Content = content;
        Variants = variants;
    }

    /// <summary>Gets the resource, overrides applied.</summary>
    public ResourceData Data { get; }

    /// <summary>Gets the content. Never change it: it is shared.</summary>
    public Sound Content { get; }

    /// <summary>Gets the variants, in the order written.</summary>
    public IReadOnlyList<SoundVariant> Variants { get; }

    /// <summary>Decodes a resource of type <c>sound</c>: its content and the headers of its files.</summary>
    /// <exception cref="InvalidDataException">A file is not an Ogg Vorbis stream.</exception>
    public static SoundDefinition Decode(ModuleResources resources, ResourceData data)
    {
        Sound content = Sound.Parser.ParseFrom(data.Content.Span);
        var variants = new List<SoundVariant>();
        if (content.Variants is not null)
        {
            for (int i = 0; i < content.Variants.Variant.Count; i++)
            {
                Variant variant = content.Variants.Variant[i];
                byte[] file = ReadFile(resources, data, variant.File);
                OggInfo info;
                try
                {
                    info = OggVorbis.ReadInfo(file);
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException($"{data.ObjectId}: '{variant.File}': {exception.Message}", exception);
                }

                variants.Add(new SoundVariant(
                    i,
                    variant.File,
                    variant.HasWeight ? variant.Weight : 1f,
                    variant.HasVolume ? variant.Volume : 1f,
                    variant.HasPitch ? variant.Pitch : 1f,
                    info,
                    file));
            }
        }

        return new SoundDefinition(data, content, variants);
    }

    private static byte[] ReadFile(ModuleResources resources, ResourceData data, string path)
    {
        using Stream stream = resources.OpenFile(data, path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>
/// One file of a sound event: its weight in the draw, its own factors, and
/// what its Ogg Vorbis headers tell. It is the same object in every context.
/// </summary>
public sealed class SoundVariant
{
    internal SoundVariant(int index, string file, float weight, float volume, float pitch, OggInfo info, byte[] encoded)
    {
        Index = index;
        File = file;
        Weight = weight;
        Volume = volume;
        Pitch = pitch;
        Channels = info.Channels;
        SampleRate = info.SampleRate;
        Frames = info.Frames;
        Encoded = encoded;
    }

    /// <summary>Gets the index of the variant in its event, in the order written.</summary>
    public int Index { get; }

    /// <summary>Gets the path of the file as the resource gives it, relative to <c>Resources/</c>; the embedded file is Ogg Vorbis whatever its extension.</summary>
    public string File { get; }

    /// <summary>Gets the weight of the variant in the draw; 1 by default.</summary>
    public float Weight { get; }

    /// <summary>Gets the factor applied to the volume of the event; 1 by default.</summary>
    public float Volume { get; }

    /// <summary>Gets the factor applied to the pitch of the event; 1 by default.</summary>
    public float Pitch { get; }

    /// <summary>Gets the number of channels: 1 (mono) or 2 (stereo).</summary>
    public int Channels { get; }

    /// <summary>Gets the frames per second.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the length in frames, one sample of each channel.</summary>
    public long Frames { get; }

    /// <summary>Gets the length in time.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>Gets the embedded Ogg Vorbis file. Never change it: it is shared.</summary>
    internal byte[] Encoded { get; }
}
