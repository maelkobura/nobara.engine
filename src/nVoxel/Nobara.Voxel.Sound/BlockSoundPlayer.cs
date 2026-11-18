// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Audio;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Voxel.Sound;

/// <summary>
/// Turns the blocks of a batch that were placed or broken into the few sounds
/// that are worth playing: one per sound event and per zone of
/// <see cref="ZoneSize"/> blocks, at most <see cref="MaxSoundsPerTick"/> in a
/// tick, each at the middle of the blocks of its zone.
/// </summary>
internal sealed class BlockSoundPlayer
{
    /// <summary>The edge, in blocks, of the zone in which the blocks of one batch that share a sound make a single sound.</summary>
    public const int ZoneSize = 4;

    /// <summary>The most sounds that blocks start in one tick, whatever the number of batches.</summary>
    public const int MaxSoundsPerTick = 16;

    private const int ZoneShift = 2;

    private readonly IAudioSystem _audio;
    private readonly IVoxelWorlds _worlds;
    private readonly IMultiBlockRegistry _multiBlocks;
    private readonly IGameTime _time;
    private readonly Dictionary<ZoneKey, int> _zoneIndices = new(MaxSoundsPerTick);
    private readonly Zone[] _zones = new Zone[MaxSoundsPerTick];
    private readonly Dictionary<ulong, GridAnchor> _anchors = [];

    // A grid is followed by one anchor for as long as it lives: the first sound on a grid creates it, which allocates once.
    private readonly Func<Grid, GridAnchor> _createAnchor = static grid => new GridAnchor(grid);
    private ulong _budgetTick = ulong.MaxValue;
    private int _started;

    public BlockSoundPlayer(IAudioSystem audio, IVoxelWorlds worlds, IMultiBlockRegistry multiBlocks, IGameTime time)
    {
        _audio = audio;
        _worlds = worlds;
        _multiBlocks = multiBlocks;
        _time = time;
    }

    /// <summary>Plays the sounds of the blocks of a batch.</summary>
    /// <param name="batch">The blocks of the types the handler subscribed to.</param>
    /// <param name="sounds">The sound of each state.</param>
    /// <param name="oldState">Whether the sound is that of the old state, as for a block that breaks.</param>
    [HotPath]
    public void Play(BlockBatchView batch, SoundEvent?[] sounds, bool oldState)
    {
        int zones = Group(batch, sounds, oldState);
        for (int i = 0; i < zones; i++)
        {
            Start(in _zones[i]);
            _zones[i] = default;
        }

        _started += zones;
    }

    // Groups the blocks of the batch by sound and zone; the only pass that grows with the batch.
    [HotPath]
    private int Group(BlockBatchView batch, SoundEvent?[] sounds, bool oldState)
    {
        ulong tick = _time.Tick;
        if (tick != _budgetTick)
        {
            _budgetTick = tick;
            _started = 0;
        }

        int zones = 0;
        _zoneIndices.Clear();
        for (int i = 0; i < batch.Count; i++)
        {
            if (batch.IsRemoved(i))
            {
                continue;
            }

            BlockChange change = batch[i];
            uint state = oldState ? change.OldState : change.NewState;
            SoundEvent? sound = state < (uint)sounds.Length ? sounds[state] : null;
            if (sound is null)
            {
                continue;
            }

            // One sound for a block of several cells: the origin makes it.
            if (change.MultiBlock != 0 && _multiBlocks.IsMultiBlock(state) && !_multiBlocks.IsOrigin(state))
            {
                continue;
            }

            BlockPosition position = change.Position;
            var key = new ZoneKey(position.Grid, sound.Index, position.X >> ZoneShift, position.Y >> ZoneShift, position.Z >> ZoneShift);
            if (!_zoneIndices.TryGetValue(key, out int index))
            {
                if (_started + zones == MaxSoundsPerTick)
                {
                    continue;
                }

                index = zones++;
                _zoneIndices.Add(key, index);
                _zones[index] = new Zone { Grid = position.Grid, Sound = sound };
            }

            ref Zone zone = ref _zones[index];
            zone.X += position.X;
            zone.Y += position.Y;
            zone.Z += position.Z;
            zone.Count++;
        }

        return zones;
    }

    // The middle of the blocks of the zone, in the frame of the grid: a sound on a grid that moves follows it.
    [HotPath]
    private void Start(in Zone zone)
    {
        Grid? grid = Find(zone.Grid);
        if (grid is null)
        {
            return;
        }

        double x = ((double)zone.X / zone.Count) + 0.5;
        double y = ((double)zone.Y / zone.Count) + 0.5;
        double z = ((double)zone.Z / zone.Count) + 0.5;
        if (grid.IsTerrain)
        {
            // The terrain is not moved: its coordinates are those of the world, which a float cannot hold far from the origin.
            _audio.PlayAt(zone.Sound!, WorldPosition.FromBlocks(x, y, z));
            return;
        }

        var local = new Vector3((float)x, (float)y, (float)z);
        if (!_anchors.TryGetValue(zone.Grid, out GridAnchor? anchor) || !ReferenceEquals(anchor.Grid, grid))
        {
            anchor = _createAnchor(grid);
            _anchors[zone.Grid] = anchor;
        }

        _audio.PlayAttached(zone.Sound!, anchor, local);
    }

    [HotPath]
    private Grid? Find(ulong identifier)
    {
        IReadOnlyList<Grid> grids = _worlds.Grids;
        for (int i = 0; i < grids.Count; i++)
        {
            if (grids[i].Identifier == identifier && !grids[i].IsDestroyed)
            {
                return grids[i];
            }
        }

        return null;
    }

    [HotPath]
    private readonly record struct ZoneKey(ulong Grid, int Sound, int X, int Y, int Z);

    [HotPath]
    private struct Zone
    {
        public ulong Grid;
        public SoundEvent? Sound;
        public long X;
        public long Y;
        public long Z;
        public int Count;
    }

    /// <summary>What a sound of a grid that is not the terrain follows: the transform of the grid, while it exists.</summary>
    private sealed class GridAnchor(Grid grid) : ISoundAnchor
    {
        public Grid Grid { get; } = grid;

        public bool TryGetTransform(out WorldTransform transform)
        {
            if (Grid.IsDestroyed)
            {
                transform = default;
                return false;
            }

            transform = Grid.Transform;
            return true;
        }
    }
}
