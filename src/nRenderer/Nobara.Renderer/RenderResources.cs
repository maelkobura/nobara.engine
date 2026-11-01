// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Graphics;
using S = Nobara.Renderer.Schemas;

namespace Nobara.Renderer;

/// <summary>The GPU objects of the rendering resources of one context.</summary>
internal sealed class RenderResources : IRenderResources
{
    private readonly ModuleContext _context;
    private readonly List<TextureArray> _arrays = [];
    private readonly Dictionary<string, RenderTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderShader> _shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderMaterial> _materials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderMesh> _meshes = new(StringComparer.Ordinal);

    // Resource identifiers, in lowercase, to object identifiers.
    private readonly Dictionary<string, string> _resourceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<RenderShader, ShaderState> _shaderStates = [];
    private readonly Dictionary<RenderMaterial, MaterialState> _materialStates = [];
    private readonly Dictionary<RenderMesh, MeshState> _meshStates = [];
    private readonly Dictionary<TextureFallback, (Texture Texture, Sampler Sampler)> _fallbacks = [];
    private GraphicsDevice? _device;
    private RenderTexture[] _textureList = [];
    private RenderShader[] _shaderList = [];
    private RenderMaterial[] _materialList = [];
    private RenderMesh[] _meshList = [];

    public RenderResources(ModuleContext context)
    {
        _context = context;
    }

    public bool IsCreated => _device is not null;

    public IReadOnlyList<TextureArray> TextureArrays => _arrays;

    public IReadOnlyList<RenderTexture> Textures => _textureList;

    public IReadOnlyList<RenderShader> Shaders => _shaderList;

    public IReadOnlyList<RenderMaterial> Materials => _materialList;

    public IReadOnlyList<RenderMesh> Meshes => _meshList;

    private GraphicsDevice Device => _device ?? throw new InvalidOperationException("The rendering resources are created when nRenderer starts.");

    public bool TryGetTexture(string id, [NotNullWhen(true)] out RenderTexture? texture) => TryGet(_textures, id, out texture);

    public bool TryGetShader(string id, [NotNullWhen(true)] out RenderShader? shader) => TryGet(_shaders, id, out shader);

    public bool TryGetMaterial(string id, [NotNullWhen(true)] out RenderMaterial? material) => TryGet(_materials, id, out material);

    public bool TryGetMesh(string id, [NotNullWhen(true)] out RenderMesh? mesh) => TryGet(_meshes, id, out mesh);

    public RenderTexture GetTexture(string id) => TryGetTexture(id, out RenderTexture? value) ? value : throw NotFound(id);

    public RenderShader GetShader(string id) => TryGetShader(id, out RenderShader? value) ? value : throw NotFound(id);

    public RenderMaterial GetMaterial(string id) => TryGetMaterial(id, out RenderMaterial? value) ? value : throw NotFound(id);

    public RenderMesh GetMesh(string id) => TryGetMesh(id, out RenderMesh? value) ? value : throw NotFound(id);

    /// <summary>Creates the GPU objects of every resource.</summary>
    /// <exception cref="RenderResourceException">Resources cannot be used; nothing is created.</exception>
    public void Start(GraphicsDevice device)
    {
        _device = device;
        var problems = new List<string>();
        try
        {
            CreateFallbacks();
            CreateTextureArrays(problems);

            foreach (ShaderAsset asset in Sorted(_context.Resources.GetAll<ShaderAsset>(RenderRules.ShaderType), a => a.Data))
            {
                var shader = new RenderShader(asset.Data.ObjectId);
                if (BuildShader(asset, problems) is ShaderState state)
                {
                    Apply(shader, state);
                    Add(_shaders, shader, asset.Data);
                }
            }

            foreach (MaterialAsset asset in Sorted(_context.Resources.GetAll<MaterialAsset>(RenderRules.MaterialType), a => a.Data))
            {
                var material = new RenderMaterial(asset.Data.ObjectId);
                if (BuildMaterial(asset, null, problems) is MaterialState state)
                {
                    Apply(material, state);
                    Add(_materials, material, asset.Data);
                }
            }

            foreach (MeshAsset asset in Sorted(_context.Resources.GetAll<MeshAsset>(RenderRules.MeshType), a => a.Data))
            {
                var mesh = new RenderMesh(asset.Data.ObjectId);
                if (BuildMesh(asset, problems) is MeshState state)
                {
                    Apply(mesh, state);
                    Add(_meshes, mesh, asset.Data);
                }
            }

            if (problems.Count > 0)
            {
                throw new RenderResourceException(problems);
            }
        }
        catch
        {
            Release();
            throw;
        }

        RefreshLists();
        _context.Logger.Info($"Created {_textureList.Length} textures in {_arrays.Count} texture arrays, {_shaderList.Length} shaders, {_materialList.Length} materials, and {_meshList.Length} meshes.");
    }

