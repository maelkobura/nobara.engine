// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Blocks;

/// <summary>
/// The block hooks: what happens to blocks, which modules subscribe to by
/// type, by tag, or for every type, and which the modules that act on blocks
/// fire. Resolve it with <c>context.Resolve&lt;IBlockHooks&gt;()</c>; there
/// is one per context.
/// </summary>
/// <remarks>
/// <para>
/// Hooks are distinct from the event bus: some are hot, such as the neighbors
/// of the blocks broken by a large explosion. A subscriber subscribes while
/// its module initializes; nBlocks builds the tables when it starts: one table
/// of subscribers per type and per kind, and one flag per state that tells
/// whether a kind has subscribers for it. A block whose type has no subscriber
/// costs nothing.
/// </para>
/// <para>
/// Subscribers run by decreasing priority; at equal priority, in the load
/// order of their modules, then by the identifier of their module, then in
/// the order they subscribed. Subscribers of every type and of one type share
/// this order. A subscription added or disposed once nBlocks has started
/// applies at the start of the next frame, before the next tick; a disposed
/// subscriber is not called from the moment it is disposed.
/// </para>
/// <para>
/// A subscriber that throws is reported with <see cref="ModuleContext.ReportFault"/>
/// on its module, and the next one runs. Call the service on the loop thread
/// of the context. A subscriber of a batch hook does not fire the same hook:
/// the changes it causes go in a new batch, fired after the current one.
/// </para>
/// </remarks>
public interface IBlockHooks
{
    /// <summary>Gets the hook before an actor places a batch of blocks: subscribers may remove blocks or cancel the batch. Subscribers are chosen by the new state.</summary>
    BlockBatchHook Placing { get; }

    /// <summary>Gets the hook once a batch placed by an actor applied, with the blocks that remained.</summary>
    BlockBatchHook Placed { get; }

    /// <summary>Gets the hook before an actor breaks a batch of blocks: subscribers may remove blocks or cancel the batch. Subscribers are chosen by the old state.</summary>
    BlockBatchHook Breaking { get; }

    /// <summary>Gets the hook once a batch broken by an actor applied, with the blocks that remained.</summary>
    BlockBatchHook Broken { get; }

    /// <summary>Gets the hook when an actor uses a block; a subscriber that answers <see langword="true"/> consumes it.</summary>
    BlockHook<BlockInteraction> Interact { get; }

    /// <summary>Gets the hook when a direct neighbor of a block changed.</summary>
    BlockHook<NeighborChange> NeighborChanged { get; }

    /// <summary>Gets the hook of a random tick of a block.</summary>
    BlockHook<BlockTick> RandomTick { get; }

    /// <summary>Gets the hook of a scheduled tick of a block that falls due.</summary>
    BlockHook<BlockTick> ScheduledTick { get; }

    /// <summary>Gets every kind of hook, standard and declared, in order of declaration.</summary>
    IReadOnlyList<BlockHookKind> Kinds { get; }

    /// <summary>
    /// Declares a kind of unit hook, such as the contact of an entity with a
    /// block. Subscriptions by type and by tag work as for the standard kinds.
    /// </summary>
    /// <typeparam name="TArgs">What the subscribers receive.</typeparam>
    /// <param name="module">The module that declares the kind, and fires it.</param>
    /// <param name="name">The qualified name: the identifier of the module, a dot, and a lowercase name, such as <c>example.physics.contact</c>.</param>
    /// <param name="consumable">Whether a subscriber that answers <see langword="true"/> stops the next ones.</param>
    /// <returns>The kind.</returns>
    /// <exception cref="ArgumentException">The name does not start with the identifier of the module, or is already declared.</exception>
    /// <exception cref="InvalidOperationException">nBlocks has started, or the context already has 64 kinds.</exception>
    BlockHook<TArgs> DeclareHook<TArgs>(ModuleContext module, string name, bool consumable = false)
        where TArgs : struct;

    /// <summary>Subscribes to a batch hook for one type of block.</summary>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="hook">The hook.</param>
    /// <param name="type">The type.</param>
    /// <param name="handler">The subscriber.</param>
    /// <param name="priority">The priority; higher runs first.</param>
    /// <returns>The handle that removes the subscriber.</returns>
    /// <exception cref="ArgumentException">The hook, the type, or the tag belongs to another context, or the hook is declared by a module that <paramref name="module"/> does not depend on.</exception>
    BlockHookHandle Subscribe(ModuleContext module, BlockBatchHook hook, BlockType type, IBlockBatchHandler handler, int priority = 0);

    /// <summary>Subscribes to a batch hook for the types of a tag; the tag is resolved again when it changes.</summary>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="hook">The hook.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="handler">The subscriber.</param>
    /// <param name="priority">The priority; higher runs first.</param>
    /// <returns>The handle that removes the subscriber.</returns>
    /// <exception cref="ArgumentException">The hook, the type, or the tag belongs to another context, or the hook is declared by a module that <paramref name="module"/> does not depend on.</exception>
    BlockHookHandle Subscribe(ModuleContext module, BlockBatchHook hook, BlockTag tag, IBlockBatchHandler handler, int priority = 0);

