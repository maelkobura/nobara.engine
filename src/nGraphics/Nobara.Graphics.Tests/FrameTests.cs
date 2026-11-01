// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>Frames in flight, deferred destruction, and the swapchain.</summary>
public sealed class FrameTests
{
    [Fact]
    public void Each_frame_waits_for_the_frame_that_used_its_slot()
    {
        var device = new FakeDevice(framesInFlight: 2);
        for (int i = 0; i < 4; i++)
        {
            device.BeginFrame();
            device.EndFrame();
        }

        // Frames 1 and 2 have nothing to wait for; frame 3 waits for frame 1 (slot 1), frame 4 for frame 2 (slot 0).
        Assert.Equal(["end 1 present", "end 0 present", "wait 1", "end 1 present", "wait 0", "end 0 present"], device.Calls);
        Assert.Equal(4ul, device.FrameNumber);
        Assert.Equal(2ul, device.CompletedFrame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_number_of_frames_in_flight_sets_the_slots(int frames)
    {
        var device = new FakeDevice(framesInFlight: frames);
        var slots = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            device.BeginFrame();
            slots.Add(device.FrameIndex);
            device.EndFrame();
        }

        Assert.Equal(Enumerable.Range(1, 6).Select(n => n % frames), slots);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeDevice(framesInFlight: 4));
    }

    [Fact]
    public void Frames_and_submissions_follow_their_order()
    {
        var device = new FakeDevice();
        CommandBuffer commands = device.CreateCommandBuffer();
        commands.Begin();
        commands.End();
        Assert.Throws<InvalidOperationException>(() => device.Submit(commands));
        Assert.Throws<InvalidOperationException>(device.EndFrame);

        device.BeginFrame();
        Assert.Throws<InvalidOperationException>(() => device.BeginFrame());
        Assert.Throws<InvalidOperationException>(device.WaitIdle);
        device.Submit(commands);
        Assert.Throws<InvalidOperationException>(() => device.Submit(commands));
        device.EndFrame();
    }

    [Fact]
    public void A_disposed_resource_lives_until_the_frames_that_may_use_it_are_finished()
    {
        var device = new FakeDevice(framesInFlight: 2);
        Texture early = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.R8Unorm, 4, 4, TextureUsage.Sampled) with { Name = "early" });
        early.Dispose();
        Assert.True(early.IsDestroyed); // no frame ever ran

        Texture texture = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.R8Unorm, 4, 4, TextureUsage.Sampled) with { Name = "used" });
        device.BeginFrame(); // frame 1 uses the texture
        texture.Dispose();
        Assert.True(texture.IsDisposed);
        Assert.False(texture.IsDestroyed);
        Assert.Equal(1, device.LiveObjectCount);
        device.EndFrame();

        device.BeginFrame(); // frame 2: frame 1 may still run
        device.EndFrame();
        Assert.False(texture.IsDestroyed);

        device.BeginFrame(); // frame 3 waits for frame 1
        Assert.True(texture.IsDestroyed);
        Assert.Equal(0, device.LiveObjectCount);
        device.EndFrame();
        Assert.Equal(["early", "used"], device.Destroyed);
    }

    [Fact]
    public void WaitIdle_and_Dispose_destroy_what_is_left()
    {
        var device = new FakeDevice();
        device.BeginFrame();
        device.EndFrame();
        Sampler sampler = device.CreateSampler(new SamplerDescription { Name = "linear" });
        sampler.Dispose();
        Assert.False(sampler.IsDestroyed);
        device.WaitIdle();
        Assert.True(sampler.IsDestroyed);

        device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        device.CreateTexture(TextureDescription.Texture2D(PixelFormat.R8Unorm, 4, 4, TextureUsage.Sampled));
        Assert.Equal(2, device.LiveObjectCount);
        Assert.Equal(2, device.NativeObjects); // a texture and the block of the buffer
        device.Dispose();
        Assert.Equal(0, device.LiveObjectCount);
        Assert.Equal(0, device.NativeObjects);
        Assert.Equal("dispose", device.Calls[^1]);
        Assert.Throws<ObjectDisposedException>(() => device.BeginFrame());
    }

    [Fact]
    public void A_resize_creates_the_swapchain_again_at_the_next_frame()
    {
        var device = new FakeDevice();
        Swapchain swapchain = device.Swapchain;
        swapchain.Resized(800, 600);
        swapchain.Resized(1024, 768);
        Assert.True(swapchain.IsOutOfDate);
        Assert.Equal(1280, swapchain.Width);

        device.BeginFrame();
        device.EndFrame();
        Assert.Equal(["recreate 1024x768", "end 1 present"], device.Calls);
        Assert.Equal(1024, swapchain.Width);
        Assert.Equal(768, swapchain.Framebuffer.Height);
        Assert.Equal(1, swapchain.Recreations);

        // The same size again changes nothing.
        swapchain.Resized(1024, 768);
        Assert.False(swapchain.IsOutOfDate);
    }

    [Fact]
    public void A_lost_surface_stops_drawing_until_it_is_recreated_through_the_resize_path()
    {
        var device = new FakeDevice();
        Swapchain swapchain = device.Swapchain;
        swapchain.SurfaceLost();
        Assert.False(device.BeginFrame());
        CommandBuffer commands = device.CreateCommandBuffer();
        commands.Begin();
        Assert.Throws<InvalidOperationException>(() => commands.BeginRenderPass(swapchain.Framebuffer, new RenderPassDescription()));
        device.EndFrame();

        swapchain.SurfaceRecreated(640, 480);
        Assert.True(device.BeginFrame());
        device.EndFrame();
        Assert.Equal(["release swapchain", "end 1", "recreate 640x480", "end 0 present"], device.Calls);
    }

    [Fact]
    public void A_minimized_window_is_not_presented()
    {
        var device = new FakeDevice();
        device.Swapchain.SetMinimized(true);
        Assert.False(device.BeginFrame());
        device.EndFrame();
        device.Swapchain.SetMinimized(false);
        Assert.True(device.BeginFrame());
        device.EndFrame();
        Assert.Equal(["end 1", "end 0 present"], device.Calls);
    }

    [Fact]
    public void A_new_vsync_applies_at_the_next_frame_after_the_fallback_of_the_driver()
    {
        var device = new FakeDevice();
        device.SetVSync(VSyncMode.Adaptive);
        Assert.Equal(VSyncMode.On, device.Swapchain.VSync);
        Assert.Empty(device.Calls);
        device.BeginFrame();
        Assert.Equal("vsync Adaptive", device.Calls[0]);
        Assert.Equal(VSyncMode.On, device.Swapchain.VSync);
        device.EndFrame();

        device.SetVSync(VSyncMode.Off);
        device.BeginFrame();
        Assert.Equal(VSyncMode.Off, device.Swapchain.VSync);
        device.EndFrame();
    }

    [Fact]
    public void PerFrame_gives_the_copy_of_the_current_slot()
    {
        var device = new FakeDevice(framesInFlight: 2);
        using var constants = new PerFrame<GpuBuffer>(device, slot => device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform) { Name = $"constants {slot}" }));
        Assert.Equal(2, constants.Count);
        device.BeginFrame();
        Assert.Same(constants[1], constants.Current);
        device.EndFrame();
        device.BeginFrame();
        Assert.Same(constants[0], constants.Current);
        device.EndFrame();
    }
}