    /// <summary>Destroys the GPU objects.</summary>
    public void Release()
    {
        foreach (ShaderState state in _shaderStates.Values)
        {
            state.Dispose();
        }

        foreach (MaterialState state in _materialStates.Values)
        {
            state.Dispose();
        }

        foreach (MeshState state in _meshStates.Values)
        {
            state.Dispose();
        }

        foreach (TextureArray array in _arrays)
        {
            array.Texture?.Dispose();
            array.Sampler?.Dispose();
        }

        foreach ((Texture texture, Sampler sampler) in _fallbacks.Values)
        {
            texture.Dispose();
            sampler.Dispose();
        }

        _shaderStates.Clear();
        _materialStates.Clear();
        _meshStates.Clear();
        _fallbacks.Clear();
        _arrays.Clear();
        _textures.Clear();
        _shaders.Clear();
        _materials.Clear();
        _meshes.Clear();
        _resourceIds.Clear();
        RefreshLists();
        _device = null;
    }

    /// <summary>Applies a reload of textures: changed texels are written again in place.</summary>
    public void ReloadTextures(ResourceReload reload)
    {
        if (_device is null)
        {
            return;
        }

        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new texture needs a restart of the game, which places the textures in their arrays");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed texture needs a restart of the game, which places the textures in their arrays");
        }

        var changed = reload.Changed.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        foreach (TextureAsset asset in _context.Resources.GetAll<TextureAsset>(RenderRules.TextureType))
        {
            if (!changed.Contains(asset.Data.ObjectId) || !_textures.TryGetValue(asset.Data.ObjectId, out RenderTexture? texture))
            {
                continue;
            }

            TextureArray array = texture.Array;
            if (asset.ArrayKey != (array.Width, array.Height, array.MipLevels, array.Filter, array.ColorSpace))
            {
                reload.Refuse(asset.Data.ObjectId, $"its size or settings change ({asset.Image.Width}x{asset.Image.Height}, {asset.Image.Levels.Length} mip levels, {asset.Filter}, {asset.ColorSpace}), which moves it to another texture array and needs a restart of the game");
                continue;
            }

            WriteLayer(array.Texture, texture.Location.Layer, asset.Image);
            texture.Version++;
        }
    }

    /// <summary>Applies a reload of shaders: the GPU objects are created again, with those of the materials that use them.</summary>
    public void ReloadShaders(ResourceReload reload)
    {
        if (_device is null)
        {
            return;
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed shader needs a restart of the game");
        }

        var added = reload.Added.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        var changed = reload.Changed.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        MaterialAsset[] materials = [.. _context.Resources.GetAll<MaterialAsset>(RenderRules.MaterialType)];
        bool listsChanged = false;
        foreach (ShaderAsset asset in _context.Resources.GetAll<ShaderAsset>(RenderRules.ShaderType))
        {
            string id = asset.Data.ObjectId;
            if (!added.Contains(id) && !changed.Contains(id))
            {
                continue;
            }

            var problems = new List<string>();
            if (BuildShader(asset, problems) is not ShaderState state)
            {
                reload.Refuse(id, string.Join(" ", problems));
                continue;
            }

            if (!_shaders.TryGetValue(id, out RenderShader? shader))
            {
                shader = new RenderShader(id);
                Apply(shader, state);
                Add(_shaders, shader, asset.Data);
                listsChanged = true;

                // Materials added with the shader wait for it.
                foreach (MaterialAsset material in materials.Where(m => m.Content.Shader == id && !_materials.ContainsKey(m.Data.ObjectId)))
                {
                    if (BuildMaterial(material, null, problems) is MaterialState materialState)
                    {
                        var created = new RenderMaterial(material.Data.ObjectId);
                        Apply(created, materialState);
                        Add(_materials, created, material.Data);
                    }
                }

                foreach (string problem in problems)
                {
                    _context.Logger.Warning($"Hot reload: {problem}");
                }

                continue;
            }

            // The materials of the shader are built against the new shader before anything changes.
            var rebuilt = new List<(RenderMaterial Material, MaterialState State)>();
            foreach (MaterialAsset material in materials.Where(m => m.Content.Shader == id && _materials.ContainsKey(m.Data.ObjectId)))
            {
                if (BuildMaterial(material, (shader, state), problems) is MaterialState materialState)
                {
                    rebuilt.Add((_materials[material.Data.ObjectId], materialState));
                }
            }

            if (problems.Count > 0)
            {
                state.Dispose();
                foreach ((_, MaterialState materialState) in rebuilt)
                {
                    materialState.Dispose();
                }

                reload.Refuse(id, $"its materials do not fit it: {string.Join(" ", problems)}");
                continue;
            }

            Apply(shader, state);
            shader.Version++;
            foreach ((RenderMaterial material, MaterialState materialState) in rebuilt)
            {
                Apply(material, materialState);
                material.Version++;
            }
        }

        if (listsChanged)
        {
            RefreshLists();
        }
    }

    /// <summary>Applies a reload of materials: each is built again, or keeps its previous form when it does not fit its shader.</summary>
    public void ReloadMaterials(ResourceReload reload)
    {
        if (_device is null)
        {
            return;
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed material needs a restart of the game");
        }

        var added = reload.Added.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        var changed = reload.Changed.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        bool listsChanged = false;
        foreach (MaterialAsset asset in _context.Resources.GetAll<MaterialAsset>(RenderRules.MaterialType))
        {
            string id = asset.Data.ObjectId;
            if (!added.Contains(id) && !changed.Contains(id))
            {
                continue;
            }

            // A shader added or changed in the same reload builds its materials itself.
            if (_shaders.TryGetValue(asset.Content.Shader, out RenderShader? shader)
                ? IsStale(shader)
                : _context.Resources.Find(asset.Content.Shader) is not null)
            {
                continue;
            }

            var problems = new List<string>();
            if (BuildMaterial(asset, null, problems) is not MaterialState state)
            {
                reload.Refuse(id, string.Join(" ", problems));
                continue;
            }

            if (_materials.TryGetValue(id, out RenderMaterial? material))
            {
                Apply(material, state);
                material.Version++;
            }
            else
            {
                material = new RenderMaterial(id);
                Apply(material, state);
                Add(_materials, material, asset.Data);
                listsChanged = true;
            }
        }

        if (listsChanged)
        {
            RefreshLists();
        }
    }

    /// <summary>Applies a reload of meshes: each is uploaded again.</summary>
    public void ReloadMeshes(ResourceReload reload)
    {
        if (_device is null)
        {
            return;
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed mesh needs a restart of the game");
        }

        var added = reload.Added.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        var changed = reload.Changed.Select(d => d.ObjectId).ToHashSet(StringComparer.Ordinal);
        bool listsChanged = false;
        foreach (MeshAsset asset in _context.Resources.GetAll<MeshAsset>(RenderRules.MeshType))
        {
            string id = asset.Data.ObjectId;
            if (!added.Contains(id) && !changed.Contains(id))
            {
                continue;
            }

            var problems = new List<string>();
            if (BuildMesh(asset, problems) is not MeshState state)
            {
                reload.Refuse(id, string.Join(" ", problems));
                continue;
            }

            if (_meshes.TryGetValue(id, out RenderMesh? mesh))
            {
                Apply(mesh, state);
                mesh.Version++;
            }
            else
            {
                mesh = new RenderMesh(id);
                Apply(mesh, state);
                Add(_meshes, mesh, asset.Data);
                listsChanged = true;
            }
        }

        if (listsChanged)
        {
            RefreshLists();
        }
    }

    private static IEnumerable<T> Sorted<T>(IEnumerable<T> assets, Func<T, ResourceData> data) =>
        assets.OrderBy(a => data(a).ObjectId, StringComparer.Ordinal);

    private static KeyNotFoundException NotFound(string id) => new($"No loaded module defines '{id}'.");

    private bool TryGet<T>(Dictionary<string, T> objects, string id, [NotNullWhen(true)] out T? value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        if (objects.TryGetValue(id, out value))
        {
            return true;
        }

        value = id.Contains("://", StringComparison.Ordinal) && _resourceIds.TryGetValue(id.ToLowerInvariant(), out string? objectId)
            ? objects.GetValueOrDefault(objectId)
            : null;
        return value is not null;
    }

    private void Add<T>(Dictionary<string, T> objects, T value, ResourceData data)
    {
        objects[data.ObjectId] = value;
        _resourceIds[data.ResourceId.ToLowerInvariant()] = data.ObjectId;
    }

    private void RefreshLists()
    {
        _textureList = [.. _textures.Values.OrderBy(t => t.ObjectId, StringComparer.Ordinal)];
        _shaderList = [.. _shaders.Values.OrderBy(s => s.ObjectId, StringComparer.Ordinal)];
        _materialList = [.. _materials.Values.OrderBy(m => m.ObjectId, StringComparer.Ordinal)];
        _meshList = [.. _meshes.Values.OrderBy(m => m.ObjectId, StringComparer.Ordinal)];
    }

    private bool IsStale(RenderShader shader) =>
        _context.Resources.Find(shader.ObjectId) is ResourceData data && _shaderStates.TryGetValue(shader, out ShaderState? state)
        && !data.ContentHash.Span.SequenceEqual(state.ContentHash);

    // Textures ---------------------------------------------------------------

    private void CreateFallbacks()
    {
        foreach ((TextureFallback fallback, TextureImage image) in new[]
        {
            (TextureFallback.White, TextureImage.Solid(1, 255, 255, 255, 255)),
            (TextureFallback.Black, TextureImage.Solid(1, 0, 0, 0, 255)),
            (TextureFallback.Normal, TextureImage.Solid(1, 128, 128, 255, 255)),
        })
        {
            Texture texture = Device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, PixelFormat.Rgba8Unorm, 1, 1, 1, 1, TextureUsage.Sampled)
            {
                Name = $"renderer fallback {fallback}",
            });
            WriteLayer(texture, 0, image);
            _fallbacks[fallback] = (texture, Device.CreateSampler(SamplerDescription.Point with { Name = $"renderer fallback {fallback}" }));
        }
    }

    private void CreateTextureArrays(List<string> problems)
    {
        int maxLayers = Math.Max(1, Device.Capabilities.MaxTextureArrayLayers);
        var groups = _context.Resources.GetAll<TextureAsset>(RenderRules.TextureType)
            .GroupBy(t => t.ArrayKey)
            .OrderBy(g => g.Key.Width)
            .ThenBy(g => g.Key.Height)
            .ThenBy(g => g.Key.MipLevels)
            .ThenBy(g => g.Key.Filter)
            .ThenBy(g => g.Key.ColorSpace);
        foreach (var group in groups)
        {
            if (group.Key.Width > Device.Capabilities.MaxTextureSize || group.Key.Height > Device.Capabilities.MaxTextureSize)
            {
                problems.AddRange(group.Select(t => $"{t.Data}: the texture is {t.Image.Width}x{t.Image.Height}, larger than the {Device.Capabilities.MaxTextureSize} texels the device supports."));
                continue;
            }

            TextureAsset[] textures = [.. group.OrderBy(t => t.Data.ObjectId, StringComparer.Ordinal)];
            for (int first = 0; first < textures.Length; first += maxLayers)
            {
                TextureAsset[] layers = textures[first..Math.Min(textures.Length, first + maxLayers)];
                var array = new TextureArray(_arrays.Count, group.Key.Width, group.Key.Height, group.Key.MipLevels, group.Key.Filter, group.Key.ColorSpace);
                PixelFormat format = group.Key.ColorSpace == TextureColorSpace.Srgb ? PixelFormat.Rgba8UnormSrgb : PixelFormat.Rgba8Unorm;
                array.Texture = Device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, format, array.Width, array.Height, layers.Length, array.MipLevels, TextureUsage.Sampled)
                {
                    Name = $"renderer textures {array.Index} ({array.Width}x{array.Height})",
                });
                FilterMode mode = group.Key.Filter == TextureFilter.Nearest ? FilterMode.Nearest : FilterMode.Linear;
                array.Sampler = Device.CreateSampler(new SamplerDescription
                {
                    MagFilter = mode,
                    MinFilter = mode,
                    MipFilter = FilterMode.Linear,
                    Name = $"renderer textures {array.Index}",
                });
                _arrays.Add(array);
                for (int layer = 0; layer < layers.Length; layer++)
                {
                    WriteLayer(array.Texture, layer, layers[layer].Image);
                    var texture = new RenderTexture(layers[layer].Data.ObjectId, layers[layer].Data.ResourceId, new TextureLocation(array.Index, layer), array);
                    array.TexturesList.Add(texture);
                    Add(_textures, texture, layers[layer].Data);
                }
            }
        }
    }

    private void WriteLayer(Texture texture, int layer, TextureImage image)
    {
        for (int level = 0; level < image.Levels.Length; level++)
        {
            int width = Math.Max(1, image.Width >> level);
            int height = Math.Max(1, image.Height >> level);
            Device.UpdateTexture(texture, new TextureRegion(level, 0, 0, layer, width, height, 1), image.Levels[level]);
        }
    }

    // Shaders ----------------------------------------------------------------

    private ShaderState? BuildShader(ShaderAsset asset, List<string> problems)
    {
        S.Shader content = asset.Content;
        string id = asset.Data.ObjectId;
        int before = problems.Count;
        foreach ((string message, _, _) in RenderRules.CheckShader(content))
        {
            problems.Add($"{asset.Data}: {message}.");
        }

        if (problems.Count > before)
        {
            return null;
        }

        var state = new ShaderState { IsCompute = content.Compute.Length > 0, ContentHash = asset.Data.ContentHash.ToArray() };
        try
        {
            state.Programs.Add(CreateProgram(asset, string.Empty, string.Empty));
            foreach (S.ShaderVariant variant in content.Variant)
            {
                state.Programs.Add(CreateProgram(asset, variant.Name, RenderRules.DefinesKey(variant.Define)));
            }
        }
        catch (Exception exception) when (exception is ShaderLoadException or InvalidDataException or ArgumentException)
        {
            state.Dispose();
            problems.Add($"{asset.Data}: {exception.Message}");
            return null;
        }

        ShaderStages stages = state.IsCompute ? ShaderStages.Compute : ShaderStages.Vertex | ShaderStages.Fragment;
        state.Textures = [.. content.Texture.Select(t => new ShaderTextureBinding(t.Name, (int)t.Binding, t.Optional, RenderRules.Fallback(t.Fallback)!.Value))];
        state.Uniforms = [.. content.Uniform.Select(u => new ShaderBufferBinding(u.Name, (int)u.Binding))];
        state.Storage = [.. content.Storage.Select(s => new ShaderBufferBinding(s.Name, (int)s.Binding))];
        if (content.Parameters is { } parameters)
        {
            int offset = 0;
            var members = new List<ShaderParameterInfo>();
            foreach (S.ShaderParameter parameter in parameters.Parameter)
            {
                ShaderParameterType type = RenderRules.ParameterType(parameter.Type)!.Value;
                (int size, int alignment) = RenderRules.Std140(type);
                offset = (offset + alignment - 1) / alignment * alignment;
                float[] defaults = parameter.Default.Length > 0 ? RenderRules.Numbers(parameter.Default)! : new float[RenderRules.Components(type)];
                members.Add(new ShaderParameterInfo(parameter.Name, type, offset, type == ShaderParameterType.Layer ? parameter.Texture : null, defaults));
                offset += size;
            }

            state.Parameters = new ShaderParameterBlock((int)parameters.Binding, Math.Max(16, (offset + 15) / 16 * 16), members);
        }

        var entries = state.Textures.OrderBy(t => t.Binding).Select(t => new ResourceLayoutEntry(t.Binding, ResourceKind.SampledTexture, stages)).ToList();
        if (state.Parameters is not null)
        {
            entries.Add(new ResourceLayoutEntry(state.Parameters.Binding, ResourceKind.UniformBuffer, stages));
        }

        if (entries.Count > 0)
        {
            state.Layout = Device.CreateResourceLayout(new ResourceLayoutDescription(entries) { Name = id + " material" });
        }

        return state;
    }

    private ShaderProgram CreateProgram(ShaderAsset asset, string variant, string definesKey)
    {
        string label = variant.Length == 0 ? asset.Data.ObjectId : $"{asset.Data.ObjectId} {variant}";
        Graphics.Shader? Stage(string stage, ShaderStages kind) => asset.Stages.ContainsKey(stage)
            ? Device.CreateShader(new ShaderDescription(kind, asset.SpirV(stage, definesKey)) { Name = $"{label} {stage}" })
            : null;

        Graphics.Shader? vertex = null, fragment = null, compute = null;
        try
        {
            vertex = Stage("vertex", ShaderStages.Vertex);
            fragment = Stage("fragment", ShaderStages.Fragment);
            compute = Stage("compute", ShaderStages.Compute);
            return new ShaderProgram(variant, vertex, fragment, compute);
        }
        catch
        {
            vertex?.Dispose();
            fragment?.Dispose();
            compute?.Dispose();
            throw;
        }
    }

    private void Apply(RenderShader shader, ShaderState state)
    {
        if (_shaderStates.Remove(shader, out ShaderState? previous))
        {
            previous.Dispose();
        }

        _shaderStates[shader] = state;
        shader.IsCompute = state.IsCompute;
        shader.Programs = state.Programs;
        shader.Textures = state.Textures;
        shader.Parameters = state.Parameters;
        shader.Uniforms = state.Uniforms;
        shader.Storage = state.Storage;
        shader.MaterialLayout = state.Layout;
    }

    // Materials --------------------------------------------------------------

    private MaterialState? BuildMaterial(MaterialAsset asset, (RenderShader Shader, ShaderState State)? candidate, List<string> problems)
    {
        S.Material content = asset.Content;
        string where = asset.Data.ToString();
        RenderShader? shader = candidate?.Shader;
        ShaderState? shaderState = candidate?.State;
        if (shader is null && (!_shaders.TryGetValue(content.Shader, out shader) || !_shaderStates.TryGetValue(shader, out shaderState)))
        {
            problems.Add($"{where}: the shader '{content.Shader}' is not loaded.");
            return null;
        }

        int before = problems.Count;
        if (shaderState!.IsCompute)
        {
            problems.Add($"{where}: '{content.Shader}' is a compute shader; a material needs a vertex and fragment shader.");
        }

        ShaderProgram? program = shaderState.Programs.FirstOrDefault(p => p.Variant == content.Variant);
        if (program is null)
        {
            string variants = string.Join(", ", shaderState.Programs.Skip(1).Select(p => p.Variant));
            problems.Add($"{where}: the shader '{content.Shader}' has no variant '{content.Variant}'{(variants.Length > 0 ? $"; it has {variants}" : "; it has none")}.");
        }

        MaterialMode? mode = RenderRules.Mode(content.Mode);
        if (mode is null)
        {
            problems.Add($"{where}: '{content.Mode}' is not a render mode: write opaque, cutout, or transparent.");
        }

        var textures = new Dictionary<string, RenderTexture>(StringComparer.Ordinal);
        foreach (S.MaterialTexture slot in content.Texture)
        {
            if (shaderState.Textures.All(t => t.Name != slot.Name))
            {
                problems.Add($"{where}: the shader '{content.Shader}' has no texture '{slot.Name}'{Names(shaderState.Textures.Select(t => t.Name))}.");
            }
            else if (textures.ContainsKey(slot.Name))
            {
                problems.Add($"{where}: the texture '{slot.Name}' is given twice.");
            }
            else if (!_textures.TryGetValue(slot.Texture, out RenderTexture? texture))
            {
                problems.Add($"{where}: the texture '{slot.Texture}' is not loaded.");
            }
            else
            {
                textures[slot.Name] = texture;
            }
        }

        foreach (ShaderTextureBinding binding in shaderState.Textures.Where(t => !t.Optional && !textures.ContainsKey(t.Name) && content.Texture.All(s => s.Name != t.Name)))
        {
            problems.Add($"{where}: the shader '{content.Shader}' needs the texture '{binding.Name}'.");
        }

        byte[] data = shaderState.Parameters is { } block ? new byte[block.Size] : [];
        if (shaderState.Parameters is { } parameters)
        {
            foreach (ShaderParameterInfo parameter in parameters.Parameters)
            {
                Write(data, parameter, parameter.Default);
            }

            var given = new HashSet<string>(StringComparer.Ordinal);
            foreach (S.MaterialParameter value in content.Parameter)
            {
                ShaderParameterInfo? parameter = parameters.Parameters.FirstOrDefault(p => p.Name == value.Name);
                if (parameter is null)
                {
                    problems.Add($"{where}: the shader '{content.Shader}' has no parameter '{value.Name}'{Names(parameters.Parameters.Where(p => p.Type != ShaderParameterType.Layer).Select(p => p.Name))}.");
                }
                else if (!given.Add(value.Name))
                {
                    problems.Add($"{where}: the parameter '{value.Name}' is given twice.");
                }
                else if (RenderRules.CheckValue(parameter.Type, value.Value, out float[] values) is string problem)
                {
                    problems.Add($"{where}: the parameter '{value.Name}': {problem}.");
                }
                else
                {
                    Write(data, parameter, values);
                }
            }

            foreach (ShaderParameterInfo layer in parameters.Parameters.Where(p => p.Type == ShaderParameterType.Layer))
            {
                int index = textures.TryGetValue(layer.Texture!, out RenderTexture? texture) ? texture.Location.Layer : -1;
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(layer.Offset), index);
            }
        }
        else if (content.Parameter.Count > 0)
        {
            problems.Add($"{where}: the shader '{content.Shader}' has no parameters.");
        }

        if (problems.Count > before)
        {
            return null;
        }

        var state = new MaterialState { Shader = shader, Program = program!, Mode = mode!.Value, Textures = textures, Data = data };
        if (shaderState.Parameters is not null)
        {
            state.Buffer = Device.CreateBuffer(new BufferDescription(data.Length, BufferUsage.Uniform) { Name = asset.Data.ObjectId });
            Device.UpdateBuffer(state.Buffer, 0, data);
        }

        if (shaderState.Layout is ResourceLayout layout)
        {
            var bound = new List<BoundResource>();
            foreach (ShaderTextureBinding binding in shaderState.Textures.OrderBy(t => t.Binding))
            {
                if (textures.TryGetValue(binding.Name, out RenderTexture? texture))
                {
                    bound.Add(BoundResource.Of(texture.Array.Texture, texture.Array.Sampler));
                }
                else
                {
                    (Texture fallback, Sampler sampler) = _fallbacks[binding.Fallback];
                    bound.Add(BoundResource.Of(fallback, sampler));
                }
            }

            if (state.Buffer is not null)
            {
                bound.Add(BoundResource.Of(state.Buffer));
            }

            state.Set = Device.CreateResourceSet(new ResourceSetDescription(layout, bound) { Name = asset.Data.ObjectId });
        }

        return state;
    }

    private static string Names(IEnumerable<string> names)
    {
        string list = string.Join(", ", names);
        return list.Length > 0 ? $"; it has {list}" : "; it has none";
    }

    private static void Write(byte[] data, ShaderParameterInfo parameter, IReadOnlyList<float> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            Span<byte> at = data.AsSpan(parameter.Offset + (i * 4), 4);
            if (parameter.Type is ShaderParameterType.Int or ShaderParameterType.Layer)
            {
                BinaryPrimitives.WriteInt32LittleEndian(at, (int)values[i]);
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(at, values[i]);
            }
        }
    }

    private void Apply(RenderMaterial material, MaterialState state)
    {
        if (_materialStates.Remove(material, out MaterialState? previous))
        {
            previous.Dispose();
        }

        _materialStates[material] = state;
        material.Shader = state.Shader;
        material.Program = state.Program;
        material.Mode = state.Mode;
        material.Textures = state.Textures;
        material.ParameterData = state.Data;
        material.ParameterBuffer = state.Buffer;
        material.ResourceSet = state.Set;
    }

    // Meshes -----------------------------------------------------------------

    private MeshState? BuildMesh(MeshAsset asset, List<string> problems)
    {
        string where = asset.Data.ToString();
        var slots = new Dictionary<string, RenderMaterial>(StringComparer.Ordinal);
        int before = problems.Count;
        foreach (S.MeshMaterial slot in asset.Content.Material)
        {
            if (asset.Mesh.Parts.All(p => p.Slot != slot.Slot))
            {
                problems.Add($"{where}: the file has no material slot '{slot.Slot}'{Names(asset.Mesh.Parts.Select(p => p.Slot))}.");
            }
            else if (!_materials.TryGetValue(slot.Material, out RenderMaterial? material))
            {
                problems.Add($"{where}: the material '{slot.Material}' is not loaded.");
            }
            else if (!slots.TryAdd(slot.Slot, material))
            {
                problems.Add($"{where}: the slot '{slot.Slot}' is given twice.");
            }
        }

        if (problems.Count > before)
        {
            return null;
        }

        MeshData mesh = asset.Mesh;
        var state = new MeshState
        {
            Vertices = Device.CreateBuffer(new BufferDescription(mesh.VertexBytes.Length, BufferUsage.Vertex) { Name = asset.Data.ObjectId + " vertices" }),
            Indices = Device.CreateBuffer(new BufferDescription(mesh.IndexBytes.Length, BufferUsage.Index) { Name = asset.Data.ObjectId + " indices" }),
            Mesh = mesh,
            Submeshes = [.. mesh.Parts.Select(p => new Submesh(p.Slot, p.FirstIndex, p.IndexCount, slots.GetValueOrDefault(p.Slot)))],
        };
        Device.UpdateBuffer(state.Vertices, 0, mesh.VertexBytes);
        Device.UpdateBuffer(state.Indices, 0, mesh.IndexBytes);
        return state;
    }

    private void Apply(RenderMesh mesh, MeshState state)
    {
        if (_meshStates.Remove(mesh, out MeshState? previous))
        {
            previous.Dispose();
        }

        _meshStates[mesh] = state;
        mesh.VertexBuffer = state.Vertices;
        mesh.IndexBuffer = state.Indices;
        mesh.VertexCount = state.Mesh.Vertices.Length;
        mesh.IndexCount = state.Mesh.Indices.Length;
        mesh.Submeshes = state.Submeshes;
        mesh.BoundsMin = state.Mesh.BoundsMin;
        mesh.BoundsMax = state.Mesh.BoundsMax;
    }

    private sealed class ShaderState : IDisposable
    {
        public bool IsCompute { get; init; }

        public byte[] ContentHash { get; init; } = [];

        public List<ShaderProgram> Programs { get; } = [];

        public IReadOnlyList<ShaderTextureBinding> Textures { get; set; } = [];

        public ShaderParameterBlock? Parameters { get; set; }

        public IReadOnlyList<ShaderBufferBinding> Uniforms { get; set; } = [];

        public IReadOnlyList<ShaderBufferBinding> Storage { get; set; } = [];

        public ResourceLayout? Layout { get; set; }

        public void Dispose()
        {
            foreach (Graphics.Shader shader in Programs.SelectMany(p => p.All))
            {
                shader.Dispose();
            }

            Layout?.Dispose();
        }
    }

    private sealed class MaterialState : IDisposable
    {
        public required RenderShader Shader { get; init; }

        public required ShaderProgram Program { get; init; }

        public MaterialMode Mode { get; init; }

        public required IReadOnlyDictionary<string, RenderTexture> Textures { get; init; }

        public required byte[] Data { get; init; }

        public GpuBuffer? Buffer { get; set; }

        public ResourceSet? Set { get; set; }

        public void Dispose()
        {
            Set?.Dispose();
            Buffer?.Dispose();
        }
    }

    private sealed class MeshState : IDisposable
    {
        public required GpuBuffer Vertices { get; init; }

        public required GpuBuffer Indices { get; init; }

        public required MeshData Mesh { get; init; }

        public required IReadOnlyList<Submesh> Submeshes { get; init; }

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
        }
    }
}
