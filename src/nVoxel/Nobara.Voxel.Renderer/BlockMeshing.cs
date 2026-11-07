// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>
/// The mesher of a block shape added by a module: it writes the geometry of
/// one block, at every remesh of its chunk. The shapes of nVoxel have their
/// own meshers: cubes are merged face by face, models placed and turned.
/// </summary>
/// <remarks>
/// It runs in a background job, on a frozen copy of the chunk: it reads
/// nothing but its context, and allocates nothing. A mesher of an
/// instance-dependent shape receives the block data of the block in the
/// context, as a frozen copy.
/// </remarks>
public interface IBlockMesher
{
    /// <summary>Writes the geometry of a block.</summary>
    /// <param name="block">The block, its neighbors, its visual, and its block data.</param>
    /// <param name="writer">Receives the vertices and triangles, in the coordinates of the cell.</param>
    [HotPath]
    void Mesh(in BlockMeshContext block, BlockMeshWriter writer);
}

/// <summary>A block that a mesher of <see cref="IBlockMesher"/> meshes, read from the frozen copy of its chunk.</summary>
[HotPath]
public readonly struct BlockMeshContext
{
    private readonly MeshInput _input;
    private readonly int _cell;

    internal BlockMeshContext(MeshInput input, BlockVisuals visuals, int x, int y, int z, BlockDataView data)
    {
        _input = input;
        Visuals = visuals;
        Position = new LocalBlockCoordinate(x, y, z);
        _cell = MeshInput.Index(x, y, z);
        State = input.States[_cell];
        Data = data;
    }

    /// <summary>The state that <see cref="GetNeighbor"/> gives for a cell of a chunk that is not loaded.</summary>
    public const uint NotLoaded = MeshInput.NotLoaded;

    /// <summary>Gets the state of the block.</summary>
    public uint State { get; }

    /// <summary>Gets the position of the block in its chunk.</summary>
    public LocalBlockCoordinate Position { get; }

    /// <summary>Gets the block data of the block, a frozen copy, when its shape is instance-dependent; empty otherwise.</summary>
    public BlockDataView Data { get; }

    /// <summary>Gets the visual of the block: its textures, tint, category, and orientation.</summary>
    public BlockVisual Visual => Visuals.Get(State);

    internal BlockVisuals Visuals { get; }

    /// <summary>Gets the state of a neighbor of the block, up to one cell away along each axis, in the chunk or in a neighboring chunk.</summary>
    /// <param name="dx">The offset along X, from -1 to 1.</param>
    /// <param name="dy">The offset along Y, from -1 to 1.</param>
    /// <param name="dz">The offset along Z, from -1 to 1.</param>
    /// <returns>The state, 0 for air, or <see cref="NotLoaded"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An offset is outside -1 to 1.</exception>
    public uint GetNeighbor(int dx, int dy, int dz)
    {
        if ((uint)(dx + 1) > 2 || (uint)(dy + 1) > 2 || (uint)(dz + 1) > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(dx), "A mesher reads the neighbors one cell away.");
        }

        return _input.States[_cell + dx + (MeshInput.Size * (dz + (MeshInput.Size * dy)))];
    }

    /// <summary>Gets whether the face of a neighbor that touches this block hides what is behind it: an opaque block whose shape covers that face.</summary>
    /// <param name="face">The face of this block, toward the neighbor.</param>
    /// <returns><see langword="true"/> when the neighbor hides the face; a chunk that is not loaded hides it.</returns>
    public bool IsHidden(BlockFace face)
    {
        int direction = BlockVisuals.Direction(face);
        return _input.Occludes(_cell + MeshInput.Step(direction), BlockVisuals.Opposite(direction), Visuals);
    }
}

/// <summary>
/// The visual of a block state, from the free properties <c>RenderTexture</c>,
/// <c>RenderModel</c>, and <c>RenderTint</c> of its block, variants applied.
/// </summary>
[HotPath]
public readonly struct BlockVisual
{
    private readonly BlockVisuals _visuals;
    private readonly uint _state;

    internal BlockVisual(BlockVisuals visuals, uint state)
    {
        _visuals = visuals;
        _state = state;
    }

    /// <summary>Gets the category of the faces of the block, from its <c>Opacity</c>.</summary>
    public ChunkCategory Category => _visuals.CategoryOf(_state);

    /// <summary>Gets the index of the tint of the block in the palette of tints, 0 for none.</summary>
    public int Tint => _visuals.TintOf(_state);

    /// <summary>Gets the rotation of the orientation of the state, in the cell (<see cref="IShapeRegistry.GetOrientation"/>).</summary>
    public Matrix4x4 Orientation => _visuals.OrientationOf(_state);

    /// <summary>Gets the texture of a face of the block, after its orientation: the face that faces <paramref name="face"/> in the world.</summary>
    /// <param name="face">The face, in the world.</param>
    /// <param name="texture">The texture.</param>
    /// <returns><see langword="false"/> when the block has no texture for that face.</returns>
    public bool TryGetTexture(BlockFace face, out TextureLocation texture) =>
        BlockVisuals.TryUnpack(_visuals.FaceTexture(_state, BlockVisuals.Direction(face)), out texture);
}

