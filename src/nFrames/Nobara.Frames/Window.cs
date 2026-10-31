// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames;

/// <summary>How the window occupies the screen.</summary>
public enum WindowMode
{
    /// <summary>A decorated window that the player can move and resize.</summary>
    Windowed,

    /// <summary>A window without borders that covers its monitor, at the resolution of the desktop: switching to another application is fast.</summary>
    Borderless,

    /// <summary>The window takes the monitor over, at a chosen resolution and refresh rate.</summary>
    Exclusive,
}

/// <summary>The shape of the mouse cursor over the window.</summary>
public enum CursorShape
{
    /// <summary>The usual arrow.</summary>
    Arrow,

    /// <summary>The text cursor, over a text field.</summary>
    Text,

    /// <summary>A pointing hand, over a link or a button.</summary>
    Hand,

    /// <summary>A cross, for precise selection.</summary>
    Crosshair,

    /// <summary>The system busy cursor.</summary>
    Wait,

    /// <summary>A horizontal double arrow, to resize a width.</summary>
    ResizeHorizontal,

    /// <summary>A vertical double arrow, to resize a height.</summary>
    ResizeVertical,

    /// <summary>A diagonal double arrow, from top-left to bottom-right.</summary>
    ResizeDiagonalDown,

    /// <summary>A diagonal double arrow, from bottom-left to top-right.</summary>
    ResizeDiagonalUp,

    /// <summary>A four-way arrow, to move something.</summary>
    Move,

    /// <summary>A forbidden sign.</summary>
    NotAllowed,
}

/// <summary>A size in whole units: logical units or pixels, as the member that returns it says.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct WindowSize(int Width, int Height);

/// <summary>A rectangle in whole units, from its top-left corner.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct WindowRect(int X, int Y, int Width, int Height);

/// <summary>A video mode of a monitor: a resolution in pixels and a refresh rate.</summary>
/// <param name="Width">The width, in pixels.</param>
/// <param name="Height">The height, in pixels.</param>
/// <param name="RefreshRate">The refresh rate, in hertz; 0 when the monitor does not report it.</param>
public readonly record struct VideoMode(int Width, int Height, double RefreshRate);

/// <summary>A monitor connected to the computer.</summary>
/// <param name="Index">The position of the monitor in <see cref="IWindowSystem.Monitors"/>: 0 is the primary monitor.</param>
/// <param name="Name">The name the operating system gives the monitor; it may be empty.</param>
/// <param name="Bounds">Where the monitor is on the desktop, in logical units.</param>
/// <param name="DesktopMode">The video mode of the desktop on this monitor.</param>
/// <param name="Modes">The video modes that exclusive full screen can use, largest first.</param>
/// <param name="ContentScale">The scale that the operating system applies to content on this monitor: 1 at 100 %, 1.5 at 150 %.</param>
public sealed record MonitorInfo(int Index, string Name, WindowRect Bounds, VideoMode DesktopMode, IReadOnlyList<VideoMode> Modes, float ContentScale)
{
    /// <summary>Gets whether this is the primary monitor.</summary>
    public bool IsPrimary => Index == 0;
}

/// <summary>What the native window is made of, for the graphics backend.</summary>
public enum NativeSurfaceKind
{
    /// <summary>No native window: the backend creates none.</summary>
    None,

    /// <summary>A Windows window: <see cref="NativeSurface.Window"/> is the <c>HWND</c>, <see cref="NativeSurface.Display"/> the <c>HINSTANCE</c>.</summary>
    Win32,
}

/// <summary>
/// The handles of the operating system behind the window, for a graphics
/// backend that creates its own surface on it, such as a Vulkan surface.
/// </summary>
/// <param name="Kind">What the handles are.</param>
/// <param name="Window">The handle of the window.</param>
/// <param name="Display">The handle of the display or of the application, as <paramref name="Kind"/> says.</param>
public readonly record struct NativeSurface(NativeSurfaceKind Kind, nint Window, nint Display);

/// <summary>The OpenGL context a graphics backend asks for.</summary>
/// <param name="Major">The major version, such as 4.</param>
/// <param name="Minor">The minor version, such as 6.</param>
/// <param name="CoreProfile"><see langword="true"/> for a core profile, without the functions that the core profile removes.</param>
/// <param name="Debug"><see langword="true"/> for a debug context, which reports errors through the debug output of OpenGL.</param>
public readonly record struct OpenGLContextOptions(int Major, int Minor, bool CoreProfile = true, bool Debug = false);

/// <summary>
/// Creates OpenGL contexts on the window. nFrames creates no graphics context
/// itself: the graphics backend asks for one here.
/// </summary>
/// <remarks>
/// The window has a default framebuffer of 8-bit red, green, blue, and alpha
/// channels, a 24-bit depth buffer, an 8-bit stencil buffer, and double
/// buffering, without multisampling: the operating system fixes the pixel
/// format of a window when it creates it.
/// </remarks>
public interface IOpenGLSurface
{
    /// <summary>Creates a context on the window and makes it current on the calling thread.</summary>
    /// <param name="options">The version and the profile.</param>
    /// <returns>The context.</returns>
    /// <exception cref="InvalidOperationException">The driver cannot create the context; the message gives its reason.</exception>
    IOpenGLContext CreateContext(in OpenGLContextOptions options);
}

