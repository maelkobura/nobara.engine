// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>A render graph ready to run: its passes in order, with their framebuffers and barriers, and the GPU objects of its resources.</summary>
internal sealed class CompiledGraph
{
    public CompiledGraph(GraphResource[] resources, Step[] steps, Texture?[] textures, GpuBuffer?[] buffers, RenderGraphStatistics statistics)
    {
        Resources = resources;
        Steps = steps;
        Textures = textures;
        Buffers = buffers;
        Statistics = statistics;
    }

    public GraphResource[] Resources { get; }

    public Step[] Steps { get; }

    public Texture?[] Textures { get; }

    public GpuBuffer?[] Buffers { get; }

    public RenderGraphStatistics Statistics { get; }

    [HotPath]
    public Texture GetTexture(RenderGraphTexture texture)
    {
        int index = texture.Index - 1;
        if ((uint)index >= (uint)Textures.Length || Textures[index] is not { } found)
        {
            throw new ArgumentException($"{texture} is not a texture of the graph that a running pass uses, or it is the screen, which is no texture.", nameof(texture));
        }

        return found;
    }

    [HotPath]
    public GpuBuffer GetBuffer(RenderGraphBuffer buffer)
    {
        int index = buffer.Index - 1;
        if ((uint)index >= (uint)Buffers.Length || Buffers[index] is not { } found)
        {
            throw new ArgumentException($"{buffer} is not a buffer of the graph that a running pass uses.", nameof(buffer));
        }

        return found;
    }

    /// <summary>A pass that runs.</summary>
    internal readonly record struct Step(GraphPass Pass, Framebuffer? Framebuffer, RenderPassDescription Description, BarrierScope Barrier, bool OnTarget);
}

/// <summary>
/// Builds a declared graph into a <see cref="CompiledGraph"/>, and keeps the
/// transient textures, buffers, and framebuffers from one build to the next,
/// so that a new build reuses what still fits.
/// </summary>
internal sealed class RenderGraphCompiler(GraphicsDevice device)
{
    private readonly List<Pooled<Texture>> _textures = [];
    private readonly List<Pooled<GpuBuffer>> _buffers = [];
    private List<(Texture[] Colors, Texture? Depth, Framebuffer Framebuffer)> _framebuffers = [];
    private int _created;

    public CompiledGraph Compile(RenderGraphBuilder graph, string pipeline)
    {
        GraphResource[] resources = [.. graph.Resources];
        GraphPass[] passes = [.. graph.Passes];
        bool[] live = Cull(resources, passes);
        var culled = new List<string>();
        var running = new List<GraphPass>();
        for (int i = 0; i < passes.Length; i++)
        {
            if (live[i])
            {
                running.Add(passes[i]);
            }
            else
            {
                culled.Add(passes[i].Name);
            }
        }

        Validate(resources, running);
        foreach (GraphPass pass in running)
        {
            try
            {
                graph.Capabilities.Require(pass.Required, $"Render pass '{pass.Name}'");
            }
            catch (NotSupportedException exception)
            {
                throw Error(pass, exception.Message, exception);
            }
        }

        foreach (Pooled<Texture> pooled in _textures)
        {
            pooled.Kept = false;
        }

        foreach (Pooled<GpuBuffer> pooled in _buffers)
        {
            pooled.Kept = false;
        }

        var textures = new Texture?[resources.Length];
        var buffers = new GpuBuffer?[resources.Length];
        (int transientTextures, int transientBuffers) = Allocate(graph.View, resources, running, textures, buffers);
        var steps = new CompiledGraph.Step[running.Count];
        var framebuffers = new List<(Texture[] Colors, Texture? Depth, Framebuffer Framebuffer)>();
        BarrierScope[] barriers = Barriers(resources, running);
        int barrierCount = 0;
        for (int i = 0; i < running.Count; i++)
        {
            GraphPass pass = running[i];
            barrierCount += barriers[i] == BarrierScope.None ? 0 : 1;
            if (pass.Compute)
            {
                steps[i] = new CompiledGraph.Step(pass, null, default, barriers[i], false);
                continue;
            }

            (Framebuffer framebuffer, bool onTarget) = FramebufferOf(pass, resources, textures, framebuffers);
            steps[i] = new CompiledGraph.Step(pass, framebuffer, Describe(pass), barriers[i], onTarget);
        }

        // What the new graph does not use anymore is retired: the device destroys it once the frames in flight are done.
        foreach ((_, _, Framebuffer framebuffer) in _framebuffers)
        {
            if (!framebuffers.Exists(f => ReferenceEquals(f.Framebuffer, framebuffer)))
            {
                framebuffer.Dispose();
            }
        }

        _framebuffers = framebuffers;
        Sweep(_textures);
        Sweep(_buffers);
        var statistics = new RenderGraphStatistics(
            pipeline,
            [.. running.Select(p => p.Name)],
            culled,
            transientTextures,
            Distinct(resources, textures),
            transientBuffers,
            Distinct(resources, buffers),
            barrierCount);
        return new CompiledGraph(resources, steps, textures, buffers, statistics);
    }

