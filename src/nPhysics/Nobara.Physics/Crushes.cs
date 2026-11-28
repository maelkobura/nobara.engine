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
/// A kinematic grid that pushes a character against something it cannot get
/// out through, as the character controller found it during a step.
/// </summary>
/// <param name="Dimension">The dimension of the character and the grid.</param>
/// <param name="Character">The entity of the character.</param>
/// <param name="Grid">The grid that pushes.</param>
/// <param name="Body">The body of the grid.</param>
/// <param name="Direction">The way the grid pushes the character, in the world, of length 1.</param>
/// <param name="Depth">How far, in blocks, the character would have to move that way to leave the voxels of the grid.</param>
[HotPath]
public readonly record struct CharacterCrush(DimensionId Dimension, EntityId Character, Grid Grid, BodyHandle Body, Vector3 Direction, float Depth);

/// <summary>What happens to a crushed character and the grid that crushes it.</summary>
public enum CrushResponseKind : byte
{
    /// <summary>The grid goes back to where it was before the step, and the character stays where it is.</summary>
    Block,

    /// <summary>The grid keeps its place, in the character, and the character stays where it is.</summary>
    Pass,

    /// <summary>The grid keeps its place, and the character goes to <see cref="CrushResponse.Position"/>.</summary>
    Move,
}

/// <summary>What a <see cref="ICrushResolver"/> answers to a crush.</summary>
/// <param name="Kind">What happens.</param>
/// <param name="Position">For <see cref="CrushResponseKind.Move"/>, where the feet of the character go, in the world.</param>
[HotPath]
public readonly record struct CrushResponse(CrushResponseKind Kind, WorldPosition Position = default)
{
    /// <summary>Gets the response that blocks the grid: the default.</summary>
    public static CrushResponse Block
    {
        [HotPath]
        get => new(CrushResponseKind.Block);
    }

    /// <summary>Gets the response that lets the grid go into the character.</summary>
    public static CrushResponse Pass
    {
        [HotPath]
        get => new(CrushResponseKind.Pass);
    }

    /// <summary>Gets the response that puts the character somewhere else and lets the grid go on.</summary>
    /// <param name="feet">Where the feet of the character go, in the world.</param>
    /// <returns>The response.</returns>
    [HotPath]
    public static CrushResponse MoveTo(in WorldPosition feet) => new(CrushResponseKind.Move, feet);
}

/// <summary>Decides what a crush does; nPhysics asks it on the loop thread, during the step.</summary>
/// <remarks>
/// It may read the entities and call the commands of nPhysics, which apply at
/// the next step; it does not change the structure of an entity world.
/// </remarks>
public interface ICrushResolver
{
    /// <summary>Decides what a crush does.</summary>
    /// <param name="crush">The grid, the character, and the way the grid pushes.</param>
    /// <returns>What happens.</returns>
    [HotPath]
    CrushResponse Resolve(in CharacterCrush crush);
}

/// <summary>The registration of the resolver of crushes; dispose it to remove the resolver.</summary>
public sealed class CrushResolverHandle : IDisposable
{
    private Action<CrushResolverHandle>? _remove;

    internal CrushResolverHandle(ModuleContext module, ICrushResolver resolver, Action<CrushResolverHandle> remove) => (Module, Resolver, _remove) = (module, resolver, remove);

    internal ModuleContext Module { get; }

    internal ICrushResolver Resolver { get; }

    /// <summary>Removes the resolver: crushes block the grid from the next step on.</summary>
    public void Dispose()
    {
        _remove?.Invoke(this);
        _remove = null;
    }
}
