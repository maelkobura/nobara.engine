// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;

namespace Nobara.Graphics.OpenGL.Tests.Support;

/// <summary>
/// Small scenes drawn through the neutral API with SPIR-V shaders, each
/// compared with the image or the values it must give. The tests and the
/// SPIR-V bench share them.
/// </summary>
internal static class Scenes
{
    public const int Size = 64;

    private static readonly byte[] Red = [255, 0, 0, 255];
    private static readonly byte[] Green = [0, 255, 0, 255];
    private static readonly byte[] Blue = [0, 0, 255, 255];
    private static readonly byte[] White = [255, 255, 255, 255];
    private static readonly byte[][] Palette = [Red, Green, Blue, White];

    private const string TriangleVertex = """
        #version 450
        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec2 inUv;
        layout(location = 0) out vec2 uv;
        void main()
        {
            uv = inUv;
            gl_Position = vec4(inPosition, 0.0, 1.0);
        }
        """;

    private const string TriangleFragment = """
        #version 450
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 color;
        layout(binding = 0) uniform sampler2D image;
        layout(std140, binding = 0) uniform Tint { vec4 tint; };
        void main()
        {
            color = texture(image, uv) * tint;
        }
        """;

    // A triangle that covers the whole target, without vertex buffer.
    private const string FullScreenVertex = """
        #version 450
        layout(location = 0) out vec2 uv;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Draws the lower-left half of the target with an indexed triangle that
    /// samples a 2 × 2 texture through a uniform tint, on a gray clear color.
    /// </summary>
    public static void TexturedTriangle(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        Texture image = scene.Add(device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, 2, 2, TextureUsage.Sampled) with { Name = "quadrants" }));
        device.UpdateTexture(image, TextureRegion.Whole(image), [.. Red, .. Green, .. Blue, .. White]);
        Sampler sampler = scene.Add(device.CreateSampler(SamplerDescription.Point));

        GpuBuffer tint = scene.Add(device.CreateBuffer(new BufferDescription(16, BufferUsage.Uniform) { Name = "tint" }));
        device.UpdateBuffer(tint, 0, MemoryMarshal.AsBytes<float>([1, 1, 1, 1]));

        // Position, then texture coordinates: the texture covers the target, the triangle its lower-left half.
        GpuBuffer vertices = scene.Add(device.CreateBuffer(new BufferDescription(48, BufferUsage.Vertex) { Name = "vertices" }));
        device.UpdateBuffer(vertices, 0, MemoryMarshal.AsBytes<float>([-1, -1, 0, 0, 1, -1, 1, 0, -1, 1, 0, 1]));
        GpuBuffer indices = scene.Add(device.CreateBuffer(new BufferDescription(6, BufferUsage.Index) { Name = "indices" }));
        device.UpdateBuffer(indices, 0, MemoryMarshal.AsBytes<ushort>([0, 1, 2]));

        ResourceLayout layout = scene.Add(device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new(0, ResourceKind.SampledTexture, ShaderStages.Fragment),
            new(0, ResourceKind.UniformBuffer, ShaderStages.Fragment),
        ])));
        ResourceSet set = scene.Add(device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(image, sampler), BoundResource.Of(tint)])));
        GraphicsPipeline pipeline = scene.Add(device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            VertexShader = scene.Add(SpirV.Load(device, ShaderStages.Vertex, TriangleVertex, "triangle.vert")),
            FragmentShader = scene.Add(SpirV.Load(device, ShaderStages.Fragment, TriangleFragment, "triangle.frag")),
            VertexBuffers = [new VertexBufferLayout(16, [new(0, VertexFormat.Float2, 0), new(1, VertexFormat.Float2, 8)])],
            ResourceLayouts = [layout],
            Outputs = new([PixelFormat.Rgba8Unorm]),
            Rasterizer = new RasterizerState { CullMode = CullMode.None },
            Name = "textured triangle",
        }));

        byte[] pixels = scene.Render(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, set);
            commands.SetVertexBuffer(0, vertices);
            commands.SetIndexBuffer(indices, IndexFormat.UInt16);
            commands.DrawIndexed(3);
        });

        // Inside the triangle, the quadrant of the texture; outside, the clear color. The diagonal is skipped.
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int sum = x + y;
                if (sum is >= Size - 2 and <= Size)
                {
                    continue;
                }

                byte[] expected = sum < Size - 2 ? Palette[((y * 2 / Size) * 2) + (x * 2 / Size)] : SceneResources.ClearBytes;
                AssertPixel(pixels, x, y, expected, 0, "textured triangle");
            }
        }
    }

    /// <summary>Samples a texture array: four layers of one texel, one layer per vertical stripe.</summary>
    public static void TextureArray(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        Texture array = scene.Add(device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, PixelFormat.Rgba8Unorm, 1, 1, 4, 1, TextureUsage.Sampled) { Name = "layers" }));
        device.UpdateTexture(array, new TextureRegion(0, 0, 0, 0, 1, 1, 4), [.. Red, .. Green, .. Blue, .. White]);
        byte[] pixels = scene.FullScreen(
            """
            #version 450
            layout(location = 0) in vec2 uv;
            layout(location = 0) out vec4 color;
            layout(binding = 0) uniform sampler2DArray layers;
            void main() { color = texture(layers, vec3(0.5, 0.5, floor(uv.x * 4.0))); }
            """,
            [new(0, ResourceKind.SampledTexture, ShaderStages.Fragment)],
            [BoundResource.Of(array, scene.Add(device.CreateSampler(SamplerDescription.Point)))]);
        AssertStripes(pixels, vertical: true, "texture array");
    }

    /// <summary>Samples a 3D texture: four slices of one texel, one slice per horizontal stripe.</summary>
    public static void Texture3D(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        Texture volume = scene.Add(device.CreateTexture(new TextureDescription(TextureDimension.Texture3D, PixelFormat.Rgba8Unorm, 1, 1, 4, 1, TextureUsage.Sampled) { Name = "volume" }));
        device.UpdateTexture(volume, new TextureRegion(0, 0, 0, 0, 1, 1, 4), [.. Red, .. Green, .. Blue, .. White]);
        byte[] pixels = scene.FullScreen(
            """
            #version 450
            layout(location = 0) in vec2 uv;
            layout(location = 0) out vec4 color;
            layout(binding = 0) uniform sampler3D volume;
            void main() { color = texture(volume, vec3(0.5, 0.5, (floor(uv.y * 4.0) + 0.5) / 4.0)); }
            """,
            [new(0, ResourceKind.SampledTexture, ShaderStages.Fragment)],
            [BoundResource.Of(volume, scene.Add(device.CreateSampler(SamplerDescription.Point)))]);
        AssertStripes(pixels, vertical: false, "3D texture");
    }

    /// <summary>Reads colors from a read-only storage buffer in the fragment stage, one per vertical stripe.</summary>
    public static void StorageBufferInFragment(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        GpuBuffer colors = scene.Add(device.CreateBuffer(new BufferDescription(64, BufferUsage.Storage) { Name = "colors" }));
        device.UpdateBuffer(colors, 0, MemoryMarshal.AsBytes<float>([1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1]));
        byte[] pixels = scene.FullScreen(
            """
            #version 450
            layout(location = 0) in vec2 uv;
            layout(location = 0) out vec4 color;
            layout(std430, binding = 0) readonly buffer Colors { vec4 colors[]; };
            void main() { color = colors[int(uv.x * 4.0)]; }
            """,
            [new(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Fragment)],
            [BoundResource.Of(colors)]);
        AssertStripes(pixels, vertical: true, "storage buffer in a fragment shader");
    }

    /// <summary>Writes a storage buffer from a compute shader, with a uniform factor, and reads it back.</summary>
    public static void ComputeStorageBuffer(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        const int count = 256;
        GpuBuffer data = scene.Add(device.CreateBuffer(new BufferDescription(count * 4, BufferUsage.Storage) { Name = "squares" }));
        GpuBuffer factor = scene.Add(device.CreateBuffer(new BufferDescription(16, BufferUsage.Uniform) { Name = "factor" }));
        device.UpdateBuffer(factor, 0, MemoryMarshal.AsBytes<uint>([3, 0, 0, 0]));
        ResourceLayout layout = scene.Add(device.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute),
            new(0, ResourceKind.UniformBuffer, ShaderStages.Compute),
        ])));
        ResourceSet set = scene.Add(device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(data), BoundResource.Of(factor)])));
        ComputePipeline pipeline = scene.Add(device.CreateComputePipeline(new ComputePipelineDescription(
            scene.Add(SpirV.Load(device, ShaderStages.Compute, """
                #version 450
                layout(local_size_x = 64) in;
                layout(std430, binding = 0) buffer Data { uint values[]; };
                layout(std140, binding = 0) uniform Factor { uint factor; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    values[i] = i * i * factor;
                }
                """, "squares.comp")),
            [layout]) { Name = "squares" }));

        scene.Run(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, set);
            commands.Dispatch(count / 64);
            commands.Barrier(BarrierScope.Transfer);
        });

        var bytes = new byte[count * 4];
        device.ReadBuffer(data, 0, bytes);
        uint[] values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
        for (uint i = 0; i < count; i++)
        {
            if (values[i] != i * i * 3)
            {
                throw new Xunit.Sdk.XunitException($"compute storage buffer: value {i} is {values[i]}, expected {i * i * 3}.");
            }
        }
    }

    /// <summary>Writes a storage texture from a compute shader: a gradient read back texel by texel.</summary>
    public static void ComputeStorageTexture(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);
        const int size = 16;
        Texture image = scene.Add(device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, size, size, TextureUsage.Storage) with { Name = "gradient" }));
        ResourceLayout layout = scene.Add(device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.StorageTexture, ShaderStages.Compute)])));
        ResourceSet set = scene.Add(device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(image)])));
        ComputePipeline pipeline = scene.Add(device.CreateComputePipeline(new ComputePipelineDescription(
            scene.Add(SpirV.Load(device, ShaderStages.Compute, """
                #version 450
                layout(local_size_x = 8, local_size_y = 8) in;
                layout(binding = 0, rgba8) uniform writeonly image2D target;
                void main()
                {
                    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
                    imageStore(target, p, vec4(p.x / 15.0, p.y / 15.0, 0.0, 1.0));
                }
                """, "gradient.comp")),
            [layout]) { Name = "gradient" }));

        scene.Run(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, set);
            commands.Dispatch(size / 8, size / 8);
            commands.Barrier(BarrierScope.Transfer);
        });

        var pixels = new byte[size * size * 4];
        device.ReadTexture(image, TextureRegion.Whole(image), pixels);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                AssertPixel(pixels, x, y, [(byte)(x * 17), (byte)(y * 17), 0, 255], 1, "compute storage texture", size);
            }
        }
    }

    /// <summary>
    /// Draws the left third of the target with an indirect draw, the two other
    /// thirds with an indexed indirect draw, and reads the color of the right
    /// third from a buffer that a compute shader, started by an indirect
    /// dispatch, wrote. The arguments sit at offsets that are not 0.
    /// </summary>
    public static void Indirect(GraphicsDevice device)
    {
        using var scene = new SceneResources(device);

        // Taken first, so the arguments below sit further in the shared block.
        scene.Add(device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex) { Name = "padding" }));
        GpuBuffer arguments = scene.Add(device.CreateBuffer(new BufferDescription(256, BufferUsage.Indirect | BufferUsage.Storage) { Name = "arguments" }));
        if (arguments.Offset == 0)
        {
            throw new Xunit.Sdk.XunitException("indirect: the arguments should not start their block.");
        }

        // At byte 16: a draw (count, instances, first, base instance); at 48: an indexed draw
        // (count, instances, first index, base vertex, base instance); at 96: a dispatch.
        uint[] args = new uint[64];
        new uint[] { 3, 1, 0, 0 }.CopyTo(args, 4);
        new uint[] { 3, 1, 3, 0, 1 }.CopyTo(args, 12);
        new uint[] { 1, 1, 1 }.CopyTo(args, 24);
        device.UpdateBuffer(arguments, 0, MemoryMarshal.AsBytes<uint>(args));

        // The indexed draw starts at index 3: the first three indices would land off screen.
        GpuBuffer indices = scene.Add(device.CreateBuffer(new BufferDescription(24, BufferUsage.Index) { Name = "indices", Dedicated = true }));
        device.UpdateBuffer(indices, 0, MemoryMarshal.AsBytes<uint>([7, 7, 7, 0, 1, 2]));

        GpuBuffer colors = scene.Add(device.CreateBuffer(new BufferDescription(48, BufferUsage.Storage) { Name = "colors" }));
        device.UpdateBuffer(colors, 0, MemoryMarshal.AsBytes<float>([1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0]));
        ResourceLayout computeLayout = scene.Add(device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Compute)])));
        ResourceSet computeSet = scene.Add(device.CreateResourceSet(new ResourceSetDescription(computeLayout, [BoundResource.Of(colors)])));
        ComputePipeline blue = scene.Add(device.CreateComputePipeline(new ComputePipelineDescription(
            scene.Add(SpirV.Load(device, ShaderStages.Compute, """
                #version 450
                layout(local_size_x = 1) in;
                layout(std430, binding = 0) buffer Colors { vec4 colors[3]; };
                void main() { colors[2] = vec4(0.0, 0.0, 1.0, 1.0); }
                """, "blue.comp")),
            [computeLayout]) { Name = "blue" }));

        ResourceLayout layout = scene.Add(device.CreateResourceLayout(new ResourceLayoutDescription([new(0, ResourceKind.ReadOnlyStorageBuffer, ShaderStages.Fragment)])));
        ResourceSet set = scene.Add(device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(colors)])));
        GraphicsPipeline pipeline = scene.Add(device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            // The base instance says which draw this is; the vertex index, the corner of a full-screen triangle.
            VertexShader = scene.Add(SpirV.Load(device, ShaderStages.Vertex, """
                #version 460
                layout(location = 0) out vec2 uv;
                layout(location = 1) flat out int draw;
                void main()
                {
                    vec2 p = gl_VertexID > 2 ? vec2(-10.0) : vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                    uv = p;
                    draw = gl_InstanceID + gl_BaseInstance;
                    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
                }
                """, "indirect.vert")),
            FragmentShader = scene.Add(SpirV.Load(device, ShaderStages.Fragment, """
                #version 450
                layout(location = 0) in vec2 uv;
                layout(location = 1) flat in int draw;
                layout(location = 0) out vec4 color;
                layout(std430, binding = 0) readonly buffer Colors { vec4 colors[3]; };
                void main()
                {
                    int third = int(uv.x * 3.0);
                    if ((draw == 0) != (third == 0)) discard;
                    color = colors[third];
                }
                """, "indirect.frag")),
            ResourceLayouts = [layout],
            Outputs = new([PixelFormat.Rgba8Unorm]),
            Rasterizer = new RasterizerState { CullMode = CullMode.None },
            Name = "indirect",
        }));

        scene.Run(commands =>
        {
            commands.SetPipeline(blue);
            commands.SetResourceSet(0, computeSet);
            commands.DispatchIndirect(arguments, 96);
            commands.Barrier(BarrierScope.StorageBuffer);
        });

        byte[] pixels = scene.Render(commands =>
        {
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, set);
            commands.DrawIndirect(arguments, 16, 1);
            commands.SetIndexBuffer(indices, IndexFormat.UInt32);
            commands.DrawIndexedIndirect(arguments, 48, 1);
        });

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (x is 21 or 42)
                {
                    continue; // the borders of the thirds
                }

                AssertPixel(pixels, x, y, Palette[x * 3 / Size], 0, "indirect draws and dispatch");
            }
        }
    }

    private static void AssertStripes(byte[] pixels, bool vertical, string scene)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int stripe = (vertical ? x : y) * 4 / Size;
                AssertPixel(pixels, x, y, Palette[stripe], 0, scene);
            }
        }
    }

    private static void AssertPixel(byte[] pixels, int x, int y, byte[] expected, int tolerance, string scene, int width = Size)
    {
        int at = ((y * width) + x) * 4;
        for (int c = 0; c < 4; c++)
        {
            if (Math.Abs(pixels[at + c] - expected[c]) > tolerance)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{scene}: pixel ({x}, {y}) is ({pixels[at]}, {pixels[at + 1]}, {pixels[at + 2]}, {pixels[at + 3]}), expected ({expected[0]}, {expected[1]}, {expected[2]}, {expected[3]}).");
            }
        }
    }

    /// <summary>The resources of a scene, an off-screen target, and the way to draw to it.</summary>
    private sealed class SceneResources(GraphicsDevice device) : IDisposable
    {
        public static readonly byte[] ClearBytes = [64, 64, 64, 255];

        private readonly List<IDisposable> _resources = [];

        public T Add<T>(T resource)
            where T : IDisposable
        {
            _resources.Add(resource);
            return resource;
        }

        /// <summary>Clears a 64 × 64 target to gray, records the draws, and reads the pixels back, bottom row first.</summary>
        public byte[] Render(Action<CommandBuffer> draw)
        {
            Texture target = Add(device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, Size, Size, TextureUsage.RenderTarget | TextureUsage.Sampled) with { Name = "target" }));
            Framebuffer framebuffer = Add(device.CreateFramebuffer(new FramebufferDescription([new(target)]) { Name = "target" }));
            Run(commands =>
            {
                commands.BeginRenderPass(framebuffer, new RenderPassDescription { ClearColor = new ColorF(64 / 255f, 64 / 255f, 64 / 255f, 1) });
                draw(commands);
                commands.EndRenderPass();
            });

            var pixels = new byte[Size * Size * 4];
            device.ReadTexture(target, TextureRegion.Whole(target), pixels);
            return pixels;
        }

        /// <summary>Draws a full-screen triangle with a fragment shader and one resource set.</summary>
        public byte[] FullScreen(string fragment, ResourceLayoutEntry[] entries, BoundResource[] resources)
        {
            ResourceLayout layout = Add(device.CreateResourceLayout(new ResourceLayoutDescription(entries)));
            ResourceSet set = Add(device.CreateResourceSet(new ResourceSetDescription(layout, resources)));
            GraphicsPipeline pipeline = Add(device.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                VertexShader = Add(SpirV.Load(device, ShaderStages.Vertex, FullScreenVertex, "fullscreen.vert")),
                FragmentShader = Add(SpirV.Load(device, ShaderStages.Fragment, fragment, "scene.frag")),
                ResourceLayouts = [layout],
                Outputs = new([PixelFormat.Rgba8Unorm]),
                Rasterizer = new RasterizerState { CullMode = CullMode.None },
                Name = "full screen",
            }));
            return Render(commands =>
            {
                commands.SetPipeline(pipeline);
                commands.SetResourceSet(0, set);
                commands.Draw(3);
            });
        }

        /// <summary>Records commands and runs them in one frame.</summary>
        public void Run(Action<CommandBuffer> record)
        {
            CommandBuffer commands = device.CreateCommandBuffer("scene");
            device.BeginFrame();
            commands.Begin();
            record(commands);
            commands.End();
            device.Submit(commands);
            device.EndFrame();
        }

        public void Dispose()
        {
            foreach (IDisposable resource in _resources)
            {
                resource.Dispose();
            }

            device.WaitIdle();
        }
    }
}
