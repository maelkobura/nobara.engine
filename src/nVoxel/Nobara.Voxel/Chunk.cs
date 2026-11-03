// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Voxel;

/// <summary>A 32³ cube of block states and per-block metadata.</summary>
public sealed class Chunk : IDisposable
{
    /// <summary>The number of cells in a chunk.</summary>
    public const int Volume = VoxelCoordinates.ChunkVolume;

    /// <summary>The name of the built-in natural channel.</summary>
    public const string NaturalChannelName = "nobara.voxel.natural";

    /// <summary>The built-in channel that identifies blocks untouched since generation.</summary>
    public static DenseChannelDefinition NaturalChannel { get; } = new(NaturalChannelName, DenseChannelBits.One, 1);

    private readonly JobData? _access;
    private readonly Dictionary<DenseChannelDefinition, DenseChannel> _channels = [];
    private readonly Dictionary<BlockDataKey, object> _blockData = [];
    private readonly DenseChannel _natural = new(NaturalChannel);
    private Palette _palette;
    private bool _disposed;

    /// <summary>Creates an air-filled chunk.</summary>
    /// <param name="coordinate">The coordinate of the chunk in its grid.</param>
    /// <param name="access">The job data used to check concurrent access, or <see langword="null"/> for a standalone chunk.</param>
    public Chunk(ChunkCoordinate coordinate, JobData? access = null)
    {
        Coordinate = coordinate;
        _access = access;
        _palette = new Palette();
    }

    /// <summary>Gets the coordinate of the chunk in its grid.</summary>
    public ChunkCoordinate Coordinate { get; }

    internal JobData AccessData
    {
        get => _access ?? throw new InvalidOperationException("The chunk has no job access declaration.");
    }

    /// <summary>Gets the version, incremented when a block or metadata value changes.</summary>
    public uint Version { get; private set; }

    /// <summary>Gets the compact block-state palette.</summary>
    public Palette Palette => _palette;

    /// <summary>Reads a block state by its local index.</summary>
    [HotPath]
    public uint GetState(int localIndex)
    {
        RequireAlive();
        _access?.CheckRead();
        return _palette.Get(localIndex);
    }

    /// <summary>Reads a block state by its local coordinate.</summary>
    [HotPath]
    public uint GetState(LocalBlockCoordinate local) => GetState(VoxelCoordinates.ToIndex(local));

    /// <summary>Changes a block state. The low-level operation is exposed for chunk storage and tests.</summary>
    /// <param name="localIndex">The local cell index.</param>
    /// <param name="state">The runtime nBlocks state index.</param>
    /// <param name="natural">Whether the new cell remains natural.</param>
    /// <returns><see langword="true"/> when the state changed.</returns>
    internal bool SetState(int localIndex, uint state, bool natural = false)
    {
        BlockApplyResult result = ApplyState(localIndex, state, natural);
        if (result.OldState != result.NewState) DetachAndReset(localIndex);
        return result.OldState != result.NewState;
    }

    internal BlockApplyResult ApplyState(int localIndex, uint state, bool natural)
    {
        RequireAlive();
        _access?.CheckWrite();
        uint oldState = _palette.Get(localIndex);
        bool changed = _palette.Set(localIndex, state);
        bool naturalChanged = _natural.Set(localIndex, natural ? 1u : 0u);
        if (changed || naturalChanged)
        {
            Version++;
        }

        return new BlockApplyResult(oldState, state, false, null);
    }

    internal RemovedBlockData? DetachAndReset(int localIndex)
    {
        RequireAlive();
        _access?.CheckWrite();
        RemovedBlockData? removed = DetachBlockData(localIndex);
        bool changed = removed is not null;
        foreach (DenseChannel channel in _channels.Values)
        {
            if (channel.Get(localIndex) != channel.Definition.DefaultValue)
            {
                channel.Set(localIndex, channel.Definition.DefaultValue);
                changed = true;
            }
        }

        if (changed) Version++;
        return removed;
    }

    /// <summary>Reads whether a block is natural without allocating the natural channel.</summary>
    [HotPath]
    public bool IsNatural(int localIndex)
    {
        RequireAlive();
        _access?.CheckRead();
        return _natural.Get(localIndex) != 0;
    }

    /// <summary>Sets the natural flag of a block.</summary>
    /// <returns><see langword="true"/> when the flag changed.</returns>
    internal bool SetNatural(int localIndex, bool natural)
    {
        RequireAlive();
        _access?.CheckWrite();
        bool changed = _natural.Set(localIndex, natural ? 1u : 0u);
        if (changed) Version++;
        return changed;
    }

