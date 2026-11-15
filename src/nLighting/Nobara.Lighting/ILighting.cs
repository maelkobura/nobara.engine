// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Core.Jobs;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The light of the grids of one context: its storage, its propagation, the
/// chunks whose light changed, frozen copies of the light of a chunk, the
/// brightness query, and the light of the terrain around the other grids.
/// Resolve it with <c>context.Resolve&lt;ILighting&gt;()</c>.
/// </summary>
/// <remarks>
/// Use it on the loop thread of the context. Every context computes its own
/// light from its own blocks; nothing is shared or sent between contexts. On
/// a server where a distribution disables nLighting, the service does not
/// exist: a module that needs the light there depends on nLighting.
/// </remarks>
public interface ILighting
{
    /// <summary>Gets whether the block channel has three components (<see cref="LightingSettings.Color"/>).</summary>
    bool IsColored { get; }

    /// <summary>Gets the size of the light and the cost of the last propagation pass.</summary>
    LightingStatistics Statistics { get; }

    /// <summary>Gets how late the light is: the work left to the propagation and the budget of the last tick.</summary>
    LightingBacklog Backlog { get; }

    /// <summary>
    /// Gets the job data that guards the light of the context. A tick system
    /// that queries the brightness or copies the light declares it as read;
    /// the propagation writes it at the <c>Late</c> stage.
    /// </summary>
    JobData LightAccess { get; }

    /// <summary>
    /// Gets the light at a block, channel by channel: the sky channel and the
    /// block channel that the propagation stores, and the light of the dynamic
    /// lights of the dimension of the grid, by their distance to the center of
    /// the block.
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <param name="block">The block, in the coordinates of the grid.</param>
    /// <param name="brightness">The light; default when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when nLighting holds no light for the chunk of the block: it is not loaded, or no session is active.</returns>
    /// <remarks>
    /// It allocates nothing. The dynamic lights are those of the last tick,
    /// and blocks do not hide them. The result is never combined with the
    /// time of day.
    /// </remarks>
    bool TryGetBrightness(Grid grid, BlockCoordinate block, out Brightness brightness);

    /// <summary>Gets the light of the terrain around a grid that is not the terrain, which the renderer uses for the light of the grid.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="sample">The sample.</param>
    /// <returns><see langword="false"/> for the terrain, and for a grid that holds no light yet.</returns>
    /// <remarks>The sample is taken again at the end of a tick when the grid moved or when the light of the terrain changed.</remarks>
    bool TryGetTerrainSample(Grid grid, out TerrainLightSample sample);

    /// <summary>Adds a point of interest, near which the propagation works first when its budget does not cover everything.</summary>
    /// <param name="module">The module that adds it, and disposes it.</param>
    /// <param name="dimension">The dimension of the point.</param>
    /// <param name="position">The position in the world.</param>
    /// <param name="direction">The direction it looks at, such as that of the camera, or zero.</param>
    /// <returns>The focus, which moves the point and removes it.</returns>
    LightFocus AddFocus(ModuleContext module, DimensionId dimension, in WorldPosition position, Vector3 direction = default);

    /// <summary>
    /// Copies the light of a chunk and of the blocks around it, one block
    /// deep, into a copy that a job may read while the light changes.
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="destination">The copy to fill; reuse it from copy to copy.</param>
    /// <returns><see langword="false"/> when nLighting holds no light for that chunk: it is not loaded, or no session is active.</returns>
    /// <remarks>It allocates nothing.</remarks>
    bool CopyFrozen(Grid grid, ChunkCoordinate chunk, LightSnapshot destination);

    /// <summary>
    /// Subscribes to the chunks whose light changed. The handler runs on the
    /// loop thread, after each propagation pass, once per chunk that changed.
    /// </summary>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The handle that ends the subscription.</returns>
    LightChangeSubscription SubscribeChanges(ModuleContext module, Action<LightChunkChange> handler);
}

/// <summary>A chunk whose light changed in a propagation pass.</summary>
/// <param name="Grid">The grid.</param>
/// <param name="Chunk">The chunk.</param>
/// <param name="TouchesBorder">Whether a block on the border of the chunk changed, which the chunks around it may read.</param>
/// <param name="Version">The version of the light of the chunk after the pass, as <see cref="LightSnapshot.Version"/> gives it.</param>
public readonly record struct LightChunkChange(Grid Grid, ChunkCoordinate Chunk, bool TouchesBorder, uint Version);

/// <summary>The size of the light of a context and the cost of its last propagation pass.</summary>
/// <param name="Chunks">The chunks that have light.</param>
/// <param name="ArrayChunks">The chunks whose light varies, which hold an array.</param>
/// <param name="StorageBytes">The bytes of those arrays, in native memory.</param>
/// <param name="LastPassChunks">The chunks that the last pass worked on.</param>
/// <param name="LastPassRounds">The rounds of jobs of the last pass.</param>
/// <param name="LastPassDuration">The time of the last pass on the loop thread, jobs included.</param>
public readonly record struct LightingStatistics(int Chunks, int ArrayChunks, long StorageBytes, int LastPassChunks, int LastPassRounds, TimeSpan LastPassDuration);

