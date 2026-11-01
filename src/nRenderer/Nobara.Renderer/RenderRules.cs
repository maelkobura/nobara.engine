// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Renderer.Schemas;

namespace Nobara.Renderer;

/// <summary>How a material is drawn.</summary>
public enum MaterialMode
{
    /// <summary>Opaque: every fragment is written.</summary>
    Opaque,

    /// <summary>Cut out: fragments below an alpha threshold are discarded, without sorting (foliage).</summary>
    Cutout,

    /// <summary>Transparent: blended over what is behind (water, glass).</summary>
    Transparent,
}

/// <summary>How a texture is filtered when it is magnified or minified.</summary>
public enum TextureFilter
{
    /// <summary>The nearest texel, as pixel art needs; mip levels are blended.</summary>
    Nearest,

    /// <summary>A linear blend of the nearest texels and mip levels.</summary>
    Linear,
}

/// <summary>How the values of a texture are read.</summary>
public enum TextureColorSpace
{
    /// <summary>Colors in sRGB, converted to linear values when they are sampled.</summary>
    Srgb,

    /// <summary>Linear values, such as normals or roughness.</summary>
    Linear,
}

/// <summary>The type of a member of the parameter block of a shader.</summary>
public enum ShaderParameterType
{
    /// <summary>A GLSL <c>float</c>.</summary>
    Float,

    /// <summary>A GLSL <c>vec2</c>.</summary>
    Vec2,

    /// <summary>A GLSL <c>vec3</c>.</summary>
    Vec3,

    /// <summary>A GLSL <c>vec4</c>.</summary>
    Vec4,

    /// <summary>A GLSL <c>int</c>.</summary>
    Int,

    /// <summary>A GLSL <c>int</c> that the renderer sets to the layer of a texture of the material, or -1 when the material leaves it out.</summary>
    Layer,
}

/// <summary>What is bound in place of an optional texture that a material leaves out.</summary>
public enum TextureFallback
{
    /// <summary>Opaque white.</summary>
    White,

    /// <summary>Opaque black.</summary>
    Black,

    /// <summary>A flat normal in tangent space: (0.5, 0.5, 1).</summary>
    Normal,
}

/// <summary>The rules on the content of the resources, shared by the build plugin and the module.</summary>
internal static class RenderRules
{
    public const string ShaderType = "shader";
    public const string MaterialType = "material";
    public const string MeshType = "mesh";
    public const string TextureType = "texture";

    /// <summary>The largest side of a texture.</summary>
    public const int MaxTextureSize = 4096;

    public static MaterialMode? Mode(string text) => text switch
    {
        "" or "opaque" => MaterialMode.Opaque,
        "cutout" => MaterialMode.Cutout,
        "transparent" => MaterialMode.Transparent,
        _ => null,
    };

    public static TextureFilter? Filter(string text) => text switch
    {
        "" or "nearest" => TextureFilter.Nearest,
        "linear" => TextureFilter.Linear,
        _ => null,
    };

    public static TextureColorSpace? ColorSpace(string text) => text switch
    {
        "" or "srgb" => TextureColorSpace.Srgb,
        "linear" => TextureColorSpace.Linear,
        _ => null,
    };

    public static TextureFallback? Fallback(string text) => text switch
    {
        "" or "white" => TextureFallback.White,
        "black" => TextureFallback.Black,
        "normal" => TextureFallback.Normal,
        _ => null,
    };

    public static ShaderParameterType? ParameterType(string text) => text switch
    {
        "float" => ShaderParameterType.Float,
        "vec2" => ShaderParameterType.Vec2,
        "vec3" => ShaderParameterType.Vec3,
        "vec4" => ShaderParameterType.Vec4,
        "int" => ShaderParameterType.Int,
        "layer" => ShaderParameterType.Layer,
        _ => null,
    };

    /// <summary>Gets the number of values of a parameter type.</summary>
    public static int Components(ShaderParameterType type) => type switch
    {
        ShaderParameterType.Vec2 => 2,
        ShaderParameterType.Vec3 => 3,
        ShaderParameterType.Vec4 => 4,
        _ => 1,
    };

