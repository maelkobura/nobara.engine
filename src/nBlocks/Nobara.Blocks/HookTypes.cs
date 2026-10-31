// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Items;

namespace Nobara.Blocks;

/// <summary>
/// A kind of block hook: what happens to blocks that modules subscribe to,
/// by type or by tag. nBlocks declares and stores the hooks; the modules that
/// act on blocks fire them.
/// </summary>
/// <remarks>
/// The standard kinds are listed by <see cref="IBlockHooks"/>. A module
/// declares more unit hooks with <see cref="IBlockHooks.DeclareHook{TArgs}"/>.
/// </remarks>
public abstract class BlockHookKind
{
    private protected BlockHookKind(string name, string moduleId, int id)
    {
        Name = name;
        ModuleId = moduleId;
        Id = id;
    }

    /// <summary>Gets the name: <c>Breaking</c> for a standard kind, a qualified name such as <c>example.physics.contact</c> for a declared one.</summary>
    public string Name { get; }

    /// <summary>Gets the module that declared the kind: <c>nobara.blocks</c> for a standard kind.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the number of the kind in its context, in order of declaration.</summary>
    internal int Id { get; }

    /// <summary>Gets the bit of the kind in the flags of a state.</summary>
    internal ulong Bit
    {
        [HotPath]
        get => 1UL << Id;
    }

    /// <summary>Returns the name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    /// <summary>Builds the tables of the kind from its subscribers, sorted, and sets its bit in the flags of the types it concerns.</summary>
    /// <returns><see langword="true"/> when a subscriber concerns every type.</returns>
    internal abstract bool Build(IReadOnlyList<HookEntry> sorted, BlockType[] types, ulong[] typeFlags);

    /// <summary>Tells whether a subscriber concerns a type.</summary>
    internal static bool Concerns(HookEntry entry, BlockType type) =>
        ReferenceEquals(entry.Type, type) || (entry.Tag is not null && entry.Tag.Contains(type));
}

/// <summary>
/// A hook fired for a batch of blocks: <c>Placing</c>, <c>Placed</c>,
/// <c>Breaking</c>, and <c>Broken</c>. A single block is a batch of one.
/// </summary>
public sealed class BlockBatchHook : BlockHookKind
{
    internal BlockBatchHook(string name, int id, bool before, bool usesOldState)
        : base(name, BlocksModule.ModuleId, id)
    {
        IsBefore = before;
        UsesOldState = usesOldState;
    }

    /// <summary>
    /// Gets whether the hook runs before the batch applies (<c>Placing</c>,
    /// <c>Breaking</c>): a subscriber may then remove blocks or cancel the
    /// batch, and may subscribe to every type at once.
    /// </summary>
    public bool IsBefore { get; }

    /// <summary>Gets whether the subscribers are chosen by the type of the old state of each block (breaking) rather than the new one (placing).</summary>
    public bool UsesOldState { get; }

    /// <summary>Gets the subscribers, sorted; built when the tables are rebuilt.</summary>
    internal BatchEntry[] Entries { get; private set; } = [];

    /// <summary>Gets, for each type, the positions in <see cref="Entries"/> of its subscribers, in order.</summary>
    internal int[][] ByType { get; private set; } = [];

    /// <summary>Gets whether a subscriber concerns every type.</summary>
    internal bool HasGlobal { get; private set; }

    /// <summary>Gets or sets whether the hook is being fired: its subscribers share one bucket each.</summary>
    internal bool IsFiring { get; set; }

    internal override bool Build(IReadOnlyList<HookEntry> sorted, BlockType[] types, ulong[] typeFlags)
    {
        BatchEntry[] entries = sorted.Cast<BatchEntry>().ToArray();
        var byType = new int[types.Length][];
        for (int t = 0; t < types.Length; t++)
        {
            byType[t] = Enumerable.Range(0, entries.Length).Where(p => Concerns(entries[p], types[t])).ToArray();
            if (byType[t].Length > 0)
            {
                typeFlags[t] |= Bit;
            }
        }

        Entries = entries;
        ByType = byType;
        HasGlobal = entries.Any(e => e.IsGlobal);
        return HasGlobal;
    }
}

