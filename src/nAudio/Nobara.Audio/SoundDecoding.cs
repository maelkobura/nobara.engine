// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using Nobara.Core;
using Nobara.Core.Jobs;
using NVorbis;

namespace Nobara.Audio;

/// <summary>
/// A variant decoded in memory: its interleaved 16-bit samples, ready to be
/// given to a backend. Only the client decodes.
/// </summary>
public sealed class SoundClip
{
    private readonly short[] _samples;

    internal SoundClip(SoundVariant variant, short[] samples, int length)
    {
        Variant = variant;
        _samples = samples;
        Length = length;
    }

    /// <summary>Gets the variant the clip was decoded from.</summary>
    public SoundVariant Variant { get; }

    /// <summary>Gets the number of channels: 1 or 2.</summary>
    public int Channels => Variant.Channels;

    /// <summary>Gets the frames per second.</summary>
    public int SampleRate => Variant.SampleRate;

    /// <summary>Gets the length in frames.</summary>
    public int Frames => Length / Variant.Channels;

    /// <summary>Gets the interleaved samples, left then right for stereo.</summary>
    public ReadOnlySpan<short> Samples => _samples.AsSpan(0, Length);

    private int Length { get; }
}

/// <summary>A block decoded by a <see cref="SoundStream"/>; give it back with <see cref="SoundStream.Return"/> once used.</summary>
public readonly struct SoundBlock
{
    [HotPath]
    internal SoundBlock(int slot, int frames, bool isLast)
    {
        Slot = slot;
        Frames = frames;
        IsLast = isLast;
    }

    /// <summary>Gets the number of frames in the block; fewer than a full block only at the end of a sound that does not loop.</summary>
    public int Frames { get; }

    /// <summary>Gets whether the block is the last one: the sound ends with it.</summary>
    public bool IsLast { get; }

    internal int Slot { get; }
}

/// <summary>Conversions of decoded samples.</summary>
internal static class Pcm
{
    /// <summary>Converts samples from -1 to 1 into 16-bit samples, clamped.</summary>
    [HotPath]
    public static void ToInt16(ReadOnlySpan<float> source, Span<short> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            float value = source[i] * 32767f;
            target[i] = value >= 32767f ? (short)32767 : value <= -32768f ? (short)-32768 : (short)value;
        }
    }
}

/// <summary>The decoding of one variant in memory, run by a background job.</summary>
internal sealed class ClipWork
{
    public ClipWork(SoundVariant variant)
    {
        Variant = variant;
        Reader = OggVorbis.Open(variant.Encoded);
        Samples = new short[checked((int)(variant.Frames * variant.Channels))];
        Scratch = ArrayPool<float>.Shared.Rent(4096 * variant.Channels);
    }

    public SoundVariant Variant { get; }

    public VorbisReader Reader { get; }

    public short[] Samples { get; }

    public float[] Scratch { get; }

    public JobHandle Handle { get; set; }

    /// <summary>Gets the number of samples written.</summary>
    public int Written { get; private set; }

    public Exception? Error { get; private set; }

    [HotPath]
    public void Run()
    {
        try
        {
            int written = 0;
            while (written < Samples.Length)
            {
                int read = Reader.ReadSamples(Scratch, 0, Math.Min(Scratch.Length, Samples.Length - written));
                if (read == 0)
                {
                    break;
                }

                Pcm.ToInt16(Scratch.AsSpan(0, read), Samples.AsSpan(written, read));
                written += read;
            }

            Written = written;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception;
        }
        finally
        {
            Reader.Dispose();
            ArrayPool<float>.Shared.Return(Scratch);
        }
    }
}

/// <summary>The background job of a <see cref="ClipWork"/>.</summary>
[HotPath]
internal readonly struct ClipJob(ClipWork work) : IJob
{
    [HotPath]
    public void Execute() => work.Run();
}

