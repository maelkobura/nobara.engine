// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Frames;

namespace Nobara.Graphics;

/// <summary>
/// The service of nGraphics: the device of the window. A module resolves it
/// with <c>context.Resolve&lt;IGraphicsSystem&gt;()</c>, after declaring a
/// dependency on <c>nobara.graphics</c>.
/// </summary>
/// <remarks>
/// nGraphics creates the device when it starts (step 4), after the window and
/// every backend: a module that needs the device uses it in its own
/// <see cref="IModule.Start"/> or later. Use it on the loop thread of the
/// client context.
/// </remarks>
public interface IGraphicsSystem
{
    /// <summary>Gets whether the device exists: from the start step on, until the module is released.</summary>
    bool IsCreated { get; }

    /// <summary>Gets the name of the backend in use, such as <c>opengl</c>.</summary>
    /// <exception cref="InvalidOperationException">The device is not created yet.</exception>
    string BackendName { get; }

    /// <summary>Gets the device.</summary>
    /// <exception cref="InvalidOperationException">The device is not created yet.</exception>
    GraphicsDevice Device { get; }
}

/// <summary>
/// Where graphics backends register. A backend module resolves it with
/// <c>context.Resolve&lt;IGraphicsBackends&gt;()</c> during its
/// initialization and registers its backend there.
/// </summary>
public interface IGraphicsBackends
{
    /// <summary>Registers a backend. nGraphics chooses one when it starts, from <c>graphics.backend</c>.</summary>
    /// <param name="module">The module of the backend.</param>
    /// <param name="backend">The backend.</param>
    /// <exception cref="InvalidOperationException">nGraphics has started already, or a backend of the same name is registered.</exception>
    void Register(ModuleContext module, IGraphicsBackend backend);
}

/// <summary>What nGraphics asks a backend for when it creates the device.</summary>
/// <param name="FramesInFlight">The number of frames in flight, from 1 to 3.</param>
/// <param name="VSync">The synchronization with the monitor.</param>
/// <param name="Logger">The journal of nGraphics, for the driver and its fallbacks.</param>
public readonly record struct GraphicsDeviceOptions(int FramesInFlight, VSyncMode VSync, Logger Logger);

/// <summary>A graphics backend: the code of one graphics API, behind the neutral API of nGraphics.</summary>
public interface IGraphicsBackend
{
    /// <summary>Gets the name of the backend, which <c>graphics.backend</c> uses, such as <c>opengl</c>.</summary>
    string Name { get; }

    /// <summary>Creates the device on the window, with its swapchain attached.</summary>
    /// <param name="window">The window of nFrames.</param>
    /// <param name="options">The options.</param>
    /// <returns>The device; nGraphics disposes it when it is released.</returns>
    /// <exception cref="NotSupportedException">The window or the driver cannot run the backend; the message says why.</exception>
    GraphicsDevice CreateDevice(IWindow window, in GraphicsDeviceOptions options);
}
