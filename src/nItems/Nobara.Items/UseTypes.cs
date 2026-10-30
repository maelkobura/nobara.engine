// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game;

namespace Nobara.Items;

/// <summary>
/// A kind of interaction with an item, such as using it on a block or
/// attacking with it. Handlers are registered for a component type and a kind.
/// </summary>
/// <remarks>
/// The four standard kinds are <see cref="IItemUsage.Use"/>,
/// <see cref="IItemUsage.UseOnBlock"/>, <see cref="IItemUsage.UseOnEntity"/>,
/// and <see cref="IItemUsage.Attack"/>; a module declares more with
/// <see cref="IItemUsage.DeclareInteraction"/>. Which input triggers which
/// kind is the business of the game, not of nItems.
/// </remarks>
public sealed class InteractionKind
{
    internal InteractionKind(string name, string moduleId, int id)
    {
        Name = name;
        ModuleId = moduleId;
        Id = id;
    }

    /// <summary>Gets the name: <c>Use</c> for a standard kind, a qualified name such as <c>example.bows.reload</c> for a declared one.</summary>
    public string Name { get; }

    /// <summary>Gets the module that declared the kind: <c>nobara.items</c> for a standard kind.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the number of the kind in its context, in order of declaration.</summary>
    internal int Id { get; }

    /// <summary>Returns the name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// Who uses an item, in a neutral form: the identifier of an entity and the
/// dimension it is in. nItems knows neither entities nor worlds.
/// </summary>
/// <remarks>An actor has at most one prolonged use at a time; it is identified by <see cref="Entity"/>.</remarks>
public readonly struct ItemActor : IEquatable<ItemActor>
{
    /// <summary>Initializes an actor.</summary>
    /// <param name="entity">The identifier of the entity that uses the item, such as a player or a mob.</param>
    /// <param name="dimension">The dimension the entity is in; its clock times the prolonged uses.</param>
    [HotPath]
    public ItemActor(ulong entity, DimensionId dimension)
    {
        Entity = entity;
        Dimension = dimension;
    }

    /// <summary>Gets the identifier of the entity.</summary>
    public ulong Entity { get; }

    /// <summary>Gets the dimension of the entity.</summary>
    public DimensionId Dimension { get; }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(ItemActor other) => Entity == other.Entity && Dimension == other.Dimension;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ItemActor other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => HashCode.Combine(Entity, Dimension.Value);

    /// <summary>Returns the actor as logs show it.</summary>
    /// <returns>For example <c>entity 42 in dimension 0</c>.</returns>
    public override string ToString() => $"entity {Entity} in {Dimension}";
}

/// <summary>What an item is used on.</summary>
public enum UseTargetKind
{
    /// <summary>Nothing, such as a use in the air.</summary>
    None,

    /// <summary>A block of a grid.</summary>
    Block,

    /// <summary>An entity.</summary>
    Entity,
}

/// <summary>A face of a block.</summary>
public enum BlockFace : byte
{
    /// <summary>No face.</summary>
    None,

    /// <summary>The bottom face, toward negative Y.</summary>
    Down,

    /// <summary>The top face, toward positive Y.</summary>
    Up,

    /// <summary>The face toward negative Z.</summary>
    North,

    /// <summary>The face toward positive Z.</summary>
    South,

    /// <summary>The face toward negative X.</summary>
    West,

