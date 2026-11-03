// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using Nobara.Core;
using Nobara.Core.Memory;

namespace Nobara.Voxel;

/// <summary>The allowed widths of a dense per-block channel.</summary>
public enum DenseChannelBits
{
    /// <summary>One bit per block.</summary>
    One = 1,
    /// <summary>Eight bits per block.</summary>
    Eight = 8,
    /// <summary>Sixteen bits per block.</summary>
    Sixteen = 16,
    /// <summary>Thirty-two bits per block.</summary>
    ThirtyTwo = 32,
}

/// <summary>Declares a dense channel that a module can attach to chunks.</summary>
public sealed class DenseChannelDefinition
{
    /// <summary>Declares a channel.</summary>
    /// <param name="name">The qualified name, unique to the declaring module.</param>
    /// <param name="bits">The width of one value.</param>
    /// <param name="defaultValue">The value of an unallocated channel.</param>
    public DenseChannelDefinition(string name, DenseChannelBits bits, uint defaultValue = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (bits is not (DenseChannelBits.One or DenseChannelBits.Eight or DenseChannelBits.Sixteen or DenseChannelBits.ThirtyTwo))
        {
            throw new ArgumentOutOfRangeException(nameof(bits));
        }

        uint max = bits == DenseChannelBits.ThirtyTwo ? uint.MaxValue : (1u << (int)bits) - 1;
        if (defaultValue > max) throw new ArgumentOutOfRangeException(nameof(defaultValue));
        Name = name;
        Bits = bits;
        DefaultValue = defaultValue;
    }

    /// <summary>Gets the qualified channel name.</summary>
    public string Name { get; }

    /// <summary>Gets the width of one channel value.</summary>
    public DenseChannelBits Bits { get; }

    /// <summary>Gets the value returned before the channel is allocated.</summary>
    public uint DefaultValue { get; }
}

/// <summary>A lazily allocated dense array of values for one chunk.</summary>
public sealed class DenseChannel : IDisposable
{
    private readonly DenseChannelDefinition _definition;
    private readonly int _valuesPerWord;
    private NativeArray<ulong>? _words;

    internal DenseChannel(DenseChannelDefinition definition) => (_definition, _valuesPerWord) = (definition, 64 / (int)definition.Bits);

    /// <summary>Gets the declaration of this channel.</summary>
    public DenseChannelDefinition Definition => _definition;

    /// <summary>Gets whether storage has been allocated because a value differs from the default.</summary>
    public bool IsAllocated => _words is not null;

    /// <summary>Reads a value without allocating.</summary>
    /// <param name="index">The local block index.</param>
    /// <returns>The value, or the declared default.</returns>
    [HotPath]
    public uint Get(int index)
    {
        RequireIndex(index);
        if (_words is null) return _definition.DefaultValue;
        int bits = (int)_definition.Bits;
        int shift = (index % _valuesPerWord) * bits;
        return (uint)((_words[index / _valuesPerWord] >> shift) & Mask(bits));
    }

    /// <summary>Writes a value, allocating storage at the first non-default value.</summary>
    /// <param name="index">The local block index.</param>
    /// <param name="value">The value in the declared range.</param>
    /// <returns><see langword="true"/> when the value changed.</returns>
    public bool Set(int index, uint value)
    {
        RequireIndex(index);
        uint max = _definition.Bits == DenseChannelBits.ThirtyTwo ? uint.MaxValue : (1u << (int)_definition.Bits) - 1;
        if (value > max) throw new ArgumentOutOfRangeException(nameof(value));
        uint old = Get(index);
        if (old == value) return false;
        if (_words is null)
        {
            _words = new NativeArray<ulong>((VoxelCoordinates.ChunkVolume + _valuesPerWord - 1) / _valuesPerWord);
            if (_definition.DefaultValue != 0)
            {
                for (int i = 0; i < VoxelCoordinates.ChunkVolume; i++) SetAllocated(i, _definition.DefaultValue);
            }
        }

        SetAllocated(index, value);
        return true;
    }

    /// <summary>Releases storage when every cell has returned to the default value.</summary>
    /// <returns><see langword="true"/> when storage was released.</returns>
    public bool Compact()
    {
        if (_words is null) return false;
        for (int i = 0; i < VoxelCoordinates.ChunkVolume; i++)
        {
            if (Get(i) != _definition.DefaultValue) return false;
        }

        _words.Dispose();
        _words = null;
        return true;
    }

    /// <summary>Releases the native channel storage.</summary>
    public void Dispose()
    {
        _words?.Dispose();
        _words = null;
        GC.SuppressFinalize(this);
    }

    private void SetAllocated(int index, uint value)
    {
        int bits = (int)_definition.Bits;
        int shift = (index % _valuesPerWord) * bits;
        ulong mask = Mask(bits) << shift;
        _words![index / _valuesPerWord] = (_words[index / _valuesPerWord] & ~mask) | ((ulong)value << shift);
    }

    [HotPath]
    private static ulong Mask(int bits) => bits == 32 ? uint.MaxValue : (1UL << bits) - 1;

    [HotPath]
    private static void RequireIndex(int index)
    {
        if ((uint)index >= VoxelCoordinates.ChunkVolume) throw new ArgumentOutOfRangeException(nameof(index));
    }
}

/// <summary>Declares storage and serialization for one kind of block data.</summary>
public interface IBlockDataType
{
    /// <summary>Gets the qualified type name.</summary>
    string Name { get; }

    /// <summary>Gets the CLR type stored by the declaration.</summary>
    Type DataType { get; }

    /// <summary>Serializes a value for a future persistence or network module.</summary>
    void Serialize(object value, IBufferWriter<byte> destination);

    /// <summary>Deserializes one value.</summary>
    object Deserialize(ReadOnlySpan<byte> source);

    /// <summary>Rotates a value by one quarter turn around the vertical axis.</summary>
    object Rotate(object value);
}

/// <summary>Reads one typed block data value from serialized bytes.</summary>
/// <typeparam name="T">The value type.</typeparam>
public delegate T BlockDataDeserializer<T>(ReadOnlySpan<byte> source);

/// <summary>Typed declaration of a block data type.</summary>
/// <typeparam name="T">The value stored by each block instance.</typeparam>
public sealed class BlockDataType<T> : IBlockDataType
{
    private readonly Action<T, IBufferWriter<byte>> _serialize;
    private readonly BlockDataDeserializer<T> _deserialize;
    private readonly Func<T, T> _rotate;

    /// <summary>Declares a block data type.</summary>
    /// <param name="name">The qualified name.</param>
    /// <param name="serialize">The serializer.</param>
    /// <param name="deserialize">The deserializer.</param>
    /// <param name="rotate">The optional quarter-turn operation; identity by default.</param>
    public BlockDataType(string name, Action<T, IBufferWriter<byte>> serialize, BlockDataDeserializer<T> deserialize, Func<T, T>? rotate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        Name = name;
        _serialize = serialize;
        _deserialize = deserialize;
        _rotate = rotate ?? (static value => value);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public Type DataType => typeof(T);

    /// <inheritdoc/>
    public void Serialize(object value, IBufferWriter<byte> destination) => _serialize((T)value, destination);

    /// <inheritdoc/>
    public object Deserialize(ReadOnlySpan<byte> source) => _deserialize(source)!;

    /// <inheritdoc/>
    public object Rotate(object value) => _rotate((T)value)!;
}
