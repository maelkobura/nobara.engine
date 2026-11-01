// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Configuration;
using Nobara.Graphics;
using Nobara.Renderer;

[assembly: ModuleEntry(typeof(Nobara.Ungp.UngpModule))]

namespace Nobara.Ungp;

/// <summary>
/// The module of the universal pipeline: it registers the render pipeline
/// <see cref="PipelineName"/> with nRenderer, provides the standard shader
/// and the default material, and publishes <see cref="IUngp"/>.
/// </summary>
public sealed class UngpModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.ungp";

    /// <summary>The name of the pipeline, which a view names in <see cref="RenderView.Pipeline"/>.</summary>
    public const string PipelineName = "ungp";

    /// <summary>The identifier of the standard shader: meshes at full brightness, textured or of one color.</summary>
    public const string StandardShader = "shader:nobara.ungp.standard";

    /// <summary>The variant of the standard shader for cut-out materials: it discards the texels whose alpha is under the parameter <c>AlphaCutoff</c>.</summary>
    public const string CutoutVariant = "Cutout";

    /// <summary>The identifier of the default material: the standard shader, white, opaque.</summary>
    public const string DefaultMaterial = "material:nobara.ungp.default";

    private UniversalPipeline? _pipeline;
    private RenderRegistration? _registration;
    private RenderRegistration? _extractor;
    private SettingSubscription? _settings;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _pipeline = new UniversalPipeline(context);
        context.Publish<IUngp>(_pipeline);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        IRenderer renderer = context.Resolve<IRenderer>();
        _pipeline!.Initialize(renderer);
        _registration = renderer.RegisterPipeline(context, _pipeline);
        _extractor = renderer.AddExtractor(context, _pipeline.ViewSettings);
        _settings = context.Settings.Subscribe(_pipeline.SettingChanged);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) =>
        _pipeline!.Start(context.Resolve<IGraphicsSystem>().Device, context.Resolve<IRenderResources>());

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _settings?.Dispose();
        _extractor?.Dispose();
        _registration?.Dispose();
        _pipeline?.Dispose();
    }
}
