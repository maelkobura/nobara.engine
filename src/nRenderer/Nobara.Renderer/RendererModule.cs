// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Graphics;

[assembly: ModuleEntry(typeof(Nobara.Renderer.RendererModule))]

namespace Nobara.Renderer;

/// <summary>
/// The module of the renderer: it defines the resource types <c>Shader</c>,
/// <c>Material</c>, <c>Mesh</c>, and <c>Texture</c>, decodes them once per
/// process, creates their GPU objects when it starts, and publishes
/// <see cref="IRenderResources"/>. It draws the frames in the phase
/// <c>Render</c> of the client loop, and publishes <see cref="IRenderer"/>
/// and <see cref="IDebugDraw"/>.
/// </summary>
public sealed class RendererModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.renderer";

    /// <summary>The type prefix of the shader identifiers: <c>shader</c>.</summary>
    public const string ShaderType = RenderRules.ShaderType;

    /// <summary>The type prefix of the material identifiers: <c>material</c>.</summary>
    public const string MaterialType = RenderRules.MaterialType;

    /// <summary>The type prefix of the mesh identifiers: <c>mesh</c>.</summary>
    public const string MeshType = RenderRules.MeshType;

    /// <summary>The type prefix of the texture identifiers: <c>texture</c>.</summary>
    public const string TextureType = RenderRules.TextureType;

    private RenderResources? _resources;
    private Renderer? _renderer;
    private GraphicsDevice? _device;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _resources = new RenderResources(context);
        RenderAssets.Register(context.Resources);
        context.Resources.RegisterReloadHandler(TextureType, _resources.ReloadTextures);
        context.Resources.RegisterReloadHandler(ShaderType, _resources.ReloadShaders);
        context.Resources.RegisterReloadHandler(MaterialType, _resources.ReloadMaterials);
        context.Resources.RegisterReloadHandler(MeshType, _resources.ReloadMeshes);
        context.Publish<IRenderResources>(_resources);

        _renderer = new Renderer(context);
        context.Publish<IRenderer>(_renderer);
        context.Publish<IDebugDraw>(_renderer.Debug);
        LoopPhase render = context.Loop.FindPhase(Renderer.Phase) is { IsFixed: false } phase
            ? phase
            : throw new InvalidOperationException($"Module '{ModuleId}' draws in the phase '{Renderer.Phase}', and the client loop has no such phase that runs once per frame; declare it in <Loop>.");
        context.Loop.Subscribe(render.Name, _renderer);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        GraphicsDevice device = context.Resolve<IGraphicsSystem>().Device;
        _resources!.Start(device);
        _renderer!.Start(device);
        _device = device;
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _renderer?.Release();
        _resources?.Release();

        // The frames in flight finish, so that the device destroys what nRenderer released before nGraphics is released.
        _device?.WaitIdle();
    }
}
