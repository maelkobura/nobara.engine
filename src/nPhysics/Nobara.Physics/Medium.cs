// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Physics;

/// <summary>The medium at a point: the fluid a body is in, as a provider describes it.</summary>
/// <param name="Density">The density, in kilograms per cubic block; 0 in a vacuum.</param>
/// <param name="Viscosity">How much the medium slows what moves in it, from 0.</param>
/// <param name="Current">The velocity of the medium, in blocks per second.</param>
public readonly record struct MediumSample(float Density, float Viscosity, Vector3 Current);

/// <summary>
/// A provider of medium, such as a fluid module: it tells nPhysics what fills
/// a point, without nPhysics depending on it.
/// </summary>
public interface IMediumProvider
{
    /// <summary>Describes the medium at a point.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="position">The point.</param>
    /// <param name="sample">The medium, when the provider has one there.</param>
    /// <returns><see langword="true"/> when the provider describes this point.</returns>
    [HotPath]
    bool TrySample(DimensionId dimension, in WorldPosition position, out MediumSample sample);
}

/// <summary>The registration of a provider of medium; dispose it to remove the provider.</summary>
public sealed class MediumProviderHandle : IDisposable
{
    private Action<MediumProviderHandle>? _remove;

    internal MediumProviderHandle(IMediumProvider provider, Action<MediumProviderHandle> remove) => (Provider, _remove) = (provider, remove);

    internal IMediumProvider Provider { get; }

    /// <summary>Removes the provider.</summary>
    public void Dispose()
    {
        _remove?.Invoke(this);
        _remove = null;
    }
}
