// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Blocks;

/// <summary>The block hooks of one context.</summary>
internal sealed class BlockHooks : IBlockHooks
{
    private const int MaxKinds = 64;

    private readonly BlockRegistry _registry;
    private readonly List<BlockHookKind> _kinds = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly List<HookEntry> _entries = [];
    private ulong[] _stateFlags = [];
    private ulong _globalFlags;
    private long _sequence;
    private bool _rebuildQueued;
    private ModuleContext? _owner;

    public BlockHooks(BlockRegistry registry)
    {
        _registry = registry;
        Placing = AddBatch("Placing", before: true, usesOldState: false);
        Placed = AddBatch("Placed", before: false, usesOldState: false);
        Breaking = AddBatch("Breaking", before: true, usesOldState: true);
        Broken = AddBatch("Broken", before: false, usesOldState: true);
        Interact = AddUnit<BlockInteraction>("Interact", BlocksModule.ModuleId, consumable: true);
        NeighborChanged = AddUnit<NeighborChange>("NeighborChanged", BlocksModule.ModuleId, consumable: false);
        RandomTick = AddUnit<BlockTick>("RandomTick", BlocksModule.ModuleId, consumable: false);
        ScheduledTick = AddUnit<BlockTick>("ScheduledTick", BlocksModule.ModuleId, consumable: false);
    }

    public BlockBatchHook Placing { get; }

    public BlockBatchHook Placed { get; }

    public BlockBatchHook Breaking { get; }

    public BlockBatchHook Broken { get; }

    public BlockHook<BlockInteraction> Interact { get; }

    public BlockHook<NeighborChange> NeighborChanged { get; }

    public BlockHook<BlockTick> RandomTick { get; }

    public BlockHook<BlockTick> ScheduledTick { get; }

    public IReadOnlyList<BlockHookKind> Kinds => _kinds;

    /// <summary>Gets whether the tables are built, which happens when nBlocks starts.</summary>
    public bool IsStarted { get; private set; }

