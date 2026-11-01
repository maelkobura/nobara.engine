// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Frames;
using Silk.NET.OpenGL;

namespace Nobara.Graphics.OpenGL;

/// <summary>
/// The OpenGL 4.6 device. Every object is created and edited through direct
/// state access; command buffers are replayed on the loop thread when they are
/// submitted; a fence ends each frame in flight; the swapchain is the default
/// framebuffer of the window.
/// </summary>
internal sealed unsafe class GLDevice : GraphicsDevice
{
    private const uint FenceTimeout = 1_000_000_000;

    private readonly IOpenGLContext _context;
    private readonly Logger _logger;
    private readonly nint[] _fences;
    private readonly Dictionary<string, byte[]> _labels = new(StringComparer.Ordinal);
    private readonly GLFramebuffer _swapchainFramebuffer;

    // State of the replay, kept across command buffers to skip redundant calls.
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[CommandBuffer.MaxVertexBuffers];
    private readonly long[] _vertexOffsets = new long[CommandBuffer.MaxVertexBuffers];
    private GLGraphicsPipeline? _graphics;
    private GpuBuffer? _indexBuffer;
    private long _indexOffset;
    private GLEnum _indexType;
    private int _indexSize;
    private bool _vertexDirty;
    private uint _program;
    private uint _vertexArray;

    private GLDevice(IOpenGLContext context, GL gl, GraphicsDeviceInfo info, GraphicsCapabilities capabilities, in GraphicsDeviceOptions options, WindowSize size)
        : base(info, capabilities, options.FramesInFlight)
    {
        _context = context;
        _logger = options.Logger;
        GL = gl;
        _fences = new nint[options.FramesInFlight];

        // Depth from 0 to 1 in clip space, as the explicit APIs; texel rows packed without padding.
        gl.ClipControl(GLEnum.LowerLeft, GLEnum.ZeroToOne);
        gl.PixelStore(GLEnum.UnpackAlignment, 1);
        gl.PixelStore(GLEnum.PackAlignment, 1);

        _swapchainFramebuffer = new GLFramebuffer(this, size.Width, size.Height);
        AttachSwapchain(_swapchainFramebuffer, ApplyVSync(options.VSync));
    }

    public GL GL { get; }

    /// <summary>Gets the counter of the OpenGL objects alive.</summary>
    public GLObjectCounter Objects { get; } = new();

