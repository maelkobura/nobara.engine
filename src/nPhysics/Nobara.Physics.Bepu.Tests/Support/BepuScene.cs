// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests.Support;

/// <summary>
/// A running engine with the BEPUphysics backend, and the helpers the tests
/// use: bodies, a flat ground of voxels injected into the world of the default
/// dimension, and ticks.
/// </summary>
internal sealed class BepuScene : IDisposable
{
    private readonly List<Chunk> _chunks = [];

    public BepuScene(Action<BepuHarness>? configure = null)
    {
        configure?.Invoke(Harness);
        Engine = Harness.Start();
    }

    public BepuHarness Harness { get; } = new();

    public Engine Engine { get; }

    public BepuProbe Probe => Harness.Probe;

    public IPhysicsSystem Physics => Harness.Probe.Physics;

    /// <summary>Gets the only world of the backend; it exists once a body is added.</summary>
    public BepuWorld World => Assert.Single(Harness.Backend.Worlds);

    /// <summary>Makes a new entity a body.</summary>
    public BodyHandle Add(WorldPosition position, PhysicsShape shape, BodyKind kind = BodyKind.Dynamic, float mass = 1, PhysicsMaterial? material = null, CollisionFilter? filter = null, bool continuous = false, Quaternion? rotation = null)
    {
        EntityId entity = Probe.CreateEntity(new WorldTransform(position, rotation ?? Quaternion.Identity, Vector3.One));
        return Physics.AddBody(DimensionId.Default, entity, new BodyDescription
        {
            Kind = kind,
            Shape = shape,
            Mass = mass,
            Material = material ?? PhysicsMaterial.Default,
            Filter = filter ?? CollisionFilter.Default,
            Continuous = continuous,
        });
    }

    /// <summary>
    /// Adds to the world a chunk whose cells below <paramref name="height"/>
    /// hold a block: a flat ground whose top is <paramref name="height"/> blocks
    /// above the corner of the chunk.
    /// </summary>
    /// <param name="offset">The chunk, relative to the origin of the world.</param>
    /// <param name="height">The number of layers of blocks, from 1 to 32.</param>
    /// <returns>The number of the chunk in the world.</returns>
    public int Ground(ChunkCoordinate offset = default, int height = 1)
    {
        Chunk chunk = Solid(height);
        _chunks.Add(chunk);
        return World.AddVoxelChunk(new VoxelChunkCollider(null!, chunk, new Vector3(offset.X, offset.Y, offset.Z) * VoxelCoordinates.ChunkSize, Quaternion.Identity));
    }

    /// <summary>Creates a chunk whose cells below a height hold a block.</summary>
    public static Chunk Solid(int height)
    {
        var chunk = new Chunk(default);
        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < VoxelCoordinates.ChunkSize; z++)
            {
                for (int x = 0; x < VoxelCoordinates.ChunkSize; x++)
                {
                    chunk.Palette.Set(VoxelCoordinates.ToIndex(new LocalBlockCoordinate(x, y, z)), 1);
                }
            }
        }

        return chunk;
    }

    public void Tick(int count = 1) => Harness.Tick(Engine, count);

    public BodyState State(BodyHandle body) => Physics.GetState(body);

    /// <summary>Gets the position of a body, in blocks from the corner of its chunk.</summary>
    public Vector3 Local(BodyHandle body) => State(body).Transform.Position.Local;

    /// <summary>Gets the height of a body, in blocks, across chunks.</summary>
    public double Height(BodyHandle body)
    {
        WorldPosition position = State(body).Transform.Position;
        return ((double)position.ChunkY * VoxelCoordinates.ChunkSize) + position.Local.Y;
    }

    public void Dispose()
    {
        Engine.MainContext.Stop();
        Engine.Dispose();
        foreach (Chunk chunk in _chunks)
        {
            chunk.Dispose();
        }

        Harness.Dispose();
    }
}
