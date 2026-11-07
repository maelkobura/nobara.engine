// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using System.Numerics;
using Nobara.Core;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>A cell of the copy whose shape depends on its block data: which faces it hides, and its data.</summary>
internal readonly record struct InstanceCell(int Cell, byte Occludes, BlockDataView Data);

/// <summary>
/// The frozen copy of a chunk that a remesh reads: the states of its cells and
/// of a border of one cell around it, in a cube of 34 cells per edge, and the
/// cells whose shape depends on their block data.
/// </summary>
internal sealed class MeshInput
{
    /// <summary>The cells along an edge of the copy: the chunk and a border of one cell.</summary>
    public const int Size = VoxelCoordinates.ChunkSize + 2;

    /// <summary>The cells of the copy.</summary>
    public const int Volume = Size * Size * Size;

    /// <summary>The state of a cell of a chunk that is not loaded: it hides the faces next to it and casts no shadow.</summary>
    public const uint NotLoaded = uint.MaxValue;

    [HotPath]
    public uint[] States { get; } = new uint[Volume];

    [HotPath]
    public List<InstanceCell> Instances { get; } = [];

    /// <summary>Gets the index in the copy of a cell, from -1 to 32 along each axis.</summary>
    [HotPath]
    public static int Index(int x, int y, int z) => x + 1 + (Size * (z + 1 + (Size * (y + 1))));

    /// <summary>Gets the step between two cells along a direction, in the order of <see cref="ChunkNormal"/>.</summary>
    [HotPath]
    public static int Step(int direction) => direction switch
    {
        0 => -1,
        1 => 1,
        2 => -Size * Size,
        3 => Size * Size,
        4 => -Size,
        _ => Size,
    };

    /// <summary>Gets whether the cell hides the face of its neighbor on its side <paramref name="face"/>.</summary>
    [HotPath]
    public bool Occludes(int cell, int face, BlockVisuals visuals)
    {
        uint state = States[cell];
        if (state == NotLoaded)
        {
            return true;
        }

        byte occludes = visuals.Occludes(state);
        if ((occludes & BlockVisuals.InstanceBit) != 0)
        {
            occludes = InstanceOccludes(cell);
        }

        return ((occludes >> face) & 1) != 0;
    }

    /// <summary>Gets whether the cell is a block that hides all its faces, which casts ambient occlusion.</summary>
    [HotPath]
    public bool IsSolid(int cell, BlockVisuals visuals)
    {
        uint state = States[cell];
        if (state == NotLoaded)
        {
            return false;
        }

        byte occludes = visuals.Occludes(state);
        if ((occludes & BlockVisuals.InstanceBit) != 0)
        {
            occludes = InstanceOccludes(cell);
        }

        return (occludes & BlockVisuals.AllFaces) == BlockVisuals.AllFaces;
    }

    /// <summary>Gets the block data of a cell whose shape is instance-dependent.</summary>
    [HotPath]
    public BlockDataView DataOf(int cell)
    {
        List<InstanceCell> instances = Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Cell == cell)
            {
                return instances[i].Data;
            }
        }

        return BlockDataView.Empty;
    }

    [HotPath]
    private byte InstanceOccludes(int cell)
    {
        List<InstanceCell> instances = Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Cell == cell)
            {
                return instances[i].Occludes;
            }
        }

        return 0;
    }
}

/// <summary>The geometry of a remesh: vertices, three lists of triangles, and the connections of the faces of the chunk.</summary>
internal sealed class MeshOutput
{
    private ChunkVertex[] _vertices = ArrayPool<ChunkVertex>.Shared.Rent(4096);
    private readonly uint[][] _indices = [ArrayPool<uint>.Shared.Rent(8192), ArrayPool<uint>.Shared.Rent(1024), ArrayPool<uint>.Shared.Rent(1024)];
    private readonly int[] _counts = new int[3];

    [HotPath]
    public int VertexCount { get; private set; }

    [HotPath]
    public SectionConnectivity Connectivity { get; set; } = SectionConnectivity.All;

    [HotPath]
    public ReadOnlySpan<ChunkVertex> Vertices => _vertices.AsSpan(0, VertexCount);

