// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Audio.Schemas;
using Nobara.AudioToolkit;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.Audio.Build;

/// <summary>
/// What the build makes of one sound file: an Ogg Vorbis source is embedded
/// as it is, unless a sound asks for mono; a WAV or FLAC source is decoded and
/// encoded with libvorbis. Every file embedded is read back by the decoder of
/// the module, so that a file the game cannot play fails the build.
/// </summary>
internal static class SoundConversion
{
    public static byte[] Convert(FileContext context, List<(string Sound, Sound Content, Variant Variant)> uses)
    {
        bool mono = uses.Exists(u => u.Content.Mono);
        float quality = uses.Max(u => u.Variant.HasQuality ? u.Variant.Quality : u.Content.HasQuality ? u.Content.Quality : AudioRules.DefaultQuality);
        string usedBy = string.Join(", ", uses.Select(u => u.Sound).Distinct());

        byte[] output;
        int channels;
        try
        {
            (output, channels) = AudioFile.Detect(context.Content) switch
            {
                AudioFormat.Ogg => FromOgg(context.Content, mono, quality, context.Path),
                AudioFormat.Wav or AudioFormat.Flac => FromSource(AudioFile.Decode(context.Content), mono, quality, context.Path),
                _ => throw new AudioFormatException("the file is not Ogg Vorbis, WAV, or FLAC"),
            };
        }
        catch (Exception exception) when (exception is AudioFormatException or InvalidDataException or NotSupportedException)
        {
            context.Error(AudioPlugin.UnsupportedFile, $"{exception.Message} (used by {usedBy})");
            return context.Content;
        }
        catch (ChannelCountException exception)
        {
            context.Error(AudioPlugin.TooManyChannels, $"the file has {exception.Channels} channels; a sound is mono or stereo (used by {usedBy})");
            return context.Content;
        }

        if (channels == 2)
        {
            foreach (string sound in uses.Where(u => u.Content.Spatial is not null).Select(u => u.Sound).Distinct())
            {
                context.Warning(AudioPlugin.StereoSpatial, $"the file is stereo and {sound} is spatial: backends place mono sounds only; add <Mono/> to the sound to mix it to mono at build time");
            }
        }

        return output;
    }

    private static (byte[] Output, int Channels) FromOgg(byte[] content, bool mono, float quality, string path)
    {
        // The whole stream is decoded, so that a file damaged after its headers fails the build.
        float[] samples = Decode(content, out OggInfo info);
        CheckChannels(info.Channels);
        if (!mono || info.Channels == 1)
        {
            return (content, info.Channels);
        }

        // Mixed to mono, then encoded again: one more lossy pass, the price of keeping one rule for every source.
        PcmAudio audio = new PcmAudio(info.Channels, info.SampleRate, samples).ToMono();
        return (Encode(audio, quality, path), 1);
    }

    private static (byte[] Output, int Channels) FromSource(PcmAudio audio, bool mono, float quality, string path)
    {
        CheckChannels(audio.Channels);
        if (mono)
        {
            audio = audio.ToMono();
        }

        return (Encode(audio, quality, path), audio.Channels);
    }

    private static byte[] Encode(PcmAudio audio, float quality, string path)
    {
        byte[] ogg = VorbisEncoder.Encode(audio, quality, Serial(path));

        // The decoder of the game decodes every file it embeds, from start to end.
        Decode(ogg, out OggInfo info);
        if (info.Channels != audio.Channels || info.SampleRate != audio.SampleRate || info.Frames != audio.Frames)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"the encoded file reads back as {info.Channels} channels at {info.SampleRate} Hz for {info.Frames} frames, instead of {audio.Channels} at {audio.SampleRate} Hz for {audio.Frames}"));
        }

        return ogg;
    }

    // Decodes a whole stream with the decoder of the game; a stream shorter than it declares is damaged.
    private static float[] Decode(byte[] ogg, out OggInfo info)
    {
        float[] samples = OggVorbis.DecodeAll(ogg, out info);
        if (samples.Length != info.Frames * info.Channels)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"the Ogg Vorbis stream declares {info.Frames} frames and decodes to {samples.Length / Math.Max(1, info.Channels)}"));
        }

        return samples;
    }

    private static void CheckChannels(int channels)
    {
        if (channels > 2)
        {
            throw new ChannelCountException(channels);
        }
    }

    // The serial number of the Ogg stream, from the path: the same source always gives the same bytes.
    private static uint Serial(string path)
    {
        uint hash = 2166136261;
        foreach (char c in path)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    private sealed class ChannelCountException(int channels) : Exception($"{channels} channels")
    {
        public int Channels { get; } = channels;
    }
}
