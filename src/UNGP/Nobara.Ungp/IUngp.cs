// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Ungp;

/// <summary>
/// The universal pipeline: its passes that can be turned off, and the
/// settings of its frames. Resolved with <c>context.Resolve&lt;IUngp&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The configuration keys of UNGP (<see cref="UngpSettings"/>) set
/// <see cref="RenderScale"/>, <see cref="SkyColor"/>, and
/// <see cref="Exposure"/> when the game starts and whenever they change; the
/// properties change them for the session, until the key changes again.
/// </para>
/// <para>
/// Turning a pass on or off builds the render graph again at the next frame.
/// The other settings apply at the next frame; <see cref="RenderScale"/>
/// builds the graph again too, since it changes the size of its textures.
/// Use the service on the loop thread of the client context.
/// </para>
/// </remarks>
public interface IUngp
{
    /// <summary>Gets the pipeline, registered with nRenderer under the name <see cref="UngpModule.PipelineName"/>.</summary>
    IRenderPipeline Pipeline { get; }

    /// <summary>Gets or sets whether the depth prepass runs: it writes the depth of opaque and cut-out geometry before the opaque pass, so that this pass shades each pixel once. On by default.</summary>
    bool DepthPrepass { get; set; }

    /// <summary>
    /// Gets or sets whether chunk sections hidden by other geometry are left
    /// out: the pass <see cref="UngpPasses.DepthPyramid"/> builds the depth
    /// pyramid that the culling of the next frame tests, and the visibility
    /// graph of nRenderer leaves out the sections that closed chunks hide. On
    /// by default. Off, the culling pass still leaves out the sections outside
    /// the view; the image does not change, only the number of sections drawn.
    /// </summary>
    /// <remarks>
    /// Setting it sets <see cref="ChunkGeometry.OcclusionCulling"/> and
    /// <see cref="ChunkGeometry.VisibilityGraph"/> of <see cref="IRenderer.Chunks"/>,
    /// which can still be set one by one afterward.
    /// </remarks>
    bool OcclusionCulling { get; set; }

    /// <summary>Gets or sets whether the sky pass runs: it fills the pixels where no geometry is with <see cref="SkyColor"/>. On by default; off, these pixels are black.</summary>
    bool Sky { get; set; }

    /// <summary>Gets or sets whether the colors are tonemapped from HDR to the target. On by default; off, they are clamped to 1.</summary>
    bool Tonemapping { get; set; }

    /// <summary>Gets or sets the color of the sky, linear, set by the key <see cref="UngpSettings.SkyColor"/>; its alpha is ignored. Values above 1 are HDR, and tonemapped.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component of the color is negative or not finite.</exception>
    ColorF SkyColor { get; set; }

    /// <summary>Gets or sets the factor applied to the HDR colors before tonemapping or clamping, set by the key <see cref="UngpSettings.Exposure"/>; 1 keeps them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    float Exposure { get; set; }

    /// <summary>Gets or sets the render resolution as a fraction of the viewport, from 0.1 to 4, set by the key <see cref="UngpSettings.RenderScale"/>. UNGP applies it to the view it draws; the image is scaled to the viewport at the output.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0.1 to 4.</exception>
    float RenderScale { get; set; }
}

/// <summary>The names of the passes of UNGP, in the order they run, as <see cref="RenderGraphStatistics.Passes"/> lists them.</summary>
public static class UngpPasses
{
    /// <summary>The culling pass, a compute pass: it tests the chunk sections against the view and the depth pyramid of the previous frame, and lists the draws of the visible ones.</summary>
    public const string Culling = "nobara.ungp.culling";

    /// <summary>The depth prepass: the depth of opaque and cut-out geometry, without color.</summary>
    public const string DepthPrepass = "nobara.ungp.depth-prepass";

    /// <summary>The opaque pass: opaque, then cut-out geometry, to the HDR scene.</summary>
    public const string Opaque = "nobara.ungp.opaque";

    /// <summary>The depth pyramid pass, a compute pass: from the depth of the scene, for the occlusion culling of the next frame. Absent when <see cref="IUngp.OcclusionCulling"/> is off.</summary>
    public const string DepthPyramid = "nobara.ungp.depth-pyramid";

    /// <summary>The sky pass: the plain sky where no geometry is.</summary>
    public const string Sky = "nobara.ungp.sky";

    /// <summary>The transparent pass: the transparent faces of chunks, then the transparent objects, each from the farthest to the nearest, blended over the scene.</summary>
    public const string Transparent = "nobara.ungp.transparent";

    /// <summary>The output pass with tonemapping: from the HDR scene to the target of the view, scaled to the viewport.</summary>
    public const string Tonemap = "nobara.ungp.tonemap";

    /// <summary>The output pass without tonemapping: the HDR scene clamped to the target of the view, scaled to the viewport.</summary>
    public const string Output = "nobara.ungp.output";
}
