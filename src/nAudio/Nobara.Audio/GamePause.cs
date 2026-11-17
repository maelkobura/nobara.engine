// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game;

namespace Nobara.Audio;

/// <summary>
/// The only place where nAudio uses nGame, its optional dependency: whether
/// the game is paused, which pauses the sounds of the buses of the world.
/// </summary>
[UsesOptionalDependency(GameModuleId)]
internal sealed class GamePause : IPauseSource
{
    /// <summary>The identifier of nGame.</summary>
    public const string GameModuleId = "nobara.game";

    private readonly IGameTime _time;

    private GamePause(IGameTime time) => _time = time;

    /// <summary>Gets whether the game is paused.</summary>
    public bool Paused => _time.Paused;

    /// <summary>Reads the pause of nGame. Call it only when nGame is active.</summary>
    /// <returns>The source, or <see langword="null"/> without the service of nGame.</returns>
    public static IPauseSource? Create(ModuleContext context) =>
        context.TryResolve(out IGameTime? time) ? new GamePause(time) : null;
}
