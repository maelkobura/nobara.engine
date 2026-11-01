// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>
/// Draws lines, boxes, and spheres in the world for a time, from any module:
/// chunk borders, colliders, paths. Resolved with
/// <c>context.Resolve&lt;IDebugDraw&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Debug drawing is on in development mode only
/// (<see cref="ModuleContext.DevelopmentMode"/>). Off, every method returns at
/// once, nothing is kept, and no pass or GPU object exists: calls cost a test.
/// </para>
/// <para>
/// A shape is drawn at every frame until its duration, in seconds of real
/// time, has passed, and at least once: a duration of 0 draws it at the next
/// frame only. nRenderer draws the lines at the insertion point
/// <see cref="RenderInsertionPoints.BeforePostProcess"/>, tested against the
/// depth of the scene. Call it on the loop thread of the client context; the
/// methods allocate nothing once the storage has grown to the number of lines.
/// </para>
/// </remarks>
public interface IDebugDraw
{
    /// <summary>Gets whether debug drawing is on: the engine runs in development mode.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets the number of lines waiting to be drawn or to expire.</summary>
    int LineCount { get; }

    /// <summary>Draws a line.</summary>
    /// <param name="from">The start.</param>
    /// <param name="to">The end.</param>
    /// <param name="color">The color, linear, with its alpha.</param>
    /// <param name="duration">How long it stays, in seconds; 0 for one frame.</param>
    [HotPath]
    void Line(in WorldPosition from, in WorldPosition to, ColorF color, float duration = 0);

    /// <summary>Draws the edges of a box aligned with the axes.</summary>
    /// <param name="center">The center.</param>
    /// <param name="halfExtents">Half the size along each axis, in blocks.</param>
    /// <param name="color">The color, linear, with its alpha.</param>
    /// <param name="duration">How long it stays, in seconds; 0 for one frame.</param>
    [HotPath]
    void Box(in WorldPosition center, Vector3 halfExtents, ColorF color, float duration = 0);

    /// <summary>Draws the edges of a rotated box.</summary>
    /// <param name="center">The center.</param>
    /// <param name="halfExtents">Half the size along each axis of the box, in blocks.</param>
    /// <param name="rotation">The rotation of the box.</param>
    /// <param name="color">The color, linear, with its alpha.</param>
    /// <param name="duration">How long it stays, in seconds; 0 for one frame.</param>
    [HotPath]
    void Box(in WorldPosition center, Vector3 halfExtents, Quaternion rotation, ColorF color, float duration = 0);

    /// <summary>Draws a sphere as three circles, one around each axis.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius, in blocks.</param>
    /// <param name="color">The color, linear, with its alpha.</param>
    /// <param name="duration">How long it stays, in seconds; 0 for one frame.</param>
    [HotPath]
    void Sphere(in WorldPosition center, float radius, ColorF color, float duration = 0);

    /// <summary>Removes every shape.</summary>
    void Clear();
}

/// <summary>The lines of debug drawing, kept on the CPU until they expire.</summary>
internal sealed class DebugDraw(bool enabled) : IDebugDraw
{
    /// <summary>The number of segments of a circle of a sphere.</summary>
    public const int CircleSegments = 24;

    private Segment[] _segments = enabled ? new Segment[256] : [];
    private int _count;
    private double _now;

    public bool IsEnabled { get; } = enabled;

    public int LineCount => _count;

    internal ReadOnlySpan<Segment> Segments => _segments.AsSpan(0, _count);

    [HotPath]
    public void Line(in WorldPosition from, in WorldPosition to, ColorF color, float duration = 0)
    {
        if (!IsEnabled)
        {
            return;
        }

        Add(from, to.RelativeTo(from), Pack(color), _now + Math.Max(0, duration));
    }

    [HotPath]
    public void Box(in WorldPosition center, Vector3 halfExtents, ColorF color, float duration = 0) => Box(center, halfExtents, Quaternion.Identity, color, duration);

