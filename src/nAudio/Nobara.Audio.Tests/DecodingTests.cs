// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using Nobara.Audio.Tests.Support;
using Nobara.Core;

namespace Nobara.Audio.Tests;

/// <summary>Decoding on a client: in memory when nAudio initializes, and in blocks for streams.</summary>
public sealed class DecodingTests : IDisposable
{
    private readonly AudioHarness _harness = new();
    private readonly Engine _engine;

    public DecodingTests()
    {
        string resources = _harness.CopyResources();
        _engine = _harness.WithGame(AudioHarness.Compiled(resources)).Start();
    }

    public void Dispose()
    {
        _engine.Dispose();
        _harness.Dispose();
    }

    private ISoundRegistry Sounds => _harness.Sounds;

    [Fact]
    public void DecodesEverySoundThatDoesNotStreamInMemory()
    {
        Sounds.CompleteDecoding();

        foreach (SoundEvent sound in Sounds.Sounds)
        {
            foreach (SoundVariant variant in sound.Variants)
            {
                bool decoded = Sounds.TryGetClip(variant, out SoundClip? clip);
                Assert.Equal(!sound.Streamed, decoded);
                if (decoded)
                {
                    Assert.Equal(variant.Frames, clip!.Frames);
                    Assert.Equal(variant.Channels, clip.Channels);
                }
            }
        }

        Assert.Equal(0, ((SoundRegistry)Sounds).PendingCount);
    }

    [Fact]
    public void DecodesTheSamplesThatAReferenceDecoderGives()
    {
        Sounds.CompleteDecoding();
        Assert.True(Sounds.TryGetClip(Sounds.Get("sound:example.sounds.click").Variants[0], out SoundClip? clip));

        byte[] reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "Sounds", "click.s16"));
        ReadOnlySpan<short> samples = clip.Samples;

        // The length is the one the file declares, the length of its source; ffmpeg leaves out the
        // last 128 frames of this file, and gives the others aligned.
        Assert.Equal(11025, samples.Length);
        Assert.Equal(11025 - 128, reference.Length / 2);
        for (int i = 0; i < reference.Length / 2; i++)
        {
            // Two decoders round the same floats to 16 bits: they may differ by one step.
            short expected = BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan(i * 2));
            Assert.InRange(samples[i], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void StreamsTheSameSamplesAsTheDecodingInMemory()
    {
        Sounds.CompleteDecoding();
        SoundVariant variant = Sounds.Get("sound:example.sounds.click").Variants[0];
        Assert.True(Sounds.TryGetClip(variant, out SoundClip? clip));

        using SoundStream stream = Sounds.OpenStream(variant, loop: false);
        short[] streamed = ReadAll(stream, variant.Channels, out int blocks);

        Assert.Equal(clip.Samples.ToArray(), streamed);
        Assert.Equal((int)Math.Ceiling((double)variant.Frames / SoundStream.BlockFrames), blocks);
    }

    [Fact]
    public void StreamsToTheEndOfAStreamedSound()
    {
        SoundVariant variant = Sounds.Get("sound:example.sounds.music").Variants[0];
        Assert.False(Sounds.TryGetClip(variant, out _));

        using SoundStream stream = Sounds.OpenStream(variant, loop: false);
        short[] samples = ReadAll(stream, variant.Channels, out _);

        Assert.Equal(variant.Frames * variant.Channels, samples.Length);
        Assert.True(stream.IsEnded);
        Assert.Null(stream.Error);
        Assert.False(stream.TryTake(out _));
    }

    [Fact]
    public void LoopsWithoutGapAndWithoutEnd()
    {
        SoundVariant variant = Sounds.Get("sound:example.sounds.music").Variants[0];
        short[] once;
        using (SoundStream single = Sounds.OpenStream(variant, loop: false))
        {
            once = ReadAll(single, variant.Channels, out _);
        }

        using SoundStream stream = Sounds.OpenStream(variant, loop: true);
        var samples = new List<short>();
        while (samples.Count < once.Length * 2.5)
        {
            SoundBlock block = Take(stream);
            Assert.False(block.IsLast);
            Assert.Equal(SoundStream.BlockFrames, block.Frames);
            samples.AddRange(stream.GetSamples(block).ToArray());
            stream.Return(block);
        }

        // The file plays again from its start right after its end, twice.
        Assert.Equal(once, samples.GetRange(0, once.Length));
        Assert.Equal(once, samples.GetRange(once.Length, once.Length));
        Assert.False(stream.IsEnded);
    }

    [Fact]
    public void AllocatesNothingForEachBlockBeyondWhatTheDecoderAllocates()
    {
        SoundVariant variant = Sounds.Get("sound:example.sounds.music").Variants[0];
        using SoundStream stream = Sounds.OpenStream(variant, loop: false);

        // NVorbis 0.10.5 allocates while it decodes, in proportion to the samples: measured alone on
        // the same file, it is the whole allowance of the stream, whose own buffers are pooled.
        long decoder = DecoderAllocation(variant);
        long before = stream.AllocatedBytes;
        ReadAll(stream, variant.Channels, out int blocks);
        long streamed = stream.AllocatedBytes - before;

        Assert.True(streamed <= decoder + (blocks * 64), $"{streamed} bytes for {blocks} blocks; NVorbis alone: {decoder}");
    }

    [Fact]
    public void RefusesABlockGivenBackTwice()
    {
        using SoundStream stream = Sounds.OpenStream(Sounds.Get("sound:example.sounds.music").Variants[0], loop: false);
        SoundBlock block = Take(stream);
        stream.Return(block);

        Assert.Throws<InvalidOperationException>(() => stream.Return(block));
        Assert.Throws<InvalidOperationException>(() => stream.GetSamples(block).Length);
    }

    // What NVorbis allocates to decode a whole file in blocks of the size of a stream, after a first pass.
    private static long DecoderAllocation(SoundVariant variant)
    {
        using NVorbis.VorbisReader reader = OggVorbis.Open(variant.Encoded);
        float[] buffer = new float[SoundStream.BlockFrames * variant.Channels];
        while (reader.ReadSamples(buffer, 0, buffer.Length) > 0)
        {
        }

        reader.SamplePosition = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (reader.ReadSamples(buffer, 0, buffer.Length) > 0)
        {
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static SoundBlock Take(SoundStream stream)
    {
        SoundBlock block;
        while (!stream.TryTake(out block))
        {
            Assert.Null(stream.Error);
            Assert.False(stream.IsEnded && !stream.TryTake(out _), "the stream ended");
            stream.Wait();
        }

        return block;
    }

    private static short[] ReadAll(SoundStream stream, int channels, out int blocks)
    {
        var samples = new List<short>();
        blocks = 0;
        while (true)
        {
            SoundBlock block = Take(stream);
            blocks++;
            Assert.Equal(block.Frames * channels, stream.GetSamples(block).Length);
            samples.AddRange(stream.GetSamples(block).ToArray());
            bool last = block.IsLast;
            stream.Return(block);
            if (last)
            {
                return samples.ToArray();
            }
        }
    }
}