/// <summary>A subscription to the chunks whose light changed.</summary>
public sealed class LightChangeSubscription : IDisposable
{
    internal LightChangeSubscription(Action<LightChunkChange> handler) => Handler = handler;

    /// <summary>Gets whether the subscription ended.</summary>
    public bool IsRevoked { get; private set; }

    internal Action<LightChunkChange> Handler { get; }

    /// <summary>Ends the subscription; the handler is not called again. Disposing twice does nothing.</summary>
    public void Dispose() => IsRevoked = true;
}

/// <summary>
/// A frozen copy of the light of a chunk and of the blocks around it, one
/// block deep: 34 × 34 × 34 blocks. A job may read it while the light changes.
/// </summary>
/// <remarks>
/// Create it once and fill it again with <see cref="ILighting.CopyFrozen"/>:
/// it holds 76 KiB. A block of a chunk that is not loaded reads as full sky
/// without block light; <see cref="HasNeighbor"/> tells which chunks were loaded.
/// </remarks>
public sealed class LightSnapshot
{
    /// <summary>The number of blocks along each axis: the chunk and one block on each side.</summary>
    public const int Size = 34;

    private readonly ushort[] _values = new ushort[Size * Size * Size];
    private int _neighbors;

    /// <summary>Gets the copied chunk.</summary>
    public ChunkCoordinate Chunk { get; private set; }

    /// <summary>Gets the version of the light of the chunk when it was copied.</summary>
    public uint Version { get; private set; }

    /// <summary>Gets the light of a block.</summary>
    /// <param name="x">The local coordinate along X, from -1 to 32.</param>
    /// <param name="y">The local coordinate along Y, from -1 to 32.</param>
    /// <param name="z">The local coordinate along Z, from -1 to 32.</param>
    /// <returns>The light; without color, the three components of the block channel are equal.</returns>
    /// <exception cref="IndexOutOfRangeException">A coordinate is outside -1 to 32.</exception>
    [HotPath]
    public LightLevel Get(int x, int y, int z) => new(_values[(x + 1) + (Size * ((z + 1) + (Size * (y + 1))))]);

    /// <summary>Tells whether a chunk around the copied one was loaded, so that its blocks in the copy hold its light.</summary>
    /// <param name="dx">The offset in chunks along X, from -1 to 1.</param>
    /// <param name="dy">The offset in chunks along Y, from -1 to 1.</param>
    /// <param name="dz">The offset in chunks along Z, from -1 to 1.</param>
    /// <returns><see langword="true"/> when that chunk was loaded; always for (0, 0, 0).</returns>
    [HotPath]
    public bool HasNeighbor(int dx, int dy, int dz) => (_neighbors & (1 << Bit(dx, dy, dz))) != 0;

    internal void Fill(LightChunk center, Func<int, int, int, LightChunk?> neighbor, bool colored)
    {
        Chunk = center.Coordinate;
        Version = center.Version;
        _neighbors = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    LightChunk? source = dx == 0 && dy == 0 && dz == 0 ? center : neighbor(dx, dy, dz);
                    if (source is not null) _neighbors |= 1 << Bit(dx, dy, dz);
                    CopyRegion(source, dx, dy, dz, colored);
                }
            }
        }
    }

    private void CopyRegion(LightChunk? source, int dx, int dy, int dz, bool colored)
    {
        (int fromX, int toX) = Range(dx);
        (int fromY, int toY) = Range(dy);
        (int fromZ, int toZ) = Range(dz);
        for (int y = fromY; y <= toY; y++)
        {
            for (int z = fromZ; z <= toZ; z++)
            {
                for (int x = fromX; x <= toX; x++)
                {
                    ushort value = source is null ? LightChunk.DefaultValue : source.Get(x + (32 * (z + (32 * y))));
                    if (!colored)
                    {
                        int level = value & 15;
                        value = (ushort)((value & 0xF000) | (level << 8) | (level << 4) | level);
                    }

                    int sx = x + (dx * 32) + 1;
                    int sy = y + (dy * 32) + 1;
                    int sz = z + (dz * 32) + 1;
                    _values[sx + (Size * (sz + (Size * sy)))] = value;
                }
            }
        }
    }

    // The local cells of a chunk at an offset that the copy holds: its last layer, all of it, or its first layer.
    private static (int From, int To) Range(int offset) => offset < 0 ? (31, 31) : offset > 0 ? (0, 0) : (0, 31);

    [HotPath]
    private static int Bit(int dx, int dy, int dz) => (dx + 1) + (3 * ((dz + 1) + (3 * (dy + 1))));
}