/// <summary>
/// A variant decoded in blocks while it plays, by background jobs, for music
/// and long ambiences. Call it from the loop thread of its context.
/// </summary>
/// <remarks>
/// <para>
/// The stream decodes ahead into a few blocks of fixed size, rented from a
/// pool when it opens: once open, it allocates no managed memory. Take the
/// blocks in order with <see cref="TryTake"/>, read their samples with
/// <see cref="GetSamples"/>, and give each one back with <see cref="Return"/>,
/// which lets the stream decode the next one.
/// </para>
/// <para>
/// A looping stream goes back to the start of the file inside a block, with
/// no gap; a stream that does not loop marks its last block with
/// <see cref="SoundBlock.IsLast"/>. Dispose the stream to give its blocks back.
/// </para>
/// </remarks>
public sealed class SoundStream : IDisposable
{
    /// <summary>The frames in one block.</summary>
    public const int BlockFrames = 8192;

    private const int BlockCount = 3;

    private readonly ModuleJobs _jobs;
    private readonly StreamWork _work;
    private readonly short[][] _blocks = new short[BlockCount][];
    private readonly SlotState[] _states = new SlotState[BlockCount];
    private readonly int[] _frames = new int[BlockCount];
    private readonly bool[] _last = new bool[BlockCount];

    // The ready blocks, in the order they were decoded: a ring of slots.
    private readonly int[] _ready = new int[BlockCount];
    private int _readyStart;
    private int _readyCount;
    private int _decoding = -1;
    private JobHandle _handle;
    private bool _ended;
    private bool _disposed;

    internal SoundStream(ModuleJobs jobs, SoundVariant variant, bool loop, long startFrame = 0)
    {
        _jobs = jobs;
        Variant = variant;
        Loop = loop;
        for (int i = 0; i < BlockCount; i++)
        {
            _blocks[i] = ArrayPool<short>.Shared.Rent(BlockFrames * variant.Channels);
        }

        _work = new StreamWork(OggVorbis.Open(variant.Encoded), ArrayPool<float>.Shared.Rent(BlockFrames * variant.Channels), variant.Channels, variant.Frames, loop);
        if (startFrame > 0 && startFrame < variant.Frames)
        {
            _work.Seek(startFrame);
        }

        Pump();
    }

    private enum SlotState : byte
    {
        Free,
        Decoding,
        Ready,
        Taken,
    }

    /// <summary>Gets the variant the stream decodes.</summary>
    public SoundVariant Variant { get; }

    /// <summary>Gets whether the stream goes back to the start at the end of the file.</summary>
    public bool Loop { get; }

    /// <summary>Gets whether the last block was decoded: no other block follows the ready ones.</summary>
    public bool IsEnded => _ended;

    /// <summary>Gets the error that stopped the decoding, or <see langword="null"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Gets the managed bytes the decoding jobs allocated since the stream opened, for the tests.</summary>
    internal long AllocatedBytes => _work.AllocatedBytes;

    /// <summary>Takes the next decoded block, when it is ready.</summary>
    /// <param name="block">The block; read it with <see cref="GetSamples"/>.</param>
    /// <returns><see langword="false"/> when the next block is still decoding, or after the last one.</returns>
    [HotPath]
    public bool TryTake(out SoundBlock block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Pump();
        if (_readyCount == 0)
        {
            block = default;
            return false;
        }

        int slot = _ready[_readyStart];
        _readyStart = (_readyStart + 1) % BlockCount;
        _readyCount--;
        _states[slot] = SlotState.Taken;
        block = new SoundBlock(slot, _frames[slot], _last[slot]);
        return true;
    }

    /// <summary>Gets the interleaved 16-bit samples of a block that was taken and not given back.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The samples, left then right for stereo.</returns>
    [HotPath]
    public ReadOnlySpan<short> GetSamples(in SoundBlock block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_states[block.Slot] != SlotState.Taken)
        {
            throw new InvalidOperationException("The block was given back to its stream.");
        }