    /// <summary>Reads a declared dense channel, returning its default when it has not been allocated.</summary>
    [HotPath]
    public uint GetChannel(DenseChannelDefinition definition, int localIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireAlive();
        _access?.CheckRead();
        if (ReferenceEquals(definition, NaturalChannel)) return _natural.Get(localIndex);
        return _channels.TryGetValue(definition, out DenseChannel? channel) ? channel.Get(localIndex) : definition.DefaultValue;
    }

    /// <summary>Writes a declared dense channel.</summary>
    /// <returns><see langword="true"/> when the value changed.</returns>
    internal bool SetChannel(DenseChannelDefinition definition, int localIndex, uint value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireAlive();
        _access?.CheckWrite();
        DenseChannel channel = ReferenceEquals(definition, NaturalChannel) ? _natural : GetChannelCore(definition);
        bool changed = channel.Set(localIndex, value);
        if (changed) Version++;
        return changed;
    }

    /// <summary>Releases dense channels whose cells all hold their defaults.</summary>
    public void CompactChannels()
    {
        RequireAlive();
        if (_natural.Compact()) Version++;
        foreach (DenseChannel channel in _channels.Values) if (channel.Compact()) Version++;
    }

    /// <summary>Reads block data attached to a cell.</summary>
    /// <returns>The value, or <see langword="null"/> when this cell has no such data.</returns>
    [HotPath]
    public T GetBlockData<T>(BlockDataType<T> type, int localIndex)
    {
        ArgumentNullException.ThrowIfNull(type);
        RequireAlive();
        _access?.CheckRead();
        return _blockData.TryGetValue(new BlockDataKey(localIndex, type), out object? value) ? (T)value : default!;
    }

    /// <summary>Attaches or replaces block data on a cell.</summary>
    /// <returns><see langword="true"/> when the stored value changed.</returns>
    internal bool SetBlockData<T>(BlockDataType<T> type, int localIndex, T value)
    {
        ArgumentNullException.ThrowIfNull(type);
        RequireAlive();
        _access?.CheckWrite();
        var key = new BlockDataKey(localIndex, type);
        if (_blockData.TryGetValue(key, out object? old) && EqualityComparer<T>.Default.Equals((T)old, value)) return false;
        _blockData[key] = value!;
        Version++;
        return true;
    }