    /// <summary>Destroys every GPU object of the graphs.</summary>
    public void Release()
    {
        foreach ((_, _, Framebuffer framebuffer) in _framebuffers)
        {
            framebuffer.Dispose();
        }

        foreach (Pooled<Texture> pooled in _textures)
        {
            pooled.Resource.Dispose();
        }

        foreach (Pooled<GpuBuffer> pooled in _buffers)
        {
            pooled.Resource.Dispose();
        }

        _framebuffers.Clear();
        _textures.Clear();
        _buffers.Clear();
    }

    // Walks the passes backward: a pass runs when it writes what lives outside the graph, what a later running pass reads, or when it is kept alive.
    private static bool[] Cull(GraphResource[] resources, GraphPass[] passes)
    {
        bool[] live = new bool[passes.Length];
        bool[] needed = new bool[resources.Length];
        for (int i = passes.Length - 1; i >= 0; i--)
        {
            GraphPass pass = passes[i];
            bool runs = pass.KeepAlive;
            foreach (Access access in pass.Accesses)
            {
                runs |= access.Write && (resources[access.Resource].External || needed[access.Resource]);
            }

            if (!runs)
            {
                continue;
            }

            live[i] = true;
            foreach (Access access in pass.Accesses)
            {
                if (access.Write && !access.Read)
                {
                    needed[access.Resource] = false;
                }
            }

            foreach (Access access in pass.Accesses)
            {
                if (access.Read)
                {
                    needed[access.Resource] = true;
                }
            }
        }

        return live;
    }

    private static void Validate(GraphResource[] resources, List<GraphPass> running)
    {
        bool[] written = new bool[resources.Length];
        foreach (GraphPass pass in running)
        {
            foreach (Access access in pass.Accesses)
            {
                GraphResource resource = resources[access.Resource];
                if (access.Read && !resource.External && !written[access.Resource])
                {
                    throw Error(pass, $"Render pass '{pass.Name}' reads '{resource.Name}', which no earlier pass writes.");
                }

                if (resource.Screen && access.Kind is not (AccessKind.ColorAttachment or AccessKind.DepthAttachment))
                {
                    throw Error(pass, $"Render pass '{pass.Name}' uses '{resource.Name}' as {access.Kind}: the screen is only drawn to.");
                }
            }

            foreach (Access access in pass.Accesses)
            {
                written[access.Resource] |= access.Write;
            }

            if (!pass.Compute)
            {
                if (pass.Colors.Count == 0 && pass.Depth is null)
                {
                    throw Error(pass, $"Raster pass '{pass.Name}' has no attachment.");
                }

                for (int i = 1; i < pass.Colors.Count; i++)
                {
                    if (pass.Colors[i].Load != pass.Colors[0].Load || pass.Colors[i].ClearColor != pass.Colors[0].ClearColor)
                    {
                        throw Error(pass, $"Raster pass '{pass.Name}': every color attachment of a pass begins with the same load action and clear color.");
                    }
                }

                bool screenColor = pass.Colors.Exists(c => resources[c.Resource].Screen);
                bool screenDepth = pass.Depth is { } depth && resources[depth.Resource].Screen;
                if ((screenColor && (pass.Colors.Count > 1 || (pass.Depth is not null && !screenDepth))) || (screenDepth && !screenColor))
                {
                    throw Error(pass, $"Raster pass '{pass.Name}': the screen is drawn with its own depth or none, and with no other attachment.");
                }
            }
        }
    }

    private static RenderGraphException Error(GraphPass pass, string message, Exception? inner = null) => new(message, pass.Name, pass.Owner.Module.Id, inner);

