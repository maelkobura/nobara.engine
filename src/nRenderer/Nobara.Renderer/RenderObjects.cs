// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>Where a texture is on the GPU: its texture array and its layer in it.</summary>
/// <param name="Array">The index of the array in <see cref="IRenderResources.TextureArrays"/>.</param>
/// <param name="Layer">The layer in the array.</param>
/// <remarks>
/// The location of a texture is the same from one start of the game to the
/// next as long as the set of textures, their sizes, and their settings do not
/// change, so modules can write it into data such as the vertices of a mesh.
/// </remarks>
public readonly record struct TextureLocation(int Array, int Layer)
{
    /// <summary>Returns the location as <c>array:layer</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Array}:{Layer}";
}

/// <summary>A texture resource, as one layer of a texture array.</summary>
public sealed class RenderTexture
{
    internal RenderTexture(string objectId, string resourceId, TextureLocation location, TextureArray array)
    {
        ObjectId = objectId;
        ResourceId = resourceId;
        Location = location;
        Array = array;
    }

    /// <summary>Gets the object identifier: <c>texture:example.textures.dirt</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the resource identifier: <c>texture://Example/Textures/Dirt</c>.</summary>
    public string ResourceId { get; }

    /// <summary>Gets the array and the layer of the texture.</summary>
    public TextureLocation Location { get; }

    /// <summary>Gets the texture array that holds the texture.</summary>
    public TextureArray Array { get; }

    /// <summary>Gets the number of times the texels were written again by a hot reload; 0 at start.</summary>
    public int Version { get; internal set; }

    /// <inheritdoc/>
    public override string ToString() => $"{ObjectId} ({Location})";
}

/// <summary>
/// A texture array: the textures of one size and one set of settings, one per
/// layer, with the sampler that reads them.
/// </summary>
public sealed class TextureArray
{
    internal TextureArray(int index, int width, int height, int mipLevels, TextureFilter filter, TextureColorSpace colorSpace)
    {
        Index = index;
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        Filter = filter;
        ColorSpace = colorSpace;
    }

    /// <summary>Gets the index of the array in <see cref="IRenderResources.TextureArrays"/>.</summary>
    public int Index { get; }

    /// <summary>Gets the width of each layer, in texels.</summary>
    public int Width { get; }

    /// <summary>Gets the height of each layer, in texels.</summary>
    public int Height { get; }

    /// <summary>Gets the number of mip levels.</summary>
    public int MipLevels { get; }

    /// <summary>Gets the filter of the sampler.</summary>
    public TextureFilter Filter { get; }

    /// <summary>Gets how the texels are read.</summary>
    public TextureColorSpace ColorSpace { get; }

    /// <summary>Gets the textures of the array, by layer.</summary>
    public IReadOnlyList<RenderTexture> Textures => _textures;

    /// <summary>Gets the GPU texture: a 2D array of <see cref="Textures"/>.Count layers.</summary>
    public Texture Texture { get; internal set; } = null!;

    /// <summary>Gets the sampler that reads it.</summary>
    public Sampler Sampler { get; internal set; } = null!;

    internal List<RenderTexture> TexturesList => _textures;

    private readonly List<RenderTexture> _textures = [];

    /// <inheritdoc/>
    public override string ToString() => $"array {Index}: {Width}x{Height} {ColorSpace} {Filter}, {MipLevels} mip levels, {_textures.Count} layers";
}

/// <summary>A texture that a shader samples.</summary>
/// <param name="Name">The name that materials use: <c>Albedo</c>.</param>
/// <param name="Binding">The binding of the <c>sampler2DArray</c> in the GLSL source.</param>
/// <param name="Optional">Whether a material may leave it out.</param>
/// <param name="Fallback">What is bound when a material leaves it out.</param>
public sealed record ShaderTextureBinding(string Name, int Binding, bool Optional, TextureFallback Fallback);

/// <summary>A member of the parameter block of a shader.</summary>
/// <param name="Name">The name that materials use: <c>Tint</c>.</param>
/// <param name="Type">The type.</param>
/// <param name="Offset">The offset of the member in the block, in bytes (std140 layout).</param>
/// <param name="Texture">For a layer, the name of its texture; otherwise <see langword="null"/>.</param>
/// <param name="Default">The values when a material does not give them.</param>
public sealed record ShaderParameterInfo(string Name, ShaderParameterType Type, int Offset, string? Texture, IReadOnlyList<float> Default);