    [HotPath]
    public int TriangleCount => (_counts[0] + _counts[1] + _counts[2]) / 3;

    [HotPath]
    public ReadOnlySpan<uint> Indices(ChunkCategory category) => _indices[(int)category].AsSpan(0, _counts[(int)category]);

    [HotPath]
    public void Reset()
    {
        VertexCount = 0;
        _counts[0] = 0;
        _counts[1] = 0;
        _counts[2] = 0;
        Connectivity = SectionConnectivity.All;
    }

    [HotPath]
    public int AddVertex(in ChunkVertex vertex)
    {
        if (VertexCount == _vertices.Length)
        {
            _vertices = Grow(_vertices, VertexCount);
        }

        _vertices[VertexCount] = vertex;
        return VertexCount++;
    }

    [HotPath]
    public void AddTriangle(ChunkCategory category, uint a, uint b, uint c)
    {
        int list = (int)category;
        uint[] indices = _indices[list];
        int count = _counts[list];
        if (count + 3 > indices.Length)
        {
            indices = Grow(indices, count);
            _indices[list] = indices;
        }

        indices[count] = a;
        indices[count + 1] = b;
        indices[count + 2] = c;
        _counts[list] = count + 3;
    }

    // The arrays grow until they hold the largest chunk, in the shared pool; they are kept after that.
    [HotPath]
    private static T[] Grow<T>(T[] array, int used)
    {
        T[] larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, used).CopyTo(larger);
        ArrayPool<T>.Shared.Return(array);
        return larger;
    }
}

/// <summary>
/// Meshes a frozen copy of a chunk: cubes merged face by face (greedy
/// meshing) with hidden faces removed and ambient occlusion at each vertex,
/// models placed and turned, blocks of other shapes by their meshers, and the
/// connections of the faces of the chunk for the visibility graph.
/// </summary>
/// <remarks>Its scratch memory is kept from one chunk to the next: once it has meshed the largest chunk, it allocates nothing.</remarks>
internal sealed class ChunkMesher
{
    private const int Edge = VoxelCoordinates.ChunkSize;
    private const ulong Mergeable = 1UL << 40;

    private readonly ulong[] _mask = new ulong[Edge * Edge];
    private readonly int[] _queue = new int[VoxelCoordinates.ChunkVolume];
    private readonly ulong[] _visited = new ulong[VoxelCoordinates.ChunkVolume / 64];
    private readonly BlockMeshWriter _writer = new();

    /// <summary>Meshes a copy.</summary>
    /// <param name="input">The copy.</param>
    /// <param name="visuals">The tables of the states.</param>
    /// <param name="output">Receives the geometry; it is reset first.</param>
    [HotPath]
    public void Mesh(MeshInput input, BlockVisuals visuals, MeshOutput output)
    {
        output.Reset();
        for (int direction = 0; direction < 6; direction++)
        {
            MeshCubeFaces(input, visuals, output, direction);
        }

        MeshOtherShapes(input, visuals, output);
        output.Connectivity = Connect(input, visuals);
    }

    // The faces of cubes toward one direction, slice by slice: a mask of the
    // visible faces of the slice, then the largest rectangles of equal faces.
    [HotPath]
    private void MeshCubeFaces(MeshInput input, BlockVisuals visuals, MeshOutput output, int direction)
    {
        int axis = direction >> 1;
        int step = MeshInput.Step(direction);
        int opposite = BlockVisuals.Opposite(direction);
        uint[] states = input.States;
        ulong[] mask = _mask;
        for (int slice = 0; slice < Edge; slice++)
        {
            bool any = false;
            for (int v = 0; v < Edge; v++)
            {
                for (int u = 0; u < Edge; u++)
                {
                    Cell(axis, slice, u, v, out int x, out int y, out int z);
                    int cell = MeshInput.Index(x, y, z);
                    uint state = states[cell];
                    ulong key = 0;
                    if (visuals.KindOf(state) == BlockVisuals.Cube && IsVisible(input, visuals, cell, state, step, opposite))
                    {
                        uint texture = visuals.FaceTexture(state, direction);
                        if (texture != 0)
                        {
                            int occlusion = Occlusion(input, visuals, cell + step, axis, out bool uniform);
                            key = 1UL
                                | ((ulong)texture << 1)
                                | ((ulong)visuals.TintOf(state) << 18)
                                | ((ulong)visuals.CategoryOf(state) << 25)
                                | ((ulong)occlusion << 27)
                                | (uniform ? Mergeable : 0);
                            any = true;
                        }
                    }

                    mask[(v * Edge) + u] = key;
                }
            }

            if (any)
            {
                Merge(output, direction, axis, slice);
            }
        }
    }

