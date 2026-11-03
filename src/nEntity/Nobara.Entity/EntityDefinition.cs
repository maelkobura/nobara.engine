// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Text;
using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Entity;

/// <summary>
/// An entity definition of a <c>.nof</c> file, loaded in one context: its
/// components and their initial values, fixed when it is loaded. Get it with
/// <see cref="IEntityWorlds.GetDefinition"/>, and create entities from it with
/// <see cref="EntityWorld.Instantiate(EntityDefinition)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Its components are those the file writes, without the optional components
/// of inactive modules, plus those the converters of the modules add. The same
/// definition may thus have other components in a context where other modules
/// are active.
/// </para>
/// <para>
/// A development build reloads the values in place when the file changes; a
/// change of the components applies once no entity world exists, when the
/// world is reloaded. Entities created before keep their values. Use it on the
/// loop thread of its context.
/// </para>
/// </remarks>
public sealed class EntityDefinition
{
    internal EntityDefinition(EntityWorlds owner, int index, ResourceData resource, DefinitionLayout layout)
    {
        Owner = owner;
        Index = index;
        ObjectId = resource.ObjectId;
        Resource = resource;
        Layout = layout;
    }

    /// <summary>Gets the object identifier of the definition: <c>entity:example.animals.pig</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the resource of the definition, overrides applied: its source, its module, and its free properties.</summary>
    public ResourceData Resource { get; internal set; }

    /// <summary>Gets the components of the entities of the definition, in the order of their registration.</summary>
    public IReadOnlyList<ComponentType> Components => Layout.Components;

    internal EntityWorlds Owner { get; }

    /// <summary>Gets the index of the definition in its context, which worlds use to find its archetype.</summary>
    internal int Index { get; }

    /// <summary>Gets the components and their values; replaced, never changed in place, when it is reloaded.</summary>
    internal DefinitionLayout Layout { [HotPath] get; set; }

    /// <summary>Gets the components and values of a reload that changed the components, applied once no world exists.</summary>
    internal DefinitionLayout? Pending { get; set; }

    /// <summary>Gets the resource that goes with <see cref="Pending"/>.</summary>
    internal ResourceData? PendingResource { get; set; }

    /// <summary>Tells whether the entities of the definition have a component.</summary>
    /// <param name="component">The component.</param>
    /// <returns><see langword="true"/> when they have it.</returns>
    public bool Has(ComponentType component) => Layout.SlotOf(component.Index) >= 0;

    /// <summary>Gets the initial value of a component on the entities of the definition.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">The definition does not have the component, or the handle comes from another context.</exception>
    public T GetValue<T>(ComponentType<T> component)
        where T : unmanaged
    {
        Owner.Registry.Require(component);
        DefinitionLayout layout = Layout;
        int slot = layout.SlotOf(component.Untyped.Index);
        if (slot < 0)
        {
            throw new ArgumentException($"The entity definition '{ObjectId}' does not have the component '{Owner.Registry.Info(component).Name}'.", nameof(component));
        }

        return MemoryMarshal.Read<T>(layout.Values.AsSpan(layout.Offsets[slot]));
    }

    /// <inheritdoc/>
    public override string ToString() => ObjectId;
}

/// <summary>The components of a definition, sorted by index as in an archetype, and their values packed in that order.</summary>
internal sealed class DefinitionLayout
{
    public DefinitionLayout(ComponentRegistry registry, IReadOnlyList<(int Index, byte[] Value)> components)
    {
        (int Index, byte[] Value)[] sorted = components.OrderBy(c => c.Index).ToArray();
        Indices = sorted.Select(c => c.Index).ToArray();
        Components = Indices.Select(i => registry.Info(new ComponentType(i)).Handle).ToArray();
        Offsets = new int[sorted.Length];
        int size = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            Offsets[i] = size;
            size += sorted[i].Value.Length;
        }

        Values = new byte[size];
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i].Value.CopyTo(Values, Offsets[i]);
        }
    }

    /// <summary>Gets the indices of the components, sorted.</summary>
    public int[] Indices { get; }

    public ComponentType[] Components { get; }

    /// <summary>Gets the offset of the value of each component in <see cref="Values"/>.</summary>
    public int[] Offsets { get; }

    /// <summary>Gets the values, packed in the order of <see cref="Indices"/>: the row that each new entity copies.</summary>
    public byte[] Values { get; }

    public int SlotOf(int componentIndex) => Array.BinarySearch(Indices, componentIndex) is var slot and >= 0 ? slot : -1;

    public bool SameComponents(DefinitionLayout other) => Indices.AsSpan().SequenceEqual(other.Indices);

    /// <summary>Describes how the components differ from those of another layout.</summary>
    public string Difference(DefinitionLayout before, ComponentRegistry registry)
    {
        var text = new StringBuilder();
        string[] added = Indices.Except(before.Indices).Select(i => registry.Info(new ComponentType(i)).Name).ToArray();
        string[] removed = before.Indices.Except(Indices).Select(i => registry.Info(new ComponentType(i)).Name).ToArray();
        if (added.Length > 0)
        {
            text.Append("added ").Append(string.Join(", ", added));
        }

        if (removed.Length > 0)
        {
            text.Append(added.Length > 0 ? "; " : string.Empty).Append("removed ").Append(string.Join(", ", removed));
        }

        return text.ToString();
    }
}

/// <summary>One reason why an entity definition cannot be loaded.</summary>
/// <param name="DefinitionId">The object identifier of the definition.</param>
/// <param name="Message">What is wrong, naming the component and the value.</param>
/// <param name="Source">Where the problem is written in the <c>.nof</c> file: the value, the component, or the definition.</param>
public sealed record EntityProblem(string DefinitionId, string Message, SourcePosition Source)
{
    /// <summary>Returns the problem as a compiler writes it.</summary>
    /// <returns>For example <c>Resources/animals.nof(9,9): entity:example.animals.pig: ...</c>.</returns>
    public override string ToString() =>
        string.IsNullOrEmpty(Source.File) ? $"{DefinitionId}: {Message}" : $"{Source}: {DefinitionId}: {Message}";
}

/// <summary>
/// Thrown when entity definitions cannot be loaded: an unknown component, a
/// value of the wrong type, a field that the component does not have, or an
/// error reported by a converter. In development mode, every definition is
/// loaded when the entity module starts, and the start of the context fails.
/// </summary>
/// <remarks>Every problem names the definition, the component, and the position in the <c>.nof</c> file.</remarks>
public sealed class EntityLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public EntityLoadException(IReadOnlyList<EntityProblem> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<EntityProblem> Problems { get; }

    private static string Describe(IReadOnlyList<EntityProblem> problems)
    {
        var text = new StringBuilder("The entity definitions cannot be loaded:");
        foreach (EntityProblem problem in problems)
        {
            text.AppendLine().Append("- ").Append(problem.ToString());
        }

        return text.ToString();
    }
}
