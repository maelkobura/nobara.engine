// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// How a body reports its contacts: a body that has it gets contact events at
/// the end of each step, the others cost nothing.
/// </summary>
/// <param name="ImpactSpeed">The slowest approach, in blocks per second along the normal of the contact, that the body reports as an impact; from 0.</param>
[HotPath]
public readonly record struct ContactReporting(float ImpactSpeed)
{
    /// <summary>The impact speed of <see cref="Default"/>: 1 block per second, the slowest approach that bounces.</summary>
    public const float DefaultImpactSpeed = 1f;

    /// <summary>Gets the reporting of contacts with the default impact speed.</summary>
    public static ContactReporting Default => new(DefaultImpactSpeed);
}

/// <summary>The fixed rules of the contact events.</summary>
public static class ContactEvents
{
    /// <summary>
    /// The steps in a row in which two parts must be apart for their contact
    /// to end: 3. A body that settles hops a little, and is apart from what it
    /// rests on for a step or two; its contact goes on.
    /// </summary>
    public const int GraceSteps = 3;

    /// <summary>
    /// How close two parts come, in blocks, their approach over the step
    /// counted, for their contact to begin: 0.01 block.
    /// </summary>
    public const float BeginReach = BackendContact.BeginReach;
}

/// <summary>
/// A contact of a body that reports its contacts, with another body or with a
/// block, as the end of a step lists it in
/// <see cref="PhysicsStepEnd.ContactsBegun"/>, <see cref="PhysicsStepEnd.ContactsEnded"/>,
/// or <see cref="PhysicsStepEnd.Impacts"/>.
/// </summary>
/// <remarks>
/// A pair of bodies that both report gives one event, whose
/// <see cref="Body"/> is the one of the lower slot. A body touches a grid
/// once, however many of its blocks it touches: the event gives the block
/// under the deepest contact. The body of a grid is a body and a grid: a
/// contact with it names both the other body and its block. A contact that
/// ends because a body was removed names a body that is no longer alive.
/// </remarks>
/// <param name="Dimension">The dimension of the bodies.</param>
/// <param name="Body">The body that reports the contact.</param>
/// <param name="Entity">Its entity.</param>
/// <param name="Other">The other body, or the default handle for a block.</param>
/// <param name="OtherEntity">The entity of the other body, or the default for a block.</param>
/// <param name="Grid">The grid of the block, or <see langword="null"/> for a body that is not the body of a grid.</param>
/// <param name="Block">The block, in the coordinates of its grid.</param>
/// <param name="State">The state of the block; 0 for a body.</param>
/// <param name="Point">Where the bodies touch, at the deepest contact.</param>
/// <param name="Normal">The normal of the contact, from the other part toward <see cref="Body"/>.</param>
/// <param name="Speed">The speed at which the two parts approached along the normal at the start of the step, in blocks per second; negative while they part.</param>
/// <param name="Impulse">An estimate of the impulse of the impact, in kilograms times blocks per second: the speed times the reduced mass of the pair (a static or kinematic body, and a block, count as of infinite mass), times one plus the restitution of the pair.</param>
[HotPath]
public readonly record struct ContactEvent(
    DimensionId Dimension,
    BodyHandle Body,
    EntityId Entity,
    BodyHandle Other,
    EntityId OtherEntity,
    Grid? Grid,
    BlockCoordinate Block,
    uint State,
    WorldPosition Point,
    Vector3 Normal,
    float Speed,
    float Impulse)
{
    /// <summary>Gets whether the other part is a block: of a grid, or of the body of a grid.</summary>
    public bool IsBlock => Grid is not null;
}