    // Whether a face of a cube shows: its neighbor does not hide it, and is not a
    // transparent block of the same type (the faces between two panes of glass
    // or two cells of water are hidden). Cut-out blocks keep the faces between them.
    [HotPath]
    private static bool IsVisible(MeshInput input, BlockVisuals visuals, int cell, uint state, int step, int opposite)
    {
        int neighbor = cell + step;
        if (input.Occludes(neighbor, opposite, visuals))
        {
            return false;
        }

        uint other = input.States[neighbor];
        return visuals.CategoryOf(state) != ChunkCategory.Transparent || visuals.TypeOf(other) != visuals.TypeOf(state);
    }

    // The ambient occlusion of the four corners of a face, from the cell in
    // front of it: 2 bits each, in the order of the corners of Corner.
    [HotPath]
    private static int Occlusion(MeshInput input, BlockVisuals visuals, int front, int axis, out bool uniform)
    {
        TangentSteps(axis, out int stepU, out int stepV);
        int packed = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            int su = corner is 1 or 2 ? stepU : -stepU;
            int sv = corner >= 2 ? stepV : -stepV;
            bool side1 = input.IsSolid(front + su, visuals);
            bool side2 = input.IsSolid(front + sv, visuals);
            int value = side1 && side2 ? 3 : (side1 ? 1 : 0) + (side2 ? 1 : 0) + (input.IsSolid(front + su + sv, visuals) ? 1 : 0);
            packed |= value << (corner * 2);
        }