    [HotPath]
    public void Box(in WorldPosition center, Vector3 halfExtents, Quaternion rotation, ColorF color, float duration = 0)
    {
        if (!IsEnabled)
        {
            return;
        }

        uint packed = Pack(color);
        double expires = _now + Math.Max(0, duration);
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -halfExtents.X : halfExtents.X, (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y, (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            corners[i] = Vector3.Transform(corner, rotation);
        }

        // The twelve edges join corners that differ by one bit.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    Add(center.Offset(corners[i]), corners[i | bit] - corners[i], packed, expires);
                }
            }
        }
    }

    [HotPath]
    public void Sphere(in WorldPosition center, float radius, ColorF color, float duration = 0)
    {
        if (!IsEnabled)
        {
            return;
        }

        uint packed = Pack(color);
        double expires = _now + Math.Max(0, duration);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 previous = Circle(axis, 0, radius);
            for (int i = 1; i <= CircleSegments; i++)
            {
                Vector3 next = Circle(axis, i * MathF.Tau / CircleSegments, radius);
                Add(center.Offset(previous), next - previous, packed, expires);
                previous = next;
            }
        }
    }

    public void Clear() => _count = 0;

    /// <summary>Moves the clock of the lines to the time of a new frame.</summary>
    [HotPath]
    internal void Advance(TimeSpan elapsed) => _now += elapsed.TotalSeconds;

    /// <summary>Removes the lines whose time has passed, once a frame drew them.</summary>
    [HotPath]
    internal void Expire()
    {
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_segments[i].ExpiresAt > _now)
            {
                _segments[kept++] = _segments[i];
            }
        }

        _count = kept;
    }

    [HotPath]
    private static Vector3 Circle(int axis, float angle, float radius)
    {
        float cos = MathF.Cos(angle) * radius;
        float sin = MathF.Sin(angle) * radius;
        return axis switch
        {
            0 => new Vector3(0, cos, sin),
            1 => new Vector3(cos, 0, sin),
            _ => new Vector3(cos, sin, 0),
        };
    }

    [HotPath]
    private static uint Pack(ColorF color) =>
        (uint)(Math.Clamp(color.R, 0, 1) * 255 + 0.5f)
        | ((uint)(Math.Clamp(color.G, 0, 1) * 255 + 0.5f) << 8)
        | ((uint)(Math.Clamp(color.B, 0, 1) * 255 + 0.5f) << 16)
        | ((uint)(Math.Clamp(color.A, 0, 1) * 255 + 0.5f) << 24);

    [HotPath]
    private void Add(in WorldPosition start, Vector3 offset, uint color, double expiresAt)
    {
        if (_count == _segments.Length)
        {
            Array.Resize(ref _segments, _segments.Length * 2);
        }

        _segments[_count++] = new Segment(start, offset, color, expiresAt);
    }

    /// <summary>A line: its start, the offset to its end, its packed color, and when it expires.</summary>
    [HotPath]
    internal readonly record struct Segment(WorldPosition Start, Vector3 Offset, uint Color, double ExpiresAt);
}

/// <summary>Draws the lines of debug drawing in the render graph, at the insertion point before post-processing.</summary>
internal sealed class DebugDrawPass : IRenderInsertion, IRenderPass, IDisposable
{
    /// <summary>The name of the pass.</summary>
    public const string PassName = "nobara.renderer.debug-draw";

    private const int VertexSize = 16;
    private readonly DebugDraw _lines;
    private readonly GraphicsDevice _device;
    private readonly Graphics.Shader _vertex;
    private readonly Graphics.Shader _fragment;
    private readonly ResourceLayout _layout;
    private readonly PerFrame<GpuBuffer> _uniforms;
    private readonly PerFrame<ResourceSet> _sets;
    private readonly GpuBuffer?[] _vertices;
    private readonly List<(PixelFormat[] Colors, PixelFormat Depth, bool Test, GraphicsPipeline Pipeline)> _pipelines = [];
    private DebugVertex[] _staging = new DebugVertex[512];
    private GraphicsPipeline? _pipeline;
    private bool _depthTest;
    private int _vertexCount;

