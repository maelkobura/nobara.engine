// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Lighting;

/// <summary>
/// Component <c>nobara.lighting.dynamic_light</c>: the light that an entity
/// carries, such as a torch held in the hand. Its position is the
/// <c>nobara.entity.transform</c> of the entity.
/// </summary>
/// <remarks>
/// A dynamic light is not propagated through the blocks: the brightness query
/// adds it by its distance (<see cref="ILighting.TryGetBrightness"/>), so it
/// costs nothing when it moves. Without color, it gives its brightest
/// component.
/// </remarks>
public struct DynamicLight
{
    /// <summary>The light at the source, each component from 0 to 15, as for a block that emits light.</summary>
    public LightColor Color;

    /// <summary>The distance, in blocks, at which the light reaches 0. A radius of 0 or less gives no light.</summary>
    public float Radius;

    /// <summary>Gets the level that one component gives at a distance: <c>floor(level × (1 − distance / radius))</c>, 0 from the radius on.</summary>
    /// <param name="level">The component at the source, from 0 to 15.</param>
    /// <param name="distance">The distance, in blocks.</param>
    /// <param name="radius">The radius, in blocks.</param>
    /// <returns>The level, from 0 to <paramref name="level"/>.</returns>
    [HotPath]
    public static int LevelAt(int level, float distance, float radius)
    {
        if (radius <= 0 || !(distance < radius)) return 0;
        return Math.Clamp((int)(level * (1 - (Math.Max(distance, 0) / radius))), 0, level);
    }
}

/// <summary>A dynamic light of a dimension, as the registry read it at the last tick.</summary>
/// <param name="Entity">The entity that carries it.</param>
/// <param name="Position">The position of the entity in the world.</param>
/// <param name="Color">Its color; without color, its three components are equal.</param>
/// <param name="Radius">The distance, in blocks, at which it reaches 0.</param>
public readonly record struct DynamicLightSource(EntityId Entity, WorldPosition Position, LightColor Color, float Radius);

/// <summary>
/// The light at a block, channel by channel, as the brightness query gives
/// it. The channels are never combined with the time of day: the sky channel
/// is the exposure to the sky, which a day and night cycle scales.
/// </summary>
/// <param name="Sky">The sky channel, from 0 to 15.</param>
/// <param name="Block">The block channel: the light of the blocks that emit light; without color, its three components are equal.</param>
/// <param name="Dynamic">The light of the dynamic lights near the block, by their distance, the brightest one per component.</param>
[HotPath]
public readonly record struct Brightness(int Sky, LightColor Block, LightColor Dynamic);

/// <summary>
/// The light of the terrain around a grid that is not the terrain, such as a
/// ship: the average of the light of the terrain at the center and the eight
/// corners of the grid, meant for the renderer to scale the light of the grid
/// by it, so that a ship that enters a cave grows darker.
/// </summary>
/// <param name="Sky">The average sky channel, from 0 to 15.</param>
/// <param name="Red">The average red component of the block channel, from 0 to 15.</param>
/// <param name="Green">The average green component of the block channel, from 0 to 15.</param>
/// <param name="Blue">The average blue component of the block channel, from 0 to 15.</param>
/// <param name="Points">
/// The points that were read: those in a loaded chunk of the terrain, outside a
/// block closed on six faces. At 0, the values are those of the last sample
/// that had points, or full sky without block light.
/// </param>
/// <param name="Version">The version of the sample, incremented when a value changes.</param>
public readonly record struct TerrainLightSample(float Sky, float Red, float Green, float Blue, int Points, uint Version);

/// <summary>
/// A point of interest of the light, such as the camera on a client: the
/// propagation spreads the light near the points of interest first when it
/// cannot spread everything in one tick.
/// </summary>
/// <remarks>
/// The centers of the area tickets of nVoxel at the <c>Active</c> or
/// <c>ReducedActive</c> level are points of interest too, so a server
/// favors the places around its players without a focus.
/// </remarks>
public sealed class LightFocus : IDisposable
{
    internal LightFocus(DimensionId dimension, in WorldPosition position, Vector3 direction)
    {
        Dimension = dimension;
        Position = position;
        Direction = direction;
    }

    /// <summary>Gets the dimension of the point.</summary>
    public DimensionId Dimension { get; }

    /// <summary>Gets the position of the point in the world.</summary>
    public WorldPosition Position { get; private set; }

    /// <summary>Gets the direction the point looks at, such as the direction of the camera, or zero; the debug window reads the light of the block it aims at.</summary>
    public Vector3 Direction { get; private set; }

    /// <summary>Gets whether the focus was disposed, by its module or at the end of the world session.</summary>
    public bool IsRevoked { get; private set; }

    /// <summary>Moves the point.</summary>
    /// <param name="position">The new position in the world.</param>
    /// <param name="direction">The direction it looks at, or zero.</param>
    /// <exception cref="ObjectDisposedException">The focus was disposed.</exception>
    public void Move(in WorldPosition position, Vector3 direction = default)
    {
        ObjectDisposedException.ThrowIf(IsRevoked, this);
        Position = position;
        Direction = direction;
    }

    /// <summary>Removes the point. Disposing twice does nothing.</summary>
    public void Dispose() => IsRevoked = true;
}

/// <summary>How late the light of a context is: the work left to its propagation and the budget of the last tick.</summary>
/// <param name="Chunks">The chunks that still have work.</param>
/// <param name="Cells">The cells that wait in the queues of those chunks, or an estimate from above.</param>
/// <param name="OldestTicks">The ticks since the oldest of those chunks got its work, or 0 without work.</param>
/// <param name="Budget">The operations a tick may do (<see cref="LightingSettings.OperationsPerTick"/>).</param>
/// <param name="LastTickOperations">The operations of the last tick.</param>
/// <param name="LastTickDuration">The time of the propagation in the last tick, on the loop thread, jobs included.</param>
public readonly record struct LightingBacklog(int Chunks, long Cells, long OldestTicks, long Budget, long LastTickOperations, TimeSpan LastTickDuration);
