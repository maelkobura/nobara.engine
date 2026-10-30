// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;
using Nobara.Game;

namespace Nobara.Items;

/// <summary>The usage service of one context.</summary>
internal sealed class ItemUsage : IItemUsage
{
    private readonly ItemRegistry _registry;
    private readonly List<InteractionKind> _kinds = [];
    private readonly Dictionary<string, InteractionKind> _kindsByName = new(StringComparer.Ordinal);
    private readonly List<HandlerEntry> _entries = [];
    private readonly Dictionary<ulong, ActiveUse> _active = [];
    private readonly List<ulong> _order = [];
    private readonly List<(ulong Entity, long Id)> _pass = [];
    private HandlerEntry[][] _global = [];
    private long _sequence;
    private long _nextUseId;

    public ItemUsage(ItemRegistry registry)
    {
        _registry = registry;
        Use = AddKind("Use", ItemsModule.ModuleId);
        UseOnBlock = AddKind("UseOnBlock", ItemsModule.ModuleId);
        UseOnEntity = AddKind("UseOnEntity", ItemsModule.ModuleId);
        Attack = AddKind("Attack", ItemsModule.ModuleId);
    }

    public InteractionKind Use { get; }

    public InteractionKind UseOnBlock { get; }

    public InteractionKind UseOnEntity { get; }

    public InteractionKind Attack { get; }

    public IReadOnlyList<InteractionKind> Kinds => _kinds;

    /// <summary>Gets or sets the game time of the context, which times the prolonged uses.</summary>
    public IGameTime? Time { get; set; }

    /// <summary>Gets the number of prolonged uses in progress.</summary>
    public int ActiveCount => _active.Count;

