// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>
/// The geometry of chunks on the GPU: groups of sections (a grid and the
/// chunks of its geometry), stored in a few large buffers shared by every
/// section, culled on the GPU, and drawn with a few indirect draws. nRenderer
/// knows neither voxels nor blocks: a mesher gives it vertices and
/// triangles.
/// </summary>
/// <remarks>
/// <para>
/// A module changes the geometry during extraction, from an
/// <see cref="IRenderExtractor"/> (<see cref="RenderExtraction.Chunks"/>), with
/// the changes since the previous frame: a group per grid, moved like an
/// object of the render world and interpolated by the same rules, and a
/// section per chunk that has geometry, added, replaced, or removed. The
/// geometry stays on the GPU; only changes are sent.
/// </para>
/// <para>
/// A pipeline draws it with three functions, declared in its render graph:
/// <see cref="AddCullingPass"/>, which lists on the GPU the sections to draw,
/// <see cref="Draw"/>, which draws one category of faces from that list in a
/// raster pass, and <see cref="AddDepthPyramidPass"/>, which keeps the depth
/// of the frame for the occlusion culling of the next one.
/// </para>
/// <para>Use it on the loop thread of the client context.</para>
/// </remarks>
public sealed partial class ChunkGeometry
{
    /// <summary>The graphics features that chunk geometry needs. Without one of them, sections are kept but nothing is drawn.</summary>
    public const GraphicsFeatures RequiredFeatures = GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect | GraphicsFeatures.IndirectCount;

    /// <summary>The binding of the storage buffer of the groups, that the resource set of <see cref="DrawLayout"/> binds for vertex shaders.</summary>
    public const int GroupsBinding = 7;

    /// <summary>The binding of the storage buffer of the palette of tints (<see cref="SetTints"/>), that the resource set of <see cref="DrawLayout"/> binds.</summary>
    public const int TintsBinding = 8;

    /// <summary>The number of tints of the palette: indices 0 to <see cref="ChunkVertex.MaxTint"/>.</summary>
    public const int TintCount = ChunkVertex.MaxTint + 1;

    /// <summary>The default name of the culling pass.</summary>
    public const string CullingPassName = "nobara.renderer.chunk-culling";

    /// <summary>The default name of the pass that builds the depth pyramid.</summary>
    public const string DepthPyramidPassName = "nobara.renderer.chunk-depth-pyramid";

    private readonly RenderWorld _world;
    private readonly ModuleContext _context;
    private GroupSlot[] _groups = new GroupSlot[16];
    private int[] _freeGroups = new int[16];
    private int _freeGroupCount;
    private int _groupsUsed;
    private SectionSlot[] _sections = new SectionSlot[256];
    private SectionConnectivity[] _connectivity = new SectionConnectivity[256];
    private int[] _freeSections = new int[256];
    private int _freeSectionCount;
    private int _sectionsUsed;
    private readonly Queue<(int Slot, int Generation)> _pending = new();
    private readonly Stack<PendingGeometry> _pendingPool = new();
    private int[] _groupSections = new int[64];
    private float _resortAngle = 5;
    private float _resortDistance = 0.5f;
    private long _uploadBudget = 16L * 1024 * 1024;
    private long _defragmentationBudget = 4L * 1024 * 1024;
    private float _defragmentationThreshold = 0.25f;

    internal ChunkGeometry(RenderWorld world, ModuleContext context)
    {
        _world = world;
        _context = context;
        VertexLayouts = [ChunkVertex.Layout(0), new VertexBufferLayout(SectionRecordSize, [new VertexAttribute(1, VertexFormat.UInt4, 0)], VertexStepMode.Instance)];
    }

    /// <summary>Gets the number of groups.</summary>
    public int GroupCount { get; private set; }

    /// <summary>Gets the number of sections.</summary>
    public int SectionCount { get; private set; }

    /// <summary>Gets whether the device has the <see cref="RequiredFeatures"/>: sections are drawn.</summary>
    public bool IsSupported { get; private set; }

    /// <summary>Gets what the geometry holds and what the GPU drew of it.</summary>
    public ChunkGeometryStatistics Statistics => CurrentStatistics();

    /// <summary>
    /// Gets the vertex buffers of a graphics pipeline that draws chunk geometry,
    /// by slot: slot 0, the vertices (<see cref="ChunkVertex.Layout"/>, location
    /// 0), and slot 1, per instance, the section (location 1: its chunk and its
    /// group, as four unsigned integers).
    /// </summary>
    public IReadOnlyList<VertexBufferLayout> VertexLayouts { get; }