/// <summary>A hook fired for one block, such as <c>Interact</c> or <c>RandomTick</c>.</summary>
/// <typeparam name="TArgs">What the subscribers receive.</typeparam>
public sealed class BlockHook<TArgs> : BlockHookKind
    where TArgs : struct
{
    internal BlockHook(string name, string moduleId, int id, bool consumable)
        : base(name, moduleId, id)
    {
        IsConsumable = consumable;
    }

    /// <summary>Gets whether a subscriber that answers <see langword="true"/> consumes the event, so that the next ones do not run, as <c>Interact</c> does.</summary>
    public bool IsConsumable { get; }

    /// <summary>Gets the subscribers of each type, sorted; built when the tables are rebuilt.</summary>
    internal UnitEntry<TArgs>[][] ByType { get; private set; } = [];

    internal override bool Build(IReadOnlyList<HookEntry> sorted, BlockType[] types, ulong[] typeFlags)
    {
        UnitEntry<TArgs>[] entries = sorted.Cast<UnitEntry<TArgs>>().ToArray();
        var byType = new UnitEntry<TArgs>[types.Length][];
        for (int t = 0; t < types.Length; t++)
        {
            byType[t] = entries.Where(e => Concerns(e, types[t])).ToArray();
            if (byType[t].Length > 0)
            {
                typeFlags[t] |= Bit;
            }
        }

        ByType = byType;
        return false;
    }
}

/// <summary>A position of a block, in a neutral form: the identifier of the entity of its grid, and its position in the grid.</summary>
/// <param name="Grid">The identifier of the entity of the grid.</param>
/// <param name="X">The X position in the grid, toward the east.</param>
/// <param name="Y">The Y position, upward.</param>
/// <param name="Z">The Z position, toward the south.</param>
[HotPath]
public readonly record struct BlockPosition(ulong Grid, int X, int Y, int Z);

/// <summary>Who acts on blocks.</summary>
public enum BlockActorKind
{
    /// <summary>Nobody named.</summary>
    None,

    /// <summary>An entity, such as a player or a mob.</summary>
    Entity,

    /// <summary>A command.</summary>
    Command,

    /// <summary>A plugin.</summary>
    Plugin,
}

/// <summary>Who acts on blocks, in a neutral form: nBlocks knows neither entities nor plugins.</summary>
public readonly struct BlockActor : IEquatable<BlockActor>
{
    private readonly string? _name;

    [HotPath]
    private BlockActor(BlockActorKind kind, ulong entity, string name)
    {
        Kind = kind;
        Entity = entity;
        _name = name;
    }

    /// <summary>Gets what the actor is.</summary>
    public BlockActorKind Kind { get; }

    /// <summary>Gets the identifier of the entity, for <see cref="BlockActorKind.Entity"/>.</summary>
    public ulong Entity { get; }

    /// <summary>Gets the name of the command or of the plugin; empty otherwise.</summary>
    public string Name
    {
        [HotPath]
        get => _name ?? string.Empty;
    }

    /// <summary>Creates an actor that is an entity.</summary>
    /// <param name="entity">The identifier of the entity.</param>
    /// <returns>The actor.</returns>
    [HotPath]
    public static BlockActor OfEntity(ulong entity) => new(BlockActorKind.Entity, entity, string.Empty);

    /// <summary>Creates an actor that is a command.</summary>
    /// <param name="name">The name of the command.</param>
    /// <returns>The actor.</returns>
    [HotPath]
    public static BlockActor OfCommand(string name) => new(BlockActorKind.Command, 0, name);

    /// <summary>Creates an actor that is a plugin.</summary>
    /// <param name="name">The name of the plugin.</param>
    /// <returns>The actor.</returns>
    [HotPath]
    public static BlockActor OfPlugin(string name) => new(BlockActorKind.Plugin, 0, name);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(BlockActor other) => Kind == other.Kind && Entity == other.Entity && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BlockActor other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => HashCode.Combine(Kind, Entity, Name);

    /// <summary>Returns the actor as logs show it.</summary>
    /// <returns>For example <c>entity 42</c> or <c>command fill</c>.</returns>
    public override string ToString() => Kind switch
    {
        BlockActorKind.Entity => $"entity {Entity}",
        BlockActorKind.None => "nobody",
        _ => $"{Kind.ToString().ToLowerInvariant()} {Name}",
    };
}