    public BlockHook<TArgs> DeclareHook<TArgs>(ModuleContext module, string name, bool consumable = false)
        where TArgs : struct
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        if (IsStarted)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' declares the block hook '{name}' after nobara.blocks started; declare hooks while modules initialize.");
        }

        string prefix = module.Module.Id + ".";
        string rest = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : string.Empty;
        if (rest.Length == 0 || !rest.Split('.').All(p => p.Length > 0 && p[0] is >= 'a' and <= 'z' && p.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')))
        {
            throw new ArgumentException($"'{name}' is not a qualified name of module '{module.Module.Id}': write '{prefix}' followed by lowercase names, such as '{prefix}contact'.", nameof(name));
        }

        if (_names.Contains(name))
        {
            throw new ArgumentException($"The block hook '{name}' is already declared.", nameof(name));
        }

        return AddUnit<TArgs>(name, module.Module.Id, consumable);
    }

    public BlockHookHandle Subscribe(ModuleContext module, BlockBatchHook hook, BlockType type, IBlockBatchHandler handler, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(type);
        CheckType(type);
        return Add(new BatchEntry(Check(module, hook), hook, priority, ++_sequence, type, null, handler));
    }

    public BlockHookHandle Subscribe(ModuleContext module, BlockBatchHook hook, BlockTag tag, IBlockBatchHandler handler, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(tag);
        CheckTag(tag);
        return Add(new BatchEntry(Check(module, hook), hook, priority, ++_sequence, null, tag, handler));
    }

    public BlockHookHandle SubscribeAll(ModuleContext module, BlockBatchHook hook, IBlockBatchHandler handler, int priority = 0)
    {
        ModuleContext checkedModule = Check(module, hook);
        if (!hook.IsBefore)
        {
            throw new ArgumentException($"Only Placing and Breaking accept subscribers of every type; subscribe to {hook.Name} by type or by tag.", nameof(hook));
        }

        return Add(new BatchEntry(checkedModule, hook, priority, ++_sequence, null, null, handler));
    }

    public BlockHookHandle Subscribe<TArgs>(ModuleContext module, BlockHook<TArgs> hook, BlockType type, IBlockHandler<TArgs> handler, int priority = 0)
        where TArgs : struct
    {
        ArgumentNullException.ThrowIfNull(type);
        CheckType(type);
        return Add(new UnitEntry<TArgs>(Check(module, hook), hook, priority, ++_sequence, type, null, handler));
    }

    public BlockHookHandle Subscribe<TArgs>(ModuleContext module, BlockHook<TArgs> hook, BlockTag tag, IBlockHandler<TArgs> handler, int priority = 0)
        where TArgs : struct
    {
        ArgumentNullException.ThrowIfNull(tag);
        CheckTag(tag);
        return Add(new UnitEntry<TArgs>(Check(module, hook), hook, priority, ++_sequence, null, tag, handler));
    }

    [HotPath]
    public bool HasSubscribers(BlockHookKind hook, uint state)
    {
        RequireStarted();
        CheckKind(hook);
        ulong[] flags = _stateFlags;
        if (state >= (uint)flags.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, $"The registry has {flags.Length} states.");
        }

        return ((flags[state] | _globalFlags) & hook.Bit) != 0;
    }

    [HotPath]
    public bool Fire(BlockBatchHook hook, BlockBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        RequireStarted();
        CheckKind(hook);
        if (batch.IsCancelled)
        {
            return false;
        }

        BatchEntry[] entries = hook.Entries;
        if (entries.Length == 0)
        {
            return true;
        }

        // The buckets of the subscribers are shared by every firing of the hook.
        if (hook.IsFiring)
        {
            throw new InvalidOperationException($"A subscriber fired {hook.Name} while it was being fired; changes it causes go in a new batch, fired after this one.");
        }

        hook.IsFiring = true;
        try
        {
            return FireBatch(hook, batch, entries);
        }
        finally
        {
            hook.IsFiring = false;
            for (int k = 0; k < entries.Length; k++)
            {
                entries[k].Bucket.Clear();
            }
        }
    }

    [HotPath]
    private bool FireBatch(BlockBatchHook hook, BlockBatch batch, BatchEntry[] entries)
    {
        // One pass: each block goes to the subscribers of its type.
        ulong[] flags = _stateFlags;
        int[] typeOfState = _registry.TypeOfState;
        int[][] byType = hook.ByType;
        ulong bit = hook.Bit;
        bool any = hook.HasGlobal;
        ReadOnlySpan<BlockChange> changes = batch.Changes;
        for (int i = 0; i < changes.Length; i++)
        {
            if (batch.IsRemoved(i))
            {
                continue;
            }

            uint state = hook.UsesOldState ? changes[i].OldState : changes[i].NewState;
            if (state >= (uint)flags.Length)
            {
                throw new ArgumentException($"Block {i} of the batch has state {state}; the registry has {flags.Length} states.", nameof(batch));
            }

            if ((flags[state] & bit) == 0)
            {
                continue;
            }

            int[] positions = byType[typeOfState[state]];
            for (int p = 0; p < positions.Length; p++)
            {
                entries[positions[p]].Bucket.Add(i);
            }

            any = true;
        }

        if (!any)
        {
            return true;
        }

        for (int k = 0; k < entries.Length; k++)
        {
            BatchEntry entry = entries[k];
            List<int> bucket = entry.Bucket;
            if (entry.IsGlobal)
            {
                bucket.Clear();
                for (int i = 0; i < changes.Length; i++)
                {
                    if (!batch.IsRemoved(i))
                    {
                        bucket.Add(i);
                    }
                }
            }
            else
            {
                // Earlier subscribers may have removed blocks.
                int kept = 0;
                for (int r = 0; r < bucket.Count; r++)
                {
                    if (!batch.IsRemoved(bucket[r]))
                    {
                        bucket[kept++] = bucket[r];
                    }
                }

                bucket.RemoveRange(kept, bucket.Count - kept);
            }

            if (bucket.Count == 0 || !entry.IsActive)
            {
                bucket.Clear();
                continue;
            }

            try
            {
                entry.Handler.Handle(new BlockBatchView(hook, batch, bucket));
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                entry.Module.ReportFault(exception);
            }

            bucket.Clear();
            if (batch.IsCancelled)
            {
                return false;
            }
        }

        return true;
    }

    [HotPath]
    public bool Fire<TArgs>(BlockHook<TArgs> hook, uint state, in TArgs args)
        where TArgs : struct
    {
        RequireStarted();
        CheckKind(hook);
        ulong[] flags = _stateFlags;
        if (state >= (uint)flags.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, $"The registry has {flags.Length} states.");
        }

        if ((flags[state] & hook.Bit) == 0)
        {
            return false;
        }

        UnitEntry<TArgs>[] entries = hook.ByType[_registry.TypeOfState[state]];
        for (int i = 0; i < entries.Length; i++)
        {
            UnitEntry<TArgs> entry = entries[i];
            if (!entry.IsActive)
            {
                continue;
            }

            bool consumed;
            try
            {
                consumed = entry.Handler.Handle(in args);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                entry.Module.ReportFault(exception);
                continue;
            }

            if (consumed && hook.IsConsumable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the tables for the first time, once every module has initialized.</summary>
    public void Start(ModuleContext owner)
    {
        _owner = owner;
        Rebuild();
        IsStarted = true;
    }

    /// <summary>Removes a subscriber: it is not called from now on, and the tables are rebuilt before the next tick.</summary>
    public void Revoke(HookEntry entry)
    {
        if (entry.Revoked)
        {
            return;
        }

        entry.Revoked = true;
        _entries.Remove(entry);
        QueueRebuild();
    }

    /// <summary>Builds the table of each kind and the flags of each state, from the subscribers and the tags of now.</summary>
    public void Rebuild()
    {
        _rebuildQueued = false;
        BlockType[] types = _registry.TypeArray;
        var typeFlags = new ulong[types.Length];
        ulong global = 0;
        foreach (BlockHookKind kind in _kinds)
        {
            List<HookEntry> sorted = _entries
                .Where(e => ReferenceEquals(e.Hook, kind) && !e.Revoked)
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.Module.LoadOrder)
                .ThenBy(e => e.Module.Module.Id, StringComparer.Ordinal)
                .ThenBy(e => e.Sequence)
                .ToList();
            if (kind.Build(sorted, types, typeFlags))
            {
                global |= kind.Bit;
            }
        }

        int[] typeOfState = _registry.TypeOfState;
        var flags = new ulong[typeOfState.Length];
        for (int state = 0; state < flags.Length; state++)
        {
            flags[state] = typeFlags[typeOfState[state]];
        }

        _stateFlags = flags;
        _globalFlags = global;
    }

    private BlockBatchHook AddBatch(string name, bool before, bool usesOldState)
    {
        var hook = new BlockBatchHook(name, _kinds.Count, before, usesOldState);
        _kinds.Add(hook);
        _names.Add(name);
        return hook;
    }

    private BlockHook<TArgs> AddUnit<TArgs>(string name, string moduleId, bool consumable)
        where TArgs : struct
    {
        if (_kinds.Count >= MaxKinds)
        {
            throw new InvalidOperationException($"The context already has {MaxKinds} kinds of block hooks, the limit.");
        }

        var hook = new BlockHook<TArgs>(name, moduleId, _kinds.Count, consumable);
        _kinds.Add(hook);
        _names.Add(name);
        return hook;
    }

    private BlockHookHandle Add(HookEntry entry)
    {
        _entries.Add(entry);
        QueueRebuild();
        return new BlockHookHandle(this, entry);
    }

    // Before nBlocks starts, the first build takes every subscriber; after,
    // the tables are rebuilt at the start of the next frame.
    private void QueueRebuild()
    {
        if (!IsStarted || _rebuildQueued || _owner is null)
        {
            return;
        }

        _rebuildQueued = true;
        _owner.Post(_ => Rebuild());
    }

    private ModuleContext Check(ModuleContext module, BlockHookKind hook)
    {
        ArgumentNullException.ThrowIfNull(module);
        CheckKind(hook);
        string owner = hook.ModuleId;
        if (owner != BlocksModule.ModuleId && owner != module.Module.Id && !module.Module.Dependencies.Any(d => d.ModuleId == owner))
        {
            throw new ArgumentException($"Module '{module.Module.Id}' subscribes to the block hook '{hook.Name}' of module '{owner}', which is not one of its dependencies.", nameof(module));
        }

        return module;
    }

    private void CheckType(BlockType type)
    {
        BlockType[] types = _registry.TypeArray;
        if ((uint)type.Index >= (uint)types.Length || !ReferenceEquals(types[type.Index], type))
        {
            throw new ArgumentException($"The type '{type.ObjectId}' belongs to another context.", nameof(type));
        }
    }

    private void CheckTag(BlockTag tag)
    {
        if (!_registry.TryGetTag(tag.Id, out BlockTag? found) || !ReferenceEquals(found, tag))
        {
            throw new ArgumentException($"The tag '{tag.Id}' belongs to another context.", nameof(tag));
        }
    }

    [HotPath]
    private void CheckKind(BlockHookKind hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if ((uint)hook.Id >= (uint)_kinds.Count || !ReferenceEquals(_kinds[hook.Id], hook))
        {
            throw new ArgumentException($"The block hook '{hook.Name}' belongs to another context.", nameof(hook));
        }
    }

    [HotPath]
    private void RequireStarted()
    {
        if (!IsStarted)
        {
            throw new InvalidOperationException("Block hooks are fired once nobara.blocks has started.");
        }
    }
}

