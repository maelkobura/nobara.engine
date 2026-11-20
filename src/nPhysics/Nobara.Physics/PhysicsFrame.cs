// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The conversions between positions in the world and positions in a physics
/// world, relative to its origin: the only place where nPhysics crosses
/// between the two, so that no absolute coordinate in single precision
/// reaches a backend.
/// </summary>
/// <remarks>
/// A position in a physics world is an offset in blocks from the corner of
/// its origin chunk. Both conversions are exact to the precision of a
/// <see cref="float"/> near the origin, however far the origin is from the
/// origin of the world.
/// </remarks>
public static class PhysicsFrame
{
    /// <summary>Gets a position in the world as an offset from the origin of a physics world.</summary>
    /// <param name="origin">The origin chunk of the physics world.</param>
    /// <param name="position">The position in the world.</param>
    /// <returns>The offset, in blocks.</returns>
    [HotPath]
    public static Vector3 ToLocal(ChunkCoordinate origin, in WorldPosition position) =>
        position.RelativeTo(new WorldPosition(origin.X, origin.Y, origin.Z, Vector3.Zero));

    /// <summary>Gets the position in the world of an offset from the origin of a physics world.</summary>
    /// <param name="origin">The origin chunk of the physics world.</param>
    /// <param name="local">The offset, in blocks.</param>
    /// <returns>The position in the world, with its chunk coordinates carried over.</returns>
    [HotPath]
    public static WorldPosition ToWorld(ChunkCoordinate origin, Vector3 local) => new(origin.X, origin.Y, origin.Z, local);

    /// <summary>Gets the chunk that holds a position, as the origin of a physics world around it.</summary>
    /// <param name="position">The position in the world.</param>
    /// <returns>The chunk.</returns>
    [HotPath]
    public static ChunkCoordinate OriginAt(in WorldPosition position) => new(position.ChunkX, position.ChunkY, position.ChunkZ);
}