        return _blocks[block.Slot].AsSpan(0, block.Frames * Variant.Channels);
    }

    /// <summary>Gives a block back, so that the stream decodes the next one into it.</summary>
    /// <param name="block">A block taken from this stream.</param>
    [HotPath]
    public void Return(in SoundBlock block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_states[block.Slot] != SlotState.Taken)
        {
            throw new InvalidOperationException("The block was already given back to its stream.");
        }

        _states[block.Slot] = SlotState.Free;
        Pump();
    }

    /// <summary>Waits for the block being decoded, if any, from the loop thread.</summary>
    public void Wait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handle.Complete();
        Pump();
    }

    /// <summary>Waits for the decoding job, then gives the blocks back to their pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Complete();
        _work.Reader.Dispose();
        ArrayPool<float>.Shared.Return(_work.Scratch);
        for (int i = 0; i < BlockCount; i++)
        {
            ArrayPool<short>.Shared.Return(_blocks[i]);
        }
    }

    // Collects the block that finished decoding, then starts the next one in a free slot.
    [HotPath]
    private void Pump()
    {
        if (_decoding >= 0)
        {
            if (!_handle.IsCompleted)
            {
                return;
            }

            int slot = _decoding;
            _decoding = -1;
            if (_work.Error is { } error)
            {
                Error = error;
                _ended = true;
                _states[slot] = SlotState.Free;
                return;
            }

            _frames[slot] = _work.Frames;
            _last[slot] = _work.End;
            _ended = _work.End;
            if (_work.Frames > 0 || _work.End)
            {
                _states[slot] = SlotState.Ready;
                _ready[(_readyStart + _readyCount) % BlockCount] = slot;
                _readyCount++;
            }
            else
            {
                _states[slot] = SlotState.Free;
            }
        }

        if (_ended)
        {
            return;
        }

        for (int i = 0; i < BlockCount; i++)
        {
            if (_states[i] == SlotState.Free)
            {
                _states[i] = SlotState.Decoding;
                _decoding = i;
                _work.Target = _blocks[i];
                _handle = _jobs.Schedule(new StreamJob(_work), priority: JobPriority.Background);
                return;
            }
        }
    }
}

/// <summary>The decoding of one block of a stream, run by a background job.</summary>
internal sealed class StreamWork(VorbisReader reader, float[] scratch, int channels, long length, bool loop)
{
    // The frames read since the start of the file, so that the stream stops at the length it
    // declares: NVorbis 0.10.5 may decode a few frames past it.
    private long _position;

    public VorbisReader Reader { get; } = reader;

    public float[] Scratch { get; } = scratch;

    /// <summary>Gets or sets the block to fill: set by the loop thread before the job runs.</summary>
    public short[] Target { get; set; } = [];

    public int Frames { get; private set; }

    public bool End { get; private set; }

    public Exception? Error { get; private set; }

    public long AllocatedBytes { get; private set; }

    /// <summary>Moves to a frame of the file, before the first block decodes.</summary>
    public void Seek(long frame)
    {
        Reader.SamplePosition = frame;
        _position = frame;
    }

    [HotPath]
    public void Run()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            int capacity = SoundStream.BlockFrames * channels;
            int written = 0;
            bool rewound = false;
            End = false;
            while (written < capacity)
            {
                long left = (length - _position) * channels;
                int read = left <= 0 ? 0 : Reader.ReadSamples(Scratch, 0, (int)Math.Min(capacity - written, left));
                if (read > 0)
                {
                    Pcm.ToInt16(Scratch.AsSpan(0, read), Target.AsSpan(written, read));
                    written += read;
                    _position += read / channels;
                    rewound = false;
                    continue;
                }

                // The end of the file: a looping stream goes on from the start, unless the file is empty.
                if (!loop || rewound)
                {
                    End = true;
                    break;
                }

                Reader.SamplePosition = 0;
                _position = 0;
                rewound = true;
            }

            Frames = written / channels;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception;
        }

        AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

/// <summary>The background job of a <see cref="StreamWork"/>.</summary>
[HotPath]
internal readonly struct StreamJob(StreamWork work) : IJob
{
    [HotPath]
    public void Execute() => work.Run();
}