/// <summary>An OpenGL context on the window. Disposing it destroys the context.</summary>
public interface IOpenGLContext : IDisposable
{
    /// <summary>Makes the context current on the calling thread.</summary>
    /// <exception cref="InvalidOperationException">The driver refuses.</exception>
    void MakeCurrent();

    /// <summary>Shows what was drawn in the back buffer of the window.</summary>
    void SwapBuffers();

    /// <summary>Sets how many vertical blanks <see cref="SwapBuffers"/> waits for: 0 for none, 1 to synchronize with the monitor, -1 for adaptive synchronization.</summary>
    /// <param name="interval">The interval.</param>
    /// <returns><see langword="false"/> when the driver does not support the interval.</returns>
    bool SetSwapInterval(int interval);

    /// <summary>Gets the address of an OpenGL function, to load the bindings.</summary>
    /// <param name="name">The name of the function, such as <c>glClear</c>.</param>
    /// <returns>The address, or 0 when the driver does not provide the function.</returns>
    nint GetProcAddress(string name);
}

/// <summary>
/// The window of the game. Its mode, monitor, and resolution come from the
/// configuration keys of nFrames; the rest is read and set here.
/// </summary>
/// <remarks>
/// Use it on the loop thread of the client context. Sizes and states are
/// those of the last events received, at the start of the frame.
/// </remarks>
public interface IWindow
{
    /// <summary>Gets or sets the title of the window.</summary>
    /// <remarks>The game can change it, to show the name of the open world for example.</remarks>
    string Title { get; set; }

    /// <summary>
    /// Gets the title composed from the game and the build:
    /// <c>{Name} {Version} — {Platform} — {Side}</c>, such as
    /// <c>Example 0.1.0 — win-x64 — Client</c>. The window starts with it.
    /// </summary>
    string DefaultTitle { get; }

    /// <summary>Gets how the window occupies the screen.</summary>
    WindowMode Mode { get; }

    /// <summary>Gets the position, in <see cref="IWindowSystem.Monitors"/>, of the monitor that holds most of the window.</summary>
    int Monitor { get; }

    /// <summary>Gets the video mode of the monitor in <see cref="WindowMode.Exclusive"/> mode, or <see langword="null"/> in the other modes.</summary>
    VideoMode? ExclusiveMode { get; }

    /// <summary>Gets the size of the window in logical units: the coordinates of the user interface and of the mouse.</summary>
    WindowSize Size { get; }

    /// <summary>Gets the size of the window in pixels: the size of the framebuffer to render to.</summary>
    WindowSize PixelSize { get; }

    /// <summary>Gets the number of pixels per logical unit: 1 at 100 % scaling, 1.5 at 150 %.</summary>
    float Scale { get; }

    /// <summary>Gets whether the window has the keyboard focus.</summary>
    bool HasFocus { get; }

    /// <summary>Gets whether the window is minimized: nothing of it is visible, and the rendering modules draw nothing.</summary>
    bool IsMinimized { get; }

    /// <summary>Gets the handles of the operating system behind the window.</summary>
    NativeSurface Surface { get; }

    /// <summary>Gets what creates OpenGL contexts on the window, or <see langword="null"/> when the backend cannot.</summary>
    IOpenGLSurface? OpenGL { get; }

    /// <summary>Gets or sets whether the mouse cursor shows over the window.</summary>
    bool CursorVisible { get; set; }

    /// <summary>Gets or sets the shape of the cursor over the window.</summary>
    CursorShape CursorShape { get; set; }

    /// <summary>Gets or sets whether the cursor stays inside the window.</summary>
    bool CursorConfined { get; set; }

    /// <summary>
    /// Gets or sets whether the mouse is in relative mode: the cursor hides and
    /// stays in place, and only the movements are reported, for a camera that
    /// follows the mouse.
    /// </summary>
    /// <remarks>The input module decides when to use it, from its layers.</remarks>
    bool RelativeMouseMode { get; set; }

    /// <summary>Gets whether the window receives text input events.</summary>
    bool IsTextInputActive { get; }

    /// <summary>
    /// Starts text input: typed characters and the compositions of input
    /// methods arrive as <see cref="RawInputKind.TextInput"/> and
    /// <see cref="RawInputKind.TextComposition"/> events. A platform with a
    /// screen keyboard shows it.
    /// </summary>
    void StartTextInput();

    /// <summary>Stops text input.</summary>
    void StopTextInput();

    /// <summary>Tells the input method where the text field is, so it shows its candidates next to it.</summary>
    /// <param name="area">The text field, in logical units of the window.</param>
    /// <param name="cursor">The position of the text cursor from the left edge of <paramref name="area"/>, in logical units.</param>
    void SetTextInputArea(WindowRect area, int cursor);
}
