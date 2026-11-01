// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>
/// The frames of nRenderer: the render world, the view, the pipelines, the
/// extractors that fill the world, and the passes that modules insert in every
/// pipeline. Resolved with <c>context.Resolve&lt;IRenderer&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// nRenderer draws a frame in the phase <c>Render</c> of the client loop:
/// it calls the extractors, which copy the changes of the frame to the render
/// world, begins the frame of the GPU, builds the render graph of the view
/// again when needed, computes the transforms of the objects seen from the
/// camera, runs the passes, and ends the frame. Without a pipeline, nothing is
/// drawn, and a warning says so once.
/// </para>
/// <para>Use it on the loop thread of the client context. Registrations are allowed from the initialization of a module on; each one returns a handle that removes it.</para>
/// </remarks>
public interface IRenderer
{
    /// <summary>Gets whether nRenderer started: it has a device and draws frames.</summary>
    bool IsCreated { get; }

    /// <summary>Gets the render world. It changes during extraction only.</summary>
    RenderWorld World { get; }

    /// <summary>Gets the view that nRenderer draws.</summary>
    RenderView MainView { get; }

    /// <summary>Gets the pipelines registered, in order.</summary>
    IReadOnlyList<IRenderPipeline> Pipelines { get; }

    /// <summary>Gets what the last build of the render graph produced, or <see langword="null"/> before the first.</summary>
    RenderGraphStatistics? LastGraph { get; }

    /// <summary>Registers an extractor, called at every frame, in the order of registration, to copy changes to the render world.</summary>
    /// <param name="module">The module of the extractor, charged with its exceptions.</param>
    /// <param name="extractor">The extractor.</param>
    /// <returns>A handle that removes the extractor.</returns>
    RenderRegistration AddExtractor(ModuleContext module, IRenderExtractor extractor);

    /// <summary>Registers a render pipeline.</summary>
    /// <param name="module">The module of the pipeline, charged with its exceptions.</param>
    /// <param name="pipeline">The pipeline.</param>
    /// <returns>A handle that removes the pipeline.</returns>
    /// <exception cref="InvalidOperationException">Another pipeline has the same name.</exception>
    /// <exception cref="NotSupportedException">The device lacks a feature that the pipeline requires; checked when nRenderer starts for a pipeline registered before.</exception>
    RenderRegistration RegisterPipeline(ModuleContext module, IRenderPipeline pipeline);

    /// <summary>Inserts passes at an insertion point of every pipeline, from the next build of the graph on.</summary>
    /// <param name="module">The module of the insertion, charged with its exceptions.</param>
    /// <param name="point">The name of the point, such as <see cref="RenderInsertionPoints.Ui"/>.</param>
    /// <param name="insertion">The insertion.</param>
    /// <param name="order">The rank at the point: lower ranks come first, and equal ranks keep the order of registration. Engines of user interface use it to order themselves.</param>
    /// <returns>A handle that removes the insertion.</returns>
    RenderRegistration Insert(ModuleContext module, string point, IRenderInsertion insertion, int order = 0);

    /// <summary>Asks for the render graph to be built again at the next frame, after a pipeline changed its passes.</summary>
    void RebuildGraph();
}

/// <summary>
/// Copies what a module draws to the render world, at every frame, before
/// the frame is drawn. A module registers it with
/// <see cref="IRenderer.AddExtractor"/>.
/// </summary>
/// <remarks>
/// The world keeps its objects from frame to frame: an extractor copies what
/// changed since its previous call, such as the objects that moved during the
/// ticks of the frame (<see cref="RenderExtraction.IsNewTick"/>).
/// </remarks>
public interface IRenderExtractor
{
    /// <summary>Copies the changes of the module to the render world.</summary>
    /// <param name="extraction">The world, the view, and where the loop is.</param>
    [LoopCallback]
    void Extract(RenderExtraction extraction);
}

/// <summary>What an extractor receives: the render world, the view, and where the loop is.</summary>
/// <remarks>The object is reused at every frame: do not keep it.</remarks>
public sealed class RenderExtraction
{
    internal RenderExtraction(RenderWorld world, RenderView view)
    {
        World = world;
        MainView = view;
    }

    /// <summary>Gets the render world, writable during extraction.</summary>
    public RenderWorld World { get; }

    /// <summary>Gets the view, whose camera an extractor may move.</summary>
    public RenderView MainView { get; }

    /// <summary>Gets the number of the frame.</summary>
    public ulong Frame { get; internal set; }

    /// <summary>Gets the number of the last tick of the simulation.</summary>
    public ulong Tick { get; internal set; }

    /// <summary>Gets whether at least one tick ran since the previous extraction: objects that the simulation moved have new transforms.</summary>
    public bool IsNewTick { get; internal set; }

    /// <summary>Gets the fraction of tick that the loop has accumulated, from 0 to 1, by which moved objects are interpolated.</summary>
    public double Alpha { get; internal set; }
}

/// <summary>A registration with nRenderer: an extractor, a pipeline, or an insertion. Disposing it removes the registration.</summary>
public sealed class RenderRegistration : IDisposable
{
    private readonly Action<RenderRegistration> _remove;

    internal RenderRegistration(ModuleContext module, Action<RenderRegistration> remove)
    {
        Module = module;
        _remove = remove;
    }

    /// <summary>Gets whether the registration is still in effect.</summary>
    public bool IsActive => !Revoked;

    internal ModuleContext Module { get; }

    internal bool Revoked { get; private set; }

    /// <summary>Removes the registration.</summary>
    public void Dispose()
    {
        if (!Revoked)
        {
            Revoked = true;
            _remove(this);
        }
    }
}
