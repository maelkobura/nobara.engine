// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Entity;

/// <summary>Which entities a query matches: those that have all of some components and none of others.</summary>
public sealed class QueryDescription
{
    /// <summary>Gets the components a matching entity has.</summary>
    public IReadOnlyList<ComponentType> All { get; init; } = [];

    /// <summary>Gets the components a matching entity does not have.</summary>
    public IReadOnlyList<ComponentType> None { get; init; } = [];
}

/// <summary>
/// The entities of a world that match a <see cref="QueryDescription"/>,
/// visited block by block. Create it once with <see cref="EntityWorld.Query"/>
/// and keep it: it follows the archetypes that appear later.
/// </summary>
/// <example>
/// <code>
/// foreach (ChunkView block in query)
/// {
///     Span&lt;Position&gt; positions = block.Write(_position);
///     ReadOnlySpan&lt;Velocity&gt; velocities = block.Read(_velocity);
///     for (int i = 0; i &lt; block.Count; i++)
///     {
///         positions[i].Value += velocities[i].Value * delta;
///     }
/// }
/// </code>
/// </example>
public sealed class EntityQuery
{
    private readonly int[] _all;
    private readonly int[] _none;
    private readonly List<EntityArchetype> _matches = [];
    private int _seen;

    internal EntityQuery(EntityWorld world, int[] all, int[] none)
    {
        World = world;
        _all = all;
        _none = none;
    }

    /// <summary>Gets the world of the query.</summary>
    public EntityWorld World { get; }

    internal List<EntityArchetype> Matches
    {
        [HotPath]
        get => _matches;
    }

    /// <summary>Counts the entities that match.</summary>
    /// <returns>The number of entities.</returns>
    [HotPath]
    public int Count()
    {
        Update();
        int count = 0;
        for (int i = 0; i < _matches.Count; i++)
        {
            count += _matches[i].EntityCount;
        }

        return count;
    }

    /// <summary>Gets the blocks of the matching entities, for <c>foreach</c>.</summary>
    /// <returns>The enumerator.</returns>
    /// <remarks>Do not change the structure of the world (create, destroy, add or remove components) while enumerating; record the changes in a <see cref="EntityCommandBuffer"/>.</remarks>
    [HotPath]
    public ChunkEnumerator GetEnumerator()
    {
        Update();
        return new ChunkEnumerator(this);
    }

    /// <summary>Adds the archetypes created since the last call.</summary>
    [HotPath]
    internal void Update()
    {
        List<EntityArchetype> archetypes = World.ArchetypeList;
        for (; _seen < archetypes.Count; _seen++)
        {
            EntityArchetype archetype = archetypes[_seen];
            bool match = true;
            foreach (int index in _all)
            {
                match &= archetype.SlotOf(index) >= 0;
            }

            foreach (int index in _none)
            {
                match &= archetype.SlotOf(index) < 0;
            }

            if (match)
            {
                _matches.Add(archetype);
            }
        }
    }

    /// <summary>Visits the blocks of the entities a query matches.</summary>
    public unsafe ref struct ChunkEnumerator
    {
        private readonly EntityQuery _query;
        private int _archetype;
        private int _block;

        [HotPath]
        internal ChunkEnumerator(EntityQuery query)
        {
            _query = query;
            _archetype = 0;
            _block = -1;
        }

        /// <summary>Gets the current block.</summary>
        public readonly ChunkView Current
        {
            [HotPath]
            get
            {
                EntityArchetype archetype = _query._matches[_archetype];
                EntityArchetype.Block block = archetype.Blocks[_block];
                return new ChunkView(archetype, block.Data, block.Count);
            }
        }

        /// <summary>Moves to the next block.</summary>
        /// <returns><see langword="false"/> after the last block.</returns>
        [HotPath]
        public bool MoveNext()
        {
            List<EntityArchetype> matches = _query._matches;
            while (_archetype < matches.Count)
            {
                if (++_block < matches[_archetype].BlockCount)
                {
                    return true;
                }

                _archetype++;
                _block = -1;
            }

            return false;
        }
    }
}
