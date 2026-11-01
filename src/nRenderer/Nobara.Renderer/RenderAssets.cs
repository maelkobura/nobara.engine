// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Resources;
using S = Nobara.Renderer.Schemas;

namespace Nobara.Renderer;

/// <summary>
/// The resources of nRenderer decoded on the CPU, once per process: the
/// GPU objects of each context are made from them.
/// </summary>
internal static class RenderAssets
{
    public static void Register(ModuleResources resources)
    {
        resources.RegisterDecoder(RenderRules.ShaderType, data => ShaderAsset.Decode(resources, data));
        resources.RegisterDecoder(RenderRules.MaterialType, MaterialAsset.Decode);
        resources.RegisterDecoder(RenderRules.MeshType, data => MeshAsset.Decode(resources, data));
        resources.RegisterDecoder(RenderRules.TextureType, data => TextureAsset.Decode(resources, data));
    }

    public static byte[] ReadFile(ModuleResources resources, ResourceData data, string path)
    {
        using Stream stream = resources.OpenFile(data, path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>A decoded shader: its content and the SPIR-V of each stage, by set of macros.</summary>
internal sealed class ShaderAsset
{
    private ShaderAsset(ResourceData data, S.Shader content, IReadOnlyDictionary<string, S.CompiledStage> stages)
    {
        Data = data;
        Content = content;
        Stages = stages;
    }

    public ResourceData Data { get; }

    public S.Shader Content { get; }

    /// <summary>Gets the compiled stages by name: <c>vertex</c>, <c>fragment</c>, <c>compute</c>.</summary>
    public IReadOnlyDictionary<string, S.CompiledStage> Stages { get; }

    public static ShaderAsset Decode(ModuleResources resources, ResourceData data)
    {
        S.Shader content = S.Shader.Parser.ParseFrom(data.Content.Span);
        var stages = new Dictionary<string, S.CompiledStage>(StringComparer.Ordinal);
        foreach ((string stage, string path) in Sources(content))
        {
            stages[stage] = S.CompiledStage.Parser.ParseFrom(RenderAssets.ReadFile(resources, data, path));
        }

        return new ShaderAsset(data, content, stages);
    }

    /// <summary>Gets the GLSL sources of a shader, by stage.</summary>
    public static IEnumerable<(string Stage, string Path)> Sources(S.Shader content)
    {
        if (content.Vertex.Length > 0)
        {
            yield return ("vertex", content.Vertex);
        }

        if (content.Fragment.Length > 0)
        {
            yield return ("fragment", content.Fragment);
        }

        if (content.Compute.Length > 0)
        {
            yield return ("compute", content.Compute);
        }
    }

    /// <summary>Gets the SPIR-V of a stage for a set of macros.</summary>
    /// <exception cref="InvalidDataException">The build did not compile that set.</exception>
    public byte[] SpirV(string stage, string definesKey)
    {
        S.CompiledVariant? variant = Stages.TryGetValue(stage, out S.CompiledStage? compiled)
            ? compiled.Variant.FirstOrDefault(v => v.Defines == definesKey)
            : null;
        return variant?.Spirv.ToByteArray()
            ?? throw new InvalidDataException($"'{Data.ObjectId}' has no SPIR-V for its {stage} stage with the macros [{definesKey.Replace('\n', ' ')}]; build the module again.");
    }
}

/// <summary>A decoded material: its content, checked against its shader when GPU objects are made.</summary>
internal sealed record MaterialAsset(ResourceData Data, S.Material Content)
{
    public static MaterialAsset Decode(ResourceData data) => new(data, S.Material.Parser.ParseFrom(data.Content.Span));
}

/// <summary>A decoded mesh: its vertices, its indices, and its material slots.</summary>
internal sealed record MeshAsset(ResourceData Data, S.Mesh Content, MeshData Mesh)
{
    public static MeshAsset Decode(ModuleResources resources, ResourceData data)
    {
        S.Mesh content = S.Mesh.Parser.ParseFrom(data.Content.Span);
        try
        {
            return new MeshAsset(data, content, MeshData.Read(RenderAssets.ReadFile(resources, data, content.File)));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{data}: {content.File}: {exception.Message}", exception);
        }
    }
}

/// <summary>A decoded texture: its texels with their mip levels, and its settings.</summary>
internal sealed record TextureAsset(ResourceData Data, TextureImage Image, TextureFilter Filter, TextureColorSpace ColorSpace)
{
    /// <summary>Gets the key of the texture array of the texture: textures with the same key share an array.</summary>
    public (int Width, int Height, int MipLevels, TextureFilter Filter, TextureColorSpace ColorSpace) ArrayKey =>
        (Image.Width, Image.Height, Image.Levels.Length, Filter, ColorSpace);

    public static TextureAsset Decode(ModuleResources resources, ResourceData data)
    {
        S.Texture content = S.Texture.Parser.ParseFrom(data.Content.Span);
        TextureColorSpace colorSpace = RenderRules.ColorSpace(content.ColorSpace) ?? throw new InvalidDataException($"{data}: '{content.ColorSpace}' is not a color space.");
        TextureFilter filter = RenderRules.Filter(content.Filter) ?? throw new InvalidDataException($"{data}: '{content.Filter}' is not a filter.");
        try
        {
            TextureImage image = TextureImage.Decode(RenderAssets.ReadFile(resources, data, content.File), (int)content.MipLevels, colorSpace == TextureColorSpace.Srgb);
            return new TextureAsset(data, image, filter, colorSpace);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{data}: {content.File}: {exception.Message}", exception);
        }
    }
}