        uniform = packed is 0 or 0x55 or 0xAA or 0xFF;
        return packed;
    }

    [HotPath]
    private void Merge(MeshOutput output, int direction, int axis, int slice)
    {
        ulong[] mask = _mask;
        for (int v = 0; v < Edge; v++)
        {
            for (int u = 0; u < Edge;)
            {
                ulong key = mask[(v * Edge) + u];
                if (key == 0)
                {
                    u++;
                    continue;
                }

                int width = 1;
                int height = 1;
                if ((key & Mergeable) != 0)
                {
                    while (u + width < Edge && mask[(v * Edge) + u + width] == key)
                    {
                        width++;
                    }

                    while (v + height < Edge && RowMatches(mask, key, u, width, v + height))
                    {
                        height++;
                    }
                }

                for (int dv = 0; dv < height; dv++)
                {
                    mask.AsSpan(((v + dv) * Edge) + u, width).Clear();
                }

                EmitQuad(output, key, direction, axis, slice, u, v, width, height);
                u += width;
            }
        }
    }

    [HotPath]
    private static bool RowMatches(ulong[] mask, ulong key, int u, int width, int v)
    {
        for (int i = 0; i < width; i++)
        {
            if (mask[(v * Edge) + u + i] != key)
            {
                return false;
            }
        }

        return true;
    }

    [HotPath]
    private static void EmitQuad(MeshOutput output, ulong key, int direction, int axis, int slice, int u, int v, int width, int height)
    {
        BlockVisuals.TryUnpack((uint)((key >> 1) & 0x1FFFF), out TextureLocation texture);
        int tint = (int)((key >> 18) & 0x7F);
        var category = (ChunkCategory)((key >> 25) & 3);
        int occlusion = (int)((key >> 27) & 0xFF);
        bool positive = (direction & 1) != 0;
        float plane = slice + (positive ? 1 : 0);
        var normal = (ChunkNormal)direction;

        // Corners 0 to 3 at (u0, v0), (u1, v0), (u1, v1), (u0, v1): counterclockwise
        // seen from the front for -X, -Y and +Z, so the order is reversed for the others.
        bool reverse = axis == 2 ? !positive : positive;
        uint first = (uint)output.VertexCount;
        for (int i = 0; i < 4; i++)
        {
            int corner = reverse ? 3 - i : i;
            int cu = corner is 1 or 2 ? width : 0;
            int cv = corner >= 2 ? height : 0;
            Vector3 position = Point(axis, plane, u + cu, v + cv);
            Vector2 uv = TextureCoordinates(direction, cu, cv, width, height);
            int ao = (occlusion >> (corner * 2)) & 3;
            output.AddVertex(new ChunkVertex(position, uv, texture, normal, ChunkLight.Full, ao, tint));
        }

        // The diagonal of the quad joins its two lighter corners, so a shadowed corner does not streak along the face.
        int a0 = (occlusion >> ((reverse ? 3 : 0) * 2)) & 3;
        int a1 = (occlusion >> ((reverse ? 2 : 1) * 2)) & 3;
        int a2 = (occlusion >> ((reverse ? 1 : 2) * 2)) & 3;
        int a3 = (occlusion >> ((reverse ? 0 : 3) * 2)) & 3;
        if (a0 + a2 > a1 + a3)
        {
            output.AddTriangle(category, first + 1, first + 2, first + 3);
            output.AddTriangle(category, first + 1, first + 3, first);
        }
        else
        {
            output.AddTriangle(category, first, first + 1, first + 2);
            output.AddTriangle(category, first, first + 2, first + 3);
        }
    }

    // Texture coordinates of a corner, in blocks: U to the right and V down,
    // seen from the front, for the sides; U along X and V along Z for the top.
    [HotPath]
    private static Vector2 TextureCoordinates(int direction, int cu, int cv, int width, int height) => direction switch
    {
        0 => new Vector2(cu, height - cv),
        1 => new Vector2(width - cu, height - cv),
        2 => new Vector2(cu, height - cv),
        3 => new Vector2(cu, cv),
        4 => new Vector2(width - cu, height - cv),
        _ => new Vector2(cu, height - cv),
    };

    // The axes of a slice: the slice along the axis of the face, U and V in it.
    // X faces: U is Z, V is Y. Y faces: U is X, V is Z. Z faces: U is X, V is Y.
    [HotPath]
    private static void Cell(int axis, int slice, int u, int v, out int x, out int y, out int z)
    {
        switch (axis)
        {
            case 0:
                (x, y, z) = (slice, v, u);
                break;
            case 1:
                (x, y, z) = (u, slice, v);
                break;
            default:
                (x, y, z) = (u, v, slice);
                break;
        }
    }

    [HotPath]
    private static Vector3 Point(int axis, float plane, float u, float v) => axis switch
    {
        0 => new Vector3(plane, v, u),
        1 => new Vector3(u, plane, v),
        _ => new Vector3(u, v, plane),
    };

    [HotPath]
    private static void TangentSteps(int axis, out int stepU, out int stepV)
    {
        switch (axis)
        {
            case 0:
                (stepU, stepV) = (MeshInput.Size, MeshInput.Size * MeshInput.Size);
                break;
            case 1:
                (stepU, stepV) = (1, MeshInput.Size);
                break;
            default:
                (stepU, stepV) = (1, MeshInput.Size * MeshInput.Size);
                break;
        }
    }

    // Models and the shapes of other modules, block by block.
    [HotPath]
    private void MeshOtherShapes(MeshInput input, BlockVisuals visuals, MeshOutput output)
    {
        uint[] states = input.States;
        for (int y = 0; y < Edge; y++)
        {
            for (int z = 0; z < Edge; z++)
            {
                for (int x = 0; x < Edge; x++)
                {
                    int cell = MeshInput.Index(x, y, z);
                    uint state = states[cell];
                    byte kind = visuals.KindOf(state);
                    if (kind == BlockVisuals.Model)
                    {
                        int model = visuals.ModelOf(state);
                        if (model >= 0)
                        {
                            PlaceModel(output, visuals.Models[model], visuals.CategoryOf(state), visuals.TintOf(state), x, y, z);
                        }
                    }
                    else if (kind == BlockVisuals.Custom)
                    {
                        BlockDataView data = (visuals.Occludes(state) & BlockVisuals.InstanceBit) != 0 ? input.DataOf(cell) : BlockDataView.Empty;
                        var context = new BlockMeshContext(input, visuals, x, y, z, data);
                        _writer.Begin(output, x, y, z, visuals.TintOf(state));
                        visuals.Meshers[visuals.MesherOf(state)].Mesh(context, _writer);
                    }
                }
            }
        }
    }

    [HotPath]
    private static void PlaceModel(MeshOutput output, ModelGeometry model, ChunkCategory category, int tint, int x, int y, int z)
    {
        var offset = new Vector3(x, y, z);
        uint first = (uint)output.VertexCount;
        ModelVertex[] vertices = model.Vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            ModelVertex v = vertices[i];
            output.AddVertex(new ChunkVertex(v.Position + offset, v.TextureCoordinates, v.Texture, v.Normal, ChunkLight.Full, 0, tint));
        }

        uint[] indices = model.Indices;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            output.AddTriangle(category, first + indices[i], first + indices[i + 1], first + indices[i + 2]);
        }
    }

    // Which faces of the chunk see each other: a flood fill of the cells that
    // do not hide all their faces, each region joining the faces it touches.
    [HotPath]
    private SectionConnectivity Connect(MeshInput input, BlockVisuals visuals)
    {
        ulong[] visited = _visited;
        Array.Clear(visited);
        int open = 0;
        for (int i = 0; i < VoxelCoordinates.ChunkVolume; i++)
        {
            if (IsClosed(input, visuals, i))
            {
                visited[i >> 6] |= 1UL << (i & 63);
            }
            else
            {
                open++;
            }
        }

        if (open == VoxelCoordinates.ChunkVolume)
        {
            return SectionConnectivity.All;
        }

        var result = SectionConnectivity.None;
        int[] queue = _queue;
        for (int start = 0; start < VoxelCoordinates.ChunkVolume; start++)
        {
            if ((visited[start >> 6] & (1UL << (start & 63))) != 0)
            {
                continue;
            }

            visited[start >> 6] |= 1UL << (start & 63);
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            int faces = 0;
            while (head < tail)
            {
                int index = queue[head++];
                int x = index & 31;
                int z = (index >> 5) & 31;
                int y = index >> 10;
                faces |= (x == 0 ? 1 : 0) | (x == 31 ? 2 : 0) | (y == 0 ? 4 : 0) | (y == 31 ? 8 : 0) | (z == 0 ? 16 : 0) | (z == 31 ? 32 : 0);
                if (x > 0) Visit(visited, queue, ref tail, index - 1);
                if (x < 31) Visit(visited, queue, ref tail, index + 1);
                if (z > 0) Visit(visited, queue, ref tail, index - 32);
                if (z < 31) Visit(visited, queue, ref tail, index + 32);
                if (y > 0) Visit(visited, queue, ref tail, index - 1024);
                if (y < 31) Visit(visited, queue, ref tail, index + 1024);
            }

            for (int a = 0; a < 6; a++)
            {
                if ((faces & (1 << a)) == 0)
                {
                    continue;
                }

                for (int b = a + 1; b < 6; b++)
                {
                    if ((faces & (1 << b)) != 0)
                    {
                        result = result.With((SectionFace)a, (SectionFace)b);
                    }
                }
            }
        }

        return result;
    }

    [HotPath]
    private static bool IsClosed(MeshInput input, BlockVisuals visuals, int localIndex)
    {
        int x = localIndex & 31;
        int z = (localIndex >> 5) & 31;
        int y = localIndex >> 10;
        return input.IsSolid(MeshInput.Index(x, y, z), visuals);
    }

    [HotPath]
    private static void Visit(ulong[] visited, int[] queue, ref int tail, int index)
    {
        ulong bit = 1UL << (index & 63);
        if ((visited[index >> 6] & bit) == 0)
        {
            visited[index >> 6] |= bit;
            queue[tail++] = index;
        }
    }
}
