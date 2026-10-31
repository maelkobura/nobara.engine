// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Silk.NET.OpenGL;

namespace Nobara.Graphics.OpenGL;

/// <summary>A block of buffer memory: one immutable buffer object.</summary>
internal sealed unsafe class GLMemoryBlock : GpuMemoryBlock
{
    private readonly GLDevice _device;

    public GLMemoryBlock(GLDevice device, long size, string name)
        : base(device, size, name)
    {
        _device = device;
        GL gl = device.GL;
        gl.CreateBuffers(1, out uint handle);
        Handle = handle;
        gl.NamedBufferStorage(handle, (nuint)size, null, BufferStorageMask.DynamicStorageBit);
        gl.ClearNamedBufferData(handle, GLEnum.R8ui, GLEnum.RedInteger, GLEnum.UnsignedByte, null);
        device.Objects.Created();
        device.Label(GLEnum.Buffer, handle, name);
    }

    public uint Handle { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        uint handle = Handle;
        _device.GL.DeleteBuffers(1, &handle);
        _device.Objects.Deleted();
    }
}

/// <summary>A texture object with immutable storage.</summary>
internal sealed unsafe class GLTexture : Texture
{
    private readonly GLDevice _device;

    public GLTexture(GLDevice device, in TextureDescription description)
        : base(device, description)
    {
        _device = device;
        GL gl = device.GL;
        Target = description.Dimension switch
        {
            TextureDimension.Texture2DArray => GLEnum.Texture2DArray,
            TextureDimension.Texture3D => GLEnum.Texture3D,
            _ => GLEnum.Texture2D,
        };
        GLFormat = GLConvert.Format(description.Format);
        gl.CreateTextures(Target, 1, out uint handle);
        Handle = handle;
        if (description.Dimension == TextureDimension.Texture2D)
        {
            gl.TextureStorage2D(handle, (uint)description.MipLevels, GLFormat.Internal, (uint)description.Width, (uint)description.Height);
        }
        else
        {
            gl.TextureStorage3D(handle, (uint)description.MipLevels, GLFormat.Internal, (uint)description.Width, (uint)description.Height, (uint)description.DepthOrLayers);
        }

        device.Objects.Created();
        device.Label(GLEnum.Texture, handle, description.Name);
    }

    public uint Handle { get; }

    public GLEnum Target { get; }

    public GLFormat GLFormat { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        uint handle = Handle;
        _device.GL.DeleteTextures(1, &handle);
        _device.Objects.Deleted();
    }
}

/// <summary>A sampler object.</summary>
internal sealed unsafe class GLSampler : Sampler
{
    private readonly GLDevice _device;

    public GLSampler(GLDevice device, in SamplerDescription d)
        : base(device, d)
    {
        _device = device;
        GL gl = device.GL;
        gl.CreateSamplers(1, out uint handle);
        Handle = handle;
        gl.SamplerParameter(handle, GLEnum.TextureMinFilter, (int)GLConvert.MinFilter(d.MinFilter, d.MipFilter));
        gl.SamplerParameter(handle, GLEnum.TextureMagFilter, (int)GLConvert.MagFilter(d.MagFilter));
        gl.SamplerParameter(handle, GLEnum.TextureWrapS, (int)GLConvert.Address(d.AddressU));
        gl.SamplerParameter(handle, GLEnum.TextureWrapT, (int)GLConvert.Address(d.AddressV));
        gl.SamplerParameter(handle, GLEnum.TextureWrapR, (int)GLConvert.Address(d.AddressW));
        gl.SamplerParameter(handle, GLEnum.TextureMinLod, d.MinLod);
        gl.SamplerParameter(handle, GLEnum.TextureMaxLod, d.MaxLod);
        gl.SamplerParameter(handle, GLEnum.TextureMaxAnisotropy, d.MaxAnisotropy);
        if (d.Compare is { } compare)
        {
            gl.SamplerParameter(handle, GLEnum.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
            gl.SamplerParameter(handle, GLEnum.TextureCompareFunc, (int)GLConvert.Compare(compare));
        }

        device.Objects.Created();
        device.Label(GLEnum.Sampler, handle, d.Name);
    }

    public uint Handle { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        _device.GL.DeleteSampler(Handle);
        _device.Objects.Deleted();
    }
}

/// <summary>A shader object loaded from SPIR-V and specialized at its entry point.</summary>
internal sealed unsafe class GLShader : Shader
{
    private const GLEnum SpirVBinaryFormat = (GLEnum)0x9551;

    private readonly GLDevice _device;

