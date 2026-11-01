// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Ungp;

/// <summary>The render pipeline of UNGP: it declares the passes of a frame, and holds the settings of <see cref="IUngp"/>.</summary>
internal sealed class UniversalPipeline : IRenderPipeline, IUngp, IDisposable
{
    /// <summary>The format of the HDR scene color.</summary>
    public const PixelFormat SceneFormat = PixelFormat.Rgba16Float;

    /// <summary>The format of the scene depth, reversed.</summary>
    public const PixelFormat DepthFormat = PixelFormat.Depth32Float;

    private readonly ModuleContext _context;
    private IRenderer? _renderer;
    private SceneDraws? _scene;
    private SceneGeometryPass? _prepass;
    private SceneGeometryPass? _opaque;
    private SkyPass? _sky;
    private OutputPass? _output;
    private bool _depthPrepass = true;
    private bool _skyEnabled = true;
    private bool _tonemapping = true;
    private float _exposure = 1;
    private float _renderScale = 1;

    public UniversalPipeline(ModuleContext context)
    {
        _context = context;
        ViewSettings = new ViewSettingsExtractor(this);
        if (UngpSettings.TryParseColor(UngpSettings.DefaultSkyColor, out ColorF sky))
        {
            SkyColor = sky;
        }
    }

    public string Name => UngpModule.PipelineName;

    // UNGP needs no optional feature of the device in this version: no compute, no storage buffers, no indirect draws.
    public GraphicsFeatures RequiredFeatures => GraphicsFeatures.None;

    public IRenderPipeline Pipeline => this;

    /// <summary>Gets the extractor that applies the render scale to the view that UNGP draws, before each frame.</summary>
    public IRenderExtractor ViewSettings { get; }

    public bool DepthPrepass
    {
        get => _depthPrepass;
        set => SetPass(ref _depthPrepass, value);
    }

    public bool Sky
    {
        get => _skyEnabled;
        set => SetPass(ref _skyEnabled, value);
    }

    public bool Tonemapping
    {
        get => _tonemapping;
        set => SetPass(ref _tonemapping, value);
    }

    public ColorF SkyColor { get; set; }

    public float Exposure
    {
        get => _exposure;
        set
        {
            if (!(value > 0 && float.IsFinite(value)))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"An exposure is positive and finite; got {value}.");
            }

            _exposure = value;
        }
    }

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

    /// <summary>Reads the configuration keys, once nRenderer is known.</summary>
    public void Initialize(IRenderer renderer)
    {
        _renderer = renderer;
        SettingChanged(UngpSettings.RenderScale);
        SettingChanged(UngpSettings.SkyColor);
        SettingChanged(UngpSettings.Exposure);
    }

    /// <summary>Creates the GPU objects of the passes from the shaders of the module.</summary>
    public void Start(GraphicsDevice device, IRenderResources resources)
    {
        _scene = new SceneDraws(_context, device);
        _prepass = new SceneGeometryPass(_scene, SceneDrawKind.DepthOnly);
        _opaque = new SceneGeometryPass(_scene, SceneDrawKind.Full);
        _sky = new SkyPass(this, device, resources.GetShader(SkyPass.ShaderId));
        _output = new OutputPass(this, device, resources.GetShader(OutputPass.ShaderId));
    }

    public void Build(RenderGraphBuilder graph)
    {
        if (_scene is null)
        {
            throw new InvalidOperationException("UNGP builds a render graph once its module has started.");
        }

        RenderGraphTexture scene = graph.CreateTexture("nobara.ungp.scene", new RenderGraphTextureDescription(SceneFormat));
        RenderGraphTexture depth = graph.CreateTexture("nobara.ungp.depth", new RenderGraphTextureDescription(DepthFormat));

        // Later: culling of chunks and objects on the GPU.
        if (_depthPrepass)
        {
            graph.AddRasterPass(UngpPasses.DepthPrepass, _prepass!).Depth(depth, LoadAction.Clear);
        }

        // Later: clusters of dynamic lights, then shadows of the sun.
        _opaque!.Kind = _depthPrepass ? SceneDrawKind.AfterPrepass : SceneDrawKind.Full;
        graph.AddRasterPass(UngpPasses.Opaque, _opaque)
            .Color(scene, LoadAction.Clear, new ColorF(0, 0, 0, 1))
            .Depth(depth, _depthPrepass ? LoadAction.Load : LoadAction.Clear);
        graph.InsertionPoint(RenderInsertionPoints.AfterOpaque, scene, depth);

        if (_skyEnabled)
        {
            graph.AddRasterPass(UngpPasses.Sky, _sky!).Color(scene).Depth(depth, write: false);
        }

        // Later: transparent geometry, sorted.
        graph.InsertionPoint(RenderInsertionPoints.BeforePostProcess, scene, depth);

        // Later: fog, bloom, and color grading, before tonemapping.
        _output!.Clamp = !_tonemapping;
        _output.Scene = scene;
        LoadAction load = graph.View.Viewport == ViewRect.Full ? LoadAction.DontCare : LoadAction.Load;
        graph.AddRasterPass(_tonemapping ? UngpPasses.Tonemap : UngpPasses.Output, _output).Read(scene).Color(graph.Target, load);
        graph.InsertionPoint(RenderInsertionPoints.Ui, graph.Target);
    }

    /// <summary>Applies a change of a configuration key of UNGP.</summary>
    public void SettingChanged(string key)
    {
        switch (key)
        {
            case UngpSettings.RenderScale:
                RenderScale = (float)_context.Settings.GetFloat(UngpSettings.RenderScale);
                break;
            case UngpSettings.Exposure:
                Exposure = (float)_context.Settings.GetFloat(UngpSettings.Exposure);
                break;
            case UngpSettings.SkyColor:
                string value = _context.Settings.GetString(UngpSettings.SkyColor);
                if (UngpSettings.TryParseColor(value, out ColorF color))
                {
                    SkyColor = color;
                }
                else
                {
                    _context.Logger.Warning($"'{UngpSettings.SkyColor}' is '{value}', which is not a color written #RRGGBB; the sky keeps its color.");
                }

                break;
        }
    }

    public void Dispose()
    {
        _output?.Dispose();
        _sky?.Dispose();
        _scene?.Dispose();
    }

    private void SetPass(ref bool field, bool value)
    {
        if (field != value)
        {
            field = value;
            _renderer?.RebuildGraph();
        }
    }

    /// <summary>Whether UNGP draws a view: the view names it, or names no pipeline and UNGP was registered first.</summary>
    private bool Draws(RenderView view)
    {
        IReadOnlyList<IRenderPipeline> pipelines = _renderer!.Pipelines;
        if (view.Pipeline is { } name)
        {
            for (int i = 0; i < pipelines.Count; i++)
            {
                if (string.Equals(pipelines[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return ReferenceEquals(pipelines[i], this);
                }
            }
        }

        return pipelines.Count > 0 && ReferenceEquals(pipelines[0], this);
    }

    /// <summary>Gives the view that UNGP draws its render scale, before the frame; nRenderer builds the graph again when it changes.</summary>
    private sealed class ViewSettingsExtractor(UniversalPipeline pipeline) : IRenderExtractor
    {
        public void Extract(RenderExtraction extraction)
        {
            RenderView view = extraction.MainView;
            if (view.RenderScale != pipeline._renderScale && pipeline.Draws(view))
            {
                view.RenderScale = pipeline._renderScale;
            }
        }
    }
}
