// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>A group of chunk sections: a grid, with its transform. The default value names no group.</summary>
/// <param name="Index">The slot of the group.</param>
/// <param name="Generation">The generation of the slot, so that an id of a destroyed group never names a newer one.</param>
[HotPath]
public readonly record struct ChunkGroupId(int Index, int Generation)
{
    /// <summary>Gets whether the id was given by <see cref="ChunkGeometry.CreateGroup"/>; it may name a destroyed group.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>A chunk section: the geometry of one chunk of a group. The default value names no section.</summary>
/// <param name="Index">The slot of the section.</param>
/// <param name="Generation">The generation of the slot, so that an id of a removed section never names a newer one.</param>
[HotPath]
public readonly record struct ChunkSectionId(int Index, int Generation)
{
    /// <summary>Gets whether the id was given by <see cref="ChunkGeometry.AddSection"/>; it may name a removed section.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>Where a section is in its group: the whole coordinates of its chunk, in chunks of <see cref="Core.Mathematics.WorldPosition.ChunkSize"/> blocks.</summary>
/// <param name="X">The chunk coordinate along X.</param>
/// <param name="Y">The chunk coordinate along Y.</param>
/// <param name="Z">The chunk coordinate along Z.</param>
[HotPath]
public readonly record struct SectionOrigin(int X, int Y, int Z);

/// <summary>A face of a chunk, for the connections that the visibility graph follows.</summary>
public enum SectionFace
{
    /// <summary>The face toward -X.</summary>
    NegativeX,

    /// <summary>The face toward +X.</summary>
    PositiveX,

    /// <summary>The face toward -Y.</summary>
    NegativeY,

    /// <summary>The face toward +Y.</summary>
    PositiveY,

    /// <summary>The face toward -Z.</summary>
    NegativeZ,

    /// <summary>The face toward +Z.</summary>
    PositiveZ,
}

/// <summary>
/// Which faces of a chunk see each other through it: two faces are connected
/// when a path of cells that do not block the view joins them inside the
/// chunk. The visibility graph crosses a section only from a face to a face
/// connected to it. A face is always connected to itself.
/// </summary>
/// <param name="Bits">The 15 pairs of distinct faces, one bit each.</param>
[HotPath]
public readonly record struct SectionConnectivity(ushort Bits)
{
    private const ushort AllBits = (1 << 15) - 1;

    /// <summary>Gets every face connected to every other: a chunk that does not block the view, such as air. The default of a section.</summary>
    public static SectionConnectivity All => new(AllBits);

    /// <summary>Gets no connection: a chunk that blocks the view through it, such as solid stone.</summary>
    public static SectionConnectivity None => new(0);

    /// <summary>Gets these connections with two more faces connected.</summary>
    /// <param name="a">A face.</param>
    /// <param name="b">Another face.</param>
    /// <returns>The connections.</returns>
    public SectionConnectivity With(SectionFace a, SectionFace b) => a == b ? this : new((ushort)(Bits | (1 << Pair(a, b))));

    /// <summary>Gets whether two faces are connected.</summary>
    /// <param name="a">A face.</param>
    /// <param name="b">Another face, or the same.</param>
    /// <returns><see langword="true"/> when the view passes from one to the other.</returns>
    public bool IsConnected(SectionFace a, SectionFace b) => a == b || (Bits & (1 << Pair(a, b))) != 0;

    // The index of an unordered pair of distinct faces, from 0 to 14.
    private static int Pair(SectionFace a, SectionFace b)
    {
        int low = Math.Min((int)a, (int)b);
        int high = Math.Max((int)a, (int)b);
        if ((uint)high > (uint)SectionFace.PositiveZ || low < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "A section has six faces.");
        }

        // Pairs (0,1)..(0,5), (1,2)..(1,5), and so on.
        return (low * (11 - low) / 2) + high - low - 1;
    }
}

/// <summary>The three categories of faces of a section, drawn in their own passes.</summary>
public enum ChunkCategory
{
    /// <summary>Opaque faces, drawn first, in any order.</summary>
    Opaque,

    /// <summary>Cut-out faces, such as leaves: opaque where they are drawn, cut by alpha testing in the shader, drawn without sorting.</summary>
    Cutout,

    /// <summary>Transparent faces, such as water and glass: sorted from back to front in each section, sections from the farthest to the nearest.</summary>
    Transparent,
}

/// <summary>
/// The geometry of a section: its vertices, and the triangles of each
/// category as indices into them. A mesher builds it; nRenderer copies it.
/// </summary>
/// <remarks>
/// The three lists of indices share the vertices; each holds three indices
/// per triangle, below the number of vertices. A list may be empty, and a
/// section with no triangle at all is drawn by nothing but still takes part
/// in the visibility graph.
/// </remarks>
public readonly ref struct ChunkSectionGeometry
{
    /// <summary>Initializes the geometry.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="opaque">The triangles of opaque faces.</param>
    /// <param name="cutout">The triangles of cut-out faces.</param>
    /// <param name="transparent">The triangles of transparent faces.</param>
    public ChunkSectionGeometry(ReadOnlySpan<ChunkVertex> vertices, ReadOnlySpan<uint> opaque, ReadOnlySpan<uint> cutout = default, ReadOnlySpan<uint> transparent = default)
    {
        Vertices = vertices;
        Opaque = opaque;
        Cutout = cutout;
        Transparent = transparent;
    }

    /// <summary>Gets the vertices.</summary>
    public ReadOnlySpan<ChunkVertex> Vertices { get; }

    /// <summary>Gets the triangles of opaque faces.</summary>
    public ReadOnlySpan<uint> Opaque { get; }

    /// <summary>Gets the triangles of cut-out faces.</summary>
    public ReadOnlySpan<uint> Cutout { get; }

    /// <summary>Gets the triangles of transparent faces.</summary>
    public ReadOnlySpan<uint> Transparent { get; }

    /// <summary>Gets which faces of the chunk see each other through it; <see cref="SectionConnectivity.All"/> by default.</summary>
    public SectionConnectivity Visibility { get; init; } = SectionConnectivity.All;
}

/// <summary>What the chunk geometry holds and what the GPU drew of it.</summary>
/// <remarks>
/// The memory figures are those of the last frame. The culling figures come
/// from the GPU and are read without waiting for it: they describe the frame
/// <see cref="CulledFrame"/>, a few frames back, or are 0 before the first.
/// </remarks>
public readonly record struct ChunkGeometryStatistics
{
    /// <summary>Gets the number of groups.</summary>
    public int Groups { get; init; }

    /// <summary>Gets the number of sections.</summary>
    public int Sections { get; init; }

    /// <summary>Gets the number of sections whose geometry waits for its upload to the GPU.</summary>
    public int PendingUploads { get; init; }

    /// <summary>Gets the size of the shared vertex buffer, in bytes.</summary>
    public long VertexBytes { get; init; }

    /// <summary>Gets the bytes of the vertex buffer that sections use.</summary>
    public long VertexBytesUsed { get; init; }

    /// <summary>Gets the size of the shared index buffer, in bytes.</summary>
    public long IndexBytes { get; init; }

    /// <summary>Gets the bytes of the index buffer that sections use.</summary>
    public long IndexBytesUsed { get; init; }

    /// <summary>
    /// Gets the fragmentation of the shared buffers, from 0 to 1: the part of
    /// their free space outside the largest free range, the larger of the two
    /// buffers. Defragmentation moves sections to bring it down.
    /// </summary>
    public float Fragmentation { get; init; }

    /// <summary>Gets the bytes that defragmentation moved during the last frame.</summary>
    public long DefragmentedBytes { get; init; }

    /// <summary>Gets the number of sections whose transparent faces were sorted again during the last frame.</summary>
    public int TransparentSorts { get; init; }

    /// <summary>Gets the number of sections that the CPU sent to the culling of the GPU in the last frame, after the visibility graph.</summary>
    public int Candidates { get; init; }

    /// <summary>Gets the number of sections that the visibility graph removed in the last frame.</summary>
    public int GraphCulled { get; init; }

    /// <summary>Gets the frame that the culling figures below describe.</summary>
    public ulong CulledFrame { get; init; }

    /// <summary>Gets the number of sections that the GPU found visible.</summary>
    public int Drawn { get; init; }

    /// <summary>Gets the number of sections outside the view.</summary>
    public int FrustumCulled { get; init; }

    /// <summary>Gets the number of sections hidden behind the depth of the previous frame.</summary>
    public int OcclusionCulled { get; init; }
}