    /// <summary>
    /// Subscribes to <see cref="Placing"/> or <see cref="Breaking"/> for every
    /// type, such as a protection of areas: the subscriber is called once per
    /// batch, with every block that remains.
    /// </summary>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="hook"><see cref="Placing"/> or <see cref="Breaking"/>.</param>
    /// <param name="handler">The subscriber.</param>
    /// <param name="priority">The priority; higher runs first.</param>
    /// <returns>The handle that removes the subscriber.</returns>
    /// <exception cref="ArgumentException">The hook runs after the batch applied, or belongs to another context.</exception>
    BlockHookHandle SubscribeAll(ModuleContext module, BlockBatchHook hook, IBlockBatchHandler handler, int priority = 0);

    /// <summary>Subscribes to a unit hook for one type of block.</summary>
    /// <typeparam name="TArgs">What the hook gives.</typeparam>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="hook">The hook.</param>
    /// <param name="type">The type.</param>
    /// <param name="handler">The subscriber.</param>
    /// <param name="priority">The priority; higher runs first.</param>
    /// <returns>The handle that removes the subscriber.</returns>
    /// <exception cref="ArgumentException">The hook, the type, or the tag belongs to another context, or the hook is declared by a module that <paramref name="module"/> does not depend on.</exception>
    BlockHookHandle Subscribe<TArgs>(ModuleContext module, BlockHook<TArgs> hook, BlockType type, IBlockHandler<TArgs> handler, int priority = 0)
        where TArgs : struct;

    /// <summary>Subscribes to a unit hook for the types of a tag.</summary>
    /// <typeparam name="TArgs">What the hook gives.</typeparam>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="hook">The hook.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="handler">The subscriber.</param>
    /// <param name="priority">The priority; higher runs first.</param>
    /// <returns>The handle that removes the subscriber.</returns>
    /// <exception cref="ArgumentException">The hook, the type, or the tag belongs to another context, or the hook is declared by a module that <paramref name="module"/> does not depend on.</exception>
    BlockHookHandle Subscribe<TArgs>(ModuleContext module, BlockHook<TArgs> hook, BlockTag tag, IBlockHandler<TArgs> handler, int priority = 0)
        where TArgs : struct;

    /// <summary>Tells whether a hook has subscribers for a state: test it before any work for the block.</summary>
    /// <param name="hook">The hook.</param>
    /// <param name="state">The state.</param>
    /// <returns><see langword="true"/> when a subscriber of the type of the state, or of every type, is in the tables.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not in the registry.</exception>
    /// <exception cref="ArgumentException">The hook belongs to another context.</exception>
    /// <exception cref="InvalidOperationException">nBlocks has not started.</exception>
    [HotPath]
    bool HasSubscribers(BlockHookKind hook, uint state);

    /// <summary>
    /// Fires a batch hook. Before the batch applies (<see cref="Placing"/>,
    /// <see cref="Breaking"/>), call it, then apply the blocks that remain
    /// unless it returns <see langword="false"/>; after, fire
    /// <see cref="Placed"/> or <see cref="Broken"/> with the same batch.
    /// </summary>
    /// <param name="hook">The hook.</param>
    /// <param name="batch">The batch.</param>
    /// <returns><see langword="false"/> when the batch is cancelled.</returns>
    /// <exception cref="ArgumentException">A block of the batch has a state that is not in the registry, or the hook belongs to another context.</exception>
    /// <remarks>
    /// The blocks are grouped by subscriber in one pass, then each subscriber
    /// is called once, with its view. A batch that touches no subscribed type
    /// calls nobody. Firing allocates nothing once the lists of the batch and
    /// of the subscribers have grown.
    /// </remarks>
    /// <exception cref="InvalidOperationException">nBlocks has not started, or a subscriber fires the hook while it is being fired.</exception>
    [HotPath]
    bool Fire(BlockBatchHook hook, BlockBatch batch);

    /// <summary>Fires a unit hook for one block.</summary>
    /// <typeparam name="TArgs">What the hook gives.</typeparam>
    /// <param name="hook">The hook.</param>
    /// <param name="state">The state of the block, which chooses the subscribers.</param>
    /// <param name="args">The event.</param>
    /// <returns><see langword="true"/> when a subscriber of a consumable hook consumed the event.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not in the registry.</exception>
    /// <exception cref="ArgumentException">The hook belongs to another context.</exception>
    /// <exception cref="InvalidOperationException">nBlocks has not started.</exception>
    [HotPath]
    bool Fire<TArgs>(BlockHook<TArgs> hook, uint state, in TArgs args)
        where TArgs : struct;
}
