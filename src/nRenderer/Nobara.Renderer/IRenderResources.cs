// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;

namespace Nobara.Renderer;

/// <summary>
/// The rendering resources of every loaded module on the GPU: texture arrays,
/// shaders, materials, and meshes, by identifier. Resolved with
/// <c>context.Resolve&lt;IRenderResources&gt;()</c>.
/// </summary>
/// <remarks>
/// nRenderer creates the GPU objects when it starts, after nGraphics; use them
/// from the start step of a module that depends on nRenderer. Use the service
/// on the loop thread of the context. In development mode, a hot reload changes
/// the objects in place, at the synchronization point of a frame: an object
/// keeps its identity and increments its <c>Version</c>.
/// </remarks>
public interface IRenderResources
{
    /// <summary>Gets whether the GPU objects exist: from the start step on, until the module is released.</summary>
    bool IsCreated { get; }

    /// <summary>Gets the texture arrays, by index.</summary>
    IReadOnlyList<TextureArray> TextureArrays { get; }

    /// <summary>Gets the textures, sorted by object identifier.</summary>
    IReadOnlyList<RenderTexture> Textures { get; }

    /// <summary>Gets the shaders, sorted by object identifier.</summary>
    IReadOnlyList<RenderShader> Shaders { get; }

    /// <summary>Gets the materials, sorted by object identifier.</summary>
    IReadOnlyList<RenderMaterial> Materials { get; }

    /// <summary>Gets the meshes, sorted by object identifier.</summary>
    IReadOnlyList<RenderMesh> Meshes { get; }

    /// <summary>Gets a texture.</summary>
    /// <param name="id">Its object identifier (<c>texture:example.textures.dirt</c>) or its resource identifier (<c>texture://Example/Textures/Dirt</c>, without regard to case).</param>
    /// <param name="texture">The texture, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGetTexture(string id, [NotNullWhen(true)] out RenderTexture? texture);

    /// <summary>Gets a shader.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <param name="shader">The shader, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGetShader(string id, [NotNullWhen(true)] out RenderShader? shader);

    /// <summary>Gets a material.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <param name="material">The material, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGetMaterial(string id, [NotNullWhen(true)] out RenderMaterial? material);

    /// <summary>Gets a mesh.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <param name="mesh">The mesh, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a loaded module defines it.</returns>
    bool TryGetMesh(string id, [NotNullWhen(true)] out RenderMesh? mesh);

    /// <summary>Gets a texture.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    RenderTexture GetTexture(string id);

    /// <summary>Gets a shader.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    RenderShader GetShader(string id);

    /// <summary>Gets a material.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <returns>The material.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    RenderMaterial GetMaterial(string id);

    /// <summary>Gets a mesh.</summary>
    /// <param name="id">Its object identifier, or its resource identifier.</param>
    /// <returns>The mesh.</returns>
    /// <exception cref="KeyNotFoundException">No loaded module defines it.</exception>
    RenderMesh GetMesh(string id);
}

/// <summary>Thrown when nRenderer cannot make GPU objects from resources, such as a material that names a texture its shader does not declare.</summary>
public sealed class RenderResourceException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, each naming its resource and where it is written.</param>
    public RenderResourceException(IReadOnlyList<string> problems)
        : base(problems.Count == 1 ? problems[0] : $"{problems.Count} rendering resources cannot be used:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}")
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<string> Problems { get; }
}
