// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Frames;

[assembly: ModuleEntry(typeof(Nobara.Graphics.OpenGL.OpenGLModule))]

namespace Nobara.Graphics.OpenGL;

/// <summary>The module of the OpenGL 4.6 backend: it registers the backend <c>opengl</c> with nGraphics.</summary>
public sealed class OpenGLModule : IModule
{
    /// <inheritdoc/>
    public void Initialize(ModuleContext context) =>
        context.Resolve<IGraphicsBackends>().Register(context, new OpenGLBackend());
}

/// <summary>The OpenGL 4.6 backend: a core context on the window, direct state access, and SPIR-V shaders.</summary>
internal sealed class OpenGLBackend : IGraphicsBackend
{
    public string Name => "opengl";

    public GraphicsDevice CreateDevice(IWindow window, in GraphicsDeviceOptions options) => GLDevice.Create(window, options);
}