    public GLShader(GLDevice device, in ShaderDescription description)
        : base(device, description)
    {
        _device = device;
        GL gl = device.GL;
        GLEnum type = description.Stage switch
        {
            ShaderStages.Vertex => GLEnum.VertexShader,
            ShaderStages.Fragment => GLEnum.FragmentShader,
            _ => GLEnum.ComputeShader,
        };
        Handle = gl.CreateShader(type);
        device.Objects.Created();
        uint handle = Handle;
        fixed (byte* code = description.SpirV.Span)
        {
            gl.ShaderBinary(1, &handle, SpirVBinaryFormat, code, (uint)description.SpirV.Length);
        }

        while (gl.GetError() != GLEnum.NoError)
        {
            // Errors of earlier calls are not this shader's.
        }

        gl.SpecializeShader(handle, description.EntryPoint, 0, (uint*)null, (uint*)null);
        GLEnum error = gl.GetError();
        gl.GetShader(handle, GLEnum.CompileStatus, out int status);
        if (status == 0 || error != GLEnum.NoError)
        {
            // An entry point that the module does not have is an invalid value, not a failed compilation.
            string log = error == GLEnum.InvalidValue ? "the module has no such entry point, or its SPIR-V is invalid" : gl.GetShaderInfoLog(handle);
            gl.DeleteShader(handle);
            device.Objects.Deleted();
            throw new ShaderLoadException($"The driver refused {description.Stage} shader '{description.Name}' at entry point '{description.EntryPoint}': {log.Trim()}");
        }

        device.Label(GLEnum.Shader, handle, description.Name);
    }

    public uint Handle { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        _device.GL.DeleteShader(Handle);
        _device.Objects.Deleted();
    }

    /// <summary>Links shaders into a program object.</summary>
    /// <exception cref="ShaderLoadException">The link failed.</exception>
    public static uint Link(GLDevice device, string? name, GLShader first, GLShader? second)
    {
        GL gl = device.GL;
        uint program = gl.CreateProgram();
        device.Objects.Created();
        gl.AttachShader(program, first.Handle);
        if (second is not null)
        {
            gl.AttachShader(program, second.Handle);
        }

        gl.LinkProgram(program);
        gl.DetachShader(program, first.Handle);
        if (second is not null)
        {
            gl.DetachShader(program, second.Handle);
        }

        gl.GetProgram(program, GLEnum.LinkStatus, out int status);
        if (status == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            device.Objects.Deleted();
            throw new ShaderLoadException($"The driver cannot link pipeline '{name}': {log.Trim()}");
        }

        device.Label(GLEnum.Program, program, name);
        return program;
    }
}

/// <summary>A graphics pipeline: a program object, a vertex array object for its vertex layout, and its fixed state.</summary>
internal sealed unsafe class GLGraphicsPipeline : GraphicsPipeline
{
    private readonly GLDevice _device;

    public GLGraphicsPipeline(GLDevice device, in GraphicsPipelineDescription d)
        : base(device, d)
    {
        _device = device;
        GL gl = device.GL;
        Program = GLShader.Link(device, d.Name, (GLShader)d.VertexShader, (GLShader?)d.FragmentShader);
        gl.CreateVertexArrays(1, out uint vao);
        VertexArray = vao;
        device.Objects.Created();
        Strides = new uint[d.VertexBuffers.Count];
        for (int slot = 0; slot < d.VertexBuffers.Count; slot++)
        {
            VertexBufferLayout layout = d.VertexBuffers[slot];
            Strides[slot] = (uint)layout.Stride;
            foreach (VertexAttribute attribute in layout.Attributes)
            {
                (int size, GLEnum type, bool normalized, bool integer) = GLConvert.Vertex(attribute.Format);
                uint location = (uint)attribute.Location;
                gl.EnableVertexArrayAttrib(vao, location);
                if (integer)
                {
                    gl.VertexArrayAttribIFormat(vao, location, size, type, (uint)attribute.Offset);
                }
                else
                {
                    gl.VertexArrayAttribFormat(vao, location, size, type, normalized, (uint)attribute.Offset);
                }

                gl.VertexArrayAttribBinding(vao, location, (uint)slot);
            }

            gl.VertexArrayBindingDivisor(vao, (uint)slot, layout.StepMode == VertexStepMode.Instance ? 1u : 0u);
        }

        device.Label(GLEnum.VertexArray, vao, d.Name);
        Mode = GLConvert.Topology(d.Topology);
        Rasterizer = d.Rasterizer;
        Depth = d.Depth;
        Blend = d.Blend;
        DepthFunc = GLConvert.Compare(d.Depth.Compare);
    }

    public uint Program { get; }

    public uint VertexArray { get; }

    /// <summary>Gets the stride of each vertex buffer slot.</summary>
    public uint[] Strides { get; }

    public GLEnum Mode { get; }

    public RasterizerState Rasterizer { get; }

    public DepthState Depth { get; }

