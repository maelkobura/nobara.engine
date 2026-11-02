// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Entity;

/// <summary>
/// A component type registered with <see cref="IEntityWorlds"/>, without its
/// C# type: what code that only knows the qualified name holds. The default
/// value names no component.
/// </summary>
/// <remarks>
/// A handle belongs to the context that registered the component, and is
/// valid in every world of that context.
/// </remarks>
public readonly struct ComponentType : IEquatable<ComponentType>
{
    // The index plus one, so that the default value is not valid.
    private readonly int _value;

    [HotPath]
    internal ComponentType(int index)
    {
        _value = index + 1;
    }

    /// <summary>Gets whether the handle names a component.</summary>
    public bool IsValid
    {
        [HotPath]
        get => _value != 0;
    }

    /// <summary>Gets the index of the component in the registry of its context, from 0.</summary>
    internal int Index
    {
        [HotPath]
        get => _value - 1;
    }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(ComponentType other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ComponentType other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => _value;

    /// <summary>Tells whether two handles name the same component.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    [HotPath]
    public static bool operator ==(ComponentType left, ComponentType right) => left.Equals(right);

    /// <summary>Tells whether two handles name different components.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    [HotPath]
    public static bool operator !=(ComponentType left, ComponentType right) => !left.Equals(right);
}

/// <summary>
/// A component type registered with <see cref="IEntityWorlds"/>, with its C#
/// type: the handle that reads and writes the values of the component.
/// </summary>
/// <typeparam name="T">The C# type of the component: an unmanaged struct.</typeparam>
/// <remarks>It converts to <see cref="ComponentType"/> wherever a component is named without its type.</remarks>
public readonly struct ComponentType<T> : IEquatable<ComponentType<T>>
    where T : unmanaged
{
    [HotPath]
    internal ComponentType(ComponentType type)
    {
        Untyped = type;
    }

    /// <summary>Gets the handle without the C# type.</summary>
    public ComponentType Untyped { get; }

    /// <summary>Gets whether the handle names a component.</summary>
    public bool IsValid
    {
        [HotPath]
        get => Untyped.IsValid;
    }

    /// <summary>Converts the handle to a handle without the C# type.</summary>
    /// <param name="type">The handle.</param>
    [HotPath]
    public static implicit operator ComponentType(ComponentType<T> type) => type.Untyped;

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(ComponentType<T> other) => Untyped.Equals(other.Untyped);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ComponentType<T> other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => Untyped.GetHashCode();

    /// <summary>Tells whether two handles name the same component.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    [HotPath]
    public static bool operator ==(ComponentType<T> left, ComponentType<T> right) => left.Equals(right);

    /// <summary>Tells whether two handles name different components.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    [HotPath]
    public static bool operator !=(ComponentType<T> left, ComponentType<T> right) => !left.Equals(right);
}

/// <summary>How a component is stored and followed.</summary>
public sealed class ComponentOptions
{
    /// <summary>
    /// Gets whether the writes to the component are recorded, entity by
    /// entity, so that a reader can find what changed since its last read
    /// (<see cref="ChunkView.HasChangedSince(ComponentType, uint)"/>). Only
    /// components whose changes someone reads should be tracked: tracking
    /// costs 4 bytes per entity and a little time per write.
    /// </summary>
    public bool TrackChanges { get; init; }
}

/// <summary>A component type registered with <see cref="IEntityWorlds"/>.</summary>
public sealed class ComponentInfo
{
    internal ComponentInfo(ComponentType handle, string name, Type type, int size, string moduleId, bool trackChanges, JobData data)
    {
        Handle = handle;
        Name = name;
        Type = type;
        Size = size;
        ModuleId = moduleId;
        TrackChanges = trackChanges;
        Data = data;
    }

    /// <summary>Gets the handle of the component.</summary>
    public ComponentType Handle { get; }

    /// <summary>Gets the qualified name of the component, such as <c>example.farm.growth</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the C# type of the component.</summary>
    public Type Type { get; }

    /// <summary>Gets the size of one value, in bytes.</summary>
    public int Size { get; }

    /// <summary>Gets the module that registered the component.</summary>
    public string ModuleId { get; }

    /// <summary>Gets whether writes to the component are tracked (<see cref="ComponentOptions.TrackChanges"/>).</summary>
    public bool TrackChanges { get; }

    /// <summary>
    /// Gets the data of the job system that stands for the values of the
    /// component, in every world of the context. Systems of the entity
    /// module declare it for you; another tick system that reads or writes
    /// the component declares it in its accesses.
    /// </summary>
    public JobData Data { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
