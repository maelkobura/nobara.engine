// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Game;

/// <summary>
/// The service that orders the simulation tick: its stages, and the tick
/// systems that run in them. Resolve it with
/// <c>context.Resolve&lt;ITickSchedule&gt;()</c>; there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// Modules add their stages and systems while they initialize. nGame
/// resolves the order when it starts, after every module has initialized,
/// and the order stays fixed from then on: adding a stage or a system later
/// throws. Disposing the handle of a system removes it from the next tick on.
/// </para>
/// <para>Call the service on the loop thread of the context.</para>
/// </remarks>
public interface ITickSchedule
{
    /// <summary>Gets whether the order is resolved, and so fixed.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// Inserts a stage in the tick. With <paramref name="after"/>, the stage
    /// runs after that stage and the stages already inserted after it, before
    /// any other stage; with only <paramref name="before"/>, right before that
    /// stage. Stages are inserted in the load order of their modules, then by
    /// name.
    /// </summary>
    /// <param name="module">The module that inserts the stage.</param>
    /// <param name="name">The qualified name of the stage: the identifier of the module, a dot, and a name, such as <c>example.farm.harvest</c>.</param>
    /// <param name="after">The stage that runs before this one, or <see langword="null"/>.</param>
    /// <param name="before">The stage that runs after this one, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The name is not qualified by the module, is already used by a stage or a system, or neither <paramref name="after"/> nor <paramref name="before"/> is given.</exception>
    /// <exception cref="InvalidOperationException">The order is already resolved.</exception>
    /// <remarks>A stage named by <paramref name="after"/> or <paramref name="before"/> that no module adds makes the start fail.</remarks>
    void AddStage(ModuleContext module, string name, string? after = null, string? before = null);

    /// <summary>Registers a tick system in a stage.</summary>
    /// <param name="module">The module that registers the system; an exception in the system follows its error policy.</param>
    /// <param name="name">The qualified name of the system: the identifier of the module, a dot, and a name, such as <c>example.farm.growth</c>.</param>
    /// <param name="stage">The stage of the system, such as <see cref="TickStages.Entities"/>.</param>
    /// <param name="system">The code to run each tick.</param>
    /// <param name="options">Its constraints, its declared accesses, and its sides; <see langword="null"/> for none.</param>
    /// <returns>The handle; dispose it to remove the system.</returns>
    /// <exception cref="ArgumentException">The name is not qualified by the module, or is already used by a stage or a system.</exception>
    /// <exception cref="InvalidOperationException">The order is already resolved.</exception>
    /// <remarks>A stage that no module adds makes the start fail.</remarks>
    TickSystemHandle AddSystem(ModuleContext module, string name, string stage, ITickSystem system, TickSystemOptions? options = null);

    /// <summary>Gets the resolved order: the stages in the order they run, each with its systems in order.</summary>
    /// <returns>The order, with the state of each system at the time of the call.</returns>
    /// <exception cref="InvalidOperationException">The order is not resolved yet.</exception>
    IReadOnlyList<TickStageInfo> GetOrder();

    /// <summary>Formats the resolved order as text, one line per stage and per system, for logs and debugging.</summary>
    /// <returns>The text.</returns>
    /// <exception cref="InvalidOperationException">The order is not resolved yet.</exception>
    string FormatOrder();
}

/// <summary>
/// Thrown when the order of the tick cannot be resolved, or when the
/// <c>&lt;ModuleConfig module="nobara.game"&gt;</c> block of
/// <c>engine.nobara</c> is not valid. The start of the context fails.
/// </summary>
public sealed class TickScheduleException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, each a sentence; those of <c>engine.nobara</c> start with the position.</param>
    public TickScheduleException(IReadOnlyList<string> problems)
        : base("The order of the simulation tick cannot be resolved:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "- " + p)))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<string> Problems { get; }
}
