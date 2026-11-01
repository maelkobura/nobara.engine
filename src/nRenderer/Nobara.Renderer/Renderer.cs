// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Loop;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>
/// Draws the frames: extraction, the frame of the GPU, the render graph of the
/// view, and the passes. Runs in the phase <c>Render</c> of the client loop.
/// </summary>
internal sealed class Renderer : IRenderer, IPhaseHandler
{
    /// <summary>The name of the phase where frames are drawn.</summary>
    public const string Phase = "Render";

    // Below this number of objects, their transforms are computed on the loop thread.
    private const int JobThreshold = 256;
    private const int JobBatch = 128;

    private readonly ModuleContext _context;
    private readonly RenderWorld _world = new();
    private readonly RenderView _view;
    private readonly RenderExtraction _extraction;
    private readonly RenderPassContext _pass = new();
    private readonly List<Entry<IRenderExtractor>> _extractors = [];
    private readonly List<Entry<IRenderPipeline>> _pipelines = [];
    private readonly List<Insertion> _insertions = [];
    private readonly HashSet<string> _warnings = [];
    private Entry<IRenderExtractor>[] _extractorArray = [];
    private Matrix4x4[] _matrices = new Matrix4x4[64];
    private GraphicsDevice? _device;
    private CommandBuffer? _commands;
    private RenderGraphCompiler? _compiler;
    private CompiledGraph? _graph;
    private GraphKey _graphKey;
    private bool _rebuild = true;
    private bool _extractorsChanged;
    private int _sequence;
    private ulong _lastTick;
    private DebugDrawPass? _debugPass;
    private RenderRegistration? _debugInsertion;

    public Renderer(ModuleContext context)
    {
        _context = context;
        _view = new RenderView("main", _world);
        _extraction = new RenderExtraction(_world, _view);
        Debug = new DebugDraw(context.DevelopmentMode);
    }

    public bool IsCreated => _device is not null;

    public RenderWorld World => _world;

    public RenderView MainView => _view;

    public IReadOnlyList<IRenderPipeline> Pipelines => [.. _pipelines.Select(p => p.Value)];

    public RenderGraphStatistics? LastGraph { get; private set; }

    public DebugDraw Debug { get; }

    public RenderRegistration AddExtractor(ModuleContext module, IRenderExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(extractor);
        var registration = new RenderRegistration(module, r =>
        {
            _extractors.RemoveAll(e => e.Registration == r);
            _extractorsChanged = true;
        });
        _extractors.Add(new Entry<IRenderExtractor>(registration, extractor));
        _extractorsChanged = true;
        return registration;
    }

    public RenderRegistration RegisterPipeline(ModuleContext module, IRenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrEmpty(pipeline.Name);
        foreach (Entry<IRenderPipeline> other in _pipelines)
        {
            if (string.Equals(other.Value.Name, pipeline.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modules '{other.Registration.Module.Module.Id}' and '{module.Module.Id}' both register a render pipeline named '{pipeline.Name}'.");
            }
        }

        _device?.Capabilities.Require(pipeline.RequiredFeatures, $"Render pipeline '{pipeline.Name}'");
        var registration = new RenderRegistration(module, r =>
        {
            _pipelines.RemoveAll(e => e.Registration == r);
            _rebuild = true;
        });
        _pipelines.Add(new Entry<IRenderPipeline>(registration, pipeline));
        _rebuild = true;
        _context.Logger.Debug($"Render pipeline '{pipeline.Name}' registered by module '{module.Module.Id}'.");
        return registration;
    }

    public RenderRegistration Insert(ModuleContext module, string point, IRenderInsertion insertion, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrEmpty(point);
        ArgumentNullException.ThrowIfNull(insertion);
        var registration = new RenderRegistration(module, r =>
        {
            _insertions.RemoveAll(e => e.Registration == r);
            _rebuild = true;
        });
        _insertions.Add(new Insertion(registration, point, insertion, order, _sequence++));
        _insertions.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Sequence.CompareTo(b.Sequence));
        _rebuild = true;
        return registration;
    }

