// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Items;

/// <summary>
/// A type of item component, registered in code by a module under a qualified
/// name such as <c>example.food.edible</c>. Items declare their default
/// values in their <c>.nof</c> file with <c>&lt;Component ref="…"&gt;</c>.
/// </summary>
/// <remarks>
/// Register types with <see cref="IItemRegistry.RegisterComponent{T}"/>. The
/// object is the key of the component in <see cref="ItemComponents"/> and in
/// the handlers of the usage service.
/// </remarks>
public abstract class ItemComponentType
{
    private protected ItemComponentType(string name, string moduleId, int id, Type valueType)
    {
        Name = name;
        ModuleId = moduleId;
        Id = id;
        ValueType = valueType;
    }

    /// <summary>Gets the qualified name: the identifier of the module, a dot, and a name, such as <c>example.food.edible</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the identifier of the module that registered the type.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the C# type of the values.</summary>
    public Type ValueType { get; }

    /// <summary>Gets the number of the type in its context, in order of registration.</summary>
    internal int Id { get; }

    /// <summary>Returns the qualified name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    /// <summary>Converts the values written in a <c>.nof</c> file.</summary>
    internal abstract object Read(ComponentValues values);

    /// <summary>Compares two values of the type with its equality.</summary>
    internal abstract bool ValuesEqual(object left, object right);

    /// <summary>Hashes a value of the type with its equality.</summary>
    internal abstract int HashValue(object value);
}

/// <summary>A type of item component whose values are of type <typeparamref name="T"/>.</summary>
/// <typeparam name="T">
/// The type of the values. Values are immutable: a stack that changes a
/// value gets a new set of components. Use a record, a read-only struct, or
/// another type that never changes once built.
/// </typeparam>
public sealed class ItemComponentType<T> : ItemComponentType
    where T : notnull
{
    private readonly Func<ComponentValues, T> _read;

    internal ItemComponentType(string name, string moduleId, int id, Func<ComponentValues, T> read, IEqualityComparer<T> equality)
        : base(name, moduleId, id, typeof(T))
    {
        _read = read;
        Equality = equality;
    }

    /// <summary>
    /// Gets the equality of the values: two stacks merge only when their
    /// components are equal by it.
    /// </summary>
    public IEqualityComparer<T> Equality { get; }

    internal override object Read(ComponentValues values) => _read(values);

    internal override bool ValuesEqual(object left, object right) => Equality.Equals((T)left, (T)right);

    internal override int HashValue(object value) => Equality.GetHashCode((T)value);
}
