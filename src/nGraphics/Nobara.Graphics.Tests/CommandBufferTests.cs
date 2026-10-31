// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>Recording checks each command at once; the backend replays what was recorded.</summary>
public sealed class CommandBufferTests
{
    private readonly FakeDevice _device = new();
    private readonly CommandBuffer _commands;
    private readonly ResourceLayout _layout;
    private readonly GraphicsPipeline _pipeline;
    private readonly ResourceSet _set;

    public CommandBufferTests()
    {
        _commands = _device.CreateCommandBuffer("main");
        _layout = _device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        _pipeline = Make.Pipeline(_device, Make.SwapchainOutputs, _layout);
        GpuBuffer constants = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform));
        _set = _device.CreateResourceSet(new ResourceSetDescription(_layout, [BoundResource.Of(constants)]));
    }

    [Fact]
    public void Records_a_frame_and_the_backend_replays_it_in_order()
    {
        GpuBuffer vertices = _device.CreateBuffer(new BufferDescription(1024, BufferUsage.Vertex));
        GpuBuffer indices = _device.CreateBuffer(new BufferDescription(1024, BufferUsage.Index));
        _device.BeginFrame();
        _commands.Begin();
        _commands.PushDebugGroup("opaque");
        _commands.UpdateBuffer(vertices, 0, [1, 2, 3, 4]);
        _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription { ClearColor = new ColorF(0.1f, 0.2f, 0.3f, 1) });
        _commands.SetPipeline(_pipeline);
        _commands.SetResourceSet(0, _set);
        _commands.SetVertexBuffer(0, vertices);
        _commands.SetIndexBuffer(indices, IndexFormat.UInt16);
        _commands.SetViewport(new Viewport(0, 0, 640, 360, 0.25f, 0.75f));
        _commands.DrawIndexed(36);
        _commands.EndRenderPass();
        _commands.PopDebugGroup();
        _commands.End();

        Command viewport = _commands.Commands.ToArray().Single(c => c.Kind == CommandKind.SetViewport);
        Assert.Equal(new Viewport(0, 0, 640, 360, 0.25f, 0.75f), viewport.GetViewport());
        Assert.Equal(new ColorF(0.1f, 0.2f, 0.3f, 1), _commands.GetRenderPass(0).ClearColor);

        _device.Submit(_commands);
        _device.EndFrame();
        Assert.Equal(
            [
                CommandKind.PushDebugGroup, CommandKind.UpdateBuffer, CommandKind.BeginRenderPass, CommandKind.SetGraphicsPipeline,
                CommandKind.SetResourceSet, CommandKind.SetVertexBuffer, CommandKind.SetIndexBuffer, CommandKind.SetViewport,
                CommandKind.DrawIndexed, CommandKind.EndRenderPass, CommandKind.PopDebugGroup,
            ],
            _device.Executed);

        var read = new byte[4];
        _device.ReadBuffer(vertices, 0, read);
        Assert.Equal([1, 2, 3, 4], read);
        Assert.False(_commands.IsReady);
    }

    [Fact]
    public void Refuses_commands_outside_their_place()
    {
        Assert.Throws<InvalidOperationException>(() => _commands.Draw(3));
        _commands.Begin();
        Assert.Throws<InvalidOperationException>(() => _commands.Begin());
        Assert.Throws<InvalidOperationException>(() => _commands.Draw(3));
        Assert.Throws<InvalidOperationException>(() => _commands.SetPipeline(_pipeline));
        Assert.Throws<InvalidOperationException>(_commands.EndRenderPass);

        _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription());
        Assert.Throws<InvalidOperationException>(() => _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription()));
        Assert.Throws<InvalidOperationException>(() => _commands.Draw(3)); // no pipeline
        _commands.SetPipeline(_pipeline);
        Assert.Throws<InvalidOperationException>(() => _commands.Draw(3)); // no resource set
        _commands.SetResourceSet(0, _set);
        Assert.Throws<InvalidOperationException>(() => _commands.DrawIndexed(3)); // no index buffer
        _commands.Draw(3);
        Assert.Throws<InvalidOperationException>(() => _commands.Dispatch(1));
        Assert.Throws<InvalidOperationException>(() => _commands.Barrier(BarrierScope.All));
        Assert.Throws<InvalidOperationException>(_commands.End);
        _commands.EndRenderPass();

        _commands.PushDebugGroup("open");
        Assert.Throws<InvalidOperationException>(_commands.End);
        _commands.PopDebugGroup();
        Assert.Throws<InvalidOperationException>(_commands.PopDebugGroup);
        _commands.End();
    }

    [Fact]
    public void Refuses_a_pipeline_whose_outputs_differ_from_the_framebuffer()
    {
        GraphicsPipeline hdr = Make.Pipeline(_device, new OutputDescription([PixelFormat.Rgba16Float]));
        _commands.Begin();
        _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription());
        InvalidOperationException mismatch = Assert.Throws<InvalidOperationException>(() => _commands.SetPipeline(hdr));
        Assert.Contains("swapchain", mismatch.Message);
    }

    [Fact]
    public void Refuses_a_set_of_another_layout_and_disposed_or_foreign_resources()
    {
        ResourceLayout other = _device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        GpuBuffer constants = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform));
        ResourceSet otherSet = _device.CreateResourceSet(new ResourceSetDescription(other, [BoundResource.Of(constants)]));
        GpuBuffer vertices = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        GpuBuffer foreign = new FakeDevice().CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));

        _commands.Begin();
        _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription());
        _commands.SetPipeline(_pipeline);
        Assert.Throws<InvalidOperationException>(() => _commands.SetResourceSet(0, otherSet));
        Assert.Throws<InvalidOperationException>(() => _commands.SetResourceSet(1, _set));
        Assert.Throws<InvalidOperationException>(() => _commands.SetVertexBuffer(0, constants));
        Assert.Throws<InvalidOperationException>(() => _commands.SetVertexBuffer(0, foreign));
        vertices.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _commands.SetVertexBuffer(0, vertices));
    }

    [Fact]
    public void Indirect_commands_need_the_feature_and_an_indirect_buffer()
    {
        var basic = new FakeDevice(GraphicsFeatures.None);
        GraphicsPipeline pipeline = Make.Pipeline(basic, Make.SwapchainOutputs);
        GpuBuffer plain = basic.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        CommandBuffer commands = basic.CreateCommandBuffer();
        commands.Begin();
        commands.BeginRenderPass(basic.Swapchain.Framebuffer, new RenderPassDescription());
        commands.SetPipeline(pipeline);
        Assert.Throws<NotSupportedException>(() => commands.DrawIndirect(plain, 0, 1));

        GraphicsPipeline full = Make.Pipeline(_device, Make.SwapchainOutputs);
        GpuBuffer arguments = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Indirect | BufferUsage.Storage));
        GpuBuffer vertices = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        _commands.Begin();
        _commands.BeginRenderPass(_device.Swapchain.Framebuffer, new RenderPassDescription());
        _commands.SetPipeline(full);
        Assert.Throws<InvalidOperationException>(() => _commands.DrawIndirect(vertices, 0, 1));
        Assert.Throws<InvalidOperationException>(() => _commands.DrawIndirect(arguments, 2, 1));
        _commands.DrawIndirect(arguments, 0, 2, 16);

        // Indexed indirect draws read indices from the start of a block: a shared range is refused.
        GpuBuffer shared = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Index));
        GpuBuffer dedicated = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Index) { Dedicated = true });
        Assert.NotEqual(0, shared.Offset);
        _commands.SetIndexBuffer(shared, IndexFormat.UInt32);
        Assert.Throws<InvalidOperationException>(() => _commands.DrawIndexedIndirect(arguments, 0, 1));
        _commands.SetIndexBuffer(dedicated, IndexFormat.UInt32, offset: 4);
        Assert.Throws<InvalidOperationException>(() => _commands.DrawIndexedIndirect(arguments, 0, 1));
        _commands.SetIndexBuffer(dedicated, IndexFormat.UInt32);
        _commands.DrawIndexedIndirect(arguments, 0, 1);
        Assert.Equal(0, dedicated.Offset);
    }

    [Fact]
    public void Dispatches_check_the_compute_pipeline_and_the_group_counts()
    {
        ResourceLayout layout = _device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute)]));
        ComputePipeline pipeline = _device.CreateComputePipeline(new ComputePipelineDescription(Make.Shader(_device, ShaderStages.Compute), [layout]));
        GpuBuffer data = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Storage));
        ResourceSet set = _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(data)]));

        _commands.Begin();
        Assert.Throws<InvalidOperationException>(() => _commands.Dispatch(1));
        _commands.SetPipeline(pipeline);
        Assert.Throws<InvalidOperationException>(() => _commands.Dispatch(1));
        _commands.SetResourceSet(0, set);
        Assert.Throws<ArgumentOutOfRangeException>(() => _commands.Dispatch(70000));
        _commands.Dispatch(4);
        _commands.Barrier(BarrierScope.StorageBuffer | BarrierScope.Transfer);
        _commands.End();
    }
}