    public void RebuildGraph() => _rebuild = true;

    public void Start(GraphicsDevice device)
    {
        _device = device;
        foreach (Entry<IRenderPipeline> pipeline in _pipelines)
        {
            device.Capabilities.Require(pipeline.Value.RequiredFeatures, $"Render pipeline '{pipeline.Value.Name}' of module '{pipeline.Registration.Module.Module.Id}'");
        }

        _world.Interpolates = _context.Loop.Phases.Any(p => p.IsFixed);
        _commands = device.CreateCommandBuffer("nobara.renderer.frame");
        _compiler = new RenderGraphCompiler(device);
        _pass.Device = device;
        _pass.View = _view;
        if (Debug.IsEnabled)
        {
            _debugPass = new DebugDrawPass(Debug, device);
            _debugInsertion = Insert(_context, RenderInsertionPoints.BeforePostProcess, _debugPass);
        }
    }

    public void Release()
    {
        _debugInsertion?.Dispose();
        _debugPass?.Dispose();
        _compiler?.Release();
        _graph = null;
        _device = null;
    }

    public void Run(in PhaseTime time)
    {
        if (_device is not { } device)
        {
            return;
        }

        Debug.Advance(time.Delta);
        RemoveInactive();
        Extract(time);
        bool available = device.BeginFrame();
        try
        {
            if (available)
            {
                Draw(device, (float)time.Alpha, time.Frame);
            }
        }
        finally
        {
            device.EndFrame();
            Debug.Expire();
        }
    }

    // The registrations of a module that its error policy disabled are removed.
    private void RemoveInactive()
    {
        for (int i = _extractors.Count - 1; i >= 0; i--)
        {
            if (!IsActive(_extractors[i].Registration))
            {
                _extractors[i].Registration.Dispose();
            }
        }

        for (int i = _pipelines.Count - 1; i >= 0; i--)
        {
            if (!IsActive(_pipelines[i].Registration))
            {
                _pipelines[i].Registration.Dispose();
            }
        }

        for (int i = _insertions.Count - 1; i >= 0; i--)
        {
            if (!IsActive(_insertions[i].Registration))
            {
                _insertions[i].Registration.Dispose();
            }
        }
    }

    private static bool IsActive(RenderRegistration registration) => registration.Module.IsModuleActive(registration.Module.Module.Id);

    private void Extract(in PhaseTime time)
    {
        if (_extractorsChanged)
        {
            _extractorArray = [.. _extractors];
            _extractorsChanged = false;
        }

        _extraction.Frame = time.Frame;
        _extraction.Tick = time.Tick;
        _extraction.IsNewTick = time.Tick != _lastTick;
        _extraction.Alpha = time.Alpha;
        _lastTick = time.Tick;
        _world.BeginExtraction(time.Tick);
        try
        {
            foreach (Entry<IRenderExtractor> entry in _extractorArray)
            {
                if (entry.Registration.Revoked)
                {
                    continue;
                }

                try
                {
                    entry.Value.Extract(_extraction);
                }
                catch (Exception exception) when (exception is not EngineStopException)
                {
                    entry.Registration.Module.ReportFault(exception);
                }
            }
        }
        finally
        {
            _world.EndExtraction();
        }
    }

    private void Draw(GraphicsDevice device, float alpha, ulong frame)
    {
        if (ChoosePipeline() is not { } pipeline)
        {
            return;
        }

        (int width, int height) = _view.TargetTexture is { } target ? (target.Width, target.Height) : (device.Swapchain.Width, device.Swapchain.Height);
        if (!_view.SetSize(width, height))
        {
            return;
        }

        var key = new GraphKey(pipeline.Value, _view.TargetTexture, _view.TargetDepthTexture, width, height, _view.Viewport, _view.RenderScale);
        if (_rebuild || key != _graphKey)
        {
            _graphKey = key;
            _rebuild = false;
            Rebuild(device, pipeline);
        }

        if (_graph is not { } graph)
        {
            return;
        }

        _view.SetCamera(alpha);
        PrepareItems(alpha);
        Execute(device, graph, frame);
    }

