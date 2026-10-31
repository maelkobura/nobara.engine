// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core.Configuration;
using Nobara.Frames;
using Nobara.Graphics.OpenGL.Tests.Support;

namespace Nobara.Graphics.OpenGL.Tests;

/// <summary>
/// The OpenGL backend on a real driver. These tests open a window and need
/// an OpenGL 4.6 driver that loads SPIR-V: they are in the category
/// <c>GPU</c>, which the public CI does not run.
/// </summary>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLDeviceTests : IDisposable
{
    private readonly GLHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Creates_an_OpenGL_device_on_the_window()
    {
        GraphicsDevice device = _harness.Device;
        Assert.Equal("opengl", _harness.Game.Context.Resolve<IGraphicsSystem>().BackendName);
        Assert.Equal("opengl", device.Info.Backend);
        Assert.Contains("4.6", device.Info.Version);
        Assert.True(device.Capabilities.Supports(GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect));
        Assert.False(device.Capabilities.Supports(GraphicsFeatures.RayTracing));
        Assert.Equal(2, device.FramesInFlight);
        Assert.Equal(VSyncMode.Off, device.Swapchain.VSync);
        WindowSize pixels = _harness.Windows.MainWindow.PixelSize;
        Assert.Equal(pixels.Width, device.Swapchain.Width);
        Assert.Equal(pixels.Height, device.Swapchain.Height);
    }

    [Fact]
    public void Draws_a_textured_triangle_off_screen_with_SPIR_V_shaders_and_reads_the_pixels_back()
    {
        Scenes.TexturedTriangle(_harness.Device);
    }

    [Fact]
    public void Runs_indirect_draws_and_dispatches_with_arguments_inside_a_shared_block()
    {
        Scenes.Indirect(_harness.Device);
    }

    [Fact]
    public void Refuses_a_SPIR_V_module_the_driver_cannot_load()
    {
        byte[] code = SpirV.Compile(ShaderStages.Vertex, "#version 450\nvoid main() { gl_Position = vec4(0.0); }");
        ShaderLoadException refused = Assert.Throws<ShaderLoadException>(() =>
            _harness.Device.CreateShader(new ShaderDescription(ShaderStages.Vertex, code, "missing") { Name = "wrong entry" }));
        Assert.Contains("missing", refused.Message);
        Assert.Equal(0, _harness.Device.LiveObjectCount);
    }

    [Fact]
    public void Resizes_and_recreated_surfaces_create_the_swapchain_again_without_leaking_GPU_objects()
    {
        GraphicsDevice device = _harness.Device;
        GLDevice gl = _harness.GL;
        CommandBuffer commands = device.CreateCommandBuffer("present");

        void Frame()
        {
            _harness.Engine.MainContext.RunFrame(); // nFrames pumps the events of the window
            if (device.BeginFrame())
            {
                commands.Begin();
                commands.BeginRenderPass(device.Swapchain.Framebuffer, new RenderPassDescription { ClearColor = new ColorF(0.2f, 0.4f, 0.6f, 1) });
                commands.EndRenderPass();
                commands.End();
                device.Submit(commands);
            }

            device.EndFrame();
        }

        Frame();
        int live = device.LiveObjectCount;
        int native = gl.Objects.Count;
        int recreations = device.Swapchain.Recreations;
        ModuleSettings settings = _harness.Game.Context.Settings;

        foreach (string mode in new[] { "borderless", "windowed", "borderless", "windowed" })
        {
            settings.Set(FramesSettings.Mode, mode);
            Frame(); // the display changes at the start of this frame
            Frame(); // the swapchain follows at the start of the next one
            WindowSize pixels = _harness.Windows.MainWindow.PixelSize;
            Assert.Equal(pixels.Width, device.Swapchain.Width);
            Assert.Equal(pixels.Height, device.Swapchain.Height);
        }

        Assert.True(device.Swapchain.Recreations >= recreations + 4, $"{device.Swapchain.Recreations - recreations} recreations after 4 changes of size.");

        // Desktop platforms do not lose their surface; the events go through the same path as on mobile.
        device.Swapchain.SurfaceLost();
        Frame();
        Assert.False(device.Swapchain.IsAvailable);
        WindowSize size = _harness.Windows.MainWindow.PixelSize;
        device.Swapchain.SurfaceRecreated(size.Width, size.Height);
        Frame();
        Assert.True(device.Swapchain.IsAvailable);

        device.WaitIdle();
        Assert.Equal(live, device.LiveObjectCount);
        Assert.Equal(native, gl.Objects.Count);
    }

    [Fact]
    public void Disposed_resources_free_their_OpenGL_objects()
    {
        GraphicsDevice device = _harness.Device;
        int native = _harness.GL.Objects.Count;
        Scenes.TexturedTriangle(device);
        Assert.Equal(0, device.LiveObjectCount);
        Assert.Equal(native + 1, _harness.GL.Objects.Count); // the shared block of buffer memory stays
    }

    [Fact]
    public void A_frame_of_recording_and_submission_allocates_no_managed_memory()
    {
        GraphicsDevice device = _harness.Device;
        Shader vertex = SpirV.Load(device, ShaderStages.Vertex, """
            #version 450
            layout(location = 0) in vec2 inPosition;
            layout(std140, binding = 0) uniform Offset { vec4 offset; };
            void main() { gl_Position = vec4(inPosition + offset.xy, 0.0, 1.0); }
            """, "offset.vert");
        Shader fragment = SpirV.Load(device, ShaderStages.Fragment, """
            #version 450
            layout(location = 0) out vec4 color;
            void main() { color = vec4(1.0); }
            """, "white.frag");
        ResourceLayout layout = device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        using var offsets = new PerFrame<GpuBuffer>(device, _ => device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform)));
        using var sets = new PerFrame<ResourceSet>(device, slot => device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(offsets[slot])])));
        GraphicsPipeline pipeline = device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            VertexShader = vertex,
            FragmentShader = fragment,
            VertexBuffers = [new VertexBufferLayout(8, [new(0, VertexFormat.Float2, 0)])],
            ResourceLayouts = [layout],
            Outputs = new([device.Swapchain.ColorFormat], device.Swapchain.DepthFormat),
            Depth = DepthState.LessEqual,
            Name = "allocation test",
        });
        GpuBuffer vertices = device.CreateBuffer(new BufferDescription(24, BufferUsage.Vertex));
        device.UpdateBuffer(vertices, 0, MemoryMarshal.AsBytes<float>([-0.1f, -0.1f, 0.1f, -0.1f, 0, 0.1f]));
        GpuBuffer indices = device.CreateBuffer(new BufferDescription(12, BufferUsage.Index));
        device.UpdateBuffer(indices, 0, MemoryMarshal.AsBytes<uint>([0, 1, 2]));
        CommandBuffer commands = device.CreateCommandBuffer("frame");
        Span<float> offset = stackalloc float[4];

        void Frame(Span<float> data)
        {
            bool visible = device.BeginFrame();
            device.UpdateBuffer(offsets.Current, 0, MemoryMarshal.AsBytes(data));
            commands.Begin();
            commands.PushDebugGroup("frame");
            if (visible)
            {
                commands.BeginRenderPass(device.Swapchain.Framebuffer, new RenderPassDescription());
                commands.SetPipeline(pipeline);
                commands.SetResourceSet(0, sets.Current);
                commands.SetVertexBuffer(0, vertices);
                commands.SetIndexBuffer(indices, IndexFormat.UInt32);
                for (int i = 0; i < 50; i++)
                {
                    commands.DrawIndexed(3);
                }

                commands.EndRenderPass();
            }

            commands.InsertDebugMarker("end");
            commands.PopDebugGroup();
            commands.End();
            device.Submit(commands);
            device.EndFrame();
        }

        for (int i = 0; i < 10; i++)
        {
            Frame(offset);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            offset[0] = i / 200f;
            Frame(offset);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        foreach (IDisposable resource in new IDisposable[] { pipeline, vertex, fragment, layout, vertices, indices })
        {
            resource.Dispose();
        }
    }
}
