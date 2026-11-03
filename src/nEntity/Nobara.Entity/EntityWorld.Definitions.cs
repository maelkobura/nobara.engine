// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Entity;

/// <summary>The entities created from entity definitions.</summary>
public sealed unsafe partial class EntityWorld
{
    // The archetype of each definition in this world, by the index of the definition.
    private EntityArchetype?[] _definitionArchetypes = [];

    /// <summary>
    /// Creates an entity from a definition: its components, with their
    /// initial values. Nothing is read from the resources; once the archetype
    /// of the definition exists in the world, nothing is allocated.
    /// </summary>
    /// <param name="definition">The definition, of the context of this world.</param>
    /// <returns>The entity.</returns>
    /// <exception cref="ArgumentException">The definition belongs to another context.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: its structure is locked.</exception>
    [HotPath]
    public EntityId Instantiate(EntityDefinition definition)
    {
        EntityId entity = default;
        Instantiate(definition, new Span<EntityId>(ref entity));
        return entity;
    }

    /// <summary>Creates entities from a definition, all with its components and their initial values.</summary>
    /// <param name="definition">The definition, of the context of this world.</param>
    /// <param name="entities">Receives the new entities; its length is the number to create.</param>
    /// <exception cref="ArgumentException">The definition belongs to another context.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: its structure is locked.</exception>
    /// <remarks>The first instantiation of a definition in a world creates its archetype, which allocates.</remarks>
    [HotPath]
    public void Instantiate(EntityDefinition definition, Span<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireUnlocked();
        if (definition.Owner != _owner)
        {
            throw new ArgumentException($"The entity definition '{definition.ObjectId}' belongs to another context.", nameof(definition));
        }

        _owner.Structure.CheckWrite();
        EntityArchetype archetype = (uint)definition.Index < (uint)_definitionArchetypes.Length && _definitionArchetypes[definition.Index] is { } known
            ? known
            : _definitionArchetype(definition);
        DefinitionLayout layout = definition.Layout;
        _records.Reserve(_records.Count + entities.Length);
        fixed (byte* template = layout.Values)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                EntityId entity = NewEntity();
                int block = archetype.AddRow(entity, ChangeVersion, template, out int row);
                Place(entity.Index, archetype.Index, block, row);
                entities[i] = entity;
            }
        }
    }

    // The rare path of an instantiation: the first one of a definition in this world.
    private EntityArchetype CreateDefinitionArchetype(EntityDefinition definition)
    {
        EntityArchetype archetype = GetArchetype((int[])definition.Layout.Indices.Clone());
        if (definition.Index >= _definitionArchetypes.Length)
        {
            Array.Resize(ref _definitionArchetypes, Math.Max(definition.Index + 1, _definitionArchetypes.Length * 2));
        }

        _definitionArchetypes[definition.Index] = archetype;
        return archetype;
    }
}