    private Entry<IRenderPipeline>? ChoosePipeline()
    {
        if (_pipelines.Count == 0)
        {
            Warn("none", "No render pipeline is registered: nothing is drawn. A pipeline module registers one with IRenderer.RegisterPipeline.");
            return null;
        }

        if (_view.Pipeline is { } name)
        {
            foreach (Entry<IRenderPipeline> entry in _pipelines)
            {
                if (string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            Warn("pipeline:" + name, $"{_view} names the render pipeline '{name}', which no module registers; nRenderer uses '{_pipelines[0].Value.Name}'.");
        }

        return _pipelines[0];
    }

    private void Rebuild(GraphicsDevice device, Entry<IRenderPipeline> pipeline)
    {
        _graph = null;
        var builder = new RenderGraphBuilder(_view, device, pipeline.Registration.Module, InsertAt);
        try
        {
            pipeline.Value.Build(builder);
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            pipeline.Registration.Module.ReportFault(exception);
            return;
        }

        foreach (Insertion insertion in _insertions)
        {
            if (!builder.DeclaredPoints.Contains(insertion.Point))
            {
                Warn(
                    $"point:{pipeline.Value.Name}:{insertion.Point}:{insertion.Registration.Module.Module.Id}",
                    $"Render pipeline '{pipeline.Value.Name}' has no insertion point '{insertion.Point}': the passes that module '{insertion.Registration.Module.Module.Id}' inserts there are not drawn.");
            }
        }

        try
        {
            _graph = _compiler!.Compile(builder, pipeline.Value.Name);
        }
        catch (RenderGraphException exception)
        {
            _context.Logger.Error($"The render graph of {_view} cannot run, and nothing is drawn until it is built again: {exception.Message} The pass is declared by module '{exception.ModuleId}'.");
            return;
        }

        LastGraph = _graph.Statistics;
        _context.Logger.Debug($"Render graph of {_view} built with pipeline '{pipeline.Value.Name}': {_graph.Statistics.Passes.Count} passes, {_graph.Statistics.CulledPasses.Count} culled, {_graph.Statistics.PhysicalTextures} transient textures.");
    }

    private int InsertAt(RenderGraphBuilder builder, RenderInsertionPoint point)
    {
        int count = 0;
        ModuleContext owner = builder.Owner;
        foreach (Insertion insertion in _insertions.ToArray())
        {
            if (insertion.Point != point.Name || insertion.Registration.Revoked)
            {
                continue;
            }

            (int passes, int resources, int points) = (builder.Passes.Count, builder.Resources.Count, builder.DeclaredPoints.Count);
            builder.Owner = insertion.Registration.Module;
            try
            {
                insertion.Value.Build(builder, point);
                count++;
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                builder.Truncate(passes, resources, points);
                insertion.Registration.Module.ReportFault(exception);
            }
            finally
            {
                builder.Owner = owner;
            }
        }

        return count;
    }

    // Computes the transform of each object seen from the camera, by jobs when there are many, then lists the parts to draw.
    private void PrepareItems(float alpha)
    {
        int slots = _world.SlotCount;
        if (_matrices.Length < slots)
        {
            Array.Resize(ref _matrices, Math.Max(slots, _matrices.Length * 2));
        }

        var job = new TransformJob(_world.Slots, _matrices, _view.FrameCamera.Position, alpha, _world.Tick, _world.Interpolates);
        if (slots >= JobThreshold)
        {
            _context.Jobs.ScheduleFor(job, slots, JobBatch).Complete();
        }
        else
        {
            for (int i = 0; i < slots; i++)
            {
                job.Execute(i);
            }
        }

        RenderWorld.ObjectSlot[] all = _world.Slots;
        int capacity = 0;
        for (int i = 0; i < slots; i++)
        {
            if (all[i].Alive && all[i].Visible)
            {
                capacity += all[i].Mesh!.Submeshes.Count;
            }
        }

        RenderItem[] items = _view.ItemBuffer(capacity);
        int count = 0;
        for (int i = 0; i < slots; i++)
        {
            ref RenderWorld.ObjectSlot slot = ref all[i];
            if (!slot.Alive || !slot.Visible)
            {
                continue;
            }

            IReadOnlyList<Submesh> parts = slot.Mesh!.Submeshes;
            for (int s = 0; s < parts.Count; s++)
            {
                if ((slot.Material ?? parts[s].Material) is { } material)
                {
                    items[count++] = new RenderItem(new RenderObjectId(i, slot.Generation), slot.Mesh, s, material, _matrices[i]);
                }
            }
        }

        _view.SetItemCount(count);
    }

    private void Execute(GraphicsDevice device, CompiledGraph graph, ulong frame)
    {
        CommandBuffer commands = _commands!;
        _pass.Commands = commands;
        _pass.Frame = frame;
        _pass.SetGraph(graph);
        commands.Begin();
        GraphPass? current = null;
        try
        {
            foreach (CompiledGraph.Step step in graph.Steps)
            {
                current = step.Pass;
                _pass.PassName = step.Pass.Name;
                _pass.Framebuffer = step.Framebuffer;
                if (step.Barrier != BarrierScope.None)
                {
                    commands.Barrier(step.Barrier);
                }

                commands.PushDebugGroup(step.Pass.Name);
                step.Pass.Pass.Prepare(_pass);
                if (step.Framebuffer is { } framebuffer)
                {
                    commands.BeginRenderPass(framebuffer, step.Description);
                    if (step.OnTarget)
                    {
                        PixelRect viewport = _view.PixelViewport;
                        commands.SetViewport(new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height));
                    }

                    step.Pass.Pass.Execute(_pass);
                    commands.EndRenderPass();
                }
                else
                {
                    step.Pass.Pass.Execute(_pass);
                }

                commands.PopDebugGroup();
            }

            commands.End();
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            // The commands of the frame are dropped: a new buffer records the next one.
            _commands = device.CreateCommandBuffer("nobara.renderer.frame");
            (current?.Owner ?? _context).ReportFault(exception);
            return;
        }

        device.Submit(commands);
    }

    private void Warn(string key, string message)
    {
        if (_warnings.Add(key))
        {
            _context.Logger.Warning(message);
        }
    }

    /// <summary>A registered extractor or pipeline.</summary>
    private sealed record Entry<T>(RenderRegistration Registration, T Value);

    /// <summary>A registered insertion, ordered by rank, then by registration.</summary>
    private sealed record Insertion(RenderRegistration Registration, string Point, IRenderInsertion Value, int Order, int Sequence);

    /// <summary>What the render graph was built for; a change builds it again.</summary>
    private readonly record struct GraphKey(IRenderPipeline? Pipeline, Texture? Target, Texture? Depth, int Width, int Height, ViewRect Viewport, float RenderScale);

    /// <summary>Computes the matrix of each object seen from the camera, interpolated between ticks.</summary>
    private readonly struct TransformJob(RenderWorld.ObjectSlot[] slots, Matrix4x4[] matrices, WorldPosition origin, float alpha, ulong tick, bool interpolates) : IJobFor
    {
        [HotPath]
        public void Execute(int index)
        {
            ref RenderWorld.ObjectSlot slot = ref slots[index];
            if (!slot.Alive)
            {
                return;
            }

            RenderTransform transform = interpolates && slot.MovedTick == tick ? RenderTransform.Interpolate(slot.Previous, slot.Current, alpha) : slot.Current;
            matrices[index] = transform.ToMatrix(origin);
        }
    }
}
