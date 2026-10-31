// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>
/// A GPU behind the neutral API: it creates resources and pipelines, runs
/// command buffers, and paces frames in flight. A backend derives it; this
/// class checks every description and every call before the backend sees
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Use the device on the loop thread of the client context. A frame goes
/// <see cref="BeginFrame"/>, one or more <see cref="Submit"/>, then
/// <see cref="EndFrame"/>, which presents the swapchain. Up to
/// <see cref="FramesInFlight"/> frames run on the GPU while the CPU records
/// the next one: <see cref="BeginFrame"/> waits for the frame that used the
/// same slot, so the CPU never rewrites what the GPU still reads. Resources
/// rewritten every frame are kept once per slot (<see cref="PerFrame{T}"/>).
/// </para>
/// <para>
/// A disposed resource is destroyed once the frames in flight that may use it
/// are finished. <see cref="LiveObjectCount"/> counts the resources not yet
/// destroyed.
/// </para>
/// </remarks>
public abstract class GraphicsDevice : IDisposable
{
    /// <summary>The size of a shared block of buffer memory: 16 MiB.</summary>
    public const long DefaultMemoryBlockSize = 16L * 1024 * 1024;

    private readonly HashSet<GraphicsResource> _live = [];
    private readonly Queue<(GraphicsResource Resource, ulong Frame)> _retired = new();
    private readonly ulong[] _slotFrames;
    private readonly GpuMemoryAllocator _memory;
    private Swapchain? _swapchain;
    private ulong _frame;
    private ulong _completed;
    private bool _inFrame;
    private VSyncMode? _pendingVSync;
    private int _blockCount;
    private bool _disposed;