    public BlendState Blend { get; }

    public GLEnum DepthFunc { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        GL gl = _device.GL;
        gl.DeleteProgram(Program);
        uint vao = VertexArray;
        gl.DeleteVertexArrays(1, &vao);
        _device.Objects.Deleted();
        _device.Objects.Deleted();
    }
}

/// <summary>A compute pipeline: a program object.</summary>
internal sealed class GLComputePipeline : ComputePipeline
{
    private readonly GLDevice _device;

    public GLComputePipeline(GLDevice device, in ComputePipelineDescription description)
        : base(device, description)
    {
        _device = device;
        Program = GLShader.Link(device, description.Name, (GLShader)description.Shader, null);
    }

    public uint Program { get; }

    [HotPath]
    protected override void OnDestroy()
    {
        _device.GL.DeleteProgram(Program);
        _device.Objects.Deleted();
    }
}

/// <summary>A framebuffer object on textures, or the default framebuffer of the window.</summary>
internal sealed unsafe class GLFramebuffer : Framebuffer
{
    private readonly GLDevice _device;

    public GLFramebuffer(GLDevice device, in FramebufferDescription d, int width, int height)
        : base(device, d.Name, width, height, Formats(d), d.Depth is { } depth ? depth.Texture.Format : PixelFormat.None, isSwapchain: false)
    {
        _device = device;
        GL gl = device.GL;
        gl.CreateFramebuffers(1, out uint handle);
        Handle = handle;
        device.Objects.Created();
        GLEnum* buffers = stackalloc GLEnum[Math.Max(1, d.Colors.Count)];
        for (int i = 0; i < d.Colors.Count; i++)
        {
            Attach(gl, handle, GLEnum.ColorAttachment0 + i, d.Colors[i]);
            buffers[i] = GLEnum.ColorAttachment0 + i;
        }

        if (d.Depth is { } depthAttachment)
        {
            GLEnum point = PixelFormats.HasStencil(depthAttachment.Texture.Format) ? GLEnum.DepthStencilAttachment : GLEnum.DepthAttachment;
            Attach(gl, handle, point, depthAttachment);
        }

        if (d.Colors.Count == 0)
        {
            buffers[0] = GLEnum.None;
            gl.NamedFramebufferDrawBuffers(handle, 1, buffers);
        }
        else
        {
            gl.NamedFramebufferDrawBuffers(handle, (uint)d.Colors.Count, buffers);
        }

        GLEnum status = gl.CheckNamedFramebufferStatus(handle, GLEnum.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            gl.DeleteFramebuffer(handle);
            device.Objects.Deleted();
            throw new NotSupportedException($"The driver cannot draw to framebuffer '{d.Name}' with these attachments: {status}.");
        }

        device.Label(GLEnum.Framebuffer, handle, d.Name);
    }

    /// <summary>Initializes the default framebuffer of the window.</summary>
    public GLFramebuffer(GLDevice device, int width, int height)
        : base(device, "swapchain", width, height, [PixelFormat.Rgba8Unorm], PixelFormat.Depth24Stencil8, isSwapchain: true)
    {
        _device = device;
        Handle = 0;
    }

    public uint Handle { get; }

    /// <summary>Gets whether a color attachment is sRGB encoded, so the device converts writes to it.</summary>
    public bool Srgb
    {
        [HotPath]
        get => !IsSwapchain && ColorFormats.Count > 0 && ColorFormats[0] == PixelFormat.Rgba8UnormSrgb;
    }

    [HotPath]
    protected override void OnDestroy()
    {
        if (Handle != 0)
        {
            _device.GL.DeleteFramebuffer(Handle);
            _device.Objects.Deleted();
        }
    }

    private static PixelFormat[] Formats(in FramebufferDescription d)
    {
        var formats = new PixelFormat[d.Colors.Count];
        for (int i = 0; i < formats.Length; i++)
        {
            formats[i] = d.Colors[i].Texture.Format;
        }

        return formats;
    }

    private static void Attach(GL gl, uint framebuffer, GLEnum point, in FramebufferAttachment attachment)
    {
        var texture = (GLTexture)attachment.Texture;
        if (texture.Dimension == TextureDimension.Texture2D)
        {
            gl.NamedFramebufferTexture(framebuffer, point, texture.Handle, attachment.MipLevel);
        }
        else
        {
            gl.NamedFramebufferTextureLayer(framebuffer, point, texture.Handle, attachment.MipLevel, attachment.Layer);
        }
    }
}

/// <summary>Counts the OpenGL objects alive, to find leaks.</summary>
internal sealed class GLObjectCounter
{
    public int Count { get; private set; }

    public void Created() => Count++;

    [HotPath]
    public void Deleted() => Count--;
}
