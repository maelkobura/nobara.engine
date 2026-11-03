// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace Nobara.Entity.Tests.Support;

internal struct Position
{
    public Vector3 Value;
}

internal struct Velocity
{
    public Vector3 Value;
}

internal struct Health
{
    public int Value;
}

internal struct Burning
{
    public float Remaining;
}

/// <summary>A large component, so that few entities fit in a block.</summary>
internal unsafe struct Cargo
{
    public fixed byte Bytes[4000];
}

/// <summary>The components of the tests, registered by module <c>example.farm</c>.</summary>
internal sealed class FarmComponents
{
    public FarmComponents(Probe probe)
    {
        Position = probe.Worlds.RegisterComponent<Position>(probe.Context, "example.farm.position", new ComponentOptions { TrackChanges = true });
        Velocity = probe.Worlds.RegisterComponent<Velocity>(probe.Context, "example.farm.velocity");
        Health = probe.Worlds.RegisterComponent(probe.Context, "example.farm.health", defaultValue: new Health { Value = 10 });
        Burning = probe.Worlds.RegisterComponent<Burning>(probe.Context, "example.farm.burning");
        Cargo = probe.Worlds.RegisterComponent<Cargo>(probe.Context, "example.farm.cargo");
    }

    public ComponentType<Position> Position { get; }

    public ComponentType<Velocity> Velocity { get; }

    public ComponentType<Health> Health { get; }

    public ComponentType<Burning> Burning { get; }

    public ComponentType<Cargo> Cargo { get; }
}

internal enum Diet
{
    Grass,
    Meat,
}

/// <summary>A component that entity definitions write by its public fields.</summary>
internal struct Stats
{
    public int Health;
    public float Speed;
    public Vector3 Size;
    public Diet Diet;
    public bool Tame;
    public byte Litter;
}

/// <summary>A component that a converter of module <c>example.motion</c> sets from the free property <c>Weight</c>.</summary>
internal struct Body
{
    public float Mass;
}

/// <summary>A component whose reader checks a bound: a volume from 0 to 1.</summary>
internal struct Voice
{
    public float Volume;
}