    public DebugDrawPass(DebugDraw lines, GraphicsDevice device)
    {
        _lines = lines;
        _device = device;
        _vertex = device.CreateShader(new ShaderDescription(ShaderStages.Vertex, EmbeddedShaders.Read("debug-draw.vert.spv")) { Name = "nobara.renderer.debug-draw.vert" });
        _fragment = device.CreateShader(new ShaderDescription(ShaderStages.Fragment, EmbeddedShaders.Read("debug-draw.frag.spv")) { Name = "nobara.renderer.debug-draw.frag" });
        _layout = device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]) { Name = "nobara.renderer.debug-draw" });
        _uniforms = new PerFrame<GpuBuffer>(device, slot => device.CreateBuffer(new BufferDescription(64, BufferUsage.Uniform) { Name = $"nobara.renderer.debug-draw.view.{slot}" }));
        _sets = new PerFrame<ResourceSet>(device, slot => device.CreateResourceSet(new ResourceSetDescription(_layout, [BoundResource.Of(_uniforms[slot])]) { Name = $"nobara.renderer.debug-draw.{slot}" }));
        _vertices = new GpuBuffer?[device.FramesInFlight];
    }

    public void Build(RenderGraphBuilder graph, in RenderInsertionPoint point)
    {
        RenderPassBuilder pass = graph.AddRasterPass(PassName, this).Color(point.Color);
        _depthTest = point.Depth.IsValid;
        if (_depthTest)
        {
            pass.Depth(point.Depth, write: false);
        }
    }

    public void Prepare(RenderPassContext context)
    {
        ReadOnlySpan<DebugDraw.Segment> segments = _lines.Segments;
        _vertexCount = segments.Length * 2;
        if (_vertexCount == 0)
        {
            return;
        }

        if (_staging.Length < _vertexCount)
        {
            Array.Resize(ref _staging, Math.Max(_vertexCount, _staging.Length * 2));
        }

        WorldPosition origin = context.View.FrameCamera.Position;
        for (int i = 0; i < segments.Length; i++)
        {
            Vector3 start = segments[i].Start.RelativeTo(origin);
            _staging[2 * i] = new DebugVertex(start, segments[i].Color);
            _staging[(2 * i) + 1] = new DebugVertex(start + segments[i].Offset, segments[i].Color);
        }

        int slot = context.FrameIndex;
        long size = (long)_vertexCount * VertexSize;
        if (_vertices[slot] is not { } buffer || buffer.Size < size)
        {
            _vertices[slot]?.Dispose();
            buffer = _device.CreateBuffer(new BufferDescription(Math.Max(size, 64 * 1024), BufferUsage.Vertex) { Name = "nobara.renderer.debug-draw.lines" });
            _vertices[slot] = buffer;
        }

        _device.UpdateBuffer(buffer, 0, MemoryMarshal.AsBytes(_staging.AsSpan(0, _vertexCount)));
        Matrix4x4 viewProjection = context.View.ViewProjectionMatrix;
        _device.UpdateBuffer(_uniforms.Current, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(ref viewProjection)));
        _pipeline = PipelineFor(context.Framebuffer!);
    }

    public void Execute(RenderPassContext context)
    {
        if (_vertexCount == 0)
        {
            return;
        }

        CommandBuffer commands = context.Commands;
        commands.SetPipeline(_pipeline!);
        commands.SetResourceSet(0, _sets.Current);
        commands.SetVertexBuffer(0, _vertices[context.FrameIndex]!);
        commands.Draw(_vertexCount);
    }

    public void Dispose()
    {
        foreach ((_, _, _, GraphicsPipeline pipeline) in _pipelines)
        {
            pipeline.Dispose();
        }

        foreach (GpuBuffer? buffer in _vertices)
        {
            buffer?.Dispose();
        }

        _sets.Dispose();
        _uniforms.Dispose();
        _layout.Dispose();
        _vertex.Dispose();
        _fragment.Dispose();
    }

    // One pipeline per set of attachment formats, created the first time the pass draws to them.
    private GraphicsPipeline PipelineFor(Framebuffer framebuffer)
    {
        IReadOnlyList<PixelFormat> colors = framebuffer.ColorFormats;
        foreach ((PixelFormat[] Colors, PixelFormat Depth, bool Test, GraphicsPipeline Pipeline) entry in _pipelines)
        {
            if (entry.Depth == framebuffer.DepthFormat && entry.Test == _depthTest && Same(entry.Colors, colors))
            {
                return entry.Pipeline;
            }
        }

        PixelFormat[] formats = [.. colors];
        GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            Name = "nobara.renderer.debug-draw",
            VertexShader = _vertex,
            FragmentShader = _fragment,
            VertexBuffers = [new VertexBufferLayout(VertexSize, [new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Byte4Norm, 12)])],
            ResourceLayouts = [_layout],
            Topology = PrimitiveTopology.LineList,
            Rasterizer = new RasterizerState { CullMode = CullMode.None },
            Depth = _depthTest ? new DepthState { TestEnabled = true, WriteEnabled = false, Compare = Camera.DepthCompare } : DepthState.Disabled,
            Blend = BlendState.AlphaBlend,
            Outputs = new OutputDescription(formats, framebuffer.DepthFormat),
        });
        _pipelines.Add((formats, framebuffer.DepthFormat, _depthTest, pipeline));
        return pipeline;
    }

    private static bool Same(PixelFormat[] formats, IReadOnlyList<PixelFormat> colors)
    {
        if (formats.Length != colors.Count)
        {
            return false;
        }

        for (int i = 0; i < formats.Length; i++)
        {
            if (formats[i] != colors[i])
            {
                return false;
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DebugVertex(Vector3 Position, uint Color);
}

/// <summary>The SPIR-V of the shaders of nRenderer itself, embedded in its assembly.</summary>
internal static class EmbeddedShaders
{
    /// <summary>The prefix of the names of the embedded shaders.</summary>
    public const string Prefix = "nobara.renderer.shaders.";

    /// <summary>Reads an embedded shader.</summary>
    /// <param name="name">The file name, such as <c>debug-draw.vert.spv</c>.</param>
    /// <returns>The SPIR-V.</returns>
    public static byte[] Read(string name)
    {
        using Stream stream = typeof(EmbeddedShaders).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"The assembly of nRenderer has no embedded shader '{name}'.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