    // Gives each transient resource a GPU object from its first to its last running pass; resources of the same description that do not overlap share one.
    private (int Textures, int Buffers) Allocate(RenderView view, GraphResource[] resources, List<GraphPass> running, Texture?[] textures, GpuBuffer?[] buffers)
    {
        int[] first = new int[resources.Length];
        int[] last = new int[resources.Length];
        var textureUsage = new TextureUsage[resources.Length];
        var bufferUsage = new BufferUsage[resources.Length];
        Array.Fill(first, -1);
        for (int p = 0; p < running.Count; p++)
        {
            foreach (Access access in running[p].Accesses)
            {
                if (first[access.Resource] < 0)
                {
                    first[access.Resource] = p;
                }

                last[access.Resource] = p;
                textureUsage[access.Resource] |= access.Kind switch
                {
                    AccessKind.Sampled => TextureUsage.Sampled,
                    AccessKind.StorageTexture => TextureUsage.Storage,
                    AccessKind.ColorAttachment => TextureUsage.RenderTarget,
                    AccessKind.DepthAttachment => TextureUsage.DepthStencil,
                    _ => TextureUsage.None,
                };
                bufferUsage[access.Resource] |= access.Kind switch
                {
                    AccessKind.Vertex => BufferUsage.Vertex,
                    AccessKind.Index => BufferUsage.Index,
                    AccessKind.Uniform => BufferUsage.Uniform,
                    AccessKind.StorageBuffer => BufferUsage.Storage,
                    AccessKind.Indirect => BufferUsage.Indirect,
                    _ => BufferUsage.None,
                };
            }
        }

        for (int r = 0; r < resources.Length; r++)
        {
            GraphResource resource = resources[r];
            if (resource.Imported is { } imported)
            {
                textures[r] = imported;
            }
            else if (resource.ImportedBuffer is { } importedBuffer)
            {
                buffers[r] = importedBuffer;
            }
        }

        int transientTextures = 0;
        int transientBuffers = 0;
        var inUse = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int p = 0; p < running.Count; p++)
        {
            for (int r = 0; r < resources.Length; r++)
            {
                GraphResource resource = resources[r];
                if (first[r] != p || resource.External)
                {
                    continue;
                }

                if (resource.IsTexture)
                {
                    transientTextures++;
                    (int width, int height) = SizeOf(view, resource.Description);
                    var key = new TextureKey(resource.Format, width, height, Math.Min(resource.Description.MipLevels, MipCount(width, height)), textureUsage[r]);
                    textures[r] = Take(_textures, inUse, key, () => device.CreateTexture(new TextureDescription(TextureDimension.Texture2D, key.Format, key.Width, key.Height, 1, key.MipLevels, key.Usage)
                    {
                        Name = $"nobara.renderer.transient.{_created++}",
                    }));
                }
                else
                {
                    transientBuffers++;
                    BufferUsage usage = bufferUsage[r] == BufferUsage.None ? BufferUsage.Vertex : bufferUsage[r];
                    var key = new BufferKey(resource.BufferSize, usage);
                    buffers[r] = Take(_buffers, inUse, key, () => device.CreateBuffer(new BufferDescription(key.Size, key.Usage) { Name = $"nobara.renderer.transient.{_created++}" }));
                }
            }

            for (int r = 0; r < resources.Length; r++)
            {
                if (last[r] == p && first[r] >= 0 && !resources[r].External)
                {
                    inUse.Remove(resources[r].IsTexture ? textures[r]! : buffers[r]!);
                }
            }
        }