    /// <summary>The face toward positive X.</summary>
    East,
}

/// <summary>
/// What an item is used on, in a neutral form: nothing, a block given by its
/// grid, its position, and the face that was hit, or an entity given by its
/// identifier. nItems depends on no world module.
/// </summary>
public readonly struct UseTarget
{
    [HotPath]
    private UseTarget(UseTargetKind kind, ulong grid, int x, int y, int z, BlockFace face, ulong entity)
    {
        Kind = kind;
        Grid = grid;
        X = x;
        Y = y;
        Z = z;
        Face = face;
        Entity = entity;
    }

    /// <summary>Gets the target of a use on nothing.</summary>
    public static UseTarget None
    {
        [HotPath]
        get => default;
    }

    /// <summary>Gets what the target is.</summary>
    public UseTargetKind Kind { get; }

    /// <summary>Gets the identifier of the entity of the grid, for a block.</summary>
    public ulong Grid { get; }

    /// <summary>Gets the X position of the block in its grid.</summary>
    public int X { get; }

    /// <summary>Gets the Y position of the block in its grid.</summary>
    public int Y { get; }

    /// <summary>Gets the Z position of the block in its grid.</summary>
    public int Z { get; }

    /// <summary>Gets the face of the block that was hit.</summary>
    public BlockFace Face { get; }

    /// <summary>Gets the identifier of the entity, for an entity.</summary>
    public ulong Entity { get; }

    /// <summary>Creates the target of a use on a block.</summary>
    /// <param name="grid">The identifier of the entity of the grid.</param>
    /// <param name="x">The X position of the block in the grid.</param>
    /// <param name="y">The Y position.</param>
    /// <param name="z">The Z position.</param>
    /// <param name="face">The face that was hit.</param>
    /// <returns>The target.</returns>
    [HotPath]
    public static UseTarget OnBlock(ulong grid, int x, int y, int z, BlockFace face) => new(UseTargetKind.Block, grid, x, y, z, face, 0);

    /// <summary>Creates the target of a use on an entity.</summary>
    /// <param name="entity">The identifier of the entity.</param>
    /// <returns>The target.</returns>
    [HotPath]
    public static UseTarget OnEntity(ulong entity) => new(UseTargetKind.Entity, 0, 0, 0, 0, BlockFace.None, entity);
}

/// <summary>
/// The item that an actor uses, as the usage service needs it: the item type
/// and the components of the stack. The type of stacks belongs to the module
/// that stores items; this view is what it hands to the service.
/// </summary>
public readonly struct ItemStackView
{
    private readonly ItemComponents? _components;

    /// <summary>Initializes a view.</summary>
    /// <param name="item">The type of the item.</param>
    /// <param name="components">The components of the stack, or <see langword="null"/> when they are the defaults of the item.</param>
    [HotPath]
    public ItemStackView(ItemType item, ItemComponents? components = null)
    {
        Item = item;
        _components = components;
    }

    /// <summary>Gets the type of the item.</summary>
    public ItemType Item { get; }

    /// <summary>Gets the components of the stack: its own, or the defaults of the item.</summary>
    public ItemComponents Components
    {
        [HotPath]
        get => _components ?? Item.Components;
    }

    /// <summary>
    /// Gets whether the stack has components other than the defaults of its
    /// item. The service then resolves the handlers from its components, a
    /// slower path than the tables of the item.
    /// </summary>
    public bool HasOwnComponents
    {
        [HotPath]
        get => _components is not null && !ReferenceEquals(_components, Item.Components);
    }

    /// <summary>Returns the item.</summary>
    /// <returns>The object identifier of the item.</returns>
    public override string ToString() => Item?.ObjectId ?? "no item";
}

/// <summary>A use of an item, as a handler receives it.</summary>
/// <typeparam name="T">The type of the values of the component of the handler.</typeparam>
public readonly struct ItemUse<T>
    where T : notnull
{
    /// <summary>Initializes a use.</summary>
    /// <param name="actor">Who uses the item.</param>
    /// <param name="stack">The item and the components of its stack.</param>
    /// <param name="kind">The kind of interaction.</param>
    /// <param name="target">What the item is used on.</param>
    /// <param name="value">The value of the component of the handler for this stack.</param>
    /// <param name="elapsed">The ticks elapsed since a prolonged use started; 0 otherwise.</param>
    /// <param name="duration">The duration of a prolonged use, in ticks; 0 otherwise.</param>
    [HotPath]
    public ItemUse(ItemActor actor, ItemStackView stack, InteractionKind kind, UseTarget target, T value, ulong elapsed, long duration)
    {
        Actor = actor;
        Stack = stack;
        Kind = kind;
        Target = target;
        Value = value;
        Elapsed = elapsed;
        Duration = duration;
    }

    /// <summary>Gets who uses the item.</summary>
    public ItemActor Actor { get; }

    /// <summary>Gets the item and the components of its stack.</summary>
    public ItemStackView Stack { get; }

    /// <summary>Gets the kind of interaction.</summary>
    public InteractionKind Kind { get; }

    /// <summary>Gets what the item is used on.</summary>
    public UseTarget Target { get; }

    /// <summary>Gets the value of the component of the handler for this stack.</summary>
    public T Value { get; }

    /// <summary>Gets the ticks of the dimension clock of the actor elapsed since the prolonged use started; 0 for an immediate use.</summary>
    public ulong Elapsed { get; }

    /// <summary>Gets the duration of the prolonged use, in ticks; 0 for an immediate use.</summary>
    public long Duration { get; }
}

/// <summary>What a handler answers.</summary>
public enum UseResultKind
{
    /// <summary>Not handled: the next handler runs.</summary>
    Pass,

    /// <summary>Handled: no other handler runs.</summary>
    Handled,

    /// <summary>Handled, as a prolonged use that the service times: no other handler runs.</summary>
    Prolonged,
}

/// <summary>The answer of a handler to a use.</summary>
public readonly struct UseResult
{
    [HotPath]
    private UseResult(UseResultKind kind, long duration, bool everyTick)
    {
        Kind = kind;
        Duration = duration;
        EveryTick = everyTick;
    }

    /// <summary>Gets the answer that lets the next handler run.</summary>
    public static UseResult Pass
    {
        [HotPath]
        get => default;
    }

    /// <summary>Gets the answer that ends the use: no other handler runs.</summary>
    public static UseResult Handled
    {
        [HotPath]
        get => new(UseResultKind.Handled, 0, false);
    }

    /// <summary>Gets what the handler answered.</summary>
    public UseResultKind Kind { get; }

    /// <summary>Gets the duration of a prolonged use, in ticks of the dimension clock of the actor.</summary>
    public long Duration { get; }

    /// <summary>Gets whether the handler is called at each tick of a prolonged use.</summary>
    public bool EveryTick { get; }

    /// <summary>
    /// Starts a prolonged use, such as eating or drawing a bow: the service
    /// calls <see cref="IItemHandler{T}.UseFinished"/> once the duration has
    /// elapsed on the dimension clock of the actor, or
    /// <see cref="IItemHandler{T}.UseInterrupted"/> before.
    /// </summary>
    /// <param name="duration">The duration in ticks, at least 1, such as a value read with <see cref="ComponentValues.GetTicks(string)"/>.</param>
    /// <param name="everyTick">Whether <see cref="IItemHandler{T}.UseTick"/> runs at each tick during the use.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is less than 1.</exception>
    [HotPath]
    public static UseResult Prolong(long duration, bool everyTick = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, 1L);
        return new UseResult(UseResultKind.Prolonged, duration, everyTick);
    }
}

