// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>The device checks every description before the backend sees it.</summary>
public sealed class ValidationTests
{
    private readonly FakeDevice _device = new();

    [Fact]
    public void Refuses_buffers_without_size_or_usage_and_usages_the_device_lacks()
    {
        Assert.Throws<ArgumentException>(() => _device.CreateBuffer(new BufferDescription(0, BufferUsage.Vertex)));
        Assert.Throws<ArgumentException>(() => _device.CreateBuffer(new BufferDescription(64, BufferUsage.None)));

        var basic = new FakeDevice(GraphicsFeatures.None);
        NotSupportedException storage = Assert.Throws<NotSupportedException>(() => basic.CreateBuffer(new BufferDescription(64, BufferUsage.Storage) { Name = "particles" }));
        Assert.Contains("particles", storage.Message);
        Assert.Contains("StorageBuffers", storage.Message);
        Assert.Throws<NotSupportedException>(() => basic.CreateBuffer(new BufferDescription(64, BufferUsage.Indirect)));
        Assert.Equal(0, basic.LiveObjectCount);
    }

    [Theory]
    [InlineData(TextureDimension.Texture2D, PixelFormat.Rgba8Unorm, 64, 64, 2, 1, TextureUsage.Sampled)] // 2D with layers
    [InlineData(TextureDimension.Texture2D, PixelFormat.Rgba8Unorm, 64, 64, 1, 8, TextureUsage.Sampled)] // more mips than the size has
    [InlineData(TextureDimension.Texture2D, PixelFormat.Rgba8Unorm, 8192, 64, 1, 1, TextureUsage.Sampled)] // over the limit
    [InlineData(TextureDimension.Texture2DArray, PixelFormat.Rgba8Unorm, 16, 16, 512, 1, TextureUsage.Sampled)] // too many layers
    [InlineData(TextureDimension.Texture3D, PixelFormat.R8Unorm, 16, 16, 1024, 1, TextureUsage.Sampled)] // 3D over the limit
    [InlineData(TextureDimension.Texture2D, PixelFormat.Rgba8Unorm, 16, 16, 1, 1, TextureUsage.DepthStencil)] // depth usage, color format
    [InlineData(TextureDimension.Texture2D, PixelFormat.Depth32Float, 16, 16, 1, 1, TextureUsage.RenderTarget)] // color usage, depth format
    [InlineData(TextureDimension.Texture2D, PixelFormat.Rgba8UnormSrgb, 16, 16, 1, 1, TextureUsage.Storage)] // sRGB storage
    [InlineData(TextureDimension.Texture2D, PixelFormat.None, 16, 16, 1, 1, TextureUsage.Sampled)] // no format
    public void Refuses_invalid_textures(TextureDimension dimension, PixelFormat format, int width, int height, int depth, int mips, TextureUsage usage)
    {
        Assert.Throws<ArgumentException>(() => _device.CreateTexture(new TextureDescription(dimension, format, width, height, depth, mips, usage)));
        Assert.Equal(0, _device.LiveObjectCount);
    }