/// <summary>
/// Receives the geometry of a block from a mesher: vertices in the
/// coordinates of the cell, from (0, 0, 0) to (1, 1, 1), and triangles in a
/// category. The writer moves them to the block, and gives them the light
/// and the tint of the block.
/// </summary>
[HotPath]
public sealed class BlockMeshWriter
{
    private MeshOutput _output = null!;
    private Vector3 _offset;
    private int _tint;
    private int _first;

    internal BlockMeshWriter()
    {
    }

    /// <summary>Adds a vertex.</summary>
    /// <param name="position">The position in the cell; the geometry may overhang the cell, up to 16 blocks out of its chunk.</param>
    /// <param name="textureCoordinates">The texture coordinates, in units of the texture.</param>
    /// <param name="texture">The texture.</param>
    /// <param name="normal">The normal.</param>
    /// <param name="ambientOcclusion">The ambient occlusion, from 0 (none) to 3.</param>
    /// <returns>The index of the vertex for <see cref="AddTriangle"/>.</returns>
    public int AddVertex(Vector3 position, Vector2 textureCoordinates, TextureLocation texture, ChunkNormal normal, int ambientOcclusion = 0) =>
        _output.AddVertex(new ChunkVertex(position + _offset, textureCoordinates, texture, normal, ChunkLight.Full, ambientOcclusion, _tint)) - _first;

    /// <summary>Adds a triangle of three vertices of this block, counterclockwise seen from its front.</summary>
    /// <param name="category">The category of the triangle.</param>
    /// <param name="a">The first vertex, as <see cref="AddVertex"/> returned it.</param>
    /// <param name="b">The second vertex.</param>
    /// <param name="c">The third vertex.</param>
    /// <exception cref="ArgumentOutOfRangeException">A vertex is not one of this block.</exception>
    public void AddTriangle(ChunkCategory category, int a, int b, int c)
    {
        int count = _output.VertexCount - _first;
        if ((uint)a >= (uint)count || (uint)b >= (uint)count || (uint)c >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "A triangle joins vertices that this block added.");
        }

        _output.AddTriangle(category, (uint)(a + _first), (uint)(b + _first), (uint)(c + _first));
    }

    internal void Begin(MeshOutput output, int x, int y, int z, int tint)
    {
        _output = output;
        _offset = new Vector3(x, y, z);
        _tint = tint;
        _first = output.VertexCount;
    }
}

/// <summary>What the meshing of chunks did, for tests and tools.</summary>
public readonly record struct VoxelMeshingStatistics
{
    /// <summary>Gets the number of chunks followed: loaded chunks of every grid.</summary>
    public int Chunks { get; init; }

    /// <summary>Gets the number of chunks that wait for a remesh.</summary>
    public int Dirty { get; init; }

    /// <summary>Gets the number of remeshes running in background jobs.</summary>
    public int Running { get; init; }

    /// <summary>Gets the number of chunks that have a section of chunk geometry.</summary>
    public int Sections { get; init; }

    /// <summary>Gets the number of remeshes started since the start.</summary>
    public long Started { get; init; }

    /// <summary>Gets the number of remeshes whose geometry reached the chunk geometry.</summary>
    public long Applied { get; init; }

    /// <summary>Gets the number of remeshes thrown away because their chunk changed while they ran.</summary>
    public long Discarded { get; init; }
}

/// <summary>
/// The meshing of the chunks of nVoxel into the chunk geometry of nRenderer,
/// on the client. Resolve it with <c>context.Resolve&lt;IVoxelMeshing&gt;()</c>.
/// </summary>
/// <remarks>Use it on the loop thread of the client context.</remarks>
public interface IVoxelMeshing
{
    /// <summary>Gets what the meshing did.</summary>
    VoxelMeshingStatistics Statistics { get; }

    /// <summary>Gets or sets the most remeshes that run at once in background jobs; twice the number of workers by default, and at least 1.</summary>
    int MaxRunning { get; set; }

    /// <summary>
    /// Registers the mesher of a shape that a module adds to nVoxel. Register
    /// it while the module registers or initializes: the meshers are fixed
    /// when nVoxel.Renderer starts, and a shape without a mesher is reported
    /// then, its blocks drawn by nothing.
    /// </summary>
    /// <param name="owner">The module of the mesher.</param>
    /// <param name="shapeId">The identifier of the shape, such as <c>shape:example.sculpted</c>.</param>
    /// <param name="mesher">The mesher.</param>
    /// <returns>The handle; disposing it before the start removes the mesher.</returns>
    /// <exception cref="ArgumentException">The shape has a mesher already, or is a shape of nVoxel.</exception>
    /// <exception cref="InvalidOperationException">nVoxel.Renderer started.</exception>
    IDisposable RegisterMesher(ModuleContext owner, string shapeId, IBlockMesher mesher);

    /// <summary>Gets the visual of a block state, once nVoxel.Renderer started.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The visual.</returns>
    /// <exception cref="InvalidOperationException">nVoxel.Renderer did not start.</exception>
    BlockVisual GetVisual(uint state);

    /// <summary>Gets whether a chunk has the geometry of its current blocks: loaded, not waiting for a remesh, and its last remesh applied.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns><see langword="true"/> when the chunk is meshed.</returns>
    bool IsMeshed(Grid grid, ChunkCoordinate chunk);
}