/// <summary>What the usage service did with a use.</summary>
public enum UseOutcome
{
    /// <summary>No handler handled it: the item does nothing for this kind.</summary>
    NotHandled,

    /// <summary>A handler handled it at once.</summary>
    Handled,

    /// <summary>A handler started a prolonged use, which the service now times.</summary>
    Started,
}

/// <summary>Why a prolonged use ended before its duration.</summary>
public enum UseInterruption
{
    /// <summary>The game interrupted it: the actor released the input, changed item, or was stopped.</summary>
    Interrupted,

    /// <summary>The actor started another prolonged use.</summary>
    Replaced,

    /// <summary>The dimension of the actor lost its clock.</summary>
    DimensionRemoved,

    /// <summary>The handler was removed.</summary>
    HandlerRemoved,
}

/// <summary>
/// The code that a module runs when an item that has a component is used in
/// a kind of interaction. It is registered with
/// <see cref="IItemUsage.AddHandler{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the values of the component.</typeparam>
/// <remarks>
/// Every method is hot path code, checked by the engine analyzers in each
/// implementation: uses happen during the tick. The service never changes the
/// stack: a handler that consumes the item does it through the module that
/// stores it.
/// </remarks>
public interface IItemHandler<T>
    where T : notnull
{
    /// <summary>Answers a use.</summary>
    /// <param name="use">The use.</param>
    /// <returns><see cref="UseResult.Pass"/>, <see cref="UseResult.Handled"/>, or <see cref="UseResult.Prolong"/>.</returns>
    [HotPath]
    UseResult Use(in ItemUse<T> use);

    /// <summary>Runs at each tick of a prolonged use that the handler started with <c>everyTick</c>, before it ends.</summary>
    /// <param name="use">The use, with the elapsed ticks.</param>
    [HotPath]
    void UseTick(in ItemUse<T> use)
    {
    }

    /// <summary>Runs once the duration of a prolonged use that the handler started has elapsed.</summary>
    /// <param name="use">The use, with the elapsed ticks.</param>
    [HotPath]
    void UseFinished(in ItemUse<T> use)
    {
    }

    /// <summary>Runs when a prolonged use that the handler started ends before its duration.</summary>
    /// <param name="use">The use, with the elapsed ticks.</param>
    /// <param name="reason">Why it ended.</param>
    [HotPath]
    void UseInterrupted(in ItemUse<T> use, UseInterruption reason)
    {
    }
}

/// <summary>A registered handler. Disposing it removes the handler.</summary>
public sealed class ItemHandlerHandle : IDisposable
{
    private readonly ItemUsage _usage;

    internal ItemHandlerHandle(ItemUsage usage, HandlerEntry entry)
    {
        _usage = usage;
        Entry = entry;
    }

    /// <summary>Gets the component type of the handler.</summary>
    public ItemComponentType Component => Entry.Component;

    /// <summary>Gets the kind of interaction of the handler.</summary>
    public InteractionKind Kind => Entry.Kind;

    /// <summary>Gets the priority of the handler.</summary>
    public int Priority => Entry.Priority;

    /// <summary>Gets whether the handle was disposed.</summary>
    public bool IsRevoked => Entry.Revoked;

    internal HandlerEntry Entry { get; }

    /// <summary>
    /// Removes the handler from the next use on. Its prolonged uses in
    /// progress are interrupted with <see cref="UseInterruption.HandlerRemoved"/>.
    /// </summary>
    public void Dispose() => _usage.Revoke(Entry);
}

/// <summary>A prolonged use in progress.</summary>
/// <param name="Actor">Who uses the item.</param>
/// <param name="Stack">The item and the components of its stack.</param>
/// <param name="Kind">The kind of interaction.</param>
/// <param name="Target">What the item is used on.</param>
/// <param name="Component">The component type of the handler that started the use.</param>
/// <param name="Elapsed">The ticks of the dimension clock of the actor elapsed since the use started.</param>
/// <param name="Duration">The duration, in ticks.</param>
[HotPath]
public readonly record struct ProlongedUse(ItemActor Actor, ItemStackView Stack, InteractionKind Kind, UseTarget Target, ItemComponentType Component, ulong Elapsed, long Duration);