    /// <summary>Gets the size and the alignment of a parameter type in the std140 layout.</summary>
    public static (int Size, int Alignment) Std140(ShaderParameterType type) => type switch
    {
        ShaderParameterType.Vec2 => (8, 8),
        ShaderParameterType.Vec3 => (12, 16),
        ShaderParameterType.Vec4 => (16, 16),
        _ => (4, 4),
    };

    /// <summary>Reads numbers separated by spaces; <see langword="null"/> when one is not a number.</summary>
    public static float[]? Numbers(string text)
    {
        string[] parts = text.Split(' ', '\t', '\n', '\r');
        var values = new List<float>(4);
        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            {
                return null;
            }

            values.Add(value);
        }

        return [.. values];
    }

    /// <summary>Checks the values of a parameter against its type; returns the problem, or <see langword="null"/>.</summary>
    public static string? CheckValue(ShaderParameterType type, string text, out float[] values)
    {
        values = Numbers(text) ?? [];
        if (type == ShaderParameterType.Layer)
        {
            return "a layer is set by the renderer, not by a value";
        }

        if (Numbers(text) is null)
        {
            return $"'{text}' is not a list of numbers separated by spaces";
        }

        int expected = Components(type);
        if (values.Length != expected)
        {
            return $"a {Name(type)} takes {expected} {(expected == 1 ? "number" : "numbers")}; got {values.Length}";
        }

        if (type == ShaderParameterType.Int && values[0] != MathF.Round(values[0]))
        {
            return $"an int takes a whole number; got {text.Trim()}";
        }

        return null;
    }

    /// <summary>Gets the name of a parameter type as written.</summary>
    public static string Name(ShaderParameterType type) => type.ToString().ToLowerInvariant();

    /// <summary>Gets the key of a set of macros: sorted, joined by line breaks.</summary>
    public static string DefinesKey(IEnumerable<string> defines) =>
        string.Join('\n', defines.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>Whether a macro has the form <c>NAME</c> or <c>NAME=VALUE</c>, with a C identifier as name.</summary>
    public static bool IsDefine(string define)
    {
        int equals = define.IndexOf('=', StringComparison.Ordinal);
        string name = equals < 0 ? define : define[..equals];
        return name.Length > 0
            && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            && !name.StartsWith("GL_", StringComparison.Ordinal)
            && (equals < 0 || !define[(equals + 1)..].Any(char.IsWhiteSpace));
    }

    /// <summary>Whether a name of a texture, a parameter, a buffer, or a variant has the form of a GLSL identifier.</summary>
    public static bool IsName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Checks a shader on its own; returns each problem with the element it is
    /// about (<c>Texture</c>, <c>Parameter</c>…) and its name, if any.
    /// </summary>
    public static IEnumerable<(string Message, string Element, string Name)> CheckShader(Shader shader)
    {
        bool graphics = shader.Vertex.Length > 0 || shader.Fragment.Length > 0;
        if (shader.Compute.Length > 0 && graphics)
        {
            yield return ("a shader is either a compute shader or a vertex and fragment shader, not both", "Compute", string.Empty);
        }
        else if (shader.Compute.Length == 0 && (shader.Vertex.Length == 0 || shader.Fragment.Length == 0))
        {
            yield return ("a shader needs a <Vertex> and a <Fragment> source, or a <Compute> source alone", shader.Vertex.Length == 0 ? "Vertex" : "Fragment", string.Empty);
        }

        var variants = new HashSet<string>(StringComparer.Ordinal);
        foreach (ShaderVariant variant in shader.Variant)
        {
            if (!IsName(variant.Name))
            {
                yield return ($"'{variant.Name}' is not a variant name: letters, digits, and underscores, not starting with a digit", "Variant", variant.Name);
            }
            else if (!variants.Add(variant.Name))
            {
                yield return ($"the variant '{variant.Name}' is declared twice", "Variant", variant.Name);
            }

            foreach (string define in variant.Define)
            {
                if (!IsDefine(define.Trim()))
                {
                    yield return ($"'{define}' is not a macro: write NAME or NAME=VALUE, with a name that does not start with GL_", "Variant", variant.Name);
                }
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var textureBindings = new HashSet<uint>();
        foreach (ShaderTexture texture in shader.Texture)
        {
            if (!IsName(texture.Name))
            {
                yield return ($"'{texture.Name}' is not a texture name: letters, digits, and underscores, not starting with a digit", "Texture", texture.Name);
            }
            else if (!names.Add(texture.Name))
            {
                yield return ($"the name '{texture.Name}' is declared twice", "Texture", texture.Name);
            }

            if (!textureBindings.Add(texture.Binding))
            {
                yield return ($"two textures use the binding {texture.Binding}", "Texture", texture.Name);
            }

            if (Fallback(texture.Fallback) is null)
            {
                yield return ($"'{texture.Fallback}' is not a fallback: write white, black, or normal", "Texture", texture.Name);
            }
            else if (texture.Fallback.Length > 0 && !texture.Optional)
            {
                yield return ($"the texture '{texture.Name}' has a fallback but is not optional: add optional=\"true\" or remove the fallback", "Texture", texture.Name);
            }
        }

        var uniformBindings = new HashSet<uint>();
        if (shader.Parameters is { } parameters)
        {
            uniformBindings.Add(parameters.Binding);
            foreach (ShaderParameter parameter in parameters.Parameter)
            {
                if (!IsName(parameter.Name))
                {
                    yield return ($"'{parameter.Name}' is not a parameter name: letters, digits, and underscores, not starting with a digit", "Parameter", parameter.Name);
                }
                else if (!names.Add(parameter.Name))
                {
                    yield return ($"the name '{parameter.Name}' is declared twice", "Parameter", parameter.Name);
                }

                ShaderParameterType? type = ParameterType(parameter.Type);
                if (type is null)
                {
                    yield return ($"'{parameter.Type}' is not a parameter type: write float, vec2, vec3, vec4, int, or layer", "Parameter", parameter.Name);
                    continue;
                }

                if (type == ShaderParameterType.Layer)
                {
                    if (parameter.Default.Length > 0)
                    {
                        yield return ($"the layer '{parameter.Name}' has no default: the renderer sets it", "Parameter", parameter.Name);
                    }

                    if (!shader.Texture.Any(t => t.Name == parameter.Texture))
                    {
                        yield return ($"the layer '{parameter.Name}' names the texture '{parameter.Texture}', which the shader does not declare", "Parameter", parameter.Name);
                    }
                }
                else
                {
                    if (parameter.Texture.Length > 0)
                    {
                        yield return ($"only a layer names a texture; '{parameter.Name}' is a {parameter.Type}", "Parameter", parameter.Name);
                    }

                    if (parameter.Default.Length > 0 && CheckValue(type.Value, parameter.Default, out _) is string problem)
                    {
                        yield return ($"the default of '{parameter.Name}': {problem}", "Parameter", parameter.Name);
                    }
                }
            }
        }

        foreach ((ShaderBuffer buffer, string element, HashSet<uint> bindings) in shader.Uniform.Select(u => (u, "Uniform", uniformBindings))
            .Concat(shader.Storage.Select(s => (s, "Storage", new HashSet<uint>()))).ToList())
        {
            if (!IsName(buffer.Name))
            {
                yield return ($"'{buffer.Name}' is not a buffer name: letters, digits, and underscores, not starting with a digit", element, buffer.Name);
            }
            else if (!names.Add(buffer.Name))
            {
                yield return ($"the name '{buffer.Name}' is declared twice", element, buffer.Name);
            }

            if (element == "Uniform" && !bindings.Add(buffer.Binding))
            {
                yield return ($"two uniform blocks use the binding {buffer.Binding}", element, buffer.Name);
            }
        }

        if (shader.Storage.GroupBy(s => s.Binding).FirstOrDefault(g => g.Count() > 1) is { } shared)
        {
            yield return ($"two storage buffers use the binding {shared.Key}", "Storage", shared.Last().Name);
        }
    }
}
