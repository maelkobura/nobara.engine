// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>Optional features of a device. A pipeline or a pass declares those it needs, and is refused when one is missing.</summary>
[Flags]
public enum GraphicsFeatures
{
    /// <summary>No feature.</summary>
    None = 0,

    /// <summary>Compute pipelines, dispatches, and storage textures.</summary>
    Compute = 1,

    /// <summary>Storage buffers, read and written by shaders.</summary>
    StorageBuffers = 2,

    /// <summary>Draws and dispatches whose arguments come from a GPU buffer.</summary>
    DrawIndirect = 4,

    /// <summary>Hardware ray tracing. No backend provides it yet.</summary>
    RayTracing = 8,
}

/// <summary>What a device supports: its optional features and its limits.</summary>
public sealed record GraphicsCapabilities
{
    /// <summary>Gets the optional features.</summary>
    public required GraphicsFeatures Features { get; init; }

    /// <summary>Gets the largest width or height of a 2D texture, in texels.</summary>
    public required int MaxTextureSize { get; init; }

    /// <summary>Gets the largest number of layers of a texture array.</summary>
    public required int MaxTextureArrayLayers { get; init; }

    /// <summary>Gets the largest width, height, or depth of a 3D texture.</summary>
    public required int MaxTexture3DSize { get; init; }

    /// <summary>Gets the highest anisotropy of a sampler.</summary>
    public required float MaxAnisotropy { get; init; }

    /// <summary>Gets the largest number of color attachments of a framebuffer.</summary>
    public required int MaxColorAttachments { get; init; }

    /// <summary>Gets the alignment of the offset of a uniform buffer range, in bytes.</summary>
    public required int UniformBufferAlignment { get; init; }

    /// <summary>Gets the alignment of the offset of a storage buffer range, in bytes.</summary>
    public required int StorageBufferAlignment { get; init; }

    /// <summary>Gets the largest size of a uniform buffer range, in bytes.</summary>
    public required long MaxUniformBufferRange { get; init; }

    /// <summary>Gets the largest number of work groups of a dispatch, per axis; 0 without <see cref="GraphicsFeatures.Compute"/>.</summary>
    public required int MaxComputeWorkGroups { get; init; }

    /// <summary>Gets whether the device has every feature of <paramref name="features"/>.</summary>
    /// <param name="features">The features.</param>
    /// <returns><see langword="true"/> when none is missing.</returns>
    [HotPath]
    public bool Supports(GraphicsFeatures features) => (Features & features) == features;

    /// <summary>Throws when the device lacks a feature of <paramref name="features"/>.</summary>
    /// <param name="features">The features needed.</param>
    /// <param name="user">Who needs them, such as the name of a pass, for the message.</param>
    /// <exception cref="NotSupportedException">A feature is missing; the message names them.</exception>
    public void Require(GraphicsFeatures features, string user)
    {
        GraphicsFeatures missing = features & ~Features;
        if (missing != GraphicsFeatures.None)
        {
            throw new NotSupportedException($"{user} needs the graphics features {missing}, which the device does not support.");
        }
    }
}

/// <summary>Describes the device and its driver, for logs and bug reports.</summary>
/// <param name="Backend">The name of the backend, such as <c>opengl</c>.</param>
/// <param name="Vendor">The vendor of the driver.</param>
/// <param name="Renderer">The name of the GPU, as the driver gives it.</param>
/// <param name="Version">The version of the API and of the driver.</param>
public sealed record GraphicsDeviceInfo(string Backend, string Vendor, string Renderer, string Version);