    public InteractionKind DeclareInteraction(ModuleContext module, string name)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        if (_registry.IsFrozen)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' declares the interaction kind '{name}' after the item registry froze; declare kinds while modules initialize.");
        }

        QualifiedName.Check(module.Module.Id, name, "interaction kind");
        if (_kindsByName.ContainsKey(name))
        {
            throw new ArgumentException($"The interaction kind '{name}' is already declared.", nameof(name));
        }

        return AddKind(name, module.Module.Id);
    }

    public bool TryGetInteraction(string name, [NotNullWhen(true)] out InteractionKind? kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _kindsByName.TryGetValue(name, out kind);
    }

    public ItemHandlerHandle AddHandler<T>(ModuleContext module, ItemComponentType<T> component, InteractionKind kind, IItemHandler<T> handler, int priority = 0)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_registry.Owns(component))
        {
            throw new ArgumentException($"The component type '{component.Name}' belongs to another context.", nameof(component));
        }

        CheckKind(kind);
        string moduleId = module.Module.Id;
        if (moduleId != component.ModuleId && !module.Module.Dependencies.Any(d => d.ModuleId == component.ModuleId))
        {
            throw new ArgumentException(
                $"Module '{moduleId}' attaches a handler to '{component.Name}', a component of module '{component.ModuleId}', which is not one of its dependencies.",
                nameof(module));
        }

        var entry = new HandlerEntry<T>(module, component, kind, handler, priority, ++_sequence);
        _entries.Add(entry);
        if (_registry.IsFrozen)
        {
            Rebuild();
        }

        return new ItemHandlerHandle(this, entry);
    }

    [HotPath]
    public UseOutcome UseItem(ItemActor actor, ItemStackView stack, InteractionKind kind, UseTarget target)
    {
        if (!_registry.IsFrozen)
        {
            throw new InvalidOperationException("Items are used once nobara.items has started.");
        }

        ItemType item = stack.Item ?? throw new ArgumentException("The stack has no item.", nameof(stack));
        CheckKind(kind);
        bool own = stack.HasOwnComponents;
        ItemComponents components = stack.Components;
        HandlerEntry[] entries = own ? _global[kind.Id] : item.Handlers[kind.Id];
        var call = new UseCall(actor, stack, kind, target, 0, 0);
        for (int i = 0; i < entries.Length; i++)
        {
            HandlerEntry entry = entries[i];
            if ((own && !components.Contains(entry.Component)) || !entry.IsActive)
            {
                continue;
            }

            UseResult result;
            try
            {
                result = entry.Use(in call);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                entry.Module.ReportFault(exception);
                continue;
            }

            switch (result.Kind)
            {
                case UseResultKind.Pass:
                    continue;
                case UseResultKind.Handled:
                    return UseOutcome.Handled;
                default:
                    Start(entry, in call, result);
                    return UseOutcome.Started;
            }
        }

        return UseOutcome.NotHandled;
    }

    [HotPath]
    public bool Interrupt(ulong entity) => Stop(entity, UseInterruption.Interrupted);

    [HotPath]
    public bool TryGetProlongedUse(ulong entity, out ProlongedUse use)
    {
        if (!_active.TryGetValue(entity, out ActiveUse active))
        {
            use = default;
            return false;
        }

        use = new ProlongedUse(active.Actor, active.Stack, active.Kind, active.Target, active.Entry.Component, Elapsed(in active), active.Duration);
        return true;
    }

    /// <summary>Times the prolonged uses: one tick of the simulation.</summary>
    [HotPath]
    public void Advance()
    {
        if (_active.Count == 0)
        {
            return;
        }

        IGameTime time = Time!;
        _pass.Clear();
        foreach (ulong entity in _order)
        {
            _pass.Add((entity, _active[entity].Id));
        }

        foreach ((ulong entity, long id) in _pass)
        {
            if (!_active.TryGetValue(entity, out ActiveUse use) || use.Id != id)
            {
                continue;
            }

            DimensionId dimension = use.Actor.Dimension;
            if (!time.HasDimension(dimension))
            {
                Stop(entity, UseInterruption.DimensionRemoved);
                continue;
            }

            if (time.IsFrozen(dimension))
            {
                continue;
            }

            ulong elapsed = time.GetClock(dimension) - use.Start;
            var call = new UseCall(use.Actor, use.Stack, use.Kind, use.Target, elapsed, use.Duration);
            if (elapsed >= (ulong)use.Duration)
            {
                _active.Remove(entity);
                _order.Remove(entity);
                if (use.Entry.IsActive)
                {
                    try
                    {
                        use.Entry.Finish(in call);
                    }
                    catch (Exception exception) when (exception is not EngineStopException)
                    {
                        use.Entry.Module.ReportFault(exception);
                    }
                }
            }
            else if (use.EveryTick && use.Entry.IsActive)
            {
                try
                {
                    use.Entry.Tick(in call);
                }
                catch (Exception exception) when (exception is not EngineStopException)
                {
                    use.Entry.Module.ReportFault(exception);
                }
            }
        }
    }

    /// <summary>Removes a handler and interrupts its prolonged uses.</summary>
    public void Revoke(HandlerEntry entry)
    {
        if (entry.Revoked)
        {
            return;
        }

        entry.Revoked = true;
        _entries.Remove(entry);
        if (_registry.IsFrozen)
        {
            Rebuild();
        }

        foreach (ulong entity in _order.ToArray())
        {
            if (_active.TryGetValue(entity, out ActiveUse use) && use.Entry == entry)
            {
                Stop(entity, UseInterruption.HandlerRemoved);
            }
        }
    }

    /// <summary>Computes the handler tables: by kind, then by item for its default components.</summary>
    public void Rebuild()
    {
        List<HandlerEntry> sorted = _entries
            .Where(e => !e.Revoked)
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Module.LoadOrder)
            .ThenBy(e => e.Component.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Sequence)
            .ToList();
        var global = new HandlerEntry[_kinds.Count][];
        for (int kind = 0; kind < global.Length; kind++)
        {
            global[kind] = sorted.Where(e => e.Kind.Id == kind).ToArray();
        }

        _global = global;
        foreach (ItemType item in _registry.Items)
        {
            var handlers = new HandlerEntry[global.Length][];
            for (int kind = 0; kind < handlers.Length; kind++)
            {
                HandlerEntry[] matching = global[kind].Where(e => item.Components.Contains(e.Component)).ToArray();
                handlers[kind] = matching.Length == 0 ? [] : matching;
            }

            item.Handlers = handlers;
        }
    }

    private InteractionKind AddKind(string name, string moduleId)
    {
        var kind = new InteractionKind(name, moduleId, _kinds.Count);
        _kinds.Add(kind);
        _kindsByName[name] = kind;
        return kind;
    }

    [HotPath]
    private void CheckKind(InteractionKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if ((uint)kind.Id >= (uint)_kinds.Count || !ReferenceEquals(_kinds[kind.Id], kind))
        {
            throw new ArgumentException($"The interaction kind '{kind.Name}' belongs to another context.", nameof(kind));
        }
    }

    [HotPath]
    private void Start(HandlerEntry entry, in UseCall call, UseResult result)
    {
        IGameTime time = Time!;
        DimensionId dimension = call.Actor.Dimension;
        if (!time.HasDimension(dimension))
        {
            throw new ArgumentException($"A prolonged use of '{call.Stack.Item.ObjectId}' starts for entity {call.Actor.Entity} in {dimension}, which has no clock.", nameof(call));
        }

        ulong entity = call.Actor.Entity;
        if (_active.ContainsKey(entity))
        {
            Stop(entity, UseInterruption.Replaced);
        }

        _active[entity] = new ActiveUse(++_nextUseId, call.Actor, call.Stack, call.Kind, call.Target, entry, time.GetClock(dimension), result.Duration, result.EveryTick);
        _order.Add(entity);
    }

    [HotPath]
    private bool Stop(ulong entity, UseInterruption reason)
    {
        if (!_active.Remove(entity, out ActiveUse use))
        {
            return false;
        }

        _order.Remove(entity);
        if (use.Entry.Module.IsModuleActive(use.Entry.Module.Module.Id))
        {
            var call = new UseCall(use.Actor, use.Stack, use.Kind, use.Target, Elapsed(in use), use.Duration);
            try
            {
                use.Entry.Interrupt(in call, reason);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                use.Entry.Module.ReportFault(exception);
            }
        }

        return true;
    }

    [HotPath]
    private ulong Elapsed(in ActiveUse use)
    {
        IGameTime time = Time!;
        DimensionId dimension = use.Actor.Dimension;
        return time.HasDimension(dimension) ? time.GetClock(dimension) - use.Start : 0;
    }

    /// <summary>A prolonged use in progress.</summary>
    [HotPath]
    private readonly record struct ActiveUse(
        long Id,
        ItemActor Actor,
        ItemStackView Stack,
        InteractionKind Kind,
        UseTarget Target,
        HandlerEntry Entry,
        ulong Start,
        long Duration,
        bool EveryTick);
}