    [Fact]
    public void Accepts_arrays_3D_textures_and_full_mip_chains()
    {
        _device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, PixelFormat.Rgba8UnormSrgb, 16, 16, 256, 5, TextureUsage.Sampled));
        _device.CreateTexture(new TextureDescription(TextureDimension.Texture3D, PixelFormat.R8Unorm, 32, 32, 64, 7, TextureUsage.Sampled | TextureUsage.Storage));
        Assert.Equal(2, _device.LiveObjectCount);
    }

    [Fact]
    public void Refuses_shaders_that_are_not_one_stage_of_SPIR_V()
    {
        Assert.Throws<ArgumentException>(() => _device.CreateShader(new ShaderDescription(ShaderStages.Vertex | ShaderStages.Fragment, Make.SpirV())));
        Assert.Throws<ArgumentException>(() => _device.CreateShader(new ShaderDescription(ShaderStages.Vertex, new byte[20])));
        Assert.Throws<ArgumentException>(() => _device.CreateShader(new ShaderDescription(ShaderStages.Vertex, Make.SpirV().AsMemory(0, 18))));
        Assert.Throws<NotSupportedException>(() => new FakeDevice(GraphicsFeatures.None).CreateShader(new ShaderDescription(ShaderStages.Compute, Make.SpirV())));
    }

    [Fact]
    public void Refuses_layouts_that_reuse_a_binding_of_one_class()
    {
        ArgumentException duplicate = Assert.Throws<ArgumentException>(() => _device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
            new(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Fragment),
        ])));
        Assert.Contains("binding 0", duplicate.Message);

        // Each class of resource has its own numbers.
        _device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new(0, ResourceKind.SampledTexture, ShaderStages.Fragment),
            new(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Fragment),
        ]));

        Assert.Throws<NotSupportedException>(() => new FakeDevice(GraphicsFeatures.None).CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Fragment)])));
    }

    [Fact]
    public void Checks_each_resource_of_a_set_against_its_entry()
    {
        ResourceLayout layout = _device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        GpuBuffer vertices = _device.CreateBuffer(new BufferDescription(512, BufferUsage.Vertex));
        GpuBuffer constants = _device.CreateBuffer(new BufferDescription(512, BufferUsage.Uniform));

        Assert.Throws<ArgumentException>(() => _device.CreateResourceSet(new ResourceSetDescription(layout, [])));
        Assert.Throws<ArgumentException>(() => _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(vertices)])));
        Assert.Throws<ArgumentException>(() => _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(constants, offset: 16)])));
        Assert.Throws<ArgumentException>(() => _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(constants, offset: 256, size: 512)])));

        ResourceSet set = _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(constants, offset: 256)]));
        Assert.Same(layout, set.Layout);
    }

    [Fact]
    public void Refuses_pipelines_whose_states_contradict_their_outputs_or_layouts()
    {
        Shader vertex = Make.Shader(_device, ShaderStages.Vertex);
        Shader fragment = Make.Shader(_device, ShaderStages.Fragment);
        var valid = new GraphicsPipelineDescription { VertexShader = vertex, FragmentShader = fragment, Outputs = new([PixelFormat.Rgba8Unorm]) };

        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with { VertexShader = fragment }));
        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with { Depth = DepthState.LessEqual }));
        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with { Outputs = new([PixelFormat.Depth32Float]) }));
        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with
        {
            VertexBuffers = [new VertexBufferLayout(8, [new(0, VertexFormat.Float3, 0)])],
        }));
        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with
        {
            VertexBuffers = [new VertexBufferLayout(12, [new(0, VertexFormat.Float1, 0)]), new VertexBufferLayout(4, [new(0, VertexFormat.Float1, 0)])],
        }));

        ResourceLayout a = _device.CreateResourceLayout(new ResourceLayoutDescription([new(1, ResourceKind.SampledTexture, ShaderStages.Fragment)]));
        ResourceLayout b = _device.CreateResourceLayout(new ResourceLayoutDescription([new(1, ResourceKind.SampledTexture, ShaderStages.Fragment)]));
        Assert.Throws<ArgumentException>(() => _device.CreateGraphicsPipeline(valid with { ResourceLayouts = [a, b] }));

        GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(valid with { ResourceLayouts = [a], Depth = DepthState.LessEqual, Outputs = Make.SwapchainOutputs });
        Assert.Single(pipeline.ResourceLayouts);
    }

    [Fact]
    public void Refuses_compute_pipelines_on_a_device_without_compute()
    {
        var basic = new FakeDevice(GraphicsFeatures.None);
        Shader vertex = Make.Shader(basic, ShaderStages.Vertex);
        Assert.Throws<NotSupportedException>(() => basic.CreateComputePipeline(new ComputePipelineDescription(vertex, [])));
        Assert.Throws<ArgumentException>(() => _device.CreateComputePipeline(new ComputePipelineDescription(Make.Shader(_device, ShaderStages.Vertex), [])));
        Assert.NotNull(_device.CreateComputePipeline(new ComputePipelineDescription(Make.Shader(_device, ShaderStages.Compute), [])));
    }

    [Fact]
    public void Refuses_framebuffers_with_wrong_usages_or_different_sizes()
    {
        Texture color = _device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, 64, 64, TextureUsage.RenderTarget | TextureUsage.Sampled));
        Texture small = _device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, 32, 32, TextureUsage.DepthStencil));
        Texture sampled = _device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, 64, 64, TextureUsage.Sampled));

        Assert.Throws<ArgumentException>(() => _device.CreateFramebuffer(new FramebufferDescription([])));
        Assert.Throws<ArgumentException>(() => _device.CreateFramebuffer(new FramebufferDescription([new(sampled)])));
        Assert.Throws<ArgumentException>(() => _device.CreateFramebuffer(new FramebufferDescription([new(color)], new FramebufferAttachment(small))));
        Assert.Throws<ArgumentException>(() => _device.CreateFramebuffer(new FramebufferDescription([new(color, MipLevel: 1)])));

        Framebuffer framebuffer = _device.CreateFramebuffer(new FramebufferDescription([new(color)]));
        Assert.Equal(64, framebuffer.Width);
        Assert.Equal([PixelFormat.Rgba8Unorm], framebuffer.ColorFormats);
    }

    [Fact]
    public void Checks_uploads_and_read_backs_against_the_resource()
    {
        GpuBuffer buffer = _device.CreateBuffer(new BufferDescription(16, BufferUsage.Uniform));
        Assert.Throws<ArgumentOutOfRangeException>(() => _device.UpdateBuffer(buffer, 8, new byte[16]));
        _device.UpdateBuffer(buffer, 4, [1, 2, 3, 4]);
        var read = new byte[4];
        _device.ReadBuffer(buffer, 4, read);
        Assert.Equal([1, 2, 3, 4], read);

        Texture texture = _device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, PixelFormat.Rgba8Unorm, 4, 4, 2, 1, TextureUsage.Sampled) { Name = "atlas" });
        Assert.Throws<ArgumentException>(() => _device.UpdateTexture(texture, new TextureRegion(0, 0, 0, 1, 4, 4, 2), new byte[128]));
        Assert.Throws<ArgumentException>(() => _device.UpdateTexture(texture, new TextureRegion(0, 0, 0, 1, 4, 4, 1), new byte[63]));
        _device.UpdateTexture(texture, new TextureRegion(0, 0, 0, 1, 4, 4, 1), new byte[64]);
        Assert.Contains("write texture atlas", _device.Calls);
    }

    [Fact]
    public void Capabilities_name_the_missing_features()
    {
        GraphicsCapabilities caps = FakeDevice.MakeCapabilities(GraphicsFeatures.Compute);
        Assert.True(caps.Supports(GraphicsFeatures.Compute));
        Assert.False(caps.Supports(GraphicsFeatures.Compute | GraphicsFeatures.RayTracing));
        NotSupportedException missing = Assert.Throws<NotSupportedException>(() => caps.Require(GraphicsFeatures.RayTracing | GraphicsFeatures.DrawIndirect, "Pass 'reflections'"));
        Assert.Contains("Pass 'reflections'", missing.Message);
        Assert.Contains("RayTracing", missing.Message);
    }
}
