// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Frames;

/// <summary>
/// The service of nFrames: the window, the monitors, the events of the window,
/// and the raw input events. A module resolves it with
/// <c>context.Resolve&lt;IWindowSystem&gt;()</c>, after declaring a dependency
/// on <c>nobara.frames</c>.
/// </summary>
/// <remarks>
/// <para>
/// The window is created when nFrames starts (step 4), once every backend is
/// registered: a module that needs it, such as a graphics backend, uses it in
/// its own <see cref="IModule.Start"/> or later. Subscriptions can be made
/// from initialization on.
/// </para>
/// <para>
/// Events are delivered on the loop thread of the client context, at the start
/// of each frame, before the first phase. Use the service from that thread.
/// </para>
/// </remarks>
public interface IWindowSystem
{
    /// <summary>Gets whether the window exists: from the start step on, until the module stops.</summary>
    bool IsCreated { get; }

    /// <summary>Gets the name of the backend in use, such as <c>sdl2</c>.</summary>
    /// <exception cref="InvalidOperationException">The window is not created yet.</exception>
    string BackendName { get; }

    /// <summary>Gets the window of the game.</summary>
    /// <exception cref="InvalidOperationException">The window is not created yet.</exception>
    IWindow MainWindow { get; }

    /// <summary>Gets the monitors, the primary one first.</summary>
    /// <exception cref="InvalidOperationException">The window is not created yet.</exception>
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Gets the raw input events of the frame, for the input module.</summary>
    IRawInput Input { get; }

    /// <summary>Subscribes a handler to the events of the window.</summary>
    /// <param name="module">The subscribing module: it is charged for the exceptions of the handler, and its handlers stop when it is disabled.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The handle that removes the handler.</returns>
    /// <remarks>Handlers run in the order they subscribed.</remarks>
    WindowSubscription Subscribe(ModuleContext module, Action<WindowEvent> handler);

    /// <summary>Subscribes a handler to the requests to close the window; the handler may cancel them.</summary>
    /// <param name="module">The subscribing module.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The handle that removes the handler.</returns>
    /// <remarks>
    /// Handlers run in the order they subscribed, all of them even after one
    /// cancels. When none cancels, nFrames hands the request to the context
    /// as a stop request from outside the game, which the module that
    /// intercepts stop requests receives (<see cref="ModuleContext.ForwardStopRequest"/>).
    /// </remarks>
    WindowSubscription SubscribeCloseRequested(ModuleContext module, Action<CloseRequest> handler);
}

/// <summary>
/// Where backends register with nFrames. A backend module resolves it with
/// <c>context.Resolve&lt;IWindowBackends&gt;()</c> during its initialization
/// and registers its backend there.
/// </summary>
public interface IWindowBackends
{
    /// <summary>Registers a backend. nFrames chooses one of the registered backends when it starts, from <c>frames.backend</c>.</summary>
    /// <param name="module">The module of the backend.</param>
    /// <param name="backend">The backend; nFrames disposes the one it uses when it is released.</param>
    /// <exception cref="InvalidOperationException">nFrames has started already, or a backend of the same name is registered.</exception>
    void Register(ModuleContext module, IWindowBackend backend);
}

/// <summary>
/// A window backend: the code of one windowing library, behind the neutral
/// API of nFrames. Only nFrames calls it, on the loop thread.
/// </summary>
public interface IWindowBackend : IDisposable
{
    /// <summary>Gets the name of the backend, which <c>frames.backend</c> uses, such as <c>sdl2</c>.</summary>
    string Name { get; }

    /// <summary>Lists the monitors.</summary>
    /// <returns>The monitors, the primary one first, each with <see cref="MonitorInfo.Index"/> set to its position.</returns>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Creates the window, hidden; nFrames applies its display, then shows it.</summary>
    /// <param name="title">The title.</param>
    /// <param name="size">The size of the window in windowed mode, in logical units.</param>
    /// <returns>The window.</returns>
    IBackendWindow CreateWindow(string title, WindowSize size);

