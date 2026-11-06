// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;

namespace Nobara.Renderer;

public sealed partial class ChunkGeometry
{
    internal const int SectionRecordSize = 96;
    internal const int GroupRecordSize = 192;
    internal const int InitialVertices = 1 << 20;
    internal const int InitialIndices = 1 << 21;
    private const int DrawSize = 20;
    private const int CounterBytes = 32;
    private const int FrameUniformSize = 416;
    private const int MaxPyramidLevels = 16;
    private const int CullGroupSize = 64;
    private const uint NoRank = uint.MaxValue;

    private readonly byte[] _zeros = new byte[CounterBytes];
    private readonly byte[] _frameBytes = new byte[FrameUniformSize];
    private readonly uint[] _counters = new uint[CounterBytes / sizeof(uint)];
    private readonly int[] _levelOffsets = new int[MaxPyramidLevels];
    private readonly int[] _levelWidths = new int[MaxPyramidLevels];
    private readonly int[] _levelHeights = new int[MaxPyramidLevels];
    private GraphicsDevice? _device;
    private SectionRecord[] _records = new SectionRecord[256];
    private GroupRecord[] _groupRecords = new GroupRecord[16];
    private int[] _dirty = new int[256];
    private bool[] _isDirty = new bool[256];
    private int _dirtyCount;
    private bool _rewriteRecords;
    private Candidate[] _candidates = new Candidate[256];
    private int _candidateCount;
    private float[] _transparentKeys = new float[64];
    private int[] _transparentItems = new int[64];
    private int _transparentCount;
    private TransparentFaces?[] _sortFaces = new TransparentFaces?[64];
    private Vector3[] _sortCameras = new Vector3[64];
    private int[] _sortSlots = new int[64];
    private RetiredRange[] _retired = new RetiredRange[64];
    private int _retiredCount;
    private long _prepareCount;
    private int _defragmentCursor;
    private bool _warnedDraw;

    private RangeAllocator? _vertexAllocator;
    private RangeAllocator? _indexAllocator;
    private GpuBuffer? _vertexBuffer;
    private GpuBuffer? _indexBuffer;
    private GpuBuffer? _sectionBuffer;
    private GpuBuffer? _groupBuffer;
    private GpuBuffer? _candidateBuffer;
    private GpuBuffer? _drawBuffer;
    private GpuBuffer? _counterBuffer;
    private GpuBuffer? _frameBuffer;
    private GpuBuffer? _emptyPyramid;
    private PerFrame<GpuBuffer>? _readback;
    private ulong[] _readbackFrames = [];
    private int _sectionCapacity;
    private int _groupCapacity;

    private ResourceLayout? _drawLayout;
    private ResourceSet? _drawSet;
    private ResourceLayout? _cullLayout;
    private ResourceSet? _cullSet;
    private Shader? _cullShader;
    private ComputePipeline? _cullPipeline;
    private CullingPass? _cullPass;
    private ResourceLayout? _pyramidLayout;
    private Shader? _pyramidShader;
    private ComputePipeline? _pyramidPipeline;
    private Sampler? _pointSampler;

    private GpuBuffer? _pyramid;
    private GpuBuffer? _levelParameters;
    private ResourceSet?[] _levelSets = [];
    private Texture? _pyramidTexture;
    private int _pyramidWidth;
    private int _pyramidHeight;
    private int _pyramidLevels;
    private long _pyramidPrepare = -1;
    private Matrix4x4 _pyramidViewProjection;

    private ulong _culledFrame;
    private long _culledPrepare = -1;
    private int _graphCulled;
    private int _sorts;
    private long _defragmented;
    private ChunkGeometryStatistics _gpuStatistics;

    /// <summary>Gets the shared vertex buffer, for tests and tools.</summary>
    internal GpuBuffer? VertexBuffer => _vertexBuffer;

    /// <summary>Gets the shared index buffer, for tests and tools.</summary>
    internal GpuBuffer? IndexBuffer => _indexBuffer;

    /// <summary>Gets the buffer of the records of the sections, for tests and tools.</summary>
    internal GpuBuffer? SectionBuffer => _sectionBuffer;

    /// <summary>Gets the sections sent to the culling of the last frame, for tests and tools.</summary>
    internal ReadOnlySpan<Candidate> Candidates => _candidates.AsSpan(0, _candidateCount);

    /// <summary>Gets the state of a section, for tests and tools.</summary>
    internal SectionSlot Slot(ChunkSectionId section) => _sections[section.Index];

    /// <summary>
    /// Gets the resource layout of the set that <see cref="Draw"/> binds: the
    /// storage buffer of the groups at <see cref="GroupsBinding"/>, read by the
    /// vertex and fragment stages. A graphics pipeline that draws chunk
    /// geometry has it at the slot it gives to <see cref="Draw"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">nRenderer did not start.</exception>
    /// <exception cref="NotSupportedException">The device lacks a feature of <see cref="RequiredFeatures"/>.</exception>
    public ResourceLayout DrawLayout
    {
        get
        {
            if (_device is null)
            {
                throw new InvalidOperationException("The resource layout of chunk geometry exists once nRenderer started: use it in Start or later.");
            }

            return _drawLayout ?? throw new NotSupportedException($"Chunk geometry needs the graphics features {RequiredFeatures}, which the device does not have.");
        }
    }