/// <summary>A subscriber of a hook, for one type, one tag, or every type.</summary>
internal abstract class HookEntry
{
    protected HookEntry(ModuleContext module, BlockHookKind hook, int priority, long sequence, BlockType? type, BlockTag? tag)
    {
        Module = module;
        Hook = hook;
        Priority = priority;
        Sequence = sequence;
        Type = type;
        Tag = tag;
    }

    public ModuleContext Module { get; }

    public BlockHookKind Hook { get; }

    public int Priority { get; }

    public long Sequence { get; }

    public BlockType? Type { get; }

    public BlockTag? Tag { get; }

    public bool IsGlobal
    {
        [HotPath]
        get => Type is null && Tag is null;
    }

    public bool Revoked { get; set; }

    /// <summary>Gets whether the subscriber runs: not removed, and its module active.</summary>
    public bool IsActive
    {
        [HotPath]
        get => !Revoked && Module.IsModuleActive(Module.Module.Id);
    }
}

/// <summary>A subscriber of a batch hook, with the list of its blocks in the batch being fired.</summary>
internal sealed class BatchEntry(ModuleContext module, BlockHookKind hook, int priority, long sequence, BlockType? type, BlockTag? tag, IBlockBatchHandler handler)
    : HookEntry(module, hook, priority, sequence, type, tag)
{
    public IBlockBatchHandler Handler { get; } = handler ?? throw new ArgumentNullException(nameof(handler));

    public List<int> Bucket { get; } = [];
}

/// <summary>A subscriber of a unit hook.</summary>
internal sealed class UnitEntry<TArgs>(ModuleContext module, BlockHookKind hook, int priority, long sequence, BlockType? type, BlockTag? tag, IBlockHandler<TArgs> handler)
    : HookEntry(module, hook, priority, sequence, type, tag)
    where TArgs : struct
{
    public IBlockHandler<TArgs> Handler { get; } = handler ?? throw new ArgumentNullException(nameof(handler));
}