/// <summary>
/// Why blocks are placed or broken: mining, damage, an explosion, or a cause
/// that a module names, such as <c>example.traps.crusher</c>. Causes are
/// compared by name.
/// </summary>
public sealed class BlockCause : IEquatable<BlockCause>
{
    /// <summary>Initializes a cause.</summary>
    /// <param name="name">The name; a module names its own causes with its identifier first.</param>
    public BlockCause(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>Gets an actor mining or placing by hand.</summary>
    public static BlockCause Hand { get; } = new("hand");

    /// <summary>Gets damage dealt to blocks.</summary>
    public static BlockCause Damage { get; } = new("damage");

    /// <summary>Gets an explosion.</summary>
    public static BlockCause Explosion { get; } = new("explosion");

    /// <summary>Gets a command.</summary>
    public static BlockCause Command { get; } = new("command");

    /// <summary>Gets the name.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(BlockCause? other) => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as BlockCause);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>Returns the name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}

/// <summary>One block of a batch: where it is, its state before, and its state after.</summary>
/// <param name="Position">The position of the block.</param>
/// <param name="OldState">The state before the batch: air for a placement.</param>
/// <param name="NewState">The state after the batch: air for a break.</param>
/// <param name="MultiBlock">
/// A number shared by the cells of one block of several cells, 0 for a
/// block of one cell. Removing one cell from a batch removes every cell with
/// the same number.
/// </param>
[HotPath]
public readonly record struct BlockChange(BlockPosition Position, uint OldState, uint NewState, ulong MultiBlock = 0);

/// <summary>
/// The blocks of one action: who acts, why, and the blocks it places or
/// breaks. The module that acts on blocks fills it, fires the hook before the
/// action, applies the blocks that remain, then fires the hook after.
/// </summary>
/// <remarks>
/// Reuse a batch with <see cref="Clear"/>: adding and firing then allocate
/// nothing once its lists have grown. Use it on the loop thread of the context.
/// </remarks>
public sealed class BlockBatch
{
    private readonly List<BlockChange> _changes;
    private readonly List<bool> _removed;
    private readonly List<int> _nextCell = [];
    private readonly Dictionary<ulong, int> _firstCell = [];
    private bool _linked;

    /// <summary>Creates an empty batch.</summary>
    /// <param name="capacity">The number of blocks it holds before it grows.</param>
    public BlockBatch(int capacity = 16)
    {
        _changes = new List<BlockChange>(capacity);
        _removed = new List<bool>(capacity);
    }

    /// <summary>Gets or sets who acts.</summary>
    public BlockActor Actor { get; set; }

    /// <summary>Gets or sets why.</summary>
    public BlockCause Cause { get; set; } = BlockCause.Hand;

    /// <summary>Gets the number of blocks added, removed ones included.</summary>
    public int Count
    {
        [HotPath]
        get => _changes.Count;
    }

    /// <summary>Gets the number of blocks that remain: added and not removed.</summary>
    public int RemainingCount { get; private set; }

    /// <summary>Gets whether a subscriber cancelled the whole batch.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>Gets the blocks added, removed ones included; test <see cref="IsRemoved"/>.</summary>
    public ReadOnlySpan<BlockChange> Changes
    {
        [HotPath]
        get => CollectionsMarshal.AsSpan(_changes);
    }

    /// <summary>Adds a block.</summary>
    /// <param name="change">The block.</param>
    [HotPath]
    public void Add(in BlockChange change)
    {
        _changes.Add(change);
        _removed.Add(false);
        RemainingCount++;
        _linked = false;
    }

    /// <summary>Tells whether a block was removed from the batch by a subscriber.</summary>
    /// <param name="index">The index of the block in <see cref="Changes"/>.</param>
    /// <returns><see langword="true"/> when it was removed.</returns>
    [HotPath]
    public bool IsRemoved(int index) => _removed[index];

    /// <summary>Empties the batch so that it can be reused, and forgets a cancellation.</summary>
    [HotPath]
    public void Clear()
    {
        _changes.Clear();
        _removed.Clear();
        RemainingCount = 0;
        IsCancelled = false;
        _linked = false;
    }

    /// <summary>Removes a block, and every cell of its block of several cells.</summary>
    [HotPath]
    internal void Remove(int index)
    {
        if (_removed[index])
        {
            return;
        }

        if (_changes[index].MultiBlock == 0)
        {
            _removed[index] = true;
            RemainingCount--;
            return;
        }

        Link();
        int cell = index;
        do
        {
            if (!_removed[cell])
            {
                _removed[cell] = true;
                RemainingCount--;
            }

            cell = _nextCell[cell];
        }
        while (cell != index);
    }

    [HotPath]
    internal void Cancel() => IsCancelled = true;

    // Chains the cells of each block of several cells in a ring, in one pass.
    [HotPath]
    private void Link()
    {
        if (_linked)
        {
            return;
        }

        _nextCell.Clear();
        _firstCell.Clear();
        for (int i = 0; i < _changes.Count; i++)
        {
            ulong group = _changes[i].MultiBlock;
            _nextCell.Add(i);
            if (group == 0)
            {
                continue;
            }

            if (_firstCell.TryGetValue(group, out int first))
            {
                _nextCell[i] = _nextCell[first];
                _nextCell[first] = i;
            }
            else
            {
                _firstCell[group] = i;
            }
        }

        _linked = true;
    }
}

/// <summary>
/// What one subscriber of a batch hook receives: the blocks of the batch
/// whose type it subscribes to, that no earlier subscriber removed, or the
/// whole batch for a subscriber of every type.
/// </summary>
/// <remarks>The view is valid during the call only.</remarks>
public readonly ref struct BlockBatchView
{
    private readonly BlockBatch _batch;
    private readonly List<int> _indices;

    [HotPath]
    internal BlockBatchView(BlockBatchHook hook, BlockBatch batch, List<int> indices)
    {
        Hook = hook;
        _batch = batch;
        _indices = indices;
    }

    /// <summary>Gets the hook.</summary>
    public BlockBatchHook Hook { get; }

    /// <summary>Gets who acts.</summary>
    public BlockActor Actor
    {
        [HotPath]
        get => _batch.Actor;
    }

    /// <summary>Gets why.</summary>
    public BlockCause Cause
    {
        [HotPath]
        get => _batch.Cause;
    }

    /// <summary>Gets the number of blocks of the view.</summary>
    public int Count
    {
        [HotPath]
        get => _indices.Count;
    }

    /// <summary>Gets a block of the view.</summary>
    /// <param name="index">The index in the view, from 0 to <see cref="Count"/> minus 1.</param>
    /// <returns>The block.</returns>
    public BlockChange this[int index]
    {
        [HotPath]
        get => _batch.Changes[_indices[index]];
    }

    /// <summary>Tells whether a block of the view was removed, by this subscriber or with a cell of the same block of several cells.</summary>
    /// <param name="index">The index in the view.</param>
    /// <returns><see langword="true"/> when it was removed.</returns>
    [HotPath]
    public bool IsRemoved(int index) => _batch.IsRemoved(_indices[index]);

    /// <summary>
    /// Removes a block from the batch, and every cell of its block of several
    /// cells: it is not applied, and the next subscribers do not see it.
    /// </summary>
    /// <param name="index">The index in the view.</param>
    /// <exception cref="InvalidOperationException">The hook runs after the batch applied.</exception>
    [HotPath]
    public void Remove(int index)
    {
        if (!Hook.IsBefore)
        {
            throw new InvalidOperationException($"{Hook.Name} runs once the batch applied; changes it causes go in a new batch.");
        }

        _batch.Remove(_indices[index]);
    }

    /// <summary>Cancels the whole batch: nothing applies, and the next subscribers are not called.</summary>
    /// <exception cref="InvalidOperationException">The hook runs after the batch applied.</exception>
    [HotPath]
    public void Cancel()
    {
        if (!Hook.IsBefore)
        {
            throw new InvalidOperationException($"{Hook.Name} runs once the batch applied; it cannot be cancelled.");
        }

        _batch.Cancel();
    }
}

/// <summary>A subscriber of a batch hook.</summary>
public interface IBlockBatchHandler
{
    /// <summary>Receives the blocks of a batch that concern the subscriber.</summary>
    /// <param name="batch">The view of the batch for this subscriber.</param>
    [HotPath]
    void Handle(BlockBatchView batch);
}

/// <summary>A subscriber of a unit hook.</summary>
/// <typeparam name="TArgs">What the hook gives.</typeparam>
public interface IBlockHandler<TArgs>
    where TArgs : struct
{
    /// <summary>Receives one event of the hook.</summary>
    /// <param name="args">The event.</param>
    /// <returns><see langword="true"/> to consume the event of a consumable hook, such as <c>Interact</c>: the next subscribers do not run. Other hooks ignore it.</returns>
    [HotPath]
    bool Handle(in TArgs args);
}

/// <summary>An actor uses a block: the arguments of <c>Interact</c>.</summary>
/// <param name="Actor">Who uses the block.</param>
/// <param name="Position">The block.</param>
/// <param name="State">Its state.</param>
/// <param name="Face">The face the actor uses.</param>
[HotPath]
public readonly record struct BlockInteraction(BlockActor Actor, BlockPosition Position, uint State, BlockFace Face);

/// <summary>A direct neighbor of a block changed: the arguments of <c>NeighborChanged</c>.</summary>
/// <param name="Position">The block that is told.</param>
/// <param name="State">Its state.</param>
/// <param name="Neighbor">The neighbor that changed.</param>
/// <param name="NeighborOldState">The state of the neighbor before.</param>
/// <param name="NeighborNewState">The state of the neighbor after.</param>
[HotPath]
public readonly record struct NeighborChange(BlockPosition Position, uint State, BlockPosition Neighbor, uint NeighborOldState, uint NeighborNewState);

/// <summary>A tick of a block: the arguments of <c>RandomTick</c> and <c>ScheduledTick</c>.</summary>
/// <param name="Position">The block.</param>
/// <param name="State">Its state.</param>
[HotPath]
public readonly record struct BlockTick(BlockPosition Position, uint State);

/// <summary>A subscription to a block hook. Disposing it removes the subscriber.</summary>
public sealed class BlockHookHandle : IDisposable
{
    private readonly BlockHooks _hooks;

    internal BlockHookHandle(BlockHooks hooks, HookEntry entry)
    {
        _hooks = hooks;
        Entry = entry;
    }

    /// <summary>Gets the hook.</summary>
    public BlockHookKind Hook => Entry.Hook;

    /// <summary>Gets the priority of the subscriber.</summary>
    public int Priority => Entry.Priority;

    /// <summary>Gets whether the handle was disposed.</summary>
    public bool IsRevoked => Entry.Revoked;

    internal HookEntry Entry { get; }

    /// <summary>
    /// Removes the subscriber: it is not called any more, and the tables are
    /// rebuilt at the start of the next frame, before the next tick.
    /// </summary>
    public void Dispose() => _hooks.Revoke(Entry);
}
