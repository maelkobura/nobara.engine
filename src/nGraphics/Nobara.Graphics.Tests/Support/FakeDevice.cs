// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Graphics.Tests.Support;

/// <summary>
/// A device without a GPU: it records what the neutral layer asks of the
/// backend, so tests check the validation, the lifetime of objects, and the
/// pacing of frames.
/// </summary>
internal sealed class FakeDevice : GraphicsDevice
{
    public FakeDevice(GraphicsFeatures features = GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect, int framesInFlight = 2, long blockSize = 1024 * 1024)
        : base(new GraphicsDeviceInfo("fake", "Example", "Fake GPU", "1.0"), MakeCapabilities(features), framesInFlight, blockSize)
    {
        AttachSwapchain(new FakeFramebuffer(this, 1280, 720, [PixelFormat.Rgba8Unorm], PixelFormat.Depth24Stencil8, swapchain: true), VSyncMode.On);
    }

    /// <summary>Gets what the backend was asked, in order: <c>wait 1</c>, <c>end 0 present</c>, <c>recreate 800x600</c>, and so on.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Gets the kinds of the commands executed.</summary>
    public List<CommandKind> Executed { get; } = [];

    /// <summary>Gets the names of the GPU objects destroyed, in order.</summary>
    public List<string> Destroyed { get; } = [];

    /// <summary>Gets the number of GPU objects alive, memory blocks included.</summary>
    public int NativeObjects { get; set; }

    /// <summary>Gets or sets whether <see cref="Calls"/> and <see cref="Executed"/> record, so allocation tests can turn them off.</summary>
    public bool Recording { get; set; } = true;

    /// <summary>Gets the bytes written to buffers, by block.</summary>
    public Dictionary<GpuMemoryBlock, byte[]> Contents { get; } = [];

    public static GraphicsCapabilities MakeCapabilities(GraphicsFeatures features) => new()
    {
        Features = features,
        MaxTextureSize = 4096,
        MaxTextureArrayLayers = 256,
        MaxTexture3DSize = 512,
        MaxAnisotropy = 16,
        MaxColorAttachments = 4,
        UniformBufferAlignment = 256,
        StorageBufferAlignment = 16,
        MaxUniformBufferRange = 65536,
        MaxComputeWorkGroups = (features & GraphicsFeatures.Compute) != 0 ? 65535 : 0,
    };

    public void Log(string call)
    {
        if (Recording)
        {
            Calls.Add(call);
        }
    }

    protected override GpuMemoryBlock CreateMemoryBlockCore(long size, string name)
    {
        var block = new FakeBlock(this, size, name);
        Contents[block] = new byte[size];
        return block;
    }

    protected override Texture CreateTextureCore(in TextureDescription description) => new FakeTexture(this, description);

    protected override Sampler CreateSamplerCore(in SamplerDescription description) => new FakeSampler(this, description);

    protected override Shader CreateShaderCore(in ShaderDescription description) => new FakeShader(this, description);

    protected override GraphicsPipeline CreateGraphicsPipelineCore(in GraphicsPipelineDescription description) => new FakeGraphicsPipeline(this, description);

    protected override ComputePipeline CreateComputePipelineCore(in ComputePipelineDescription description) => new FakeComputePipeline(this, description);

    protected override Framebuffer CreateFramebufferCore(in FramebufferDescription description)
    {
        FramebufferAttachment first = description.Colors.Count > 0 ? description.Colors[0] : description.Depth!.Value;
        return new FakeFramebuffer(
            this,
            first.Texture.Width >> first.MipLevel,
            first.Texture.Height >> first.MipLevel,
            description.Colors.Select(c => c.Texture.Format).ToArray(),
            description.Depth?.Texture.Format ?? PixelFormat.None,
            swapchain: false);
    }

    protected override void WriteBufferCore(GpuMemoryBlock block, long offset, ReadOnlySpan<byte> data) => data.CopyTo(Contents[block].AsSpan((int)offset));

    protected override void WriteTextureCore(Texture texture, in TextureRegion region, ReadOnlySpan<byte> data) => Log($"write texture {texture.Name}");

    protected override void ReadBufferCore(GpuMemoryBlock block, long offset, Span<byte> destination) => Contents[block].AsSpan((int)offset, destination.Length).CopyTo(destination);

    protected override void ReadTextureCore(Texture texture, in TextureRegion region, Span<byte> destination) => destination.Clear();