/// <summary>The arguments of a call to a handler.</summary>
internal readonly struct UseCall
{
    [HotPath]
    public UseCall(ItemActor actor, ItemStackView stack, InteractionKind kind, UseTarget target, ulong elapsed, long duration)
    {
        Actor = actor;
        Stack = stack;
        Kind = kind;
        Target = target;
        Elapsed = elapsed;
        Duration = duration;
    }

    public ItemActor Actor { get; }

    public ItemStackView Stack { get; }

    public InteractionKind Kind { get; }

    public UseTarget Target { get; }

    public ulong Elapsed { get; }

    public long Duration { get; }
}

/// <summary>A handler attached to a component type and a kind.</summary>
internal abstract class HandlerEntry
{
    protected HandlerEntry(ModuleContext module, ItemComponentType component, InteractionKind kind, int priority, long sequence)
    {
        Module = module;
        Component = component;
        Kind = kind;
        Priority = priority;
        Sequence = sequence;
    }

    public ModuleContext Module { get; }

    public ItemComponentType Component { get; }

    public InteractionKind Kind { get; }

    public int Priority { get; }

    public long Sequence { get; }

    public bool Revoked { get; set; }

    /// <summary>Gets whether the handler runs: not removed, and its module active.</summary>
    public bool IsActive
    {
        [HotPath]
        get => !Revoked && Module.IsModuleActive(Module.Module.Id);
    }

    [HotPath]
    public abstract UseResult Use(in UseCall call);

    [HotPath]
    public abstract void Tick(in UseCall call);

    [HotPath]
    public abstract void Finish(in UseCall call);

    [HotPath]
    public abstract void Interrupt(in UseCall call, UseInterruption reason);
}

/// <summary>A handler of a component type whose values are of type <typeparamref name="T"/>.</summary>
internal sealed class HandlerEntry<T> : HandlerEntry
    where T : notnull
{
    private readonly ItemComponentType<T> _component;
    private readonly IItemHandler<T> _handler;

    public HandlerEntry(ModuleContext module, ItemComponentType<T> component, InteractionKind kind, IItemHandler<T> handler, int priority, long sequence)
        : base(module, component, kind, priority, sequence)
    {
        _component = component;
        _handler = handler;
    }

    [HotPath]
    public override UseResult Use(in UseCall call) =>
        call.Stack.Components.TryGet(_component, out T? value) ? _handler.Use(Describe(in call, value)) : UseResult.Pass;

    [HotPath]
    public override void Tick(in UseCall call)
    {
        if (call.Stack.Components.TryGet(_component, out T? value))
        {
            _handler.UseTick(Describe(in call, value));
        }
    }

    [HotPath]
    public override void Finish(in UseCall call)
    {
        if (call.Stack.Components.TryGet(_component, out T? value))
        {
            _handler.UseFinished(Describe(in call, value));
        }
    }

    [HotPath]
    public override void Interrupt(in UseCall call, UseInterruption reason)
    {
        if (call.Stack.Components.TryGet(_component, out T? value))
        {
            _handler.UseInterrupted(Describe(in call, value), reason);
        }
    }

    [HotPath]
    private static ItemUse<T> Describe(in UseCall call, T value) =>
        new(call.Actor, call.Stack, call.Kind, call.Target, value, call.Elapsed, call.Duration);
}