    /// <summary>Gets or sets whether the culling of the GPU removes the sections hidden behind the depth of the previous frame; <see langword="true"/> by default.</summary>
    public bool OcclusionCulling { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the CPU follows the visibility graph of each group
    /// before the culling of the GPU, so that sections closed off from the
    /// camera are not sent to it; <see langword="true"/> by default.
    /// </summary>
    public bool VisibilityGraph { get; set; } = true;

    /// <summary>
    /// Gets or sets the turn, in degrees, of the direction from a section to the
    /// camera after which its transparent faces are sorted again; 5 by default,
    /// from 0 to 90.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 90.</exception>
    public float TransparentResortAngle
    {
        get => _resortAngle;
        set => _resortAngle = value is >= 0 and <= 90 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The angle is from 0 to 90 degrees.");
    }

    /// <summary>
    /// Gets or sets the distance, in blocks, that the camera moves at least
    /// before the transparent faces of a section are sorted again, for a
    /// camera near or inside the section; 0.5 by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public float TransparentResortDistance
    {
        get => _resortDistance;
        set => _resortDistance = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The distance is at least 0.");
    }

    /// <summary>
    /// Gets or sets the bytes of geometry sent to the GPU per frame; 16 MiB by
    /// default. Sections beyond it wait for the next frames, in the order they
    /// were given; at least one section is sent per frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long UploadBudget
    {
        get => _uploadBudget;
        set => _uploadBudget = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The budget is more than 0.");
    }

    /// <summary>Gets or sets the bytes that defragmentation moves per frame, at most; 4 MiB by default, 0 to turn it off.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long DefragmentationBudget
    {
        get => _defragmentationBudget;
        set => _defragmentationBudget = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The budget is at least 0.");
    }

    /// <summary>Gets or sets the fragmentation of a shared buffer above which defragmentation moves sections, from 0 to 1; 0.25 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float DefragmentationThreshold
    {
        get => _defragmentationThreshold;
        set => _defragmentationThreshold = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold is from 0 to 1.");
    }

    /// <summary>Gets the palette of tints, in linear colors: index 0 is white unless <see cref="SetTints"/> changed it.</summary>
    public ReadOnlySpan<Vector3> Tints => _tints;

    /// <summary>
    /// Sets the palette of tints that the vertices name by index
    /// (<see cref="ChunkVertex.Tint"/>): linear colors that multiply the
    /// texture in the shader (<c>nobara_chunkTint</c>). Indices beyond the
    /// colors given are white. The palette reaches the GPU with the next frame.
    /// </summary>
    /// <param name="colors">The colors, from index 0; at most <see cref="TintCount"/>. Index 0 should stay white, the tint of vertices without one.</param>
    /// <exception cref="ArgumentException">There are more than <see cref="TintCount"/> colors.</exception>
    public void SetTints(ReadOnlySpan<Vector3> colors)
    {
        if (colors.Length > TintCount)
        {
            throw new ArgumentException($"The palette of tints holds at most {TintCount} colors.", nameof(colors));
        }

        _tints.AsSpan().Fill(Vector3.One);
        colors.CopyTo(_tints);
        for (int i = 0; i < TintCount; i++)
        {
            _tintRecords[i] = new Vector4(_tints[i], 1);
        }

        _tintsChanged = true;
    }

    /// <summary>Creates a group: a grid whose sections share its transform.</summary>
    /// <param name="transform">The transform of the grid: where the corner of its chunk (0, 0, 0) is, its rotation, and its scale.</param>
    /// <param name="visible">Whether its sections are drawn.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    public ChunkGroupId CreateGroup(in WorldTransform transform, bool visible = true)
    {
        CheckWritable();
        int index;
        if (_freeGroupCount > 0)
        {
            index = _freeGroups[--_freeGroupCount];
        }
        else
        {
            if (_groupsUsed == _groups.Length)
            {
                Array.Resize(ref _groups, _groups.Length * 2);
                Array.Resize(ref _freeGroups, _groups.Length);
            }

            index = _groupsUsed++;
        }

        ref GroupSlot slot = ref _groups[index];
        slot.Generation++;
        slot.Alive = true;
        slot.Visible = visible;
        slot.Previous = transform;
        slot.Current = transform;
        slot.MovedTick = ulong.MaxValue;
        slot.PyramidValid = false;
        slot.Graph ??= new VisibilityGraph();
        GroupCount++;
        return new ChunkGroupId(index, slot.Generation);
    }

    /// <summary>Moves a group as the simulation did during the last tick: it is interpolated until the next tick, as objects are.</summary>
    /// <param name="group">The group.</param>
    /// <param name="transform">The transform at the last tick.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The group does not exist.</exception>
    [HotPath]
    public void MoveGroup(ChunkGroupId group, in WorldTransform transform)
    {
        ref GroupSlot slot = ref GetGroup(group);
        if (!_world.Interpolates)
        {
            slot.Previous = transform;
        }
        else if (slot.MovedTick != _world.Tick)
        {
            slot.Previous = slot.Current;
            slot.MovedTick = _world.Tick;
        }

        slot.Current = transform;
    }

    /// <summary>Places a group without interpolation.</summary>
    /// <param name="group">The group.</param>
    /// <param name="transform">The transform.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The group does not exist.</exception>
    [HotPath]
    public void TeleportGroup(ChunkGroupId group, in WorldTransform transform)
    {
        ref GroupSlot slot = ref GetGroup(group);
        slot.Previous = transform;
        slot.Current = transform;
        slot.MovedTick = ulong.MaxValue;
        slot.PyramidValid = false;
    }

    /// <summary>Shows or hides the sections of a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="visible"><see langword="true"/> to draw them.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The group does not exist.</exception>
    [HotPath]
    public void SetGroupVisible(ChunkGroupId group, bool visible) => GetGroup(group).Visible = visible;

    /// <summary>Gets the last transform given to a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The transform.</returns>
    /// <exception cref="ArgumentException">The group does not exist.</exception>
    public WorldTransform GetGroupTransform(ChunkGroupId group)
    {
        CheckGroup(group);
        return _groups[group.Index].Current;
    }

    /// <summary>Destroys a group and removes its sections.</summary>
    /// <param name="group">The group.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The group does not exist.</exception>
    public void DestroyGroup(ChunkGroupId group)
    {
        ref GroupSlot slot = ref GetGroup(group);
        int count = 0;
        if (_groupSections.Length < slot.Graph!.Count)
        {
            _groupSections = new int[Math.Max(slot.Graph.Count, _groupSections.Length * 2)];
        }

        foreach (int section in slot.Graph.Slots)
        {
            _groupSections[count++] = section;
        }

        for (int i = 0; i < count; i++)
        {
            RemoveSlot(_groupSections[i]);
        }

        slot.Alive = false;
        slot.Visible = false;
        _freeGroups[_freeGroupCount++] = group.Index;
        GroupCount--;
    }

    /// <summary>Gets whether a group exists.</summary>
    /// <param name="group">The group.</param>
    /// <returns><see langword="true"/> when it was created and not destroyed.</returns>
    [HotPath]
    public bool Contains(ChunkGroupId group) =>
        (uint)group.Index < (uint)_groupsUsed && _groups[group.Index].Alive && _groups[group.Index].Generation == group.Generation;

    /// <summary>Gets whether a section exists.</summary>
    /// <param name="section">The section.</param>
    /// <returns><see langword="true"/> when it was added and not removed.</returns>
    [HotPath]
    public bool Contains(ChunkSectionId section) =>
        (uint)section.Index < (uint)_sectionsUsed && _sections[section.Index].Alive && _sections[section.Index].Generation == section.Generation;

    /// <summary>
    /// Adds the section of a chunk to a group, with its geometry, which is
    /// copied. It is drawn once the geometry reaches the GPU, within the
    /// <see cref="UploadBudget"/> of a frame.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <param name="origin">The chunk of the section in the group.</param>
    /// <param name="geometry">The geometry.</param>
    /// <returns>The id of the section.</returns>
    /// <exception cref="InvalidOperationException">The world is not being extracted, or the group has a section at this origin.</exception>
    /// <exception cref="ArgumentException">The group does not exist, or the geometry is not valid: a list of indices that is not whole triangles, or an index outside the vertices.</exception>
    public ChunkSectionId AddSection(ChunkGroupId group, SectionOrigin origin, in ChunkSectionGeometry geometry)
    {
        ref GroupSlot owner = ref GetGroup(group);
        if (owner.Graph!.Contains(origin))
        {
            throw new InvalidOperationException($"The group {group} has a section at {origin} already: replace it with ReplaceSection.");
        }

        PendingGeometry pending = Copy(geometry);
        int index;
        if (_freeSectionCount > 0)
        {
            index = _freeSections[--_freeSectionCount];
        }
        else
        {
            if (_sectionsUsed == _sections.Length)
            {
                Array.Resize(ref _sections, _sections.Length * 2);
                Array.Resize(ref _connectivity, _sections.Length);
                Array.Resize(ref _freeSections, _sections.Length);
            }

            index = _sectionsUsed++;
        }

        ref SectionSlot slot = ref _sections[index];
        int generation = slot.Generation + 1;
        slot = new SectionSlot
        {
            Generation = generation,
            Alive = true,
            Group = group.Index,
            Origin = origin,
            VertexStart = -1,
            IndexStart = -1,
            TransparentStart = -1,
            Pending = pending,
        };
        _connectivity[index] = SectionConnectivity.All;
        owner.Graph.Add(origin, index);
        _pending.Enqueue((index, generation));
        SectionCount++;
        MarkDirty(index);
        return new ChunkSectionId(index, generation);
    }

    /// <summary>
    /// Replaces the geometry of a section, which is copied. The previous
    /// geometry is drawn until the new one reaches the GPU.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <param name="geometry">The new geometry.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The section does not exist, or the geometry is not valid.</exception>
    public void ReplaceSection(ChunkSectionId section, in ChunkSectionGeometry geometry)
    {
        ref SectionSlot slot = ref GetSection(section);
        PendingGeometry pending = Copy(geometry);
        if (slot.Pending is { } previous)
        {
            Recycle(previous);
        }
        else
        {
            _pending.Enqueue((section.Index, slot.Generation));
        }

        slot.Pending = pending;
    }

    /// <summary>Removes a section; it is no longer drawn from this frame on.</summary>
    /// <param name="section">The section.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The section does not exist.</exception>
    public void RemoveSection(ChunkSectionId section)
    {
        GetSection(section);
        RemoveSlot(section.Index);
    }

    /// <summary>Gets the origin and the group of a section.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Its group and its origin in the group.</returns>
    /// <exception cref="ArgumentException">The section does not exist.</exception>
    public (ChunkGroupId Group, SectionOrigin Origin) GetSectionOrigin(ChunkSectionId section)
    {
        CheckSection(section);
        ref SectionSlot slot = ref _sections[section.Index];
        return (new ChunkGroupId(slot.Group, _groups[slot.Group].Generation), slot.Origin);
    }

    /// <summary>Gets whether the geometry of a section reached the GPU, so that it is drawn: its last geometry, not one still waiting.</summary>
    /// <param name="section">The section.</param>
    /// <returns><see langword="true"/> when nothing waits for its upload.</returns>
    /// <exception cref="ArgumentException">The section does not exist.</exception>
    public bool IsUploaded(ChunkSectionId section)
    {
        CheckSection(section);
        return _sections[section.Index].Pending is null;
    }

    private void RemoveSlot(int index)
    {
        ref SectionSlot slot = ref _sections[index];
        if (slot.Pending is { } pending)
        {
            Recycle(pending);
        }

        Retire(ref slot);
        ReturnTransparent(slot.Transparent);
        _groups[slot.Group].Graph!.Remove(slot.Origin);
        int generation = slot.Generation;
        slot = new SectionSlot { Generation = generation, VertexStart = -1, IndexStart = -1, TransparentStart = -1 };
        _freeSections[_freeSectionCount++] = index;
        SectionCount--;
        MarkDirty(index);
    }

    // Copies the geometry into pooled arrays, checks it, and computes its bounds and the centers of its transparent triangles.
    private PendingGeometry Copy(in ChunkSectionGeometry geometry)
    {
        ReadOnlySpan<ChunkVertex> vertices = geometry.Vertices;
        Check(geometry.Opaque, vertices.Length, nameof(geometry.Opaque));
        Check(geometry.Cutout, vertices.Length, nameof(geometry.Cutout));
        Check(geometry.Transparent, vertices.Length, nameof(geometry.Transparent));
        PendingGeometry pending = _pendingPool.Count > 0 ? _pendingPool.Pop() : new PendingGeometry();
        pending.VertexCount = vertices.Length;
        pending.OpaqueCount = geometry.Opaque.Length;
        pending.CutoutCount = geometry.Cutout.Length;
        pending.Visibility = geometry.Visibility;
        pending.Vertices = vertices.Length == 0 ? [] : ArrayPool<ChunkVertex>.Shared.Rent(vertices.Length);
        vertices.CopyTo(pending.Vertices);
        int indices = geometry.Opaque.Length + geometry.Cutout.Length;
        pending.Indices = indices == 0 ? [] : ArrayPool<uint>.Shared.Rent(indices);
        geometry.Opaque.CopyTo(pending.Indices);
        geometry.Cutout.CopyTo(pending.Indices.AsSpan(geometry.Opaque.Length));

        Vector3 min = vertices.Length == 0 ? Vector3.Zero : new Vector3(float.MaxValue);
        Vector3 max = vertices.Length == 0 ? Vector3.Zero : new Vector3(float.MinValue);
        foreach (ChunkVertex vertex in vertices)
        {
            Vector3 position = vertex.Position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        pending.BoundsMin = min;
        pending.BoundsMax = max;
        pending.Transparent = geometry.Transparent.Length == 0 ? null : TransparentFaces.Create(vertices, geometry.Transparent);
        return pending;
    }

    private static void Check(ReadOnlySpan<uint> indices, int vertexCount, string name)
    {
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException($"The {name} indices of a section are whole triangles, three per triangle; got {indices.Length}.", name);
        }

        foreach (uint index in indices)
        {
            if (index >= (uint)vertexCount)
            {
                throw new ArgumentException($"The {name} indices of a section name its {vertexCount} vertices; got {index}.", name);
            }
        }
    }

    private void Recycle(PendingGeometry pending)
    {
        if (pending.Vertices.Length > 0)
        {
            ArrayPool<ChunkVertex>.Shared.Return(pending.Vertices);
        }

        if (pending.Indices.Length > 0)
        {
            ArrayPool<uint>.Shared.Return(pending.Indices);
        }

        ReturnTransparent(pending.Transparent);
        pending.Vertices = [];
        pending.Indices = [];
        pending.Transparent = null;
        _pendingPool.Push(pending);
    }

    private static void ReturnTransparent(TransparentFaces? faces) => faces?.Return();

    [HotPath]
    private ref GroupSlot GetGroup(ChunkGroupId group)
    {
        CheckWritable();
        CheckGroup(group);
        return ref _groups[group.Index];
    }

    [HotPath]
    private ref SectionSlot GetSection(ChunkSectionId section)
    {
        CheckWritable();
        CheckSection(section);
        return ref _sections[section.Index];
    }

    [HotPath]
    private void CheckGroup(ChunkGroupId group)
    {
        if (!Contains(group))
        {
            throw new ArgumentException($"The chunk group {group} does not exist: it was destroyed, or never created.", nameof(group));
        }
    }

    [HotPath]
    private void CheckSection(ChunkSectionId section)
    {
        if (!Contains(section))
        {
            throw new ArgumentException($"The chunk section {section} does not exist: it was removed, or never added.", nameof(section));
        }
    }

    [HotPath]
    private void CheckWritable()
    {
        if (!_world.IsWritable)
        {
            throw new InvalidOperationException("Chunk geometry changes during extraction only: change it from an extractor (IRenderer.AddExtractor).");
        }
    }

    /// <summary>The state of a group.</summary>
    internal struct GroupSlot
    {
        public int Generation;
        public bool Alive;
        public bool Visible;
        public WorldTransform Previous;
        public WorldTransform Current;
        public ulong MovedTick;
        public VisibilityGraph? Graph;

        // The camera seen from the group, for the frame.
        public int ChunkX, ChunkY, ChunkZ;
        public Vector3 Local;
        public Matrix4x4 Basis;

        // The same when the depth pyramid was built.
        public bool PyramidValid;
        public int PyramidX, PyramidY, PyramidZ;
        public Vector3 PyramidLocal;
        public Matrix4x4 PyramidBasis;
    }

    /// <summary>The state of a section; ranges are in elements of the shared buffers, -1 for none.</summary>
    internal struct SectionSlot
    {
        public int Generation;
        public bool Alive;
        public bool Uploaded;
        public int Group;
        public SectionOrigin Origin;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public int VertexStart;
        public int VertexCount;
        public int IndexStart;
        public int OpaqueCount;
        public int CutoutCount;
        public int TransparentStart;
        public int TransparentCount;
        public PendingGeometry? Pending;
        public TransparentFaces? Transparent;
    }

    /// <summary>The geometry of a section waiting for its upload, in pooled arrays.</summary>
    internal sealed class PendingGeometry
    {
        public ChunkVertex[] Vertices = [];
        public int VertexCount;
        public uint[] Indices = [];
        public int OpaqueCount;
        public int CutoutCount;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public SectionConnectivity Visibility;
        public TransparentFaces? Transparent;

        public long Bytes => ((long)VertexCount * ChunkVertex.Size) + ((long)(OpaqueCount + CutoutCount) * sizeof(uint));
    }
}