        return (transientTextures, transientBuffers);
    }

    // The number of GPU objects behind the transient resources.
    private static int Distinct<T>(GraphResource[] resources, T?[] objects)
        where T : class
    {
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < resources.Length; r++)
        {
            if (!resources[r].External && objects[r] is { } found)
            {
                seen.Add(found);
            }
        }

        return seen.Count;
    }

    private static T Take<T>(List<Pooled<T>> pool, HashSet<object> inUse, object key, Func<T> create)
        where T : GraphicsResource
    {
        foreach (Pooled<T> pooled in pool)
        {
            if (pooled.Key.Equals(key) && !inUse.Contains(pooled.Resource))
            {
                pooled.Kept = true;
                inUse.Add(pooled.Resource);
                return pooled.Resource;
            }
        }

        var created = new Pooled<T>(key, create()) { Kept = true };
        pool.Add(created);
        inUse.Add(created.Resource);
        return created.Resource;
    }

    private static void Sweep<T>(List<Pooled<T>> pool)
        where T : GraphicsResource
    {
        foreach (Pooled<T> pooled in pool)
        {
            if (!pooled.Kept)
            {
                pooled.Resource.Dispose();
            }
        }

        pool.RemoveAll(p => !p.Kept);
    }

    // A barrier before a pass makes visible what earlier passes wrote as storage, for the way this pass uses it.
    // Each resource remembers the scopes already made visible since its last storage write, whatever the order of the declarations of a pass.
    private static BarrierScope[] Barriers(GraphResource[] resources, List<GraphPass> running)
    {
        var barriers = new BarrierScope[running.Count];
        bool[] written = new bool[resources.Length];
        var covered = new BarrierScope[resources.Length];
        for (int p = 0; p < running.Count; p++)
        {
            foreach (Access access in running[p].Accesses)
            {
                if (!written[access.Resource])
                {
                    continue;
                }

                BarrierScope scope = access.Kind switch
                {
                    AccessKind.Sampled => BarrierScope.Texture,
                    AccessKind.StorageTexture => BarrierScope.StorageTexture,
                    AccessKind.ColorAttachment or AccessKind.DepthAttachment => BarrierScope.RenderTarget,
                    AccessKind.Vertex or AccessKind.Index => BarrierScope.VertexInput,
                    AccessKind.Uniform => BarrierScope.Uniform,
                    AccessKind.StorageBuffer => BarrierScope.StorageBuffer,
                    AccessKind.Indirect => BarrierScope.Indirect,
                    _ => BarrierScope.Transfer,
                };
                if ((covered[access.Resource] & scope) == 0)
                {
                    barriers[p] |= scope;
                    covered[access.Resource] |= scope;
                }
            }

            foreach (Access access in running[p].Accesses)
            {
                if (access.Write && access.Kind is AccessKind.StorageTexture or AccessKind.StorageBuffer)
                {
                    written[access.Resource] = true;
                    covered[access.Resource] = BarrierScope.None;
                }
            }
        }

        return barriers;
    }

    private (Framebuffer Framebuffer, bool OnTarget) FramebufferOf(GraphPass pass, GraphResource[] resources, Texture?[] textures, List<(Texture[] Colors, Texture? Depth, Framebuffer Framebuffer)> used)
    {
        bool onTarget = pass.Colors.Exists(c => resources[c.Resource].IsViewTarget) || (pass.Depth is { } d && resources[d.Resource].IsViewTarget);
        if (pass.Colors.Exists(c => resources[c.Resource].Screen))
        {
            return (device.Swapchain.Framebuffer, true);
        }

        Texture[] colors = [.. pass.Colors.Select(c => textures[c.Resource]!)];
        Texture? depth = pass.Depth is { } attachment ? textures[attachment.Resource] : null;
        foreach ((Texture[] Colors, Texture? Depth, Framebuffer Framebuffer) entry in used.Concat(_framebuffers))
        {
            if (ReferenceEquals(entry.Depth, depth) && entry.Colors.SequenceEqual(colors, ReferenceEqualityComparer.Instance))
            {
                if (!used.Contains(entry))
                {
                    used.Add(entry);
                }

                return (entry.Framebuffer, onTarget);
            }
        }

        Framebuffer created = device.CreateFramebuffer(new FramebufferDescription(
            [.. colors.Select(c => new FramebufferAttachment(c))],
            depth is null ? null : new FramebufferAttachment(depth))
        {
            Name = $"nobara.renderer.{pass.Name}",
        });
        used.Add((colors, depth, created));
        return (created, onTarget);
    }

    private static RenderPassDescription Describe(GraphPass pass)
    {
        Attachment? color = pass.Colors.Count > 0 ? pass.Colors[0] : null;
        return new RenderPassDescription
        {
            ColorLoad = color?.Load ?? LoadAction.Load,
            ClearColor = color?.ClearColor ?? default,
            DepthLoad = pass.Depth?.Load ?? LoadAction.Load,
            ClearDepth = pass.Depth?.ClearDepth ?? Camera.ClearDepth,
        };
    }

    private static (int Width, int Height) SizeOf(RenderView view, in RenderGraphTextureDescription description) => description.Size switch
    {
        RenderTextureSize.Fixed => (description.Width, description.Height),
        RenderTextureSize.TargetResolution => (Scaled(view.PixelViewport.Width, description.Scale), Scaled(view.PixelViewport.Height, description.Scale)),
        _ => (Scaled(view.RenderWidth, description.Scale), Scaled(view.RenderHeight, description.Scale)),
    };

    private static int Scaled(int size, float scale) => Math.Max(1, (int)MathF.Round(size * scale));

    private static int MipCount(int width, int height) => System.Numerics.BitOperations.Log2((uint)Math.Max(width, height)) + 1;

    private sealed class Pooled<T>(object key, T resource)
        where T : GraphicsResource
    {
        public object Key { get; } = key;

        public T Resource { get; } = resource;

        public bool Kept { get; set; }
    }

    private readonly record struct TextureKey(PixelFormat Format, int Width, int Height, int MipLevels, TextureUsage Usage);

    private readonly record struct BufferKey(long Size, BufferUsage Usage);
}
