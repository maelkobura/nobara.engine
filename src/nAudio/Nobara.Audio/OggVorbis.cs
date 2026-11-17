// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using NVorbis;

namespace Nobara.Audio;

/// <summary>What the headers of an Ogg Vorbis file tell.</summary>
/// <param name="Channels">1 or 2 for a sound that nAudio plays.</param>
/// <param name="SampleRate">The frames per second.</param>
/// <param name="Frames">The length, in frames: one sample of each channel.</param>
internal readonly record struct OggInfo(int Channels, int SampleRate, long Frames);

/// <summary>The access to NVorbis, the Ogg Vorbis decoder, shared by the module and its build plugin.</summary>
internal static class OggVorbis
{
    /// <summary>Gets whether bytes start like an Ogg file.</summary>
    public static bool IsOgg(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';

    /// <summary>Opens a decoder over a whole file in memory.</summary>
    /// <exception cref="InvalidDataException">The bytes are not an Ogg Vorbis stream.</exception>
    public static VorbisReader Open(byte[] data)
    {
        if (!IsOgg(data))
        {
            throw new InvalidDataException("the file is not an Ogg file");
        }

        try
        {
            return new VorbisReader(new MemoryStream(data, writable: false), closeOnDispose: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or EndOfStreamException or NotSupportedException)
        {
            throw new InvalidDataException($"the file is not a readable Ogg Vorbis stream: {exception.Message}", exception);
        }
    }

    /// <summary>Reads the headers of a file: its channels, its rate, and its length.</summary>
    /// <exception cref="InvalidDataException">The bytes are not an Ogg Vorbis stream.</exception>
    public static OggInfo ReadInfo(byte[] data)
    {
        using VorbisReader reader = Open(data);
        return new OggInfo(reader.Channels, reader.SampleRate, reader.TotalSamples);
    }

    /// <summary>Decodes a whole file to interleaved samples from -1 to 1, for the build and the tests; the length is the one the stream declares.</summary>
    public static float[] DecodeAll(byte[] data, out OggInfo info)
    {
        using VorbisReader reader = Open(data);
        info = new OggInfo(reader.Channels, reader.SampleRate, reader.TotalSamples);
        var samples = new List<float>((int)Math.Min(int.MaxValue / 2, info.Frames * info.Channels));
        float[] buffer = new float[4096 * reader.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        // NVorbis 0.10.5 may decode a few frames past the length that the last page declares.
        long length = info.Frames * info.Channels;
        if (samples.Count > length)
        {
            samples.RemoveRange((int)length, samples.Count - (int)length);
        }

        return samples.ToArray();
    }
}
