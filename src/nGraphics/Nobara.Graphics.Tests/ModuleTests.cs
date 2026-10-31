// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>The module: the choice of the backend, the configuration keys, and the link with the window.</summary>
public sealed class ModuleTests : IDisposable
{
    private readonly GraphicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Creates_the_device_when_it_starts_with_the_defaults_of_its_keys()
    {
        using Engine engine = _harness.Start();
        IGraphicsSystem graphics = _harness.Game.Context.Resolve<IGraphicsSystem>();
        Assert.True(graphics.IsCreated);
        Assert.Equal("fake", graphics.BackendName);
        Assert.Same(_harness.Backends[0].Device, graphics.Device);
        Assert.Equal(2, _harness.Backends[0].Options.FramesInFlight);
        Assert.Equal(VSyncMode.On, _harness.Backends[0].Options.VSync);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Graphics device created by backend 'fake': Fake GPU"));
    }

    [Fact]
    public void Uses_the_backend_the_key_names_and_falls_back_with_a_warning()
    {
        _harness.Backends.Add(new FakeGraphicsBackend("other"));
        _harness.Setting(GraphicsSettings.Backend, "OTHER").Setting(GraphicsSettings.FramesInFlight, "3").Setting(GraphicsSettings.VSync, "off");
        using (Engine engine = _harness.Start())
        {
            Assert.Equal("other", _harness.Game.Context.Resolve<IGraphicsSystem>().BackendName);
            Assert.Equal(3, _harness.Backends[1].Options.FramesInFlight);
            Assert.Equal(VSyncMode.Off, _harness.Backends[1].Options.VSync);
            Assert.Null(_harness.Backends[0].Device);
        }

        using var second = new GraphicsHarness();
        second.Setting(GraphicsSettings.Backend, "vulkan");
        using Engine fallback = second.Start();
        Assert.Equal("fake", second.Game.Context.Resolve<IGraphicsSystem>().BackendName);
        Assert.Contains(second.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'vulkan'"));
    }

    [Fact]
    public void Follows_the_window_size_and_minimized_state()
    {
        using Engine engine = _harness.Start();
        FakeDevice device = _harness.Backends[0].Device!;
        _harness.Windows.Queue(WindowEventKind.Resized, w => w.PixelSize = new WindowSize(1600, 900));
        engine.MainContext.RunFrame(); // nFrames pumps the event at the start of the frame

        Assert.True(device.Swapchain.IsOutOfDate);
        device.BeginFrame();
        device.EndFrame();
        Assert.Equal(1600, device.Swapchain.Width);
        Assert.Contains("recreate 1600x900", device.Calls);

        _harness.Windows.Queue(WindowEventKind.Minimized, w => w.IsMinimized = true);
        engine.MainContext.RunFrame();
        Assert.False(device.BeginFrame());
        device.EndFrame();
    }

    [Fact]
    public void Applies_a_new_vsync_at_the_next_frame()
    {
        using Engine engine = _harness.Start();
        FakeDevice device = _harness.Backends[0].Device!;
        _harness.Game.Context.Settings.Set(GraphicsSettings.VSync, "adaptive");
        engine.MainContext.RunFrame(); // the change reaches nGraphics at the synchronization point
        device.BeginFrame();
        device.EndFrame();
        Assert.Contains("vsync Adaptive", device.Calls);
    }

    [Fact]
    public void Releases_the_device_and_reports_resources_left_behind()
    {
        FakeDevice device;
        using (Engine engine = _harness.Start())
        {
            device = _harness.Backends[0].Device!;
            device.CreateTexture(TextureDescription.Texture2D(PixelFormat.R8Unorm, 4, 4, TextureUsage.Sampled) with { Name = "forgotten" });
        }

        Assert.Equal("dispose", device.Calls[^1]);
        Assert.Equal(0, device.NativeObjects);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Texture 'forgotten'"));
    }
}
