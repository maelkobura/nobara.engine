// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>How presenting a frame waits for the monitor.</summary>
public enum VSyncMode
{
    /// <summary>No wait: frames show as soon as they are ready, and may tear.</summary>
    Off,

    /// <summary>Each frame waits for the vertical blank of the monitor.</summary>
    On,

    /// <summary>Waits for the vertical blank, unless the frame is late: then it shows at once. Falls back to <see cref="On"/> when the driver lacks it.</summary>
    Adaptive,
}

/// <summary>
/// The images shown in the window: a back buffer drawn through
/// <see cref="Framebuffer"/>, presented by <see cref="GraphicsDevice.EndFrame"/>.
/// </summary>
/// <remarks>
/// The device creates the swapchain again when the window changes size or
/// gets its surface back after losing it; both go through the same code, at
/// the start of the next frame. While the window is minimized or its surface
/// is lost, <see cref="IsAvailable"/> is <see langword="false"/> and nothing
/// can be drawn to it.
/// </remarks>
public sealed class Swapchain
{
    private readonly GraphicsDevice _device;
    private int _wantedWidth;
    private int _wantedHeight;

    internal Swapchain(GraphicsDevice device, Framebuffer framebuffer, VSyncMode vsync)
    {
        _device = device;
        Framebuffer = framebuffer;
        VSync = vsync;
        _wantedWidth = framebuffer.Width;
        _wantedHeight = framebuffer.Height;
    }

    /// <summary>Gets the framebuffer of the back buffer, for <see cref="CommandBuffer.BeginRenderPass"/>.</summary>
    public Framebuffer Framebuffer { get; }

    /// <summary>Gets the width of the back buffer, in pixels.</summary>
    public int Width => Framebuffer.Width;

    /// <summary>Gets the height of the back buffer, in pixels.</summary>
    public int Height => Framebuffer.Height;

    /// <summary>Gets the format of the back buffer.</summary>
    public PixelFormat ColorFormat => Framebuffer.ColorFormatArray[0];

    /// <summary>Gets the format of the depth buffer of the back buffer, or <see cref="PixelFormat.None"/>.</summary>
    public PixelFormat DepthFormat => Framebuffer.DepthFormat;

    /// <summary>Gets the synchronization in use, after the fallback of the driver.</summary>
    public VSyncMode VSync { get; internal set; }

    /// <summary>Gets how many times the swapchain was created again, after a resize or a recreated surface.</summary>
    public int Recreations { get; private set; }

    /// <summary>Gets whether the window is minimized.</summary>
    public bool IsMinimized { get; private set; }

    /// <summary>Gets whether the surface of the window is lost, until the operating system gives it back.</summary>
    public bool IsSurfaceLost { get; private set; }

    /// <summary>Gets whether the back buffer can be drawn and presented this frame.</summary>
    public bool IsAvailable
    {
        [HotPath]
        get => !IsMinimized && !IsSurfaceLost && Framebuffer.Width > 0 && Framebuffer.Height > 0;
    }

    /// <summary>Gets whether the swapchain waits to be created again at the start of the next frame.</summary>
    public bool IsOutOfDate { get; private set; }

    /// <summary>Reports a new size of the window in pixels: the swapchain is created again at the start of the next frame.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    internal void Resized(int width, int height)
    {
        if (width == _wantedWidth && height == _wantedHeight && !IsOutOfDate)
        {
            return;
        }

        _wantedWidth = width;
        _wantedHeight = height;
        IsOutOfDate = true;
    }

    /// <summary>Reports that the window was minimized or restored.</summary>
    /// <param name="minimized"><see langword="true"/> when minimized.</param>
    internal void SetMinimized(bool minimized) => IsMinimized = minimized;

    /// <summary>Reports that the surface of the window was taken away: the device frees what depends on it.</summary>
    internal void SurfaceLost()
    {
        if (!IsSurfaceLost)
        {
            IsSurfaceLost = true;
            _device.ReleaseSwapchain();
        }
    }

    /// <summary>Reports that the surface is back: the swapchain is created again, as after a resize.</summary>
    /// <param name="width">The width of the window in pixels.</param>
    /// <param name="height">The height.</param>
    internal void SurfaceRecreated(int width, int height)
    {
        IsSurfaceLost = false;
        _wantedWidth = width;
        _wantedHeight = height;
        IsOutOfDate = true;
    }

    /// <summary>Creates the swapchain again if it is out of date; called at the start of a frame.</summary>
    [HotPath]
    internal void Update()
    {
        if (!IsOutOfDate || IsSurfaceLost)
        {
            return;
        }

        IsOutOfDate = false;
        _device.RecreateSwapchain(_wantedWidth, _wantedHeight);
        Framebuffer.Width = _wantedWidth;
        Framebuffer.Height = _wantedHeight;
        Recreations++;
    }
}