    /// <summary>Creates the device: an OpenGL 4.6 core context on the window, current on the calling thread.</summary>
    /// <exception cref="NotSupportedException">The window has no OpenGL support, or the driver lacks OpenGL 4.6 or SPIR-V.</exception>
    public static GLDevice Create(IWindow window, in GraphicsDeviceOptions options)
    {
        IOpenGLSurface surface = window.OpenGL
            ?? throw new NotSupportedException("The window backend cannot create OpenGL contexts.");
        IOpenGLContext context;
        try
        {
            context = surface.CreateContext(new OpenGLContextOptions(4, 6, CoreProfile: true));
        }
        catch (InvalidOperationException exception)
        {
            throw new NotSupportedException($"The OpenGL backend needs an OpenGL 4.6 core driver: {exception.Message}", exception);
        }

        try
        {
            GL gl = GL.GetApi(context.GetProcAddress);
            string version = gl.GetStringS(StringName.Version) ?? string.Empty;
            gl.GetInteger(GLEnum.MajorVersion, out int major);
            gl.GetInteger(GLEnum.MinorVersion, out int minor);
            if (major < 4 || (major == 4 && minor < 6))
            {
                throw new NotSupportedException($"The OpenGL backend needs OpenGL 4.6; the driver gives {version}.");
            }

            gl.GetInteger(GLEnum.NumShaderBinaryFormats, out int count);
            Span<int> formats = stackalloc int[Math.Max(count, 1)];
            gl.GetInteger(GLEnum.ShaderBinaryFormats, formats);
            if (!formats[..count].Contains(0x9551))
            {
                throw new NotSupportedException($"The driver ({version}) does not load SPIR-V shaders.");
            }

            var info = new GraphicsDeviceInfo("opengl", gl.GetStringS(StringName.Vendor) ?? string.Empty, gl.GetStringS(StringName.Renderer) ?? string.Empty, version);
            return new GLDevice(context, gl, info, ReadCapabilities(gl), options, window.PixelSize);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Gives an object a label for capture tools.</summary>
    public void Label(GLEnum identifier, uint handle, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            GL.ObjectLabel(identifier, handle, (uint)Encoding.UTF8.GetByteCount(name), name);
        }
    }

    protected override GpuMemoryBlock CreateMemoryBlockCore(long size, string name) => new GLMemoryBlock(this, size, name);

    protected override Texture CreateTextureCore(in TextureDescription description) => new GLTexture(this, description);

    protected override Sampler CreateSamplerCore(in SamplerDescription description) => new GLSampler(this, description);

    protected override Shader CreateShaderCore(in ShaderDescription description) => new GLShader(this, description);

    protected override GraphicsPipeline CreateGraphicsPipelineCore(in GraphicsPipelineDescription description) => new GLGraphicsPipeline(this, description);

    protected override ComputePipeline CreateComputePipelineCore(in ComputePipelineDescription description) => new GLComputePipeline(this, description);

    protected override Framebuffer CreateFramebufferCore(in FramebufferDescription description)
    {
        FramebufferAttachment first = description.Colors.Count > 0 ? description.Colors[0] : description.Depth!.Value;
        return new GLFramebuffer(this, description, Math.Max(1, first.Texture.Width >> first.MipLevel), Math.Max(1, first.Texture.Height >> first.MipLevel));
    }

    [HotPath]
    protected override void WriteBufferCore(GpuMemoryBlock block, long offset, ReadOnlySpan<byte> data)
    {
        fixed (byte* bytes = data)
        {
            GL.NamedBufferSubData(((GLMemoryBlock)block).Handle, (nint)offset, (nuint)data.Length, bytes);
        }
    }

    protected override void WriteTextureCore(Texture texture, in TextureRegion region, ReadOnlySpan<byte> data)
    {
        var gl = (GLTexture)texture;
        fixed (byte* bytes = data)
        {
            if (texture.Dimension == TextureDimension.Texture2D)
            {
                GL.TextureSubImage2D(gl.Handle, region.MipLevel, region.X, region.Y, (uint)region.Width, (uint)region.Height, gl.GLFormat.Format, gl.GLFormat.Type, bytes);
            }
            else
            {
                GL.TextureSubImage3D(gl.Handle, region.MipLevel, region.X, region.Y, region.Z, (uint)region.Width, (uint)region.Height, (uint)region.Depth, gl.GLFormat.Format, gl.GLFormat.Type, bytes);
            }
        }
    }

    protected override void ReadBufferCore(GpuMemoryBlock block, long offset, Span<byte> destination)
    {
        // Writes of shaders reach reads of the CPU only after a barrier.
        GL.MemoryBarrier(uint.MaxValue);
        fixed (byte* bytes = destination)
        {
            GL.GetNamedBufferSubData(((GLMemoryBlock)block).Handle, (nint)offset, (nuint)destination.Length, bytes);
        }
    }

    protected override void ReadTextureCore(Texture texture, in TextureRegion region, Span<byte> destination)
    {
        GL.MemoryBarrier(uint.MaxValue);
        var gl = (GLTexture)texture;
        fixed (byte* bytes = destination)
        {
            GL.GetTextureSubImage(gl.Handle, region.MipLevel, region.X, region.Y, region.Z, (uint)region.Width, (uint)region.Height, (uint)region.Depth, gl.GLFormat.Format, gl.GLFormat.Type, (uint)destination.Length, bytes);
        }
    }

    [HotPath]
    protected override void ExecuteCore(CommandBuffer commands)
    {
        ResetBindings();
        ReadOnlySpan<Command> list = commands.Commands;
        for (int i = 0; i < list.Length; i++)
        {
            ref readonly Command c = ref list[i];
            switch (c.Kind)
            {
                case CommandKind.BeginRenderPass:
                    BeginRenderPass(commands.GetObject<GLFramebuffer>(c.Object), commands.GetRenderPass(c.A));
                    break;
                case CommandKind.EndRenderPass:
                    break;
                case CommandKind.SetGraphicsPipeline:
                    SetGraphicsPipeline(commands.GetObject<GLGraphicsPipeline>(c.Object));
                    break;
                case CommandKind.SetComputePipeline:
                    UseProgram(commands.GetObject<GLComputePipeline>(c.Object).Program);
                    break;
                case CommandKind.SetResourceSet:
                    BindSet(commands.GetObject<ResourceSet>(c.Object));
                    break;
                case CommandKind.SetVertexBuffer:
                    _vertexBuffers[c.A] = commands.GetObject<GpuBuffer>(c.Object);
                    _vertexOffsets[c.A] = c.X;
                    _vertexDirty = true;
                    break;
                case CommandKind.SetIndexBuffer:
                    _indexBuffer = commands.GetObject<GpuBuffer>(c.Object);
                    _indexOffset = c.X;
                    _indexType = c.A == (int)IndexFormat.UInt16 ? GLEnum.UnsignedShort : GLEnum.UnsignedInt;
                    _indexSize = c.A == (int)IndexFormat.UInt16 ? 2 : 4;
                    _vertexDirty = true;
                    break;
                case CommandKind.SetViewport:
                {
                    Viewport v = c.GetViewport();
                    GL.ViewportIndexed(0, v.X, v.Y, v.Width, v.Height);
                    GL.DepthRangeIndexed(0, v.MinDepth, v.MaxDepth);
                    break;
                }

                case CommandKind.SetScissor:
                    GL.Scissor(c.A, c.B, (uint)c.C, (uint)c.D);
                    break;
                case CommandKind.Draw:
                    ApplyVertexInput();
                    GL.DrawArraysInstancedBaseInstance(_graphics!.Mode, c.C, (uint)c.A, (uint)c.B, (uint)c.D);
                    break;
                case CommandKind.DrawIndexed:
                    ApplyVertexInput();
                    GL.DrawElementsInstancedBaseVertexBaseInstance(_graphics!.Mode, (uint)c.A, _indexType, (void*)(_indexBuffer!.Offset + _indexOffset + ((long)c.C * _indexSize)), (uint)c.B, (int)c.X, (uint)c.D);
                    break;
                case CommandKind.DrawIndirect:
                {
                    GpuBuffer arguments = commands.GetObject<GpuBuffer>(c.Object);
                    ApplyVertexInput();
                    GL.BindBuffer(GLEnum.DrawIndirectBuffer, ((GLMemoryBlock)arguments.Block).Handle);
                    GL.MultiDrawArraysIndirect(_graphics!.Mode, (void*)(arguments.Offset + c.X), (uint)c.A, (uint)c.B);
                    break;
                }

                case CommandKind.DrawIndexedIndirect:
                {
                    GpuBuffer arguments = commands.GetObject<GpuBuffer>(c.Object);
                    ApplyVertexInput();
                    GL.BindBuffer(GLEnum.DrawIndirectBuffer, ((GLMemoryBlock)arguments.Block).Handle);
                    GL.MultiDrawElementsIndirect(_graphics!.Mode, _indexType, (void*)(arguments.Offset + c.X), (uint)c.A, (uint)c.B);
                    break;
                }

                case CommandKind.Dispatch:
                    GL.DispatchCompute((uint)c.A, (uint)c.B, (uint)c.C);
                    break;
                case CommandKind.DispatchIndirect:
                {
                    GpuBuffer arguments = commands.GetObject<GpuBuffer>(c.Object);
                    GL.BindBuffer(GLEnum.DispatchIndirectBuffer, ((GLMemoryBlock)arguments.Block).Handle);
                    GL.DispatchComputeIndirect((nint)(arguments.Offset + c.X));
                    break;
                }

                case CommandKind.Barrier:
                    GL.MemoryBarrier(GLConvert.Barrier((BarrierScope)c.A));
                    break;
                case CommandKind.UpdateBuffer:
                {
                    GpuBuffer buffer = commands.GetObject<GpuBuffer>(c.Object);
                    WriteBufferCore(buffer.Block, buffer.Offset + c.X, commands.GetData(c.Y, c.A));
                    break;
                }

                case CommandKind.CopyBuffer:
                {
                    GpuBuffer source = commands.GetObject<GpuBuffer>(c.Object);
                    GpuBuffer destination = commands.GetObject<GpuBuffer>(c.Object2);
                    GL.CopyNamedBufferSubData(((GLMemoryBlock)source.Block).Handle, ((GLMemoryBlock)destination.Block).Handle, (nint)(source.Offset + c.X), (nint)(destination.Offset + c.Z), (nuint)c.Y);
                    break;
                }

                case CommandKind.GenerateMipmaps:
                    GL.GenerateTextureMipmap(commands.GetObject<GLTexture>(c.Object).Handle);
                    break;
                case CommandKind.PushDebugGroup:
                {
                    byte[] label = Utf8(commands.GetObject<string>(c.Object));
                    fixed (byte* text = label)
                    {
                        GL.PushDebugGroup(GLEnum.DebugSourceApplication, 0, (uint)label.Length, text);
                    }

                    break;
                }

                case CommandKind.PopDebugGroup:
                    GL.PopDebugGroup();
                    break;
                case CommandKind.InsertDebugMarker:
                {
                    byte[] label = Utf8(commands.GetObject<string>(c.Object));
                    fixed (byte* text = label)
                    {
                        GL.DebugMessageInsert(GLEnum.DebugSourceApplication, GLEnum.DebugTypeMarker, 0, GLEnum.DebugSeverityNotification, (uint)label.Length, text);
                    }

                    break;
                }
            }
        }
    }

    [HotPath]
    protected override void WaitForFrameCore(int slot)
    {
        nint fence = _fences[slot];
        if (fence == 0)
        {
            return;
        }

        while (true)
        {
            GLEnum result = GL.ClientWaitSync(fence, (uint)SyncObjectMask.Bit, FenceTimeout);
            if (result is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied or GLEnum.WaitFailed)
            {
                break;
            }
        }

        GL.DeleteSync(fence);
        _fences[slot] = 0;
    }

    [HotPath]
    protected override void EndFrameCore(int slot, bool present)
    {
        if (present)
        {
            _context.SwapBuffers();
        }

        _fences[slot] = GL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
    }

    /// <summary>The default framebuffer follows the window on its own: only the size known to the device changes.</summary>
    [HotPath]
    protected override void RecreateSwapchainCore(int width, int height)
    {
    }

    [HotPath]
    protected override VSyncMode SetVSyncCore(VSyncMode mode) => ApplyVSync(mode);

    protected override void WaitIdleCore() => GL.Finish();

    protected override void DisposeCore()
    {
        for (int i = 0; i < _fences.Length; i++)
        {
            if (_fences[i] != 0)
            {
                GL.DeleteSync(_fences[i]);
                _fences[i] = 0;
            }
        }

        if (Objects.Count != 0)
        {
            _logger.Warning($"{Objects.Count} OpenGL objects are still alive when the device is destroyed.");
        }

        _context.Dispose();
        GL.Dispose();
    }

    private static GraphicsCapabilities ReadCapabilities(GL gl)
    {
        int Get(GLEnum name)
        {
            gl.GetInteger(name, out int value);
            return value;
        }

        gl.GetFloat(GLEnum.MaxTextureMaxAnisotropy, out float anisotropy);
        int groups = int.MaxValue;
        for (uint axis = 0; axis < 3; axis++)
        {
            gl.GetInteger(GLEnum.MaxComputeWorkGroupCount, axis, out int count);
            groups = Math.Min(groups, count);
        }

        return new GraphicsCapabilities
        {
            // OpenGL 4.6 core has compute shaders, storage buffers, and indirect draws.
            Features = GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect,
            MaxTextureSize = Get(GLEnum.MaxTextureSize),
            MaxTextureArrayLayers = Get(GLEnum.MaxArrayTextureLayers),
            MaxTexture3DSize = Get(GLEnum.Max3DTextureSize),
            MaxAnisotropy = Math.Max(1, anisotropy),
            MaxColorAttachments = Math.Min(Get(GLEnum.MaxColorAttachments), Get(GLEnum.MaxDrawBuffers)),
            UniformBufferAlignment = Get(GLEnum.UniformBufferOffsetAlignment),
            StorageBufferAlignment = Get(GLEnum.ShaderStorageBufferOffsetAlignment),
            MaxUniformBufferRange = Get(GLEnum.MaxUniformBlockSize),
            MaxComputeWorkGroups = groups,
        };
    }

    [HotPath]
    private VSyncMode ApplyVSync(VSyncMode mode)
    {
        switch (mode)
        {
            case VSyncMode.Off:
                _context.SetSwapInterval(0);
                return VSyncMode.Off;
            case VSyncMode.Adaptive when _context.SetSwapInterval(-1):
                return VSyncMode.Adaptive;
            case VSyncMode.Adaptive:
                _logger.Warning("The driver has no adaptive synchronization; vsync is on.");
                _context.SetSwapInterval(1);
                return VSyncMode.On;
            default:
                _context.SetSwapInterval(1);
                return VSyncMode.On;
        }
    }

    [HotPath]
    private byte[] Utf8(string text)
    {
        if (!_labels.TryGetValue(text, out byte[]? bytes))
        {
            bytes = Encoding.UTF8.GetBytes(text);
            _labels[text] = bytes;
        }

        return bytes;
    }

    /// <summary>
    /// Forgets the bindings of the previous command buffer. Its objects may be
    /// destroyed since, and OpenGL reuses their names for new objects, so a
    /// cache keyed on names would skip a needed bind.
    /// </summary>
    [HotPath]
    private void ResetBindings()
    {
        Array.Clear(_vertexBuffers);
        _indexBuffer = null;
        _graphics = null;
        _program = uint.MaxValue;
        _vertexArray = uint.MaxValue;
        _vertexDirty = true;
    }

    [HotPath]
    private void UseProgram(uint program)
    {
        if (_program != program)
        {
            GL.UseProgram(program);
            _program = program;
        }
    }

    [HotPath]
    private void BeginRenderPass(GLFramebuffer framebuffer, in RenderPassDescription pass)
    {
        GL.BindFramebuffer(GLEnum.Framebuffer, framebuffer.Handle);
        if (framebuffer.Srgb)
        {
            GL.Enable(GLEnum.FramebufferSrgb);
        }
        else
        {
            GL.Disable(GLEnum.FramebufferSrgb);
        }

        GL.ViewportIndexed(0, 0, 0, framebuffer.Width, framebuffer.Height);
        GL.DepthRangeIndexed(0, 0, 1);
        GL.Scissor(0, 0, (uint)framebuffer.Width, (uint)framebuffer.Height);

        // Clears obey the write masks and the scissor test: open them, and let the next pipeline set its own.
        GL.Disable(GLEnum.ScissorTest);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(true);
        GL.StencilMask(0xFF);
        _graphics = null;

        if (pass.ColorLoad == LoadAction.Clear)
        {
            ColorF color = pass.ClearColor;
            float* rgba = stackalloc float[4] { color.R, color.G, color.B, color.A };
            int attachments = framebuffer.ColorFormats.Count;
            for (int i = 0; i < attachments; i++)
            {
                GL.ClearNamedFramebuffer(framebuffer.Handle, GLEnum.Color, i, rgba);
            }
        }

        if (pass.DepthLoad == LoadAction.Clear && framebuffer.DepthFormat != PixelFormat.None)
        {
            if (PixelFormats.HasStencil(framebuffer.DepthFormat))
            {
                GL.ClearNamedFramebuffer(framebuffer.Handle, GLEnum.DepthStencil, 0, pass.ClearDepth, pass.ClearStencil);
            }
            else
            {
                float depth = pass.ClearDepth;
                GL.ClearNamedFramebuffer(framebuffer.Handle, GLEnum.Depth, 0, &depth);
            }
        }
    }

    [HotPath]
    private void SetGraphicsPipeline(GLGraphicsPipeline pipeline)
    {
        if (ReferenceEquals(_graphics, pipeline))
        {
            return;
        }

        _graphics = pipeline;
        UseProgram(pipeline.Program);
        if (_vertexArray != pipeline.VertexArray)
        {
            GL.BindVertexArray(pipeline.VertexArray);
            _vertexArray = pipeline.VertexArray;
        }

        _vertexDirty = true;
        RasterizerState r = pipeline.Rasterizer;
        if (r.CullMode == CullMode.None)
        {
            GL.Disable(GLEnum.CullFace);
        }
        else
        {
            GL.Enable(GLEnum.CullFace);
            GL.CullFace(r.CullMode == CullMode.Front ? GLEnum.Front : GLEnum.Back);
        }

        GL.FrontFace(r.FrontFace == FrontFace.Clockwise ? GLEnum.CW : GLEnum.Ccw);
        GL.PolygonMode(GLEnum.FrontAndBack, r.FillMode == FillMode.Wireframe ? GLEnum.Line : GLEnum.Fill);
        Toggle(GLEnum.ScissorTest, r.ScissorTest);
        Toggle(GLEnum.DepthClamp, r.DepthClamp);

        DepthState d = pipeline.Depth;
        Toggle(GLEnum.DepthTest, d.TestEnabled || d.WriteEnabled);
        GL.DepthFunc(d.TestEnabled ? pipeline.DepthFunc : GLEnum.Always);
        GL.DepthMask(d.WriteEnabled);

        BlendState b = pipeline.Blend;
        Toggle(GLEnum.Blend, b.Enabled);
        if (b.Enabled)
        {
            GL.BlendFuncSeparate(GLConvert.Blend(b.SourceColor), GLConvert.Blend(b.DestinationColor), GLConvert.Blend(b.SourceAlpha), GLConvert.Blend(b.DestinationAlpha));
            GL.BlendEquationSeparate(GLConvert.Blend(b.ColorOperation), GLConvert.Blend(b.AlphaOperation));
        }

        ColorWriteMask mask = b.WriteMask;
        GL.ColorMask((mask & ColorWriteMask.Red) != 0, (mask & ColorWriteMask.Green) != 0, (mask & ColorWriteMask.Blue) != 0, (mask & ColorWriteMask.Alpha) != 0);
    }

    [HotPath]
    private void Toggle(GLEnum capability, bool enabled)
    {
        if (enabled)
        {
            GL.Enable(capability);
        }
        else
        {
            GL.Disable(capability);
        }
    }

    [HotPath]
    private void ApplyVertexInput()
    {
        if (!_vertexDirty)
        {
            return;
        }

        _vertexDirty = false;
        GLGraphicsPipeline pipeline = _graphics!;
        uint[] strides = pipeline.Strides;
        for (int slot = 0; slot < strides.Length; slot++)
        {
            GpuBuffer? buffer = _vertexBuffers[slot];
            if (buffer is not null)
            {
                GL.VertexArrayVertexBuffer(pipeline.VertexArray, (uint)slot, ((GLMemoryBlock)buffer.Block).Handle, (nint)(buffer.Offset + _vertexOffsets[slot]), strides[slot]);
            }
        }

        if (_indexBuffer is not null)
        {
            GL.VertexArrayElementBuffer(pipeline.VertexArray, ((GLMemoryBlock)_indexBuffer.Block).Handle);
        }
    }

    [HotPath]
    private void BindSet(ResourceSet set)
    {
        ReadOnlySpan<BoundResource> resources = set.ResourceSpan;
        IReadOnlyList<ResourceLayoutEntry> entries = set.Layout.Entries;
        for (int i = 0; i < resources.Length; i++)
        {
            ResourceLayoutEntry entry = entries[i];
            ref readonly BoundResource resource = ref resources[i];
            uint binding = (uint)entry.Binding;
            switch (entry.Kind)
            {
                case ResourceKind.UniformBuffer or ResourceKind.ReadOnlyStorageBuffer or ResourceKind.ReadWriteStorageBuffer:
                {
                    GpuBuffer buffer = resource.Buffer!;
                    long size = resource.Size == 0 ? buffer.Size - resource.Offset : resource.Size;
                    GLEnum target = entry.Kind == ResourceKind.UniformBuffer ? GLEnum.UniformBuffer : GLEnum.ShaderStorageBuffer;
                    GL.BindBufferRange(target, binding, ((GLMemoryBlock)buffer.Block).Handle, (nint)(buffer.Offset + resource.Offset), (nuint)size);
                    break;
                }

                case ResourceKind.SampledTexture:
                    GL.BindTextureUnit(binding, ((GLTexture)resource.Texture!).Handle);
                    GL.BindSampler(binding, ((GLSampler)resource.Sampler!).Handle);
                    break;
                default:
                {
                    var texture = (GLTexture)resource.Texture!;
                    GL.BindImageTexture(binding, texture.Handle, 0, texture.Dimension != TextureDimension.Texture2D, 0, GLEnum.ReadWrite, texture.GLFormat.Internal);
                    break;
                }
            }
        }
    }
}
