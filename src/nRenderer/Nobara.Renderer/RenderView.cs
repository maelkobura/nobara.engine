// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>A rectangle in a fraction of the size of a target, from its bottom-left corner.</summary>
/// <param name="X">The left edge, from 0 to 1.</param>
/// <param name="Y">The bottom edge, from 0 to 1.</param>
/// <param name="Width">The width, from 0 to 1.</param>
/// <param name="Height">The height, from 0 to 1.</param>
public readonly record struct ViewRect(float X, float Y, float Width, float Height)
{
    /// <summary>Gets the whole target.</summary>
    public static ViewRect Full => new(0, 0, 1, 1);
}

/// <summary>
/// What nRenderer draws a frame of: a camera, a target (the screen or a
/// texture), a viewport in that target, and the pipeline that draws it.
/// </summary>
/// <remarks>
/// <para>
/// nRenderer has one view, <see cref="IRenderer.MainView"/>, drawn to the
/// screen by default. Its size follows the size in pixels of the window; while
/// the window is minimized, nothing is drawn.
/// </para>
/// <para>
/// The pipeline may draw at a resolution other than the target:
/// <see cref="RenderScale"/> sets the render resolution
/// (<see cref="RenderWidth"/>, <see cref="RenderHeight"/>) as a fraction of
/// the viewport, for upscaling later.
/// </para>
/// <para>
/// The camera, like the render world, changes during extraction only. The
/// other settings change on the loop thread at any time, and apply at the next
/// frame. The members that describe the frame (matrices, items, sizes) are
/// valid while the render graph runs.
/// </para>
/// </remarks>
public sealed class RenderView
{
    private readonly RenderWorld _world;
    private RenderItem[] _items = new RenderItem[64];
    private int _itemCount;
    private Camera _previous = new();
    private Camera _current = new();
    private ulong _movedTick = ulong.MaxValue;
    private float _renderScale = 1;
    private ViewRect _viewport = ViewRect.Full;

    internal RenderView(string name, RenderWorld world)
    {
        Name = name;
        _world = world;
    }

    /// <summary>Gets the name of the view, for messages and capture tools.</summary>
    public string Name { get; }

    /// <summary>Gets the last camera given to the view.</summary>
    public Camera Camera => _current;

    /// <summary>Gets the texture the view draws to, or <see langword="null"/> when it draws to the screen.</summary>
    public Texture? TargetTexture { get; private set; }

    /// <summary>Gets the depth texture of a texture target, or <see langword="null"/>.</summary>
    public Texture? TargetDepthTexture { get; private set; }

    /// <summary>Gets or sets the part of the target the view covers; the whole target by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or leaves the target.</exception>
    public ViewRect Viewport
    {
        get => _viewport;
        set
        {
            if (!(value.Width > 0 && value.Height > 0 && value.X >= 0 && value.Y >= 0 && value.X + value.Width <= 1 && value.Y + value.Height <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"A viewport is a non-empty rectangle inside the target, from 0 to 1; got {value}.");
            }

            _viewport = value;
        }
    }

    /// <summary>Gets or sets the name of the pipeline that draws the view, or <see langword="null"/> for the first pipeline registered.</summary>
    public string? Pipeline { get; set; }

    /// <summary>Gets or sets the render resolution as a fraction of the viewport, from 0.1 to 4; 1 by default. The pipeline sets it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0.1 to 4.</exception>
    public float RenderScale
    {
        get => _renderScale;
        set
        {
            if (!(value >= 0.1f && value <= 4f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"A render scale is from 0.1 to 4; got {value}.");
            }

            _renderScale = value;
        }
    }

    /// <summary>Gets the width of the target, in pixels, for the frame.</summary>
    public int TargetWidth { get; private set; }

    /// <summary>Gets the height of the target, in pixels, for the frame.</summary>
    public int TargetHeight { get; private set; }

    /// <summary>Gets the viewport in pixels of the target, from its bottom-left corner, for the frame.</summary>
    public PixelRect PixelViewport { get; private set; }

    /// <summary>Gets the width of the render resolution, in pixels, for the frame.</summary>
    public int RenderWidth { get; private set; }

    /// <summary>Gets the height of the render resolution, in pixels, for the frame.</summary>
    public int RenderHeight { get; private set; }

    /// <summary>Gets the camera of the frame, interpolated between ticks.</summary>
    public Camera FrameCamera { get; private set; } = new();