    protected override void ExecuteCore(CommandBuffer commands)
    {
        foreach (Command command in commands.Commands)
        {
            if (Recording)
            {
                Executed.Add(command.Kind);
            }

            if (command.Kind == CommandKind.UpdateBuffer)
            {
                GpuBuffer buffer = commands.GetObject<GpuBuffer>(command.Object);
                WriteBufferCore(buffer.Block, buffer.Offset + command.X, commands.GetData(command.Y, command.A));
            }
        }
    }

    protected override void WaitForFrameCore(int slot)
    {
        if (Recording)
        {
            Log($"wait {slot}");
        }
    }

    protected override void EndFrameCore(int slot, bool present)
    {
        if (Recording)
        {
            Log(present ? $"end {slot} present" : $"end {slot}");
        }
    }

    protected override void RecreateSwapchainCore(int width, int height) => Log($"recreate {width}x{height}");

    protected override void ReleaseSwapchainCore() => Log("release swapchain");

    protected override VSyncMode SetVSyncCore(VSyncMode mode)
    {
        Log($"vsync {mode}");
        return mode == VSyncMode.Adaptive ? VSyncMode.On : mode;
    }

    protected override void WaitIdleCore() => Log("idle");

    protected override void DisposeCore() => Log("dispose");

    internal void Created() => NativeObjects++;

    internal void Gone(GraphicsResource resource)
    {
        NativeObjects--;
        Destroyed.Add(resource.Name ?? resource.GetType().Name);
    }
}

internal sealed class FakeBlock : GpuMemoryBlock
{
    private readonly FakeDevice _device;

    public FakeBlock(FakeDevice device, long size, string name)
        : base(device, size, name)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeTexture : Texture
{
    private readonly FakeDevice _device;

    public FakeTexture(FakeDevice device, in TextureDescription description)
        : base(device, description)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeSampler : Sampler
{
    private readonly FakeDevice _device;

    public FakeSampler(FakeDevice device, in SamplerDescription description)
        : base(device, description)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeShader : Shader
{
    private readonly FakeDevice _device;

    public FakeShader(FakeDevice device, in ShaderDescription description)
        : base(device, description)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeGraphicsPipeline : GraphicsPipeline
{
    private readonly FakeDevice _device;

    public FakeGraphicsPipeline(FakeDevice device, in GraphicsPipelineDescription description)
        : base(device, description)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeComputePipeline : ComputePipeline
{
    private readonly FakeDevice _device;

    public FakeComputePipeline(FakeDevice device, in ComputePipelineDescription description)
        : base(device, description)
    {
        _device = device;
        device.Created();
    }

    protected override void OnDestroy() => _device.Gone(this);
}

internal sealed class FakeFramebuffer : Framebuffer
{
    private readonly FakeDevice _device;

    public FakeFramebuffer(FakeDevice device, int width, int height, PixelFormat[] colors, PixelFormat depth, bool swapchain)
        : base(device, swapchain ? "swapchain" : null, width, height, colors, depth, swapchain)
    {
        _device = device;
        if (!swapchain)
        {
            device.Created();
        }
    }

    protected override void OnDestroy()
    {
        if (!IsSwapchain)
        {
            _device.Gone(this);
        }
    }
}

/// <summary>Builds valid descriptions and SPIR-V headers for the tests.</summary>
internal static class Make
{
    /// <summary>Gets the smallest bytes that pass the SPIR-V header check.</summary>
    public static byte[] SpirV()
    {
        var code = new byte[20];
        BitConverter.GetBytes(0x07230203u).CopyTo(code, 0);
        return code;
    }

    public static Shader Shader(GraphicsDevice device, ShaderStages stage) => device.CreateShader(new ShaderDescription(stage, SpirV()) { Name = stage.ToString() });

    public static GraphicsPipeline Pipeline(GraphicsDevice device, OutputDescription outputs, params ResourceLayout[] layouts) => device.CreateGraphicsPipeline(new GraphicsPipelineDescription
    {
        VertexShader = Shader(device, ShaderStages.Vertex),
        FragmentShader = Shader(device, ShaderStages.Fragment),
        ResourceLayouts = layouts,
        Outputs = outputs,
        Name = "pipeline",
    });

    public static OutputDescription SwapchainOutputs => new([PixelFormat.Rgba8Unorm], PixelFormat.Depth24Stencil8);
}
