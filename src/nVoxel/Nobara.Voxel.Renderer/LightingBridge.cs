// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Lighting;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>
/// The only place where nVoxel.Renderer uses nLighting, its optional
/// dependency: the frozen copies of the light of the chunks it meshes, the
/// chunks whose light changed, and the light of the terrain around the other
/// grids.
/// </summary>
[UsesOptionalDependency(LightingModuleId)]
internal sealed class LightingBridge : MeshLighting
{
    /// <summary>The identifier of nLighting.</summary>
    public const string LightingModuleId = "nobara.lighting";

    private readonly ILighting _lighting;
    private readonly LightChangeSubscription _changes;

    private LightingBridge(ModuleContext context, ILighting lighting, Action<Grid, ChunkCoordinate, bool> changed)
    {
        _lighting = lighting;
        _changes = lighting.SubscribeChanges(context, change => changed(change.Grid, change.Chunk, change.TouchesBorder));
    }

    /// <summary>Subscribes to the light of nLighting. Call it only when nLighting is active.</summary>
    /// <param name="context">The context of nVoxel.Renderer.</param>
    /// <param name="changed">Receives each chunk whose light changed, and whether a block on its border changed.</param>
    /// <returns>The light; <see langword="null"/> without the service of nLighting.</returns>
    public static MeshLighting? Create(ModuleContext context, Action<Grid, ChunkCoordinate, bool> changed) =>
        context.TryResolve(out ILighting? lighting) ? new LightingBridge(context, lighting, changed) : null;

    /// <inheritdoc/>
    public override MeshLightCopy CreateCopy() => new SnapshotCopy();

    /// <inheritdoc/>
    public override bool TryCopy(Grid grid, ChunkCoordinate chunk, MeshLightCopy copy) =>
        _lighting.CopyFrozen(grid, chunk, ((SnapshotCopy)copy).Snapshot);

    /// <inheritdoc/>
    public override bool TryGetTerrainSample(Grid grid, out ChunkLight light, out uint version)
    {
        if (!_lighting.TryGetTerrainSample(grid, out TerrainLightSample sample))
        {
            light = default;
            version = 0;
            return false;
        }

        light = new ChunkLight(Level(sample.Sky), Level(sample.Red), Level(sample.Green), Level(sample.Blue));
        version = sample.Version;
        return true;
    }

    /// <inheritdoc/>
    public override void Dispose() => _changes.Dispose();

    private static float Level(float value) => Math.Clamp(value, 0, 15);

    /// <summary>A frozen copy of nLighting, expanded into the copy of the chunk in the job of the remesh.</summary>
    private sealed class SnapshotCopy : MeshLightCopy
    {
        public LightSnapshot Snapshot { get; } = new();

        [HotPath]
        public override void Expand(ushort[] destination)
        {
            LightSnapshot snapshot = Snapshot;
            for (int y = -1; y <= VoxelCoordinates.ChunkSize; y++)
            {
                for (int z = -1; z <= VoxelCoordinates.ChunkSize; z++)
                {
                    int row = MeshInput.Index(-1, y, z);
                    for (int x = 0; x < MeshInput.Size; x++)
                    {
                        LightLevel level = snapshot.Get(x - 1, y, z);
                        destination[row + x] = (ushort)((level.Sky << 12) | (level.Red << 8) | (level.Green << 4) | level.Blue);
                    }
                }
            }
        }
    }
}