    /// <summary>Gets the view matrix of the frame: the rotation of the camera, which stands at the origin.</summary>
    public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>Gets the projection matrix of the frame, with reversed depth, for the aspect ratio of the viewport.</summary>
    public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>Gets the product of <see cref="ViewMatrix"/> and <see cref="ProjectionMatrix"/>.</summary>
    public Matrix4x4 ViewProjectionMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>Gets the parts of the visible objects to draw in the frame, in the order of their slots in the world.</summary>
    public ReadOnlySpan<RenderItem> Items => _items.AsSpan(0, _itemCount);

    /// <summary>Moves the camera as the simulation did during the last tick: it is interpolated until the next tick, as objects are.</summary>
    /// <param name="camera">The camera at the last tick.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    [HotPath]
    public void MoveCamera(in Camera camera)
    {
        CheckWritable();
        if (!_world.Interpolates)
        {
            _previous = camera;
        }
        else if (_movedTick != _world.Tick)
        {
            _previous = _current;
            _movedTick = _world.Tick;
        }

        _current = camera;
    }

    /// <summary>Places the camera without interpolation, for a jump or a camera that the frame moves, such as a free camera.</summary>
    /// <param name="camera">The camera.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    [HotPath]
    public void TeleportCamera(in Camera camera)
    {
        CheckWritable();
        _previous = camera;
        _current = camera;
        _movedTick = ulong.MaxValue;
    }

    /// <summary>Draws the view to a texture from the next frame on.</summary>
    /// <param name="color">A texture with <see cref="TextureUsage.RenderTarget"/>.</param>
    /// <param name="depth">A depth texture of the same size with <see cref="TextureUsage.DepthStencil"/>, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">A texture lacks its usage, or the sizes differ.</exception>
    public void SetTarget(Texture color, Texture? depth = null)
    {
        ArgumentNullException.ThrowIfNull(color);
        if ((color.Usage & TextureUsage.RenderTarget) == 0 || color.Dimension != TextureDimension.Texture2D)
        {
            throw new ArgumentException($"The target of view '{Name}' is a 2D texture with TextureUsage.RenderTarget; got {color}.", nameof(color));
        }

        if (depth is not null && ((depth.Usage & TextureUsage.DepthStencil) == 0 || depth.Width != color.Width || depth.Height != color.Height))
        {
            throw new ArgumentException($"The depth target of view '{Name}' has TextureUsage.DepthStencil and the size of the color target; got {depth}.", nameof(depth));
        }

        TargetTexture = color;
        TargetDepthTexture = depth;
    }

    /// <summary>Draws the view to the screen from the next frame on.</summary>
    public void SetScreenTarget()
    {
        TargetTexture = null;
        TargetDepthTexture = null;
    }

    /// <inheritdoc/>
    public override string ToString() => $"view '{Name}'";

    /// <summary>Sets the sizes of the frame from the size of the target.</summary>
    /// <returns><see langword="false"/> when the target has no pixel.</returns>
    [HotPath]
    internal bool SetSize(int targetWidth, int targetHeight)
    {
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
        int x = (int)MathF.Round(_viewport.X * targetWidth);
        int y = (int)MathF.Round(_viewport.Y * targetHeight);
        int width = Math.Min(targetWidth - x, (int)MathF.Round(_viewport.Width * targetWidth));
        int height = Math.Min(targetHeight - y, (int)MathF.Round(_viewport.Height * targetHeight));
        PixelViewport = new PixelRect(x, y, width, height);
        RenderWidth = Math.Max(1, (int)MathF.Round(width * _renderScale));
        RenderHeight = Math.Max(1, (int)MathF.Round(height * _renderScale));
        return width > 0 && height > 0;
    }

    /// <summary>Computes the camera and the matrices of the frame.</summary>
    [HotPath]
    internal void SetCamera(float alpha)
    {
        Camera camera = _world.Interpolates && _movedTick == _world.Tick ? Camera.Interpolate(_previous, _current, alpha) : _current;
        FrameCamera = camera;
        ViewMatrix = camera.GetViewMatrix();
        ProjectionMatrix = camera.GetProjectionMatrix((float)PixelViewport.Width / PixelViewport.Height);
        ViewProjectionMatrix = ViewMatrix * ProjectionMatrix;
    }

    /// <summary>Gets room for the items of the frame.</summary>
    [HotPath]
    internal RenderItem[] ItemBuffer(int capacity)
    {
        if (_items.Length < capacity)
        {
            Array.Resize(ref _items, Math.Max(capacity, _items.Length * 2));
        }

        return _items;
    }

    /// <summary>Sets the number of items of the frame.</summary>
    [HotPath]
    internal void SetItemCount(int count) => _itemCount = count;

    [HotPath]
    private void CheckWritable()
    {
        if (!_world.IsWritable)
        {
            throw new InvalidOperationException("The camera of a view changes during extraction only: change it from an extractor (IRenderer.AddExtractor).");
        }
    }
}