    /// <summary>
    /// Adds the culling pass to a render graph: a compute pass that tests each
    /// section against the view, then against the depth pyramid of the
    /// previous frame, and lists the draws of the visible ones. Declare it
    /// before the passes that call <see cref="Draw"/>.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <param name="name">The name of the pass.</param>
    /// <returns>The pass, which the graph keeps even though nothing in the graph reads what it writes.</returns>
    public RenderPassBuilder AddCullingPass(RenderGraphBuilder graph, string name = CullingPassName)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _cullPass ??= new CullingPass(this);
        return graph.AddComputePass(name, _cullPass).Require(RequiredFeatures).KeepAlive();
    }

    /// <summary>
    /// Adds the pass that builds the depth pyramid from the depth of the
    /// frame: a compute pass, kept for the occlusion culling of the next frame.
    /// Declare it after the passes that write the depth of the opaque and
    /// cut-out geometry, such as a depth pre-pass.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <param name="depth">The depth texture, sampled; at the render resolution.</param>
    /// <param name="name">The name of the pass.</param>
    /// <returns>The pass.</returns>
    public RenderPassBuilder AddDepthPyramidPass(RenderGraphBuilder graph, RenderGraphTexture depth, string name = DepthPyramidPassName)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.AddComputePass(name, new PyramidPass(this, depth)).Read(depth).Require(RequiredFeatures).KeepAlive();
    }

    /// <summary>
    /// Draws one category of faces of the visible sections, inside a raster
    /// pass, after the culling pass of the frame: a few indirect draws.
    /// Bind first a graphics pipeline with <see cref="VertexLayouts"/> and
    /// <see cref="DrawLayout"/> at <paramref name="slot"/>, and its other
    /// resource sets. Transparent faces are drawn from the farthest section to
    /// the nearest, each section sorted from back to front.
    /// </summary>
    /// <param name="context">The context of the pass.</param>
    /// <param name="category">The category.</param>
    /// <param name="slot">The slot of <see cref="DrawLayout"/> in the bound pipeline.</param>
    public void Draw(RenderPassContext context, ChunkCategory category, int slot)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_culledFrame != context.Frame || _culledPrepare != _prepareCount)
        {
            if (!_warnedDraw)
            {
                _warnedDraw = true;
                _context.Logger.Warning("Chunk geometry is drawn before its culling pass ran in the frame: nothing is drawn. Declare ChunkGeometry.AddCullingPass before the passes that draw it.");
            }

            return;
        }

        if (_candidateCount == 0 || _vertexBuffer is null || _indexBuffer is null)
        {
            return;
        }

        CommandBuffer commands = context.Commands;
        EnsureDrawSet();
        commands.SetResourceSet(slot, _drawSet!);
        commands.SetVertexBuffer(0, _vertexBuffer);
        commands.SetVertexBuffer(1, _sectionBuffer!);
        commands.SetIndexBuffer(_indexBuffer, IndexFormat.UInt32);
        long list = (long)_sectionCapacity * DrawSize;
        switch (category)
        {
            case ChunkCategory.Opaque:
                commands.DrawIndexedIndirectCount(_drawBuffer!, 0, _counterBuffer!, 0, _candidateCount);
                break;
            case ChunkCategory.Cutout:
                commands.DrawIndexedIndirectCount(_drawBuffer!, list, _counterBuffer!, sizeof(uint), _candidateCount);
                break;
            case ChunkCategory.Transparent when _transparentCount > 0:
                commands.DrawIndexedIndirect(_drawBuffer!, 2 * list, _transparentCount);
                break;
        }
    }

    /// <summary>Creates the GPU objects that do not depend on the sections, when the device has the features.</summary>
    internal void Start(GraphicsDevice device)
    {
        _device = device;
        IsSupported = device.Capabilities.Supports(RequiredFeatures);
        if (!IsSupported)
        {
            _context.Logger.Debug($"The device lacks some of the graphics features {RequiredFeatures}: chunk geometry is kept, and not drawn.");
            return;
        }

        _drawLayout = device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(GroupsBinding, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Vertex | ShaderStages.Fragment)]) { Name = "nobara.renderer.chunks.draw" });
        _cullLayout = device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(1, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(2, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(3, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(4, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(5, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Compute),
        ]) { Name = "nobara.renderer.chunks.cull" });
        _pyramidLayout = device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Compute),
            new ResourceLayoutEntry(0, ResourceKind.SampledTexture, ShaderStages.Compute),
            new ResourceLayoutEntry(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute),
        ]) { Name = "nobara.renderer.chunks.pyramid" });
        _cullShader = device.CreateShader(new ShaderDescription(ShaderStages.Compute, EmbeddedShaders.Read("chunk-cull.comp.spv")) { Name = "nobara.renderer.chunk-cull.comp" });
        _pyramidShader = device.CreateShader(new ShaderDescription(ShaderStages.Compute, EmbeddedShaders.Read("chunk-pyramid.comp.spv")) { Name = "nobara.renderer.chunk-pyramid.comp" });
        _cullPipeline = device.CreateComputePipeline(new ComputePipelineDescription(_cullShader, [_cullLayout]) { Name = "nobara.renderer.chunks.cull" });
        _pyramidPipeline = device.CreateComputePipeline(new ComputePipelineDescription(_pyramidShader, [_pyramidLayout]) { Name = "nobara.renderer.chunks.pyramid" });
        _pointSampler = device.CreateSampler(SamplerDescription.Point with { Name = "nobara.renderer.chunks.depth" });
        _counterBuffer = device.CreateBuffer(new BufferDescription(CounterBytes, BufferUsage.Indirect | BufferUsage.Storage) { Name = "nobara.renderer.chunks.counters" });
        _frameBuffer = device.CreateBuffer(new BufferDescription(FrameUniformSize, BufferUsage.Uniform) { Name = "nobara.renderer.chunks.frame" });
        _emptyPyramid = device.CreateBuffer(new BufferDescription(16, BufferUsage.Storage) { Name = "nobara.renderer.chunks.no-pyramid" });
        _readback = new PerFrame<GpuBuffer>(device, slot => device.CreateBuffer(new BufferDescription(CounterBytes, BufferUsage.Storage) { Name = $"nobara.renderer.chunks.statistics.{slot}" }));
        _readbackFrames = new ulong[device.FramesInFlight];
        EnsureSectionBuffers(1);
        EnsureGroupBuffer(1);
    }

    /// <summary>Destroys the GPU objects and gives the pooled arrays back.</summary>
    internal void Release()
    {
        for (int i = 0; i < _sectionsUsed; i++)
        {
            if (_sections[i].Pending is { } pending)
            {
                Recycle(pending);
            }

            ReturnTransparent(_sections[i].Transparent);
            _sections[i] = default;
        }

        _sectionsUsed = 0;
        foreach (ResourceSet? set in _levelSets)
        {
            set?.Dispose();
        }

        _drawSet?.Dispose();
        _cullSet?.Dispose();
        _levelParameters?.Dispose();
        _pyramid?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _sectionBuffer?.Dispose();
        _groupBuffer?.Dispose();
        _candidateBuffer?.Dispose();
        _drawBuffer?.Dispose();
        _counterBuffer?.Dispose();
        _frameBuffer?.Dispose();
        _emptyPyramid?.Dispose();
        _readback?.Dispose();
        _cullPipeline?.Dispose();
        _pyramidPipeline?.Dispose();
        _cullShader?.Dispose();
        _pyramidShader?.Dispose();
        _pointSampler?.Dispose();
        _drawLayout?.Dispose();
        _cullLayout?.Dispose();
        _pyramidLayout?.Dispose();
        _device = null;
        IsSupported = false;
    }

    /// <summary>
    /// Prepares the frame, at the start of its commands: gives back the ranges
    /// that no frame in flight reads, reads the figures of the culling of an
    /// earlier frame, uploads waiting geometry, places the camera in each
    /// group, lists the candidate sections, sorts transparent faces, moves
    /// sections to defragment, and writes what the culling reads.
    /// </summary>
    internal void PrepareFrame(CommandBuffer commands, RenderView view, float alpha)
    {
        if (!IsSupported)
        {
            return;
        }

        commands.PushDebugGroup("nobara.renderer.chunks");
        _prepareCount++;
        _sorts = 0;
        _defragmented = 0;
        ReleaseRetired();
        ReadStatistics();
        Upload(commands);
        PrepareGroups(view, alpha);
        ListCandidates(commands);
        Defragment(commands);

        EnsureSectionBuffers(_sectionsUsed);
        EnsureGroupBuffer(_groupsUsed);
        FlushRecords(commands);
        if (_groupsUsed > 0)
        {
            commands.UpdateBuffer(_groupBuffer!, 0, MemoryMarshal.AsBytes(_groupRecords.AsSpan(0, _groupsUsed)));
        }

        if (_candidateCount > 0)
        {
            commands.UpdateBuffer(_candidateBuffer!, 0, MemoryMarshal.AsBytes(_candidates.AsSpan(0, _candidateCount)));
        }

        WriteFrame(commands, view);
        commands.UpdateBuffer(_counterBuffer!, 0, _zeros);
        commands.PopDebugGroup();
    }

    private void Cull(RenderPassContext context)
    {
        _culledFrame = context.Frame;
        _culledPrepare = _prepareCount;
        int slot = context.FrameIndex;
        if (!IsSupported)
        {
            return;
        }

        if (_candidateCount == 0)
        {
            _readbackFrames[slot] = 0;
            _gpuStatistics = new ChunkGeometryStatistics { CulledFrame = context.Frame };
            return;
        }

        CommandBuffer commands = context.Commands;
        EnsureCullSet();
        commands.SetPipeline(_cullPipeline!);
        commands.SetResourceSet(0, _cullSet!);
        commands.Dispatch(IntMath.DivCeil(_candidateCount, CullGroupSize));
        commands.Barrier(BarrierScope.Indirect | BarrierScope.StorageBuffer | BarrierScope.VertexInput | BarrierScope.Transfer);
        commands.CopyBuffer(_counterBuffer!, 0, _readback![slot], 0, CounterBytes);
        _readbackFrames[slot] = context.Frame;
    }

    private void BuildPyramid(RenderPassContext context, RenderGraphTexture depthTexture)
    {
        if (!IsSupported)
        {
            return;
        }

        Texture depth = context.GetTexture(depthTexture);
        if (depth.Width != _pyramidWidth || depth.Height != _pyramidHeight || _pyramid is null)
        {
            CreatePyramid(depth.Width, depth.Height);
        }

        if (!ReferenceEquals(depth, _pyramidTexture))
        {
            CreatePyramidSets(depth);
        }

        CommandBuffer commands = context.Commands;
        commands.SetPipeline(_pyramidPipeline!);
        for (int level = 0; level < _pyramidLevels; level++)
        {
            commands.SetResourceSet(0, _levelSets[level]!);
            commands.Dispatch(IntMath.DivCeil(_levelWidths[level], 8), IntMath.DivCeil(_levelHeights[level], 8));
            commands.Barrier(BarrierScope.StorageBuffer);
        }

        _pyramidPrepare = _prepareCount;
        _pyramidViewProjection = context.View.ViewProjectionMatrix;
        for (int g = 0; g < _groupsUsed; g++)
        {
            ref GroupSlot group = ref _groups[g];
            group.PyramidValid = group.Alive;
            group.PyramidX = group.ChunkX;
            group.PyramidY = group.ChunkY;
            group.PyramidZ = group.ChunkZ;
            group.PyramidLocal = group.Local;
            group.PyramidBasis = group.Basis;
        }
    }

    // Levels of the pyramid: level 0 has a value per 2 x 2 pixels of the depth, each level above half as many, down to 1 x 1.
    private void CreatePyramid(int width, int height)
    {
        GraphicsDevice device = _device!;
        _pyramid?.Dispose();
        _levelParameters?.Dispose();
        _pyramidWidth = width;
        _pyramidHeight = height;
        _pyramidPrepare = -1;
        int levels = 0;
        int offset = 0;
        int w = width;
        int h = height;
        do
        {
            w = (w + 1) / 2;
            h = (h + 1) / 2;
            _levelOffsets[levels] = offset;
            _levelWidths[levels] = w;
            _levelHeights[levels] = h;
            offset += w * h;
            levels++;
        }
        while ((w > 1 || h > 1) && levels < MaxPyramidLevels);

        _pyramidLevels = levels;
        _pyramid = device.CreateBuffer(new BufferDescription((long)offset * sizeof(float), BufferUsage.Storage) { Name = "nobara.renderer.chunks.pyramid" });
        int stride = Math.Max(32, device.Capabilities.UniformBufferAlignment);
        _levelParameters = device.CreateBuffer(new BufferDescription((long)stride * levels, BufferUsage.Uniform) { Name = "nobara.renderer.chunks.pyramid-levels" });
        Span<uint> parameters = stackalloc uint[8];
        for (int level = 0; level < levels; level++)
        {
            parameters[0] = level == 0 ? 0u : (uint)_levelOffsets[level - 1];
            parameters[1] = (uint)(level == 0 ? width : _levelWidths[level - 1]);
            parameters[2] = (uint)(level == 0 ? height : _levelHeights[level - 1]);
            parameters[3] = level == 0 ? 1u : 0u;
            parameters[4] = (uint)_levelOffsets[level];
            parameters[5] = (uint)_levelWidths[level];
            parameters[6] = (uint)_levelHeights[level];
            parameters[7] = 0;
            device.UpdateBuffer(_levelParameters, (long)stride * level, MemoryMarshal.AsBytes(parameters));
        }

        _pyramidTexture = null;
        _cullSet?.Dispose();
        _cullSet = null;
    }

    private void CreatePyramidSets(Texture depth)
    {
        foreach (ResourceSet? set in _levelSets)
        {
            set?.Dispose();
        }

        int stride = Math.Max(32, _device!.Capabilities.UniformBufferAlignment);
        _levelSets = new ResourceSet?[_pyramidLevels];
        for (int level = 0; level < _pyramidLevels; level++)
        {
            _levelSets[level] = _device.CreateResourceSet(new ResourceSetDescription(
                _pyramidLayout!,
                [BoundResource.Of(_levelParameters!, (long)stride * level, 32), BoundResource.Of(depth, _pointSampler!), BoundResource.Of(_pyramid!)]) { Name = "nobara.renderer.chunks.pyramid" });
        }

        _pyramidTexture = depth;
    }

    [HotPath]
    private void ReleaseRetired()
    {
        int framesInFlight = _device!.FramesInFlight;
        int kept = 0;
        for (int i = 0; i < _retiredCount; i++)
        {
            RetiredRange range = _retired[i];
            if (_prepareCount >= range.Frame + framesInFlight)
            {
                (range.Index ? _indexAllocator! : _vertexAllocator!).Free(range.Start, range.Length);
            }
            else
            {
                _retired[kept++] = range;
            }
        }

        _retiredCount = kept;
    }

    [HotPath]
    private void ReadStatistics()
    {
        int slot = _device!.FrameIndex;
        ulong frame = _readbackFrames[slot];
        if (frame == 0)
        {
            return;
        }

        // The slot of this frame finished on the GPU: reading it does not wait.
        _device.ReadBuffer(_readback![slot], 0, MemoryMarshal.AsBytes(_counters.AsSpan()));
        _readbackFrames[slot] = 0;
        _gpuStatistics = new ChunkGeometryStatistics
        {
            CulledFrame = frame,
            Drawn = (int)_counters[2],
            FrustumCulled = (int)_counters[3],
            OcclusionCulled = (int)_counters[4],
        };
    }

    // Uploads the waiting geometry, in order, within the budget of the frame; at least one section.
    private void Upload(CommandBuffer commands)
    {
        long bytes = 0;
        while (_pending.Count > 0)
        {
            (int index, int generation) = _pending.Peek();
            ref SectionSlot slot = ref _sections[index];
            if (!slot.Alive || slot.Generation != generation || slot.Pending is not { } pending)
            {
                _pending.Dequeue();
                continue;
            }

            if (bytes > 0 && bytes + pending.Bytes > _uploadBudget)
            {
                break;
            }

            _pending.Dequeue();
            bytes += pending.Bytes;
            Retire(ref slot);
            ReturnTransparent(slot.Transparent);
            slot.VertexCount = pending.VertexCount;
            slot.OpaqueCount = pending.OpaqueCount;
            slot.CutoutCount = pending.CutoutCount;
            slot.BoundsMin = pending.BoundsMin;
            slot.BoundsMax = pending.BoundsMax;
            slot.Transparent = pending.Transparent;
            slot.TransparentCount = pending.Transparent is { } faces ? faces.TriangleCount * 3 : 0;
            pending.Transparent = null;
            if (pending.VertexCount > 0)
            {
                slot.VertexStart = AllocateVertices(commands, pending.VertexCount);
                commands.UpdateBuffer(_vertexBuffer!, (long)slot.VertexStart * ChunkVertex.Size, MemoryMarshal.AsBytes(pending.Vertices.AsSpan(0, pending.VertexCount)));
            }

            int indices = pending.OpaqueCount + pending.CutoutCount;
            if (indices > 0)
            {
                slot.IndexStart = AllocateIndices(commands, indices);
                commands.UpdateBuffer(_indexBuffer!, (long)slot.IndexStart * sizeof(uint), MemoryMarshal.AsBytes(pending.Indices.AsSpan(0, indices)));
            }

            if (_connectivity[index] != pending.Visibility)
            {
                _connectivity[index] = pending.Visibility;
                _groups[slot.Group].Graph!.Invalidate();
            }

            slot.Uploaded = true;
            slot.Pending = null;
            Recycle(pending);
            MarkDirty(index);
        }
    }

    // Places the camera of the frame in each group, from the transform of the group interpolated between ticks.
    [HotPath]
    private void PrepareGroups(RenderView view, float alpha)
    {
        if (_groupRecords.Length < _groupsUsed)
        {
            Array.Resize(ref _groupRecords, _groups.Length);
        }

        WorldPosition camera = view.FrameCamera.Position;
        for (int g = 0; g < _groupsUsed; g++)
        {
            ref GroupSlot slot = ref _groups[g];
            ref GroupRecord record = ref _groupRecords[g];
            if (!slot.Alive)
            {
                record = default;
                continue;
            }

            WorldTransform transform = _world.Interpolates && slot.MovedTick == _world.Tick ? WorldTransform.Interpolate(slot.Previous, slot.Current, alpha) : slot.Current;
            CameraIn(transform, camera, out slot.ChunkX, out slot.ChunkY, out slot.ChunkZ, out slot.Local);
            slot.Basis = Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation);
            record.CameraX = slot.ChunkX;
            record.CameraY = slot.ChunkY;
            record.CameraZ = slot.ChunkZ;
            record.Visible = slot.Visible ? 1 : 0;
            record.CameraLocal = new Vector4(slot.Local, 1);
            record.Basis = slot.Basis;
            record.PreviousX = slot.PyramidX;
            record.PreviousY = slot.PyramidY;
            record.PreviousZ = slot.PyramidZ;
            record.PreviousLocal = new Vector4(slot.PyramidLocal, slot.PyramidValid ? 1 : 0);
            record.PreviousBasis = slot.PyramidBasis;
        }
    }

    // Lists the sections that the culling of the GPU tests: every uploaded section of each visible group, or those its visibility graph reaches.
    private void ListCandidates(CommandBuffer commands)
    {
        if (_candidates.Length < _sectionsUsed)
        {
            Array.Resize(ref _candidates, _sections.Length);
        }

        _candidateCount = 0;
        _transparentCount = 0;
        _graphCulled = 0;
        int sorts = 0;
        for (int g = 0; g < _groupsUsed; g++)
        {
            ref GroupSlot group = ref _groups[g];
            if (!group.Alive || !group.Visible)
            {
                continue;
            }

            VisibilityGraph graph = group.Graph!;
            if (VisibilityGraph && graph.Search(new SectionOrigin(group.ChunkX, group.ChunkY, group.ChunkZ), _connectivity))
            {
                ReadOnlySpan<int> reached = graph.Reached;
                _graphCulled += graph.Count - reached.Length;
                foreach (int section in reached)
                {
                    Consider(section, ref group, ref sorts);
                }
            }
            else
            {
                foreach (int section in graph.Slots)
                {
                    Consider(section, ref group, ref sorts);
                }
            }
        }

        if (sorts == 1)
        {
            _sortFaces[0]!.Sort(_sortCameras[0]);
        }
        else if (sorts > 1)
        {
            _context.Jobs.ScheduleFor(new TransparentFaces.SortJob(_sortFaces, _sortCameras), sorts, 16).Complete();
        }

        for (int i = 0; i < sorts; i++)
        {
            ref SectionSlot slot = ref _sections[_sortSlots[i]];
            TransparentFaces faces = _sortFaces[i]!;
            if (slot.TransparentStart >= 0)
            {
                RetireRange(true, slot.TransparentStart, slot.TransparentCount);
            }

            slot.TransparentStart = AllocateIndices(commands, slot.TransparentCount);
            commands.UpdateBuffer(_indexBuffer!, (long)slot.TransparentStart * sizeof(uint), MemoryMarshal.AsBytes(faces.SortedIndices));
            MarkDirty(_sortSlots[i]);
            _sortFaces[i] = null;
        }

        _sorts = sorts;
        _transparentKeys.AsSpan(0, _transparentCount).Sort(_transparentItems.AsSpan(0, _transparentCount));
        for (int rank = 0; rank < _transparentCount; rank++)
        {
            _candidates[_transparentItems[rank]].Rank = (uint)rank;
        }
    }

    [HotPath]
    private void Consider(int index, ref GroupSlot group, ref int sorts)
    {
        ref SectionSlot slot = ref _sections[index];
        if (!slot.Uploaded || slot.VertexCount == 0)
        {
            return;
        }

        _candidates[_candidateCount] = new Candidate { Slot = (uint)index, Rank = NoRank };
        if (slot.Transparent is { } faces)
        {
            // The camera in blocks from the corner of the section.
            Vector3 camera = new Vector3(group.ChunkX - slot.Origin.X, group.ChunkY - slot.Origin.Y, group.ChunkZ - slot.Origin.Z) * WorldPosition.ChunkSize + group.Local;
            Vector3 center = (slot.BoundsMin + slot.BoundsMax) * 0.5f;
            if (_transparentCount == _transparentKeys.Length)
            {
                Array.Resize(ref _transparentKeys, _transparentKeys.Length * 2);
                Array.Resize(ref _transparentItems, _transparentKeys.Length);
            }

            _transparentKeys[_transparentCount] = -Vector3.Transform(center - camera, group.Basis).LengthSquared();
            _transparentItems[_transparentCount] = _candidateCount;
            _transparentCount++;
            if (slot.TransparentStart < 0 || faces.NeedsSort(camera, center, _resortDistance, _resortAngle))
            {
                if (sorts == _sortFaces.Length)
                {
                    Array.Resize(ref _sortFaces, sorts * 2);
                    Array.Resize(ref _sortCameras, sorts * 2);
                    Array.Resize(ref _sortSlots, sorts * 2);
                }

                _sortFaces[sorts] = faces;
                _sortCameras[sorts] = camera;
                _sortSlots[sorts] = index;
                sorts++;
            }
        }

        _candidateCount++;
    }

    // Moves sections down to holes of the shared buffers, within the budget of the frame, when they are fragmented.
    [HotPath]
    private void Defragment(CommandBuffer commands)
    {
        long budget = _defragmentationBudget;
        if (budget == 0 || _sectionsUsed == 0)
        {
            return;
        }

        bool vertices = _vertexAllocator is { } v && v.Fragmentation > _defragmentationThreshold;
        bool indices = _indexAllocator is { } i && i.Fragmentation > _defragmentationThreshold;
        if (!vertices && !indices)
        {
            return;
        }

        long moved = 0;
        for (int step = 0; step < _sectionsUsed && moved < budget; step++)
        {
            int index = _defragmentCursor;
            _defragmentCursor = (_defragmentCursor + 1) % _sectionsUsed;
            ref SectionSlot slot = ref _sections[index];
            if (!slot.Alive)
            {
                continue;
            }

            if (vertices && slot.VertexStart >= 0 && _vertexAllocator!.TryAllocateBelow(slot.VertexCount, slot.VertexStart, out int vertex))
            {
                Move(commands, _vertexBuffer!, ChunkVertex.Size, false, ref slot.VertexStart, vertex, slot.VertexCount);
                moved += (long)slot.VertexCount * ChunkVertex.Size;
                MarkDirty(index);
            }

            int count = slot.OpaqueCount + slot.CutoutCount;
            if (indices && slot.IndexStart >= 0 && _indexAllocator!.TryAllocateBelow(count, slot.IndexStart, out int first))
            {
                Move(commands, _indexBuffer!, sizeof(uint), true, ref slot.IndexStart, first, count);
                moved += (long)count * sizeof(uint);
                MarkDirty(index);
            }

            if (indices && slot.TransparentStart >= 0 && _indexAllocator!.TryAllocateBelow(slot.TransparentCount, slot.TransparentStart, out int transparent))
            {
                Move(commands, _indexBuffer!, sizeof(uint), true, ref slot.TransparentStart, transparent, slot.TransparentCount);
                moved += (long)slot.TransparentCount * sizeof(uint);
                MarkDirty(index);
            }
        }

        _defragmented = moved;
    }

    [HotPath]
    private void Move(CommandBuffer commands, GpuBuffer buffer, int size, bool index, ref int start, int to, int count)
    {
        commands.CopyBuffer(buffer, (long)start * size, buffer, (long)to * size, (long)count * size);
        RetireRange(index, start, count);
        start = to;
    }

    private int AllocateVertices(CommandBuffer commands, int count)
    {
        if (_vertexAllocator is null)
        {
            int capacity = Math.Max(InitialVertices, count);
            _vertexAllocator = new RangeAllocator(capacity);
            _vertexBuffer = _device!.CreateBuffer(new BufferDescription((long)capacity * ChunkVertex.Size, BufferUsage.Vertex) { Dedicated = true, Name = "nobara.renderer.chunks.vertices" });
        }

        return Allocate(commands, _vertexAllocator, ref _vertexBuffer, count, ChunkVertex.Size, BufferUsage.Vertex, "nobara.renderer.chunks.vertices");
    }

    private int AllocateIndices(CommandBuffer commands, int count)
    {
        if (_indexAllocator is null)
        {
            int capacity = Math.Max(InitialIndices, count);
            _indexAllocator = new RangeAllocator(capacity);
            _indexBuffer = _device!.CreateBuffer(new BufferDescription((long)capacity * sizeof(uint), BufferUsage.Index) { Dedicated = true, Name = "nobara.renderer.chunks.indices" });
        }

        return Allocate(commands, _indexAllocator, ref _indexBuffer, count, sizeof(uint), BufferUsage.Index, "nobara.renderer.chunks.indices");
    }

    // A range of a shared buffer; when none is free, the buffer doubles, its content copied on the GPU, in order with the commands of the frame.
    private int Allocate(CommandBuffer commands, RangeAllocator allocator, ref GpuBuffer? buffer, int count, int size, BufferUsage usage, string name)
    {
        if (allocator.TryAllocate(count, out int start))
        {
            return start;
        }

        long wanted = Math.Max((long)allocator.Capacity * 2, (long)allocator.Capacity + count);
        int capacity = (int)Math.Min(wanted, int.MaxValue / size);
        if (capacity <= allocator.Capacity)
        {
            throw new InvalidOperationException($"The shared buffer '{name}' of chunk geometry is full: {allocator.Capacity} elements.");
        }

        GpuBuffer grown = _device!.CreateBuffer(new BufferDescription((long)capacity * size, usage) { Dedicated = true, Name = name });
        commands.CopyBuffer(buffer!, 0, grown, 0, buffer!.Size);
        buffer.Dispose();
        buffer = grown;
        allocator.Grow(capacity);
        return allocator.TryAllocate(count, out start) ? start : throw new InvalidOperationException($"The shared buffer '{name}' of chunk geometry has no room for {count} elements.");
    }

    [HotPath]
    private void Retire(ref SectionSlot slot)
    {
        if (slot.VertexStart >= 0)
        {
            RetireRange(false, slot.VertexStart, slot.VertexCount);
        }

        if (slot.IndexStart >= 0)
        {
            RetireRange(true, slot.IndexStart, slot.OpaqueCount + slot.CutoutCount);
        }

        if (slot.TransparentStart >= 0)
        {
            RetireRange(true, slot.TransparentStart, slot.TransparentCount);
        }

        slot.VertexStart = -1;
        slot.IndexStart = -1;
        slot.TransparentStart = -1;
    }

    // A range that frames in flight may still read is given back to its allocator once they finished.
    [HotPath]
    private void RetireRange(bool index, int start, int length)
    {
        if (_retiredCount == _retired.Length)
        {
            Array.Resize(ref _retired, _retired.Length * 2);
        }

        _retired[_retiredCount++] = new RetiredRange(index, start, length, _prepareCount);
    }

    [HotPath]
    private void MarkDirty(int index)
    {
        if (_isDirty.Length <= index)
        {
            Array.Resize(ref _isDirty, _sections.Length);
            Array.Resize(ref _dirty, _sections.Length);
        }

        if (!_isDirty[index])
        {
            _isDirty[index] = true;
            _dirty[_dirtyCount++] = index;
        }
    }

    [HotPath]
    private void FlushRecords(CommandBuffer commands)
    {
        for (int i = 0; i < _dirtyCount; i++)
        {
            int index = _dirty[i];
            _isDirty[index] = false;
            WriteRecord(index);
            if (!_rewriteRecords)
            {
                commands.UpdateBuffer(_sectionBuffer!, (long)index * SectionRecordSize, MemoryMarshal.AsBytes(new ReadOnlySpan<SectionRecord>(ref _records[index])));
            }
        }

        _dirtyCount = 0;
        if (_rewriteRecords && _sectionsUsed > 0)
        {
            commands.UpdateBuffer(_sectionBuffer!, 0, MemoryMarshal.AsBytes(_records.AsSpan(0, _sectionsUsed)));
        }

        _rewriteRecords = false;
    }

    [HotPath]
    private void WriteRecord(int index)
    {
        ref SectionSlot slot = ref _sections[index];
        ref SectionRecord record = ref _records[index];
        if (!slot.Alive || !slot.Uploaded)
        {
            record = default;
            return;
        }

        record.X = slot.Origin.X;
        record.Y = slot.Origin.Y;
        record.Z = slot.Origin.Z;
        record.Group = slot.Group;
        record.BoundsMin = new Vector4(slot.BoundsMin, 0);
        record.BoundsMax = new Vector4(slot.BoundsMax, 0);
        uint baseVertex = slot.VertexStart >= 0 ? (uint)slot.VertexStart : 0;
        bool indexed = slot.IndexStart >= 0;
        record.Opaque = new DrawRange(indexed ? (uint)slot.IndexStart : 0, indexed ? (uint)slot.OpaqueCount : 0, baseVertex);
        record.Cutout = new DrawRange(indexed ? (uint)(slot.IndexStart + slot.OpaqueCount) : 0, indexed ? (uint)slot.CutoutCount : 0, baseVertex);
        bool transparent = slot.TransparentStart >= 0;
        record.Transparent = new DrawRange(transparent ? (uint)slot.TransparentStart : 0, transparent ? (uint)slot.TransparentCount : 0, baseVertex);
    }

    // The buffers sized by the number of section slots: records, candidates, and draws.
    private void EnsureSectionBuffers(int sections)
    {
        if (_sectionCapacity >= sections && _sectionBuffer is not null)
        {
            return;
        }

        int capacity = Math.Max(Math.Max(sections, 256), _sectionCapacity * 2);
        GraphicsDevice device = _device!;
        _sectionBuffer?.Dispose();
        _candidateBuffer?.Dispose();
        _drawBuffer?.Dispose();
        _sectionBuffer = device.CreateBuffer(new BufferDescription((long)capacity * SectionRecordSize, BufferUsage.Vertex | BufferUsage.Storage) { Name = "nobara.renderer.chunks.sections" });
        _candidateBuffer = device.CreateBuffer(new BufferDescription((long)capacity * Marshal.SizeOf<Candidate>(), BufferUsage.Storage) { Name = "nobara.renderer.chunks.candidates" });
        _drawBuffer = device.CreateBuffer(new BufferDescription(3L * capacity * DrawSize, BufferUsage.Indirect | BufferUsage.Storage) { Name = "nobara.renderer.chunks.draws" });
        _sectionCapacity = capacity;
        if (_records.Length < capacity)
        {
            Array.Resize(ref _records, capacity);
        }

        _rewriteRecords = true;
        _cullSet?.Dispose();
        _cullSet = null;
    }

    private void EnsureGroupBuffer(int groups)
    {
        if (_groupCapacity >= groups && _groupBuffer is not null)
        {
            return;
        }

        int capacity = Math.Max(Math.Max(groups, 16), _groupCapacity * 2);
        _groupBuffer?.Dispose();
        _groupBuffer = _device!.CreateBuffer(new BufferDescription((long)capacity * GroupRecordSize, BufferUsage.Storage) { Name = "nobara.renderer.chunks.groups" });
        _groupCapacity = capacity;
        _cullSet?.Dispose();
        _cullSet = null;
        _drawSet?.Dispose();
        _drawSet = null;
    }

    private void EnsureCullSet()
    {
        _cullSet ??= _device!.CreateResourceSet(new ResourceSetDescription(
            _cullLayout!,
            [
                BoundResource.Of(_frameBuffer!),
                BoundResource.Of(_sectionBuffer!),
                BoundResource.Of(_groupBuffer!),
                BoundResource.Of(_candidateBuffer!),
                BoundResource.Of(_drawBuffer!),
                BoundResource.Of(_counterBuffer!),
                BoundResource.Of(_pyramid ?? _emptyPyramid!),
            ]) { Name = "nobara.renderer.chunks.cull" });
    }

    private void EnsureDrawSet() =>
        _drawSet ??= _device!.CreateResourceSet(new ResourceSetDescription(_drawLayout!, [BoundResource.Of(_groupBuffer!)]) { Name = "nobara.renderer.chunks.draw" });

    [HotPath]
    private void WriteFrame(CommandBuffer commands, RenderView view)
    {
        Span<byte> bytes = _frameBytes;
        Matrix4x4 viewProjection = view.ViewProjectionMatrix;
        MemoryMarshal.Write(bytes, in viewProjection);
        MemoryMarshal.Write(bytes[64..], in _pyramidViewProjection);
        bool occlusion = OcclusionCulling && _pyramid is not null && _pyramidPrepare == _prepareCount - 1;
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(bytes[128..]);
        words[0] = (uint)_candidateCount;
        words[1] = occlusion ? 1u : 0u;
        words[2] = (uint)Math.Max(1, _pyramidLevels);
        words[3] = (uint)_transparentCount;
        words[4] = (uint)_pyramidWidth;
        words[5] = (uint)_pyramidHeight;
        words[6] = (uint)_sectionCapacity;
        words[7] = 0;
        for (int level = 0; level < MaxPyramidLevels; level++)
        {
            words[8 + (4 * level)] = (uint)_levelOffsets[level];
            words[9 + (4 * level)] = (uint)_levelWidths[level];
            words[10 + (4 * level)] = (uint)_levelHeights[level];
            words[11 + (4 * level)] = 0;
        }

        commands.UpdateBuffer(_frameBuffer!, 0, bytes);
    }

    [HotPath]
    private ChunkGeometryStatistics CurrentStatistics()
    {
        float vertexFragmentation = _vertexAllocator?.Fragmentation ?? 0;
        float indexFragmentation = _indexAllocator?.Fragmentation ?? 0;
        return _gpuStatistics with
        {
            Groups = GroupCount,
            Sections = SectionCount,
            PendingUploads = PendingCount(),
            VertexBytes = (long)(_vertexAllocator?.Capacity ?? 0) * ChunkVertex.Size,
            VertexBytesUsed = (long)(_vertexAllocator?.Used ?? 0) * ChunkVertex.Size,
            IndexBytes = (long)(_indexAllocator?.Capacity ?? 0) * sizeof(uint),
            IndexBytesUsed = (long)(_indexAllocator?.Used ?? 0) * sizeof(uint),
            Fragmentation = Math.Max(vertexFragmentation, indexFragmentation),
            DefragmentedBytes = _defragmented,
            TransparentSorts = _sorts,
            Candidates = _candidateCount,
            GraphCulled = _graphCulled,
        };
    }

    [HotPath]
    private int PendingCount()
    {
        int count = 0;
        for (int i = 0; i < _sectionsUsed; i++)
        {
            if (_sections[i].Pending is not null)
            {
                count++;
            }
        }

        return count;
    }

    // The position of the camera in the frame of a group, in double precision, as a whole chunk and a position in it.
    [HotPath]
    internal static void CameraIn(in WorldTransform group, in WorldPosition camera, out int chunkX, out int chunkY, out int chunkZ, out Vector3 local)
    {
        const double Size = WorldPosition.ChunkSize;
        WorldPosition origin = group.Position;
        double dx = (((long)camera.ChunkX - origin.ChunkX) * Size) + ((double)camera.Local.X - origin.Local.X);
        double dy = (((long)camera.ChunkY - origin.ChunkY) * Size) + ((double)camera.Local.Y - origin.Local.Y);
        double dz = (((long)camera.ChunkZ - origin.ChunkZ) * Size) + ((double)camera.Local.Z - origin.Local.Z);

        // The inverse rotation: v + 2 q x (q x v + w v), with q the conjugate.
        double qx = -group.Rotation.X, qy = -group.Rotation.Y, qz = -group.Rotation.Z, qw = group.Rotation.W;
        double tx = (qy * dz) - (qz * dy) + (qw * dx);
        double ty = (qz * dx) - (qx * dz) + (qw * dy);
        double tz = (qx * dy) - (qy * dx) + (qw * dz);
        double rx = dx + (2 * ((qy * tz) - (qz * ty)));
        double ry = dy + (2 * ((qz * tx) - (qx * tz)));
        double rz = dz + (2 * ((qx * ty) - (qy * tx)));
        rx = group.Scale.X == 0 ? 0 : rx / group.Scale.X;
        ry = group.Scale.Y == 0 ? 0 : ry / group.Scale.Y;
        rz = group.Scale.Z == 0 ? 0 : rz / group.Scale.Z;
        double cx = Math.Clamp(Math.Floor(rx / Size), int.MinValue, int.MaxValue);
        double cy = Math.Clamp(Math.Floor(ry / Size), int.MinValue, int.MaxValue);
        double cz = Math.Clamp(Math.Floor(rz / Size), int.MinValue, int.MaxValue);
        chunkX = (int)cx;
        chunkY = (int)cy;
        chunkZ = (int)cz;
        local = new Vector3((float)(rx - (cx * Size)), (float)(ry - (cy * Size)), (float)(rz - (cz * Size)));
    }

    /// <summary>The record of a section read by the culling and, per instance, by vertex shaders.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SectionRecord
    {
        public int X, Y, Z, Group;
        public Vector4 BoundsMin;
        public Vector4 BoundsMax;
        public DrawRange Opaque;
        public DrawRange Cutout;
        public DrawRange Transparent;
    }

    /// <summary>The first index, the number of indices, and the base vertex of a draw.</summary>
    [HotPath]
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct DrawRange(uint first, uint count, uint baseVertex)
    {
        public readonly uint First = first;
        public readonly uint Count = count;
        public readonly uint BaseVertex = baseVertex;
        public readonly uint Unused;
    }

    /// <summary>The record of a group: the camera seen from the group, now and when the depth pyramid was built.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupRecord
    {
        public int CameraX, CameraY, CameraZ, Visible;
        public Vector4 CameraLocal;
        public Matrix4x4 Basis;
        public int PreviousX, PreviousY, PreviousZ, Unused;
        public Vector4 PreviousLocal;
        public Matrix4x4 PreviousBasis;
    }

    /// <summary>A section sent to the culling, and its place among the transparent draws.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Candidate
    {
        public uint Slot;
        public uint Rank;
    }

    [HotPath]
    private readonly record struct RetiredRange(bool Index, int Start, int Length, long Frame);

    private sealed class CullingPass(ChunkGeometry geometry) : IRenderPass
    {
        public void Execute(RenderPassContext context) => geometry.Cull(context);
    }

    private sealed class PyramidPass(ChunkGeometry geometry, RenderGraphTexture depth) : IRenderPass
    {
        public void Execute(RenderPassContext context) => geometry.BuildPyramid(context, depth);
    }
}
