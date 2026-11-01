// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Google.Protobuf;
using Nobara.ResourceCompiler.Plugins;
using Nobara.Renderer.Schemas;

[assembly: ResourcePlugin(typeof(Nobara.Renderer.Build.RendererPlugin))]

namespace Nobara.Renderer.Build;

/// <summary>
/// The resource plugin of the types <c>Shader</c>, <c>Material</c>,
/// <c>Mesh</c>, and <c>Texture</c>: it compiles the GLSL sources of shaders
/// to SPIR-V with shaderc, and checks the files and the rules that the schema
/// cannot declare.
/// </summary>
/// <remarks>
/// The schema already checks the form of the fields, the references to
/// shaders, textures, and materials, and that the files exist. A material is
/// checked against its shader when nRenderer loads it, since the shader may
/// come from another module.
/// </remarks>
public sealed class RendererPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a shader whose stages, variants, textures, parameters, or buffers are not valid.</summary>
    public const string InvalidShader = "NREN001";

    /// <summary>The diagnostic of a GLSL source that does not compile, at its line.</summary>
    public const string GlslError = "NREN002";

    /// <summary>The diagnostic of a material whose mode, variant, textures, or parameters are not valid.</summary>
    public const string InvalidMaterial = "NREN003";

    /// <summary>The diagnostic of a texture whose file or settings are not valid.</summary>
    public const string InvalidTexture = "NREN004";

    /// <summary>The diagnostic of a mesh whose file or material slots are not valid.</summary>
    public const string InvalidMesh = "NREN005";

    /// <summary>The diagnostic of a file used in two ways, such as one GLSL source as two stages.</summary>
    public const string SharedFile = "NREN006";

    /// <summary>The diagnostic of a GLSL warning, at its line.</summary>
    public const string GlslWarning = "NREN007";

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = ["Shader", "Material", "Mesh", "Texture"];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An override changes some fields only; nRenderer checks the result when it loads it.
        if (string.Equals((string?)context.Element.Attribute("override"), "true", StringComparison.Ordinal))
        {
            return;
        }

        switch (context.Message.Name)
        {
            case "Shader":
                ValidateShader(context);
                break;
            case "Material":
                ValidateMaterial(context);
                break;
            case "Mesh":
                ValidateMesh(context);
                break;
            case "Texture":
                ValidateTexture(context);
                break;
        }
    }

    /// <inheritdoc/>
    public override byte[] ProcessFile(FileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string element = context.References.Count > 0 ? context.References[0].Element : string.Empty;
        return element switch
        {
            "Shader" => CompileShader(context),
            "Mesh" => CheckMesh(context),
            "Texture" => CheckTexture(context),
            _ => context.Content,
        };
    }

    private static void ValidateShader(ResourceContext context)
    {
        Shader shader = context.Parse(Shader.Parser);
        foreach ((string message, string element, string name) in RenderRules.CheckShader(shader))
        {
            XElement? at = context.Content.Descendants(element).FirstOrDefault(e => name.Length == 0 || (string?)e.Attribute("name") == name)
                ?? context.Content.Descendants(element).FirstOrDefault();
            context.Error(InvalidShader, message, at);
        }
    }

    private static void ValidateMaterial(ResourceContext context)
    {
        Material material = context.Parse(Material.Parser);
        if (RenderRules.Mode(material.Mode) is null)
        {
            context.Error(InvalidMaterial, $"'{material.Mode}' is not a render mode: write opaque, cutout, or transparent", context.Content.Element("Mode"));
        }

        if (material.Variant.Length > 0 && !RenderRules.IsName(material.Variant))
        {
            context.Error(InvalidMaterial, $"'{material.Variant}' is not a variant name", context.Content.Element("Variant"));
        }

        var textures = new HashSet<string>(StringComparer.Ordinal);
        foreach ((MaterialTexture texture, XElement at) in material.Texture.Zip(context.Content.Elements("Texture")))
        {
            if (!textures.Add(texture.Name))
            {
                context.Error(InvalidMaterial, $"the texture '{texture.Name}' is given twice", at);
            }
        }

        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach ((MaterialParameter parameter, XElement at) in material.Parameter.Zip(context.Content.Elements("Parameter")))
        {
            if (!parameters.Add(parameter.Name))
            {
                context.Error(InvalidMaterial, $"the parameter '{parameter.Name}' is given twice", at);
            }

            if (RenderRules.Numbers(parameter.Value) is not { Length: > 0 })
            {
                context.Error(InvalidMaterial, $"'{parameter.Value}' is not a list of numbers separated by spaces", at);
            }
        }
    }

    private static void ValidateMesh(ResourceContext context)
    {
        Mesh mesh = context.Parse(Mesh.Parser);
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach ((MeshMaterial slot, XElement at) in mesh.Material.Zip(context.Content.Elements("Material")))
        {
            if (!slots.Add(slot.Slot))
            {
                context.Error(InvalidMesh, $"the slot '{slot.Slot}' is given twice", at);
            }
        }

        if (!mesh.File.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            context.Error(InvalidMesh, $"'{mesh.File}' is not a glTF binary file: a mesh reads a .glb file", context.Content.Element("File"));
        }
    }

    private static void ValidateTexture(ResourceContext context)
    {
        Texture texture = context.Parse(Texture.Parser);
        if (RenderRules.Filter(texture.Filter) is null)
        {
            context.Error(InvalidTexture, $"'{texture.Filter}' is not a filter: write nearest or linear", context.Content.Element("Filter"));
        }

        if (RenderRules.ColorSpace(texture.ColorSpace) is null)
        {
            context.Error(InvalidTexture, $"'{texture.ColorSpace}' is not a color space: write srgb or linear", context.Content.Element("ColorSpace"));
        }

        if (!texture.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            context.Error(InvalidTexture, $"'{texture.File}' is not a PNG file: a texture reads a .png file", context.Content.Element("File"));
        }
    }

    private static byte[] CheckTexture(FileContext context)
    {
        if (TextureImage.CheckPng(context.Content, out _, out _) is string problem)
        {
            context.Error(InvalidTexture, $"{problem} (used by {context.ReferencedBy})");
        }

        return context.Content;
    }

    private static byte[] CheckMesh(FileContext context)
    {
        MeshData mesh;
        try
        {
            mesh = MeshData.Read(context.Content);
        }
        catch (InvalidDataException exception)
        {
            context.Error(InvalidMesh, $"{exception.Message} (used by {context.ReferencedBy})");
            return context.Content;
        }

        foreach (FileReference reference in context.References)
        {
            foreach (MeshMaterial slot in reference.Parse(Mesh.Parser).Material)
            {
                if (mesh.Parts.All(p => p.Slot != slot.Slot))
                {
                    string slots = string.Join(", ", mesh.Parts.Select(p => p.Slot));
                    context.Error(InvalidMesh, $"'{reference.ObjectId}' gives a material to the slot '{slot.Slot}', which the file does not have; its slots are {slots}");
                }
            }
        }

        return context.Content;
    }

    private static byte[] CompileShader(FileContext context)
    {
        // The stage of the source, and every set of macros that its shaders ask for.
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var sets = new SortedSet<string>(StringComparer.Ordinal);
        var variants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FileReference reference in context.References)
        {
            Shader shader = reference.Parse(Shader.Parser);
            foreach ((string stage, string path) in ShaderAsset.Sources(shader))
            {
                if (path == context.Path)
                {
                    stages.Add(stage);
                }
            }

            sets.Add(string.Empty);
            variants.TryAdd(string.Empty, string.Empty);
            foreach (ShaderVariant variant in shader.Variant)
            {
                string key = RenderRules.DefinesKey(variant.Define);
                sets.Add(key);
                variants.TryAdd(key, variant.Name);
            }
        }

        if (stages.Count != 1)
        {
            context.Error(SharedFile, $"the GLSL source is used as {string.Join(" and ", stages.Order(StringComparer.Ordinal))} stages; a source is one stage");
            return context.Content;
        }

        string stageName = stages.Single();
        var compiled = new CompiledStage { Stage = stageName };
        using var compiler = new GlslCompiler();
        foreach (string set in sets)
        {
            GlslResult result = compiler.Compile(context.Content, context.Path, stageName, set.Length == 0 ? [] : set.Split('\n'));
            string label = variants[set].Length == 0 ? string.Empty : $" (variant {variants[set]})";
            foreach (GlslMessage message in result.Messages)
            {
                if (message.IsError)
                {
                    context.Error(GlslError, message.Text + label, message.Line);
                }
                else
                {
                    context.Warning(GlslWarning, message.Text + label, message.Line);
                }
            }

            if (result.SpirV is not null)
            {
                compiled.Variant.Add(new CompiledVariant { Defines = set, Spirv = ByteString.CopyFrom(result.SpirV) });
            }
        }

        return compiled.ToByteArray();
    }
}