/// <summary>The uniform block of the parameters of a material.</summary>
/// <param name="Binding">The binding of the block in the GLSL source.</param>
/// <param name="Size">The size of the block, in bytes, rounded up to 16.</param>
/// <param name="Parameters">The members, in order.</param>
public sealed record ShaderParameterBlock(int Binding, int Size, IReadOnlyList<ShaderParameterInfo> Parameters);

/// <summary>A buffer that the pipeline binds for a shader.</summary>
/// <param name="Name">The name that the pipeline uses: <c>Camera</c>.</param>
/// <param name="Binding">The binding in the GLSL source.</param>
public sealed record ShaderBufferBinding(string Name, int Binding);

/// <summary>The GPU shaders of one variant of a shader resource.</summary>
public sealed class ShaderProgram
{
    internal ShaderProgram(string variant, Graphics.Shader? vertex, Graphics.Shader? fragment, Graphics.Shader? compute)
    {
        Variant = variant;
        Vertex = vertex;
        Fragment = fragment;
        Compute = compute;
    }

    /// <summary>Gets the name of the variant; empty for the shader as written.</summary>
    public string Variant { get; }

    /// <summary>Gets the vertex shader, or <see langword="null"/> for a compute shader.</summary>
    public Graphics.Shader? Vertex { get; }

    /// <summary>Gets the fragment shader, or <see langword="null"/> for a compute shader.</summary>
    public Graphics.Shader? Fragment { get; }

    /// <summary>Gets the compute shader, or <see langword="null"/>.</summary>
    public Graphics.Shader? Compute { get; }

    internal IEnumerable<Graphics.Shader> All => new[] { Vertex, Fragment, Compute }.OfType<Graphics.Shader>();
}

/// <summary>A shader resource: its GPU shaders, one program per variant, and the resources it reads.</summary>
public sealed class RenderShader
{
    internal RenderShader(string objectId)
    {
        ObjectId = objectId;
    }

    /// <summary>Gets the object identifier: <c>shader:example.shaders.lit</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets whether it is a compute shader.</summary>
    public bool IsCompute { get; internal set; }

    /// <summary>Gets the programs, the shader as written first, then each variant in the order declared.</summary>
    public IReadOnlyList<ShaderProgram> Programs { get; internal set; } = [];

    /// <summary>Gets the textures that materials give.</summary>
    public IReadOnlyList<ShaderTextureBinding> Textures { get; internal set; } = [];

    /// <summary>Gets the parameter block that materials fill, or <see langword="null"/>.</summary>
    public ShaderParameterBlock? Parameters { get; internal set; }

    /// <summary>Gets the uniform buffers that the pipeline binds.</summary>
    public IReadOnlyList<ShaderBufferBinding> Uniforms { get; internal set; } = [];

    /// <summary>Gets the storage buffers that the pipeline binds.</summary>
    public IReadOnlyList<ShaderBufferBinding> Storage { get; internal set; } = [];

    /// <summary>
    /// Gets the layout of the resource set of a material of this shader: its
    /// textures, by binding, then its parameter block; <see langword="null"/>
    /// when the shader reads neither.
    /// </summary>
    public ResourceLayout? MaterialLayout { get; internal set; }

    /// <summary>Gets the number of times a hot reload created the GPU objects again; 0 at start.</summary>
    /// <remarks>A pipeline built from <see cref="Programs"/> is built again when the version changes.</remarks>
    public int Version { get; internal set; }

    /// <summary>Gets the program of a variant.</summary>
    /// <param name="variant">The name of a variant, or an empty string for the shader as written.</param>
    /// <param name="program">The program, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the shader has the variant.</returns>
    public bool TryGetProgram(string variant, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ShaderProgram? program)
    {
        ArgumentNullException.ThrowIfNull(variant);
        program = Programs.FirstOrDefault(p => p.Variant == variant);
        return program is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;
}

/// <summary>A material resource: its shader, its textures and parameters bound in a resource set, and its render mode.</summary>
public sealed class RenderMaterial
{
    internal RenderMaterial(string objectId)
    {
        ObjectId = objectId;
    }

