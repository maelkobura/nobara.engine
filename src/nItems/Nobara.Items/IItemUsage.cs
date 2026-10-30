// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;

namespace Nobara.Items;

/// <summary>
/// The usage service of items: the kinds of interaction, the handlers that
/// modules attach to item components, and the service that uses an item,
/// immediate or prolonged. Resolve it with
/// <c>context.Resolve&lt;IItemUsage&gt;()</c>; there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// When an item is used, the handlers of its components for that kind run one
/// after the other, by decreasing priority; the first one that does not answer
/// <see cref="UseResult.Pass"/> stops the others.
/// </para>
/// <para>
/// Use is authoritative on the server: the controller of the player (in stage
/// <c>Intents</c> of nGame) or an artificial intelligence calls
/// <see cref="UseItem"/>. Call the service on the loop thread of the context.
/// </para>
/// </remarks>
public interface IItemUsage
{
    /// <summary>Gets the standard kind <c>Use</c>: a use in the air, without target.</summary>
    InteractionKind Use { get; }

    /// <summary>Gets the standard kind <c>UseOnBlock</c>: a use on a block, given by its grid, position, and face.</summary>
    InteractionKind UseOnBlock { get; }

    /// <summary>Gets the standard kind <c>UseOnEntity</c>: a use on an entity.</summary>
    InteractionKind UseOnEntity { get; }

    /// <summary>Gets the standard kind <c>Attack</c>: an attack, on nothing, a block, or an entity.</summary>
    InteractionKind Attack { get; }

    /// <summary>Gets every kind, the standard ones first, then the declared ones in order of declaration.</summary>
    IReadOnlyList<InteractionKind> Kinds { get; }

    /// <summary>Declares a kind of interaction, such as throwing or reloading.</summary>
    /// <param name="module">The module that declares the kind.</param>
    /// <param name="name">The qualified name: the identifier of the module, a dot, and a lowercase name, such as <c>example.bows.reload</c>.</param>
    /// <returns>The kind.</returns>
    /// <exception cref="ArgumentException">The name is not qualified by the module, or is already declared.</exception>
    /// <exception cref="InvalidOperationException">The registry is frozen: kinds are declared while modules initialize.</exception>
    InteractionKind DeclareInteraction(ModuleContext module, string name);

    /// <summary>Finds a kind by name.</summary>
    /// <param name="name">The name: <c>UseOnBlock</c>, or a qualified name.</param>
    /// <param name="kind">The kind, or <see langword="null"/> when none has that name.</param>
    /// <returns><see langword="true"/> when the kind exists.</returns>
    bool TryGetInteraction(string name, [NotNullWhen(true)] out InteractionKind? kind);

    /// <summary>
    /// Attaches a handler to a component type, for a kind of interaction.
    /// </summary>
    /// <typeparam name="T">The type of the values of the component.</typeparam>
    /// <param name="module">The module of the handler: the module that registered the component type, or a module that depends on it. An exception of the handler follows its error policy.</param>
    /// <param name="component">The component type.</param>
    /// <param name="kind">The kind of interaction.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="priority">
    /// The order among the handlers of the components of a stack: the highest
    /// runs first. At equal priority, the handlers run in the load order of
    /// their modules, then by the name of their component, then in the order
    /// they were added.
    /// </param>
    /// <returns>The handle; dispose it to remove the handler.</returns>
    /// <exception cref="ArgumentException">The module neither registered the component type nor depends on the module that did, or the kind or the type belongs to another context.</exception>
    /// <remarks>
    /// A handler added once the registry is frozen rebuilds the tables of the
    /// items at once, outside the tick: add handlers while modules initialize
    /// when possible.
    /// </remarks>
    ItemHandlerHandle AddHandler<T>(ModuleContext module, ItemComponentType<T> component, InteractionKind kind, IItemHandler<T> handler, int priority = 0)
        where T : notnull;

    /// <summary>Uses an item.</summary>
    /// <param name="actor">Who uses the item.</param>
    /// <param name="stack">The item and the components of its stack.</param>
    /// <param name="kind">The kind of interaction.</param>
    /// <param name="target">What the item is used on.</param>
    /// <returns>What the handlers did.</returns>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet: items are used once nItems has started.</exception>
    /// <exception cref="ArgumentException">A handler starts a prolonged use, and the dimension of the actor has no clock.</exception>
    /// <remarks>
    /// <para>
    /// A stack with its default components takes the handlers that the
    /// registry computed for its item when it froze; a stack with its own
    /// components goes through every handler of the kind, which is slower.
    /// </para>
    /// <para>
    /// A handler that starts a prolonged use while the actor has one
    /// interrupts the previous one with <see cref="UseInterruption.Replaced"/>.
    /// The method allocates nothing.
    /// </para>
    /// </remarks>
    [HotPath]
    UseOutcome UseItem(ItemActor actor, ItemStackView stack, InteractionKind kind, UseTarget target);

    /// <summary>Interrupts the prolonged use of an actor, such as when it releases the input or changes item.</summary>
    /// <param name="entity">The identifier of the entity of the actor.</param>
    /// <returns><see langword="true"/> when the actor had a prolonged use.</returns>
    [HotPath]
    bool Interrupt(ulong entity);

    /// <summary>Gets the prolonged use of an actor.</summary>
    /// <param name="entity">The identifier of the entity of the actor.</param>
    /// <param name="use">The use, or the default when the actor has none.</param>
    /// <returns><see langword="true"/> when the actor has a prolonged use.</returns>
    [HotPath]
    bool TryGetProlongedUse(ulong entity, out ProlongedUse use);
}