    internal void SetBlockDataValue(IBlockDataType type, int localIndex, object value)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        RequireAlive();
        _access?.CheckWrite();
        _blockData[new BlockDataKey(localIndex, type)] = value;
        Version++;
    }

    internal void CopyCellMetadataTo(Chunk destination, int sourceIndex, int destinationIndex, int quarterTurns = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RequireAlive();
        destination.RequireAlive();
        _access?.CheckRead();
        destination._access?.CheckWrite();

        if (_natural.Get(sourceIndex) != NaturalChannel.DefaultValue)
        {
            destination._natural.Set(destinationIndex, _natural.Get(sourceIndex));
        }

        foreach (DenseChannel channel in _channels.Values)
        {
            uint value = channel.Get(sourceIndex);
            if (value != channel.Definition.DefaultValue)
            {
                destination.SetChannel(channel.Definition, destinationIndex, value);
            }
        }

        foreach ((BlockDataKey key, object value) in _blockData.Where(pair => pair.Key.LocalIndex == sourceIndex))
        {
            object copy = quarterTurns == 0 ? value : BlockRotation.RotateData(key.Type, value, quarterTurns);
            destination.SetBlockDataValue(key.Type, destinationIndex, copy);
        }
    }

    internal void RotateBlockData(int localIndex, int quarterTurns = 1)
    {
        RequireAlive();
        _access?.CheckWrite();
        foreach (BlockDataKey key in _blockData.Keys.Where(k => k.LocalIndex == localIndex).ToArray())
        {
            _blockData[key] = BlockRotation.RotateData(key.Type, _blockData[key], quarterTurns);
        }

        Version++;
    }

    /// <summary>Removes all metadata attached to a cell.</summary>
    internal void RemoveBlockData(int localIndex)
    {
        RequireAlive();
        _access?.CheckWrite();
        bool changed = false;
        foreach (BlockDataKey key in _blockData.Keys.Where(k => k.LocalIndex == localIndex).ToArray()) changed |= _blockData.Remove(key);
        if (changed) Version++;
    }

    /// <summary>Compacts the palette in a background-friendly operation.</summary>
    public void CompactPalette()
    {
        RequireAlive();
        _palette.Compact(Volume);
    }

    /// <summary>Copies this chunk and optional neighboring border chunks into an immutable snapshot.</summary>
    /// <param name="borders">Neighbor chunks whose cells are needed by a consumer.</param>
    /// <returns>The frozen copy.</returns>
    public ChunkSnapshot CopyFrozen(params Chunk[] borders)
    {
        RequireAlive();
        ArgumentNullException.ThrowIfNull(borders);
        _access?.CheckRead();
        var copy = new uint[Volume];
        for (int i = 0; i < Volume; i++) copy[i] = _palette.Get(i);
        var borderCopies = new Dictionary<ChunkCoordinate, uint[]>(borders.Length);
        foreach (Chunk border in borders)
        {
            ArgumentNullException.ThrowIfNull(border);
            var values = new uint[Volume];
            for (int i = 0; i < Volume; i++) values[i] = border.GetState(i);
            borderCopies[border.Coordinate] = values;
        }

        return new ChunkSnapshot(Coordinate, Version, copy, borderCopies);
    }

    /// <summary>Releases palette, channel and metadata storage.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _palette.Dispose();
        _natural.Dispose();
        foreach (DenseChannel channel in _channels.Values) channel.Dispose();
        _channels.Clear();
        _blockData.Clear();
        GC.SuppressFinalize(this);
    }

    private DenseChannel GetChannelCore(DenseChannelDefinition definition)
    {
        if (!_channels.TryGetValue(definition, out DenseChannel? channel))
        {
            channel = new DenseChannel(definition);
            _channels.Add(definition, channel);
        }

        return channel;
    }

    private void ResetMetadata(int localIndex)
    {
        RemoveBlockData(localIndex);
        foreach (DenseChannel channel in _channels.Values) channel.Set(localIndex, channel.Definition.DefaultValue);
    }

    private RemovedBlockData? DetachBlockData(int localIndex)
    {
        List<BlockDataEntry>? values = null;
        foreach (BlockDataKey key in _blockData.Keys.Where(k => k.LocalIndex == localIndex).ToArray())
        {
            values ??= [];
            values.Add(new BlockDataEntry(key.Type, _blockData[key]));
            _blockData.Remove(key);
        }

        return values is null ? null : new RemovedBlockData(values);
    }

    [HotPath]
    private void RequireAlive() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly struct BlockDataKey : IEquatable<BlockDataKey>
    {
        [HotPath]
        public BlockDataKey(int localIndex, IBlockDataType type) => (LocalIndex, Type) = (localIndex, type);

        public int LocalIndex { get; }

        public IBlockDataType Type { get; }

        public bool Equals(BlockDataKey other) => LocalIndex == other.LocalIndex && ReferenceEquals(Type, other.Type);

        public override bool Equals(object? obj) => obj is BlockDataKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(LocalIndex, Type);
    }
}

internal readonly struct BlockApplyResult
{
    [HotPath]
    public BlockApplyResult(uint oldState, uint newState, bool dataChanged, RemovedBlockData? removedData) =>
        (OldState, NewState, DataChanged, RemovedData) = (oldState, newState, dataChanged, removedData);

    public uint OldState { get; }
    public uint NewState { get; }
    public bool DataChanged { get; }
    public RemovedBlockData? RemovedData { get; }
}

/// <summary>An immutable chunk copy suitable for a mesher, lighting job, or serializer.</summary>
public sealed class ChunkSnapshot
{
    private readonly uint[] _states;
    private readonly IReadOnlyDictionary<ChunkCoordinate, uint[]> _borders;

    internal ChunkSnapshot(ChunkCoordinate coordinate, uint version, uint[] states, IReadOnlyDictionary<ChunkCoordinate, uint[]> borders) =>
        (Coordinate, Version, _states, _borders) = (coordinate, version, states, borders);

    /// <summary>Gets the coordinate of the copied chunk.</summary>
    public ChunkCoordinate Coordinate { get; }

    /// <summary>Gets the source version.</summary>
    public uint Version { get; }

    /// <summary>Reads a copied cell of the main chunk.</summary>
    [HotPath]
    public uint GetState(int localIndex) => _states[localIndex];

    /// <summary>Reads a copied cell of a requested border chunk.</summary>
    /// <returns><see langword="true"/> when that border was requested.</returns>
    [HotPath]
    public bool TryGetBorderState(ChunkCoordinate coordinate, int localIndex, out uint state)
    {
        if (_borders.TryGetValue(coordinate, out uint[]? values))
        {
            state = values[localIndex];
            return true;
        }

        state = 0;
        return false;
    }
}
