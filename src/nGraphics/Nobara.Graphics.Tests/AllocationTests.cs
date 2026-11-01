// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>Recording and submitting a frame allocates no managed memory once the buffers have grown.</summary>
public sealed class AllocationTests
{
    [Fact]
    public void A_frame_of_recording_and_submission_allocates_nothing()
    {
        var device = new FakeDevice { Recording = false };
        ResourceLayout layout = device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        GraphicsPipeline pipeline = Make.Pipeline(device, Make.SwapchainOutputs, layout);
        using var constants = new PerFrame<GpuBuffer>(device, _ => device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform)));
        var sets = new PerFrame<ResourceSet>(device, slot => device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(constants[slot])])));
        GpuBuffer vertices = device.CreateBuffer(new BufferDescription(4096, BufferUsage.Vertex));
        GpuBuffer indices = device.CreateBuffer(new BufferDescription(4096, BufferUsage.Index));
        CommandBuffer commands = device.CreateCommandBuffer();
        Span<byte> data = stackalloc byte[64];

        void Frame(Span<byte> bytes)
        {
            device.BeginFrame();
            device.UpdateBuffer(constants.Current, 0, bytes);
            commands.Begin();
            commands.PushDebugGroup("frame");
            commands.UpdateBuffer(vertices, 0, bytes);
            commands.BeginRenderPass(device.Swapchain.Framebuffer, new RenderPassDescription());
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, sets.Current);
            commands.SetVertexBuffer(0, vertices);
            commands.SetIndexBuffer(indices, IndexFormat.UInt32);
            commands.SetViewport(new Viewport(0, 0, 1280, 720));
            commands.SetScissor(new PixelRect(0, 0, 1280, 720));
            for (int i = 0; i < 100; i++)
            {
                commands.DrawIndexed(36, firstIndex: i * 36);
            }

            commands.EndRenderPass();
            commands.PopDebugGroup();
            commands.End();
            device.Submit(commands);
            device.EndFrame();
        }

        for (int i = 0; i < 4; i++)
        {
            Frame(data);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Frame(data);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
