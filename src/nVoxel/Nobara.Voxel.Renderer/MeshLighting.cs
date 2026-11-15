// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>
/// The light that the meshing reads, from nLighting when it is active. Its
/// members use the types of nVoxel.Renderer only, so that the code that runs
/// without nLighting never loads a type of nLighting; <see cref="LightingBridge"/>
/// implements it.
/// </summary>
internal abstract class MeshLighting : IDisposable
{
    /// <summary>Creates a copy of the light of a chunk, to fill again from remesh to remesh.</summary>
    /// <returns>The copy.</returns>
    public abstract MeshLightCopy CreateCopy();

    /// <summary>Copies the light of a chunk and of a border of one block around it, on the loop thread.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="copy">The copy to fill, from <see cref="CreateCopy"/>.</param>
    /// <returns><see langword="false"/> when the chunk has no light yet.</returns>
    public abstract bool TryCopy(Grid grid, ChunkCoordinate chunk, MeshLightCopy copy);

    /// <summary>Gets the light of the terrain around a grid that is not the terrain.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="light">The light, each channel from 0 to 15.</param>
    /// <param name="version">The version of the sample, which grows when it changes.</param>
    /// <returns><see langword="false"/> for the terrain, and for a grid without a sample yet.</returns>
    public abstract bool TryGetTerrainSample(Grid grid, out ChunkLight light, out uint version);

    /// <summary>Ends the subscription to the chunks whose light changed.</summary>
    public abstract void Dispose();
}

/// <summary>A copy of the light of a chunk and of a border of one block around it, that a remesh reads in its job.</summary>
internal abstract class MeshLightCopy
{
    /// <summary>Writes the light into the order of <see cref="MeshInput.Light"/>.</summary>
    /// <param name="destination">The light of the cells of the copy of the chunk.</param>
    [HotPath]
    public abstract void Expand(ushort[] destination);
}
