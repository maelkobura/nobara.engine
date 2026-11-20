// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Nobara.Core;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The chunks of voxels of a world: each is a static of BEPUphysics whose shape
/// is a <see cref="VoxelChunkShape"/>, placed at the corner of the chunk with
/// the rotation of its grid. Every cell that holds a block collides as a whole
/// cube.
/// </summary>
internal sealed partial class BepuWorld
{
    private Buffer<ChunkEntry> _chunks;
    private int _chunkHigh;
    private int _chunkFree = -1;

    /// <summary>Gets the number of chunks of voxels in the world.</summary>
    public int ChunkCount { get; private set; }

    /// <inheritdoc/>
    public override int AddVoxelChunk(in VoxelChunkCollider chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk.Chunk);
        int id;
        if (_chunkFree >= 0)
        {
            id = _chunkFree;
            _chunkFree = _chunks[id].NextFree;
        }
        else
        {
            if (_chunkHigh == _chunks.Length)
            {
                _pool.ResizeToAtLeast(ref _chunks, _chunks.Length * 2, _chunkHigh);
            }

            id = _chunkHigh++;
        }

        nint handle = GCHandle.ToIntPtr(GCHandle.Alloc(chunk.Chunk));
        TypedIndex shape = Simulation.Shapes.Add(new VoxelChunkShape(handle));
        StaticHandle body = Simulation.Statics.Add(new StaticDescription(new RigidPose(chunk.Position, System.Numerics.Quaternion.Normalize(chunk.Rotation)), shape, ContinuousDetection.Passive));
        _surfaces.Allocate(body) = Surface.Voxels;
        _chunks[id] = new ChunkEntry { Used = true, Chunk = handle, Shape = shape, Static = body, NextFree = -1 };
        ChunkCount++;
        return id;
    }

    /// <inheritdoc/>
    public override void RemoveVoxelChunk(int chunk)
    {
        if ((uint)chunk >= (uint)_chunkHigh || !_chunks[chunk].Used)
        {
            throw new ArgumentException($"The world holds no chunk {chunk}.", nameof(chunk));
        }

        ref ChunkEntry entry = ref _chunks[chunk];
        Simulation.Statics.Remove(entry.Static);
        Simulation.Shapes.Remove(entry.Shape);
        GCHandle.FromIntPtr(entry.Chunk).Free();
        entry = new ChunkEntry { NextFree = _chunkFree };
        _chunkFree = chunk;
        ChunkCount--;
    }

    /// <summary>A chunk of voxels of the world.</summary>
    private struct ChunkEntry
    {
        public bool Used;
        public nint Chunk;
        public TypedIndex Shape;
        public StaticHandle Static;
        public int NextFree;
    }
}