    /// <summary>Initializes the device.</summary>
    /// <param name="info">The device and its driver.</param>
    /// <param name="capabilities">What the device supports.</param>
    /// <param name="framesInFlight">The number of frames the GPU may run while the CPU records, from 1 to 3.</param>
    /// <param name="memoryBlockSize">The size of a shared block of buffer memory.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framesInFlight"/> is outside 1 to 3.</exception>
    protected GraphicsDevice(GraphicsDeviceInfo info, GraphicsCapabilities capabilities, int framesInFlight, long memoryBlockSize = DefaultMemoryBlockSize)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framesInFlight, 3);
        Info = info;
        Capabilities = capabilities;
        FramesInFlight = framesInFlight;
        _slotFrames = new ulong[framesInFlight];
        long alignment = (long)BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16, Math.Max(capabilities.UniformBufferAlignment, capabilities.StorageBufferAlignment)));
        _memory = new GpuMemoryAllocator(memoryBlockSize, alignment, size => CreateMemoryBlockCore(size, $"nobara.graphics.block.{_blockCount++}"));
    }

    /// <summary>Gets the device and its driver.</summary>
    public GraphicsDeviceInfo Info { get; }

    /// <summary>Gets what the device supports.</summary>
    public GraphicsCapabilities Capabilities { get; }

    /// <summary>Gets the number of frames the GPU may run while the CPU records the next one.</summary>
    public int FramesInFlight { get; }

    /// <summary>Gets the slot of the current frame, from 0 to <see cref="FramesInFlight"/> - 1: the copy of per-frame resources to use.</summary>
    public int FrameIndex
    {
        [HotPath]
        get => (int)(_frame % (ulong)FramesInFlight);
    }

    /// <summary>Gets the number of the current frame, or of the last one; 0 before the first.</summary>
    public ulong FrameNumber => _frame;

    /// <summary>Gets the number of the last frame the GPU finished, as far as the device knows.</summary>
    public ulong CompletedFrame => _completed;

    /// <summary>Gets whether a frame is open: between <see cref="BeginFrame"/> and <see cref="EndFrame"/>.</summary>
    public bool IsInFrame => _inFrame;

    /// <summary>Gets the swapchain of the window.</summary>
    /// <exception cref="InvalidOperationException">The backend has not attached it yet.</exception>
    public Swapchain Swapchain
    {
        [HotPath]
        get => _swapchain ?? throw new InvalidOperationException("The backend has not attached the swapchain yet.");
    }

    /// <summary>Gets the number of resources created and not yet destroyed, disposed ones waiting for their frame included.</summary>
    public int LiveObjectCount => _live.Count;

    /// <summary>Gets how much GPU memory the buffers hold.</summary>
    public GpuMemoryStatistics Memory => _memory.Statistics;

    /// <summary>Lists the resources created and not yet destroyed, to find leaks.</summary>
    /// <returns>A copy of the list.</returns>
    public IReadOnlyList<GraphicsResource> GetLiveObjects() => [.. _live];

    /// <summary>Creates a buffer, sub-allocated from a shared block of GPU memory unless it is large.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The buffer, filled with zeros or with undefined bytes, as the backend does.</returns>
    /// <exception cref="ArgumentException">The description is invalid.</exception>
    /// <exception cref="NotSupportedException">A usage needs a feature the device lacks.</exception>
    public GpuBuffer CreateBuffer(in BufferDescription description)
    {
        CheckAlive();
        if (description.Size <= 0 || description.Usage == BufferUsage.None)
        {
            throw new ArgumentException($"A buffer needs a positive size and at least one usage; got {description.Size} bytes and {description.Usage}.", nameof(description));
        }

        if ((description.Usage & BufferUsage.Storage) != 0)
        {
            Capabilities.Require(GraphicsFeatures.StorageBuffers, $"Storage buffer '{description.Name}'");
        }

        if ((description.Usage & BufferUsage.Indirect) != 0)
        {
            Capabilities.Require(GraphicsFeatures.DrawIndirect, $"Indirect buffer '{description.Name}'");
        }

        return Track(new GpuBuffer(this, description, _memory, _memory.Allocate(description.Size)));
    }

    /// <summary>Creates a texture, its content undefined.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">The description is invalid or exceeds a limit of the device.</exception>
    /// <exception cref="NotSupportedException">A usage needs a feature the device lacks.</exception>
    public Texture CreateTexture(in TextureDescription description)
    {
        CheckAlive();
        ValidateTexture(description);
        return Track(CreateTextureCore(description));
    }

    /// <summary>Creates a sampler.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The sampler.</returns>
    /// <exception cref="ArgumentException">The anisotropy or the mip range is invalid.</exception>
    public Sampler CreateSampler(in SamplerDescription description)
    {
        CheckAlive();
        if (description.MaxAnisotropy < 1 || description.MaxAnisotropy > Math.Max(1, Capabilities.MaxAnisotropy) || description.MinLod > description.MaxLod)
        {
            throw new ArgumentException($"A sampler needs an anisotropy from 1 to {Capabilities.MaxAnisotropy} and MinLod at most MaxLod.", nameof(description));
        }

        return Track(CreateSamplerCore(description));
    }

    /// <summary>Creates a shader from SPIR-V.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ArgumentException">The stage is not one stage, or the bytes are not a SPIR-V module.</exception>
    /// <exception cref="NotSupportedException">A compute shader on a device without <see cref="GraphicsFeatures.Compute"/>.</exception>
    /// <exception cref="ShaderLoadException">The driver refused the module.</exception>
    public Shader CreateShader(in ShaderDescription description)
    {
        CheckAlive();
        if (description.Stage is not (ShaderStages.Vertex or ShaderStages.Fragment or ShaderStages.Compute))
        {
            throw new ArgumentException($"A shader has exactly one stage; got {description.Stage}.", nameof(description));
        }

        if (description.Stage == ShaderStages.Compute)
        {
            Capabilities.Require(GraphicsFeatures.Compute, $"Compute shader '{description.Name}'");
        }

        ReadOnlySpan<byte> code = description.SpirV.Span;
        if (code.Length < 20 || code.Length % 4 != 0 || BitConverter.ToUInt32(code) != 0x07230203u)
        {
            throw new ArgumentException($"Shader '{description.Name}' is not a SPIR-V module: it needs the SPIR-V header, little-endian, and a length multiple of 4.", nameof(description));
        }

        if (string.IsNullOrEmpty(description.EntryPoint))
        {
            throw new ArgumentException("A shader needs the name of its entry point.", nameof(description));
        }

        return Track(CreateShaderCore(description));
    }

    /// <summary>Creates a resource layout.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentException">Two entries of the same class share a binding, or an entry has no stage.</exception>
    /// <exception cref="NotSupportedException">An entry needs a feature the device lacks.</exception>
    public ResourceLayout CreateResourceLayout(in ResourceLayoutDescription description)
    {
        CheckAlive();
        ArgumentNullException.ThrowIfNull(description.Entries);
        var bindings = new HashSet<(int, int)>();
        foreach (ResourceLayoutEntry entry in description.Entries)
        {
            if (entry.Binding < 0 || entry.Stages == ShaderStages.None)
            {
                throw new ArgumentException($"Resource layout '{description.Name}': each entry needs a binding of 0 or more and at least one stage.", nameof(description));
            }

            if (!bindings.Add((BindingClass(entry.Kind), entry.Binding)))
            {
                throw new ArgumentException($"Resource layout '{description.Name}': two entries of the class of {entry.Kind} share binding {entry.Binding}.", nameof(description));
            }

            if (entry.Kind is ResourceKind.ReadOnlyStorageBuffer or ResourceKind.ReadWriteStorageBuffer)
            {
                Capabilities.Require(GraphicsFeatures.StorageBuffers, $"Resource layout '{description.Name}'");
            }

            if (entry.Kind == ResourceKind.StorageTexture || (entry.Stages & ShaderStages.Compute) != 0)
            {
                Capabilities.Require(GraphicsFeatures.Compute, $"Resource layout '{description.Name}'");
            }
        }

        return Track(CreateResourceLayoutCore(description));
    }

    /// <summary>Creates a resource set.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">A resource does not match its entry: missing, wrong usage, out of range, or misaligned.</exception>
    public ResourceSet CreateResourceSet(in ResourceSetDescription description)
    {
        CheckAlive();
        CheckOwned(description.Layout, nameof(description));
        ArgumentNullException.ThrowIfNull(description.Resources);
        ResourceLayoutEntry[] entries = description.Layout.EntryArray;
        if (description.Resources.Count != entries.Length)
        {
            throw new ArgumentException($"Resource set '{description.Name}' has {description.Resources.Count} resources for the {entries.Length} entries of its layout.", nameof(description));
        }

        for (int i = 0; i < entries.Length; i++)
        {
            ValidateBound(description.Name, entries[i], description.Resources[i]);
        }

        return Track(CreateResourceSetCore(description));
    }

    /// <summary>Creates an immutable graphics pipeline.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ArgumentException">A shader has the wrong stage, a state contradicts the outputs, or the vertex or resource layouts are invalid.</exception>
    /// <exception cref="ShaderLoadException">The driver refused to link the shaders.</exception>
    public GraphicsPipeline CreateGraphicsPipeline(in GraphicsPipelineDescription description)
    {
        CheckAlive();
        string name = description.Name ?? "unnamed";
        CheckOwned(description.VertexShader, nameof(description));
        if (description.VertexShader.Stage != ShaderStages.Vertex)
        {
            throw new ArgumentException($"Graphics pipeline '{name}': the vertex shader is a {description.VertexShader.Stage} shader.", nameof(description));
        }

        if (description.FragmentShader is { } fragment)
        {
            CheckOwned(fragment, nameof(description));
            if (fragment.Stage != ShaderStages.Fragment)
            {
                throw new ArgumentException($"Graphics pipeline '{name}': the fragment shader is a {fragment.Stage} shader.", nameof(description));
            }
        }

        IReadOnlyList<PixelFormat> colors = description.Outputs.Colors ?? throw new ArgumentException($"Graphics pipeline '{name}' needs its output formats.", nameof(description));
        if (colors.Count > Capabilities.MaxColorAttachments)
        {
            throw new ArgumentException($"Graphics pipeline '{name}' draws to {colors.Count} color attachments; the device has at most {Capabilities.MaxColorAttachments}.", nameof(description));
        }

        foreach (PixelFormat color in colors)
        {
            if (color == PixelFormat.None || PixelFormats.IsDepth(color))
            {
                throw new ArgumentException($"Graphics pipeline '{name}': {color} is not a color format.", nameof(description));
            }
        }

        PixelFormat depth = description.Outputs.Depth;
        if (depth != PixelFormat.None && !PixelFormats.IsDepth(depth))
        {
            throw new ArgumentException($"Graphics pipeline '{name}': {depth} is not a depth format.", nameof(description));
        }

        if ((description.Depth.TestEnabled || description.Depth.WriteEnabled) && depth == PixelFormat.None)
        {
            throw new ArgumentException($"Graphics pipeline '{name}' tests or writes depth without a depth output.", nameof(description));
        }

        ValidateVertexLayouts(name, description.VertexBuffers ?? []);
        ValidateLayouts(name, description.ResourceLayouts ?? []);
        return Track(CreateGraphicsPipelineCore(description));
    }

    /// <summary>Creates an immutable compute pipeline. Requires <see cref="GraphicsFeatures.Compute"/>.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ArgumentException">The shader is not a compute shader, or the layouts are invalid.</exception>
    /// <exception cref="NotSupportedException">The device lacks <see cref="GraphicsFeatures.Compute"/>.</exception>
    /// <exception cref="ShaderLoadException">The driver refused to link the shader.</exception>
    public ComputePipeline CreateComputePipeline(in ComputePipelineDescription description)
    {
        CheckAlive();
        string name = description.Name ?? "unnamed";
        Capabilities.Require(GraphicsFeatures.Compute, $"Compute pipeline '{name}'");
        CheckOwned(description.Shader, nameof(description));
        if (description.Shader.Stage != ShaderStages.Compute)
        {
            throw new ArgumentException($"Compute pipeline '{name}': the shader is a {description.Shader.Stage} shader.", nameof(description));
        }

        ValidateLayouts(name, description.ResourceLayouts ?? []);
        return Track(CreateComputePipelineCore(description));
    }

    /// <summary>Creates a framebuffer on textures.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The framebuffer.</returns>
    /// <exception cref="ArgumentException">No attachment, a texture without the right usage or format, a level or layer out of range, or attachments of different sizes.</exception>
    public Framebuffer CreateFramebuffer(in FramebufferDescription description)
    {
        CheckAlive();
        string name = description.Name ?? "unnamed";
        IReadOnlyList<FramebufferAttachment> colors = description.Colors ?? [];
        if (colors.Count == 0 && description.Depth is null)
        {
            throw new ArgumentException($"Framebuffer '{name}' has no attachment.", nameof(description));
        }

        if (colors.Count > Capabilities.MaxColorAttachments)
        {
            throw new ArgumentException($"Framebuffer '{name}' has {colors.Count} color attachments; the device has at most {Capabilities.MaxColorAttachments}.", nameof(description));
        }

        int width = -1;
        int height = -1;
        foreach (FramebufferAttachment color in colors)
        {
            ValidateAttachment(name, color, depth: false, ref width, ref height);
        }

        if (description.Depth is { } depthAttachment)
        {
            ValidateAttachment(name, depthAttachment, depth: true, ref width, ref height);
        }

        return Track(CreateFramebufferCore(description));
    }

    /// <summary>Creates a command buffer, recorded again every frame.</summary>
    /// <param name="name">The debug name, or <see langword="null"/>.</param>
    /// <returns>The command buffer.</returns>
    public CommandBuffer CreateCommandBuffer(string? name = null)
    {
        CheckAlive();
        return new CommandBuffer(this, name);
    }

    /// <summary>Writes bytes to a buffer now, in order with the commands already submitted.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The first byte written.</param>
    /// <param name="data">The bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bytes do not fit.</exception>
    /// <remarks>Allocates nothing. Do not rewrite a range a frame in flight still reads: use one buffer per slot instead.</remarks>
    [HotPath]
    public void UpdateBuffer(GpuBuffer buffer, long offset, ReadOnlySpan<byte> data)
    {
        CheckUsable(buffer);
        if (offset < 0 || offset + data.Length > buffer.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"{data.Length} bytes at byte {offset} do not fit in {buffer} of {buffer.Size} bytes.");
        }

        WriteBufferCore(buffer.Block, buffer.Offset + offset, data);
    }

    /// <summary>Writes texels to a region of a texture now, in order with the commands already submitted.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="region">The region; one mip level.</param>
    /// <param name="data">The texels, in the format of the texture, rows packed without padding, layers one after the other.</param>
    /// <exception cref="ArgumentException">The region is outside the texture, or the data has the wrong length.</exception>
    public void UpdateTexture(Texture texture, in TextureRegion region, ReadOnlySpan<byte> data)
    {
        CheckUsable(texture);
        ValidateRegion(texture, region);
        if (data.Length != region.SizeIn(texture.Format))
        {
            throw new ArgumentException($"The region of {texture} needs {region.SizeIn(texture.Format)} bytes; got {data.Length}.", nameof(data));
        }

        WriteTextureCore(texture, region, data);
    }

    /// <summary>Reads bytes of a buffer back to the CPU. Waits for the GPU: for tests and tools, not for frames.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The first byte read.</param>
    /// <param name="destination">Receives the bytes; its length is the number of bytes read.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the buffer.</exception>
    public void ReadBuffer(GpuBuffer buffer, long offset, Span<byte> destination)
    {
        CheckUsable(buffer);
        if (offset < 0 || offset + destination.Length > buffer.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"{destination.Length} bytes at byte {offset} are outside {buffer} of {buffer.Size} bytes.");
        }

        ReadBufferCore(buffer.Block, buffer.Offset + offset, destination);
    }

    /// <summary>Reads texels of a texture back to the CPU. Waits for the GPU: for tests and tools, not for frames.</summary>
    /// <param name="texture">A texture with a color format.</param>
    /// <param name="region">The region; one mip level.</param>
    /// <param name="destination">Receives the texels, rows packed without padding; its length is the size of the region.</param>
    /// <exception cref="ArgumentException">The region is outside the texture, the format is a depth format, or the destination has the wrong length.</exception>
    public void ReadTexture(Texture texture, in TextureRegion region, Span<byte> destination)
    {
        CheckUsable(texture);
        ValidateRegion(texture, region);
        if (PixelFormats.IsDepth(texture.Format) || destination.Length != region.SizeIn(texture.Format))
        {
            throw new ArgumentException($"{texture} is read back with a color format into {region.SizeIn(texture.Format)} bytes; got {texture.Format} and {destination.Length} bytes.", nameof(destination));
        }

        ReadTextureCore(texture, region, destination);
    }

    /// <summary>
    /// Begins a frame: waits until the GPU finished the frame that used the
    /// same slot, destroys the resources no frame uses anymore, applies a new
    /// synchronization, and creates the swapchain again if the window changed.
    /// </summary>
    /// <returns><see langword="true"/> when the swapchain can be drawn this frame; <see langword="false"/> while the window is minimized or its surface is lost, when only off-screen work makes sense.</returns>
    /// <exception cref="InvalidOperationException">A frame is already open.</exception>
    [HotPath]
    public bool BeginFrame()
    {
        CheckAlive();
        if (_inFrame)
        {
            throw new InvalidOperationException("A frame is already open: call EndFrame first.");
        }

        _frame++;
        int slot = FrameIndex;
        ulong previous = _slotFrames[slot];
        if (previous != 0)
        {
            WaitForFrameCore(slot);
            _slotFrames[slot] = 0;
            if (previous > _completed)
            {
                _completed = previous;
            }
        }

        DestroyRetired();
        Swapchain swapchain = Swapchain;
        if (_pendingVSync is { } vsync)
        {
            _pendingVSync = null;
            swapchain.VSync = SetVSyncCore(vsync);
        }

        swapchain.Update();
        _inFrame = true;
        return swapchain.IsAvailable;
    }

    /// <summary>Runs recorded commands on the GPU, in the open frame.</summary>
    /// <param name="commands">Commands ended and not yet submitted.</param>
    /// <exception cref="InvalidOperationException">No frame is open, or the commands are not ended or already submitted.</exception>
    [HotPath]
    public void Submit(CommandBuffer commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!_inFrame)
        {
            throw new InvalidOperationException("Submit commands inside a frame: call BeginFrame first.");
        }

        if (!ReferenceEquals(commands.Device, this) || !commands.IsReady)
        {
            throw new InvalidOperationException("Submit commands of this device, ended with End, once.");
        }

        commands.MarkSubmitted();
        ExecuteCore(commands);
    }

    /// <summary>Ends the frame: the GPU signals its end, and the swapchain is presented when it is available.</summary>
    /// <exception cref="InvalidOperationException">No frame is open.</exception>
    [HotPath]
    public void EndFrame()
    {
        if (!_inFrame)
        {
            throw new InvalidOperationException("No frame is open: call BeginFrame first.");
        }

        int slot = FrameIndex;
        EndFrameCore(slot, Swapchain.IsAvailable);
        _slotFrames[slot] = _frame;
        _inFrame = false;
    }

    /// <summary>Asks for another synchronization with the monitor; it applies at the start of the next frame.</summary>
    /// <param name="mode">The mode.</param>
    public void SetVSync(VSyncMode mode) => _pendingVSync = mode;

    /// <summary>Waits until the GPU finished all work, then destroys the retired resources. Outside frames.</summary>
    /// <exception cref="InvalidOperationException">A frame is open.</exception>
    public void WaitIdle()
    {
        CheckAlive();
        if (_inFrame)
        {
            throw new InvalidOperationException("WaitIdle runs outside frames: call EndFrame first.");
        }

        WaitIdleCore();
        Array.Clear(_slotFrames);
        _completed = _frame;
        DestroyRetired();
    }

    /// <summary>Waits for the GPU, destroys every resource left, then the device.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        WaitIdleCore();
        _completed = ulong.MaxValue;
        DestroyRetired();
        foreach (GraphicsResource resource in _live.ToArray())
        {
            resource.Destroy();
        }

        _live.Clear();
        _memory.DestroyAll();
        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Retires a disposed resource: destroyed now when no frame can use it, otherwise after its frame.</summary>
    internal void Retire(GraphicsResource resource)
    {
        if (_disposed)
        {
            return;
        }

        if (_frame <= _completed)
        {
            DestroyNow(resource);
            return;
        }

        _retired.Enqueue((resource, _frame));
    }

    /// <summary>Frees what depends on the surface, which the window lost.</summary>
    internal void ReleaseSwapchain() => ReleaseSwapchainCore();

    /// <summary>Creates the swapchain again at a new size.</summary>
    [HotPath]
    internal void RecreateSwapchain(int width, int height) => RecreateSwapchainCore(width, height);

    /// <summary>Attaches the swapchain; the backend calls it once, from its constructor.</summary>
    /// <param name="framebuffer">The framebuffer of the back buffer, with <see cref="Framebuffer.IsSwapchain"/>.</param>
    /// <param name="vsync">The synchronization in use.</param>
    /// <exception cref="InvalidOperationException">The swapchain is attached already, or the framebuffer is not a swapchain framebuffer.</exception>
    protected void AttachSwapchain(Framebuffer framebuffer, VSyncMode vsync)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        if (_swapchain is not null || !framebuffer.IsSwapchain || framebuffer.ColorFormatArray.Length != 1)
        {
            throw new InvalidOperationException("Attach the swapchain once, with a swapchain framebuffer of one color attachment.");
        }

        _swapchain = new Swapchain(this, framebuffer, vsync);
    }

    /// <summary>Creates a block of GPU memory for buffers, filled with zeros.</summary>
    /// <param name="size">The size, in bytes.</param>
    /// <param name="name">The debug name.</param>
    /// <returns>The block.</returns>
    protected abstract GpuMemoryBlock CreateMemoryBlockCore(long size, string name);

    /// <summary>Creates a texture from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The texture.</returns>
    protected abstract Texture CreateTextureCore(in TextureDescription description);

    /// <summary>Creates a sampler from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The sampler.</returns>
    protected abstract Sampler CreateSamplerCore(in SamplerDescription description);

    /// <summary>Creates a shader from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ShaderLoadException">The driver refused the module.</exception>
    protected abstract Shader CreateShaderCore(in ShaderDescription description);

    /// <summary>Creates a resource layout from a validated description. By default, an object without backend state.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The layout.</returns>
    protected virtual ResourceLayout CreateResourceLayoutCore(in ResourceLayoutDescription description) => new(this, description);

    /// <summary>Creates a resource set from a validated description. By default, an object without backend state.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The set.</returns>
    protected virtual ResourceSet CreateResourceSetCore(in ResourceSetDescription description) => new(this, description);

    /// <summary>Creates a graphics pipeline from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ShaderLoadException">The driver refused to link the shaders.</exception>
    protected abstract GraphicsPipeline CreateGraphicsPipelineCore(in GraphicsPipelineDescription description);

    /// <summary>Creates a compute pipeline from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ShaderLoadException">The driver refused to link the shader.</exception>
    protected abstract ComputePipeline CreateComputePipelineCore(in ComputePipelineDescription description);

    /// <summary>Creates a framebuffer from a validated description.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The framebuffer.</returns>
    protected abstract Framebuffer CreateFramebufferCore(in FramebufferDescription description);

    /// <summary>Writes bytes to a block.</summary>
    /// <param name="block">The block.</param>
    /// <param name="offset">The first byte written, from the start of the block.</param>
    /// <param name="data">The bytes.</param>
    [HotPath]
    protected abstract void WriteBufferCore(GpuMemoryBlock block, long offset, ReadOnlySpan<byte> data);

    /// <summary>Writes texels to a validated region.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="region">The region.</param>
    /// <param name="data">The texels.</param>
    protected abstract void WriteTextureCore(Texture texture, in TextureRegion region, ReadOnlySpan<byte> data);

    /// <summary>Reads bytes of a block, waiting for the GPU.</summary>
    /// <param name="block">The block.</param>
    /// <param name="offset">The first byte read, from the start of the block.</param>
    /// <param name="destination">Receives the bytes.</param>
    protected abstract void ReadBufferCore(GpuMemoryBlock block, long offset, Span<byte> destination);

    /// <summary>Reads texels of a validated region, waiting for the GPU.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="region">The region.</param>
    /// <param name="destination">Receives the texels.</param>
    protected abstract void ReadTextureCore(Texture texture, in TextureRegion region, Span<byte> destination);

    /// <summary>Runs submitted commands.</summary>
    /// <param name="commands">The commands, validated while recorded.</param>
    [HotPath]
    protected abstract void ExecuteCore(CommandBuffer commands);

    /// <summary>Waits until the GPU finished the frame that ended in a slot.</summary>
    /// <param name="slot">The slot.</param>
    [HotPath]
    protected abstract void WaitForFrameCore(int slot);

    /// <summary>Ends a frame: marks its end on the GPU for <see cref="WaitForFrameCore"/>, and presents the swapchain when asked.</summary>
    /// <param name="slot">The slot of the frame.</param>
    /// <param name="present"><see langword="true"/> to present the swapchain.</param>
    [HotPath]
    protected abstract void EndFrameCore(int slot, bool present);

    /// <summary>Creates the swapchain again at a new size, after a resize or a recreated surface.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    [HotPath]
    protected abstract void RecreateSwapchainCore(int width, int height);

    /// <summary>Frees what depends on the surface, which the window lost. By default, nothing.</summary>
    protected virtual void ReleaseSwapchainCore()
    {
    }

    /// <summary>Applies a synchronization with the monitor.</summary>
    /// <param name="mode">The mode asked for.</param>
    /// <returns>The mode in use, after the fallback of the driver.</returns>
    [HotPath]
    protected abstract VSyncMode SetVSyncCore(VSyncMode mode);

    /// <summary>Waits until the GPU finished all work.</summary>
    protected abstract void WaitIdleCore();

    /// <summary>Destroys the device itself, after every resource.</summary>
    protected abstract void DisposeCore();

    private static int BindingClass(ResourceKind kind) => kind switch
    {
        ResourceKind.UniformBuffer => 0,
        ResourceKind.ReadOnlyStorageBuffer or ResourceKind.ReadWriteStorageBuffer => 1,
        ResourceKind.SampledTexture => 2,
        _ => 3,
    };

    private static int MipCount(int width, int height, int depth) => BitOperations.Log2((uint)Math.Max(width, Math.Max(height, depth))) + 1;

    private T Track<T>(T resource)
        where T : GraphicsResource
    {
        _live.Add(resource);
        return resource;
    }

    [HotPath]
    private void DestroyRetired()
    {
        while (_retired.Count > 0 && _retired.Peek().Frame <= _completed)
        {
            DestroyNow(_retired.Dequeue().Resource);
        }
    }

    [HotPath]
    private void DestroyNow(GraphicsResource resource)
    {
        resource.Destroy();
        _live.Remove(resource);
    }

    [HotPath]
    private void CheckAlive()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GraphicsDevice));
        }
    }

    [HotPath]
    private void CheckUsable(GraphicsResource resource)
    {
        CheckAlive();
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.IsDisposed || !ReferenceEquals(resource.Device, this))
        {
            throw new InvalidOperationException($"{resource} is disposed or belongs to another device.");
        }
    }

    private void CheckOwned(GraphicsResource? resource, string parameter)
    {
        if (resource is null || resource.IsDisposed || !ReferenceEquals(resource.Device, this))
        {
            throw new ArgumentException($"{resource?.ToString() ?? "A resource"} is missing, disposed, or belongs to another device.", parameter);
        }
    }

    private void ValidateTexture(in TextureDescription d)
    {
        string name = d.Name ?? "unnamed";
        if (d.Format == PixelFormat.None || d.Usage == TextureUsage.None || d.Width <= 0 || d.Height <= 0 || d.DepthOrLayers <= 0 || d.MipLevels <= 0)
        {
            throw new ArgumentException($"Texture '{name}' needs a format, a usage, and positive sizes and mip levels.", nameof(d));
        }

        GraphicsCapabilities caps = Capabilities;
        switch (d.Dimension)
        {
            case TextureDimension.Texture2D when d.DepthOrLayers != 1:
                throw new ArgumentException($"Texture '{name}' is 2D: its DepthOrLayers is 1.", nameof(d));
            case TextureDimension.Texture2D or TextureDimension.Texture2DArray when d.Width > caps.MaxTextureSize || d.Height > caps.MaxTextureSize:
                throw new ArgumentException($"Texture '{name}' is {d.Width}x{d.Height}; the device takes at most {caps.MaxTextureSize} texels per side.", nameof(d));
            case TextureDimension.Texture2DArray when d.DepthOrLayers > caps.MaxTextureArrayLayers:
                throw new ArgumentException($"Texture '{name}' has {d.DepthOrLayers} layers; the device takes at most {caps.MaxTextureArrayLayers}.", nameof(d));
            case TextureDimension.Texture3D when d.Width > caps.MaxTexture3DSize || d.Height > caps.MaxTexture3DSize || d.DepthOrLayers > caps.MaxTexture3DSize:
                throw new ArgumentException($"Texture '{name}' is {d.Width}x{d.Height}x{d.DepthOrLayers}; the device takes at most {caps.MaxTexture3DSize} texels per side in 3D.", nameof(d));
        }

        int depth = d.Dimension == TextureDimension.Texture3D ? d.DepthOrLayers : 1;
        int full = MipCount(d.Width, d.Height, depth);
        if (d.MipLevels > full)
        {
            throw new ArgumentException($"Texture '{name}' asks for {d.MipLevels} mip levels; its size has {full}.", nameof(d));
        }

        bool isDepth = PixelFormats.IsDepth(d.Format);
        if ((d.Usage & TextureUsage.DepthStencil) != 0 && (!isDepth || d.Dimension == TextureDimension.Texture3D))
        {
            throw new ArgumentException($"Texture '{name}' is a depth attachment: it needs a depth format and is not 3D.", nameof(d));
        }

        if ((d.Usage & TextureUsage.RenderTarget) != 0 && isDepth)
        {
            throw new ArgumentException($"Texture '{name}' is a color attachment: {d.Format} is a depth format.", nameof(d));
        }

        if ((d.Usage & TextureUsage.Storage) != 0)
        {
            caps.Require(GraphicsFeatures.Compute, $"Storage texture '{name}'");
            if (isDepth || d.Format == PixelFormat.Rgba8UnormSrgb)
            {
                throw new ArgumentException($"Texture '{name}' is a storage texture: {d.Format} cannot be written by shaders.", nameof(d));
            }
        }
    }

    private void ValidateBound(string? set, ResourceLayoutEntry entry, BoundResource resource)
    {
        string where = $"Resource set '{set}', binding {entry.Binding} ({entry.Kind})";
        switch (entry.Kind)
        {
            case ResourceKind.UniformBuffer or ResourceKind.ReadOnlyStorageBuffer or ResourceKind.ReadWriteStorageBuffer:
            {
                GpuBuffer buffer = resource.Buffer ?? throw new ArgumentException($"{where} needs a buffer.", nameof(resource));
                CheckOwned(buffer, nameof(resource));
                bool uniform = entry.Kind == ResourceKind.UniformBuffer;
                BufferUsage usage = uniform ? BufferUsage.Uniform : BufferUsage.Storage;
                long size = resource.Size == 0 ? buffer.Size - resource.Offset : resource.Size;
                int alignment = uniform ? Capabilities.UniformBufferAlignment : Capabilities.StorageBufferAlignment;
                if ((buffer.Usage & usage) == 0)
                {
                    throw new ArgumentException($"{where}: {buffer} needs BufferUsage.{usage}.", nameof(resource));
                }

                if (resource.Offset < 0 || size <= 0 || resource.Offset + size > buffer.Size || resource.Offset % alignment != 0)
                {
                    throw new ArgumentException($"{where}: the range at byte {resource.Offset} of {size} bytes is outside {buffer} or its offset is not a multiple of {alignment}.", nameof(resource));
                }

                if (uniform && size > Capabilities.MaxUniformBufferRange)
                {
                    throw new ArgumentException($"{where}: a uniform range holds at most {Capabilities.MaxUniformBufferRange} bytes; got {size}.", nameof(resource));
                }

                break;
            }

            case ResourceKind.SampledTexture:
            {
                Texture texture = resource.Texture ?? throw new ArgumentException($"{where} needs a texture.", nameof(resource));
                CheckOwned(texture, nameof(resource));
                CheckOwned(resource.Sampler, nameof(resource));
                if ((texture.Usage & TextureUsage.Sampled) == 0)
                {
                    throw new ArgumentException($"{where}: {texture} needs TextureUsage.Sampled.", nameof(resource));
                }

                break;
            }

            default:
            {
                Texture texture = resource.Texture ?? throw new ArgumentException($"{where} needs a texture.", nameof(resource));
                CheckOwned(texture, nameof(resource));
                if ((texture.Usage & TextureUsage.Storage) == 0)
                {
                    throw new ArgumentException($"{where}: {texture} needs TextureUsage.Storage.", nameof(resource));
                }

                break;
            }
        }
    }

    private void ValidateLayouts(string pipeline, IReadOnlyList<ResourceLayout> layouts)
    {
        if (layouts.Count > CommandBuffer.MaxResourceSets)
        {
            throw new ArgumentException($"Pipeline '{pipeline}' has {layouts.Count} resource layouts; at most {CommandBuffer.MaxResourceSets}.", nameof(layouts));
        }

        var bindings = new HashSet<(int, int)>();
        foreach (ResourceLayout layout in layouts)
        {
            CheckOwned(layout, nameof(layouts));
            foreach (ResourceLayoutEntry entry in layout.EntryArray)
            {
                if (!bindings.Add((BindingClass(entry.Kind), entry.Binding)))
                {
                    throw new ArgumentException($"Pipeline '{pipeline}': two layouts bind the class of {entry.Kind} at {entry.Binding}; OpenGL-style bindings are shared by all the layouts of a pipeline.", nameof(layouts));
                }
            }
        }
    }

    private static void ValidateVertexLayouts(string pipeline, IReadOnlyList<VertexBufferLayout> buffers)
    {
        if (buffers.Count > CommandBuffer.MaxVertexBuffers)
        {
            throw new ArgumentException($"Pipeline '{pipeline}' has {buffers.Count} vertex buffers; at most {CommandBuffer.MaxVertexBuffers}.", nameof(buffers));
        }

        var locations = new HashSet<int>();
        foreach (VertexBufferLayout buffer in buffers)
        {
            if (buffer.Stride <= 0 || buffer.Attributes is null)
            {
                throw new ArgumentException($"Pipeline '{pipeline}': a vertex buffer needs a positive stride and its attributes.", nameof(buffers));
            }

            foreach (VertexAttribute attribute in buffer.Attributes)
            {
                if (attribute.Location < 0 || attribute.Offset < 0 || attribute.Offset + VertexFormats.SizeOf(attribute.Format) > buffer.Stride)
                {
                    throw new ArgumentException($"Pipeline '{pipeline}': attribute at location {attribute.Location} does not fit in a stride of {buffer.Stride} bytes.", nameof(buffers));
                }

                if (!locations.Add(attribute.Location))
                {
                    throw new ArgumentException($"Pipeline '{pipeline}': two vertex attributes use location {attribute.Location}.", nameof(buffers));
                }
            }
        }
    }

    private void ValidateAttachment(string framebuffer, in FramebufferAttachment attachment, bool depth, ref int width, ref int height)
    {
        Texture texture = attachment.Texture;
        CheckOwned(texture, nameof(attachment));
        TextureUsage needed = depth ? TextureUsage.DepthStencil : TextureUsage.RenderTarget;
        if ((texture.Usage & needed) == 0)
        {
            throw new ArgumentException($"Framebuffer '{framebuffer}': {texture} needs TextureUsage.{needed}.", nameof(attachment));
        }

        int layers = texture.Dimension == TextureDimension.Texture3D ? Math.Max(1, texture.DepthOrLayers >> attachment.MipLevel) : texture.DepthOrLayers;
        if (attachment.MipLevel < 0 || attachment.MipLevel >= texture.MipLevels || attachment.Layer < 0 || attachment.Layer >= layers)
        {
            throw new ArgumentException($"Framebuffer '{framebuffer}': level {attachment.MipLevel}, layer {attachment.Layer} is outside {texture}.", nameof(attachment));
        }

        int w = Math.Max(1, texture.Width >> attachment.MipLevel);
        int h = Math.Max(1, texture.Height >> attachment.MipLevel);
        if (width >= 0 && (w != width || h != height))
        {
            throw new ArgumentException($"Framebuffer '{framebuffer}': attachments have different sizes, {width}x{height} and {w}x{h}.", nameof(attachment));
        }

        width = w;
        height = h;
    }

    private static void ValidateRegion(Texture texture, in TextureRegion region)
    {
        int w = Math.Max(1, texture.Width >> Math.Clamp(region.MipLevel, 0, 31));
        int h = Math.Max(1, texture.Height >> Math.Clamp(region.MipLevel, 0, 31));
        int d = texture.Dimension == TextureDimension.Texture3D ? Math.Max(1, texture.DepthOrLayers >> Math.Clamp(region.MipLevel, 0, 31)) : texture.DepthOrLayers;
        if (region.MipLevel < 0 || region.MipLevel >= texture.MipLevels
            || region.X < 0 || region.Y < 0 || region.Z < 0 || region.Width <= 0 || region.Height <= 0 || region.Depth <= 0
            || region.X + region.Width > w || region.Y + region.Height > h || region.Z + region.Depth > d)
        {
            throw new ArgumentException($"The region {region} is outside {texture} ({w}x{h}x{d} at level {region.MipLevel}).", nameof(region));
        }
    }
}

/// <summary>The driver refused a shader module or the link of a pipeline; the message holds its log.</summary>
public sealed class ShaderLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">The message, with the log of the driver.</param>
    public ShaderLoadException(string message)
        : base(message)
    {
    }
}