    /// <summary>Gets the object identifier: <c>material:example.materials.dirt</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the shader.</summary>
    public RenderShader Shader { get; internal set; } = null!;

    /// <summary>Gets the program of the variant the material uses.</summary>
    public ShaderProgram Program { get; internal set; } = null!;

    /// <summary>Gets the render mode.</summary>
    public MaterialMode Mode { get; internal set; }

    /// <summary>Gets the textures, by the names the shader declares; an optional texture left out is absent.</summary>
    public IReadOnlyDictionary<string, RenderTexture> Textures { get; internal set; } = new Dictionary<string, RenderTexture>();

    /// <summary>Gets the content of the parameter block, as the GPU reads it; empty when the shader has none.</summary>
    public ReadOnlyMemory<byte> ParameterData { get; internal set; }

    /// <summary>Gets the uniform buffer of the parameter block, or <see langword="null"/>.</summary>
    public GpuBuffer? ParameterBuffer { get; internal set; }

    /// <summary>Gets the resource set to bind with <see cref="RenderShader.MaterialLayout"/>, or <see langword="null"/> when the shader has no such layout.</summary>
    public ResourceSet? ResourceSet { get; internal set; }

    /// <summary>Gets the number of times a hot reload built the material again; 0 at start.</summary>
    public int Version { get; internal set; }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;
}

/// <summary>A part of a mesh drawn with one material.</summary>
/// <param name="Slot">The name of the material slot in the file: the name of a glTF material, <c>#</c> and its index when it has no name, or <c>default</c> for primitives without material.</param>
/// <param name="FirstIndex">The first index of the part in the index buffer.</param>
/// <param name="IndexCount">The number of indices.</param>
/// <param name="Material">The material that the resource gives to the slot, or <see langword="null"/> when it gives none.</param>
public sealed record Submesh(string Slot, int FirstIndex, int IndexCount, RenderMaterial? Material);

/// <summary>A mesh resource on the GPU: one vertex buffer and one index buffer shared by its parts.</summary>
public sealed class RenderMesh
{
    /// <summary>The size of a vertex, in bytes.</summary>
    public const int VertexSize = 48;

    internal RenderMesh(string objectId)
    {
        ObjectId = objectId;
    }

    /// <summary>
    /// Gets the layout of the vertices: position (<c>vec3</c>, location 0),
    /// normal (<c>vec3</c>, location 1), texture coordinates (<c>vec2</c>,
    /// location 2), and tangent (<c>vec4</c>, location 3, w is the sign of the
    /// bitangent).
    /// </summary>
    public static VertexBufferLayout VertexLayout { get; } = new(VertexSize,
    [
        new VertexAttribute(0, VertexFormat.Float3, 0),
        new VertexAttribute(1, VertexFormat.Float3, 12),
        new VertexAttribute(2, VertexFormat.Float2, 24),
        new VertexAttribute(3, VertexFormat.Float4, 32),
    ]);

    /// <summary>Gets the object identifier: <c>mesh:example.meshes.duck</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the vertex buffer, in <see cref="VertexLayout"/>.</summary>
    public GpuBuffer VertexBuffer { get; internal set; } = null!;

    /// <summary>Gets the index buffer, 32-bit indices.</summary>
    public GpuBuffer IndexBuffer { get; internal set; } = null!;

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount { get; internal set; }

    /// <summary>Gets the number of indices.</summary>
    public int IndexCount { get; internal set; }

    /// <summary>Gets the parts, one per material slot, in the order of the slots in the file.</summary>
    public IReadOnlyList<Submesh> Submeshes { get; internal set; } = [];

    /// <summary>Gets the smallest corner of the box around the vertices.</summary>
    public Vector3 BoundsMin { get; internal set; }

    /// <summary>Gets the largest corner of the box around the vertices.</summary>
    public Vector3 BoundsMax { get; internal set; }

    /// <summary>Gets the number of times a hot reload uploaded the mesh again; 0 at start.</summary>
    public int Version { get; internal set; }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;
}
