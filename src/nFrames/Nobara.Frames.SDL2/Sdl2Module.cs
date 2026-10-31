// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

[assembly: ModuleEntry(typeof(Nobara.Frames.SDL2.Sdl2Module))]

namespace Nobara.Frames.SDL2;

/// <summary>
/// The module of the SDL2 window backend: it registers the backend
/// <c>sdl2</c> with nFrames during its initialization.
/// </summary>
public sealed class Sdl2Module : IModule
{
    /// <inheritdoc/>
    public void Initialize(ModuleContext context) =>
        context.Resolve<IWindowBackends>().Register(context, new Sdl2Backend(context.Logger));
}