    /// <summary>
    /// Waits until an event arrives or <paramref name="wait"/> elapses, then
    /// hands every pending event to <paramref name="sink"/>.
    /// </summary>
    /// <param name="sink">What receives the events.</param>
    /// <param name="wait">The longest wait for a first event; <see cref="TimeSpan.Zero"/> to return at once.</param>
    /// <returns>The time spent waiting.</returns>
    TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait);

    /// <summary>Gets the name of a key on the current keyboard layout.</summary>
    /// <param name="scancode">The physical key.</param>
    /// <returns>The name, or an empty string.</returns>
    string GetKeyName(Scancode scancode);
}

/// <summary>What nFrames asks a backend window to show: a mode on a monitor.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Monitor">The position of the monitor in the list of the backend; it exists.</param>
/// <param name="Exclusive">In <see cref="WindowMode.Exclusive"/> mode, the video mode, one of those the monitor lists.</param>
public readonly record struct DisplayRequest(WindowMode Mode, int Monitor, VideoMode Exclusive);

/// <summary>The window as a backend provides it. nFrames wraps it in the <see cref="IWindow"/> that modules see.</summary>
public interface IBackendWindow : IDisposable
{
    /// <summary>Gets the size in logical units.</summary>
    WindowSize Size { get; }

    /// <summary>Gets the size in pixels.</summary>
    WindowSize PixelSize { get; }

    /// <summary>Gets the position of the monitor that holds most of the window.</summary>
    int Monitor { get; }

    /// <summary>Gets whether the window has the keyboard focus.</summary>
    bool HasFocus { get; }

    /// <summary>Gets whether the window is minimized.</summary>
    bool IsMinimized { get; }

    /// <summary>Gets the handles of the operating system.</summary>
    NativeSurface Surface { get; }

    /// <summary>Gets what creates OpenGL contexts, or <see langword="null"/>.</summary>
    IOpenGLSurface? OpenGL { get; }

    /// <summary>Sets the title.</summary>
    /// <param name="title">The title.</param>
    void SetTitle(string title);

    /// <summary>Shows the window in a mode, on a monitor, and shows it if it is hidden.</summary>
    /// <param name="request">The mode, the monitor, and the video mode for exclusive full screen.</param>
    /// <returns><see langword="null"/> when applied, or the reason the operating system refused.</returns>
    string? ApplyDisplay(in DisplayRequest request);

    /// <summary>Shows or hides the cursor over the window.</summary>
    /// <param name="visible"><see langword="true"/> to show it.</param>
    void SetCursorVisible(bool visible);

    /// <summary>Sets the shape of the cursor.</summary>
    /// <param name="shape">The shape.</param>
    void SetCursorShape(CursorShape shape);

    /// <summary>Keeps the cursor in the window, or lets it out.</summary>
    /// <param name="confined"><see langword="true"/> to keep it in.</param>
    void SetCursorConfined(bool confined);

    /// <summary>Turns the relative mouse mode on or off.</summary>
    /// <param name="relative"><see langword="true"/> to turn it on.</param>
    void SetRelativeMouseMode(bool relative);

    /// <summary>Starts or stops text input.</summary>
    /// <param name="active"><see langword="true"/> to start it.</param>
    void SetTextInput(bool active);

    /// <summary>Tells the input method where the text field is.</summary>
    /// <param name="area">The field, in logical units.</param>
    /// <param name="cursor">The position of the text cursor in the field, in logical units.</param>
    void SetTextInputArea(WindowRect area, int cursor);
}

/// <summary>What receives the events that a backend pumps. nFrames implements it.</summary>
public interface IWindowEventSink
{
    /// <summary>Reports a change of the window or of the application; nFrames reads the new state from the window.</summary>
    /// <param name="kind">What changed.</param>
    void OnWindowEvent(WindowEventKind kind);

    /// <summary>Reports a request to close the window, or to end the application.</summary>
    void OnCloseRequested();

    /// <summary>Reports a change of the keyboard layout.</summary>
    void OnKeyboardLayoutChanged();

    /// <summary>Reports a raw input event.</summary>
    /// <param name="e">The event.</param>
    /// <param name="text">The characters of a text or composition event; empty for the others.</param>
    void OnInput(in RawInputEvent e, ReadOnlySpan<char> text);
}
