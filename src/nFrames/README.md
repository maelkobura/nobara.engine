# nFrames

This page is the module sheet of nFrames, the window module, and of its
backend extension nFrames.SDL2. It describes what they provide to other
modules and the rules they enforce. Terms are defined in [the
glossary](../../docs/explanation/glossary.md).

## Purpose

nFrames gives the client a window behind a neutral API: its display modes,
its size and scale, its events, the requests to close it, cursor and text
input primitives, the native surface for the graphics backend, and the raw
input events that the input module reads. A backend extension implements
it with a windowing library; nFrames.SDL2 does it with SDL2.

## Status

`experimental`

## Level

nFrames:

<!-- nobara:generated:begin level -->
Platform module, on the client. Requires an active backend on the client.
<!-- nobara:generated:end -->

nFrames.SDL2:

<!-- nobara:generated:begin level nobara.frames.sdl2 -->
Platform module, on the client. Backend of `nobara.frames`.
<!-- nobara:generated:end -->

## Dependencies

nFrames:

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.event` | `^1.0` | Optional |
<!-- nobara:generated:end -->

nFrames.SDL2:

<!-- nobara:generated:begin dependencies nobara.frames.sdl2 -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.frames` | `^1.0` | Required |

Third-party libraries: `Silk.NET.SDL` 2.23.0.
<!-- nobara:generated:end -->

Third-party libraries of nFrames.SDL2: `Silk.NET.SDL` (MIT), with
`Silk.NET.Core` and `Silk.NET.Maths` (MIT), and the native SDL2 library of
`Ultz.Native.SDL` (zlib). No other module sees their types.

## Entry points

| Member | Use |
| --- | --- |
| `IWindowSystem` | The service of nFrames, resolved with `context.Resolve<IWindowSystem>()`: the window, the monitors, the subscriptions, and the raw input. |
| `IWindowSystem.MainWindow`, `IWindow` | The window: title, mode, monitor, sizes, scale, focus, minimized state, native surface, OpenGL contexts, cursor, and text input. |
| `IWindowSystem.Monitors`, `MonitorInfo`, `VideoMode` | The monitors, the primary one first, with their video modes. |
| `IWindowSystem.Subscribe`, `WindowEvent`, `WindowEventKind` | The events of the window and of the application. |
| `IWindowSystem.SubscribeCloseRequested`, `CloseRequest` | The requests to close the window, which a handler can cancel. |
| `WindowSubscription` | Removes a handler when disposed. |
| `IWindowSystem.Input`, `IRawInput`, `RawInputEvent` | The raw input events of the frame. |
| `IOpenGLSurface`, `IOpenGLContext`, `OpenGLContextOptions` | OpenGL contexts on the window, for the graphics backend. |
| `NativeSurface` | The handles of the operating system behind the window. |
| `FramesSettings` | The names of the configuration keys. |
| `IWindowBackends`, `IWindowBackend`, `IBackendWindow`, `IWindowEventSink`, `DisplayRequest` | The contract of a window backend. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Window

nFrames creates the window when it starts (step 4), after every backend
registered during initialization. A module that needs the window, such as a
graphics backend, uses it in its own `Start` or later; before that,
`MainWindow` throws. Subscriptions can be made from initialization on.

```csharp
public void Start(ModuleContext context)
{
    IWindow window = context.Resolve<IWindowSystem>().MainWindow;
    IOpenGLContext gl = window.OpenGL!.CreateContext(new OpenGLContextOptions(4, 6));
}
```

The window starts with the title `{Name} {Version} — {Platform} — {Side}`,
such as `Example 0.1.0 — win-x64 — Client`: the name and version of the
game, the runtime identifier of the process, and the side. The game can
change it with `Title`. On Windows, the window takes the icon of the
executable, which the build makes from `<Icon>` of `project.nobara`.

`Size` is in logical units, the coordinates of the user interface and of the
mouse; `PixelSize` is the size of the framebuffer; `Scale` is the number of
pixels per logical unit. The window has a default framebuffer of 8-bit color
and alpha channels, a 24-bit depth buffer, an 8-bit stencil buffer, and
double buffering, without multisampling: the operating system fixes it when
it creates the window. The graphics backend chooses the version and the
profile of the OpenGL context.

`CursorVisible`, `CursorShape`, `CursorConfined`, and `RelativeMouseMode`
are primitives: the input module decides when to use them.
`StartTextInput`, `StopTextInput`, and `SetTextInputArea` control the
typed text and the input methods.

### Events of the window

Handlers run on the loop thread, at the start of each frame, in the order
they subscribed. An exception of a handler is charged to its module.

| Event | When |
| --- | --- |
| `Resized` | The logical size or the pixel size changed. |
| `ScaleChanged` | The number of pixels per logical unit changed. |
| `FocusGained`, `FocusLost` | The window got or lost the keyboard focus. |
| `Minimized`, `Restored` | The window was minimized, or is visible again. Render nothing while it is minimized. |
| `MonitorChanged` | Most of the window moved to another monitor. |
| `ModeChanged` | The display changed after a change of the configuration keys. |
| `MonitorsChanged` | A monitor was connected, disconnected, or changed. |
| `Suspending`, `Resumed`, `SurfaceLost`, `SurfaceRecreated` | The operating system suspends or resumes the application, or takes its surface away; desktop platforms do not send them. |

Each change is reported once: nFrames compares the state of the window with
the state of the previous event.

### Closing the window

A request to close the window, from the player or from the operating
system, goes to the handlers of `SubscribeCloseRequested`. A handler that
calls `CloseRequest.Cancel` keeps the game running, to ask the player first,
and stops the game later itself. When no handler cancels, nFrames hands the
request to the context with `ModuleContext.ForwardStopRequest`, as Ctrl+C:
the module that intercepts stop requests, such as
[nContext](../nContext/README.md), receives it; without one, the context
stops after the frame.

### Raw input

`IWindowSystem.Input` holds the raw input events of the frame, in the order
they happened: keys by [scancode](../../docs/explanation/glossary.md#scancode),
mouse buttons, mouse and wheel movements, typed text, and the compositions
of input methods. nFrames fills it at the start of each frame and empties it
at the start of the next. It is the source of [nInput](../nInput/README.md);
other modules read input through that module.

```csharp
foreach (RawInputEvent e in input.Events)
{
    if (e.Kind == RawInputKind.TextInput)
    {
        ReadOnlySpan<char> text = input.GetText(e);
    }
}
```

`GetKeyName` gives the name of a key on the keyboard layout of the player,
such as `Z` for `Scancode.W` on an AZERTY keyboard. `LayoutVersion` grows
when the layout changes.

### Backends

A backend extension resolves `IWindowBackends` during its initialization and
registers an `IWindowBackend`. When nFrames starts, it uses the backend that
`frames.backend` names, or the first one registered when the key is `auto`
or names a backend that no module registers (with a warning). A distribution
where nFrames runs without a backend does not build, and does not start.

| Extension | Backend name | Library |
| --- | --- | --- |
| nFrames.SDL2 (`nobara.frames.sdl2`) | `sdl2` | SDL2, through Silk.NET |

## Events

Emitted on the [event bus](../nEvent/README.md), synchronously, when nEvent is
active:

| Event | When |
| --- | --- |
| `WindowFocusChanged` | The window gained or lost the keyboard focus. |
| `WindowCloseRequested` | A close was requested and no handler of `SubscribeCloseRequested` cancelled it, just before the stop request. It cannot be cancelled. |

Listened to: none.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `frames.backend` | user | string | `auto` |  | client |
| `frames.backgroundFps` | user | int | `30` | 1 to 1000 | client |
| `frames.fullscreenHeight` | user | int | `0` | 0 to 16384 | client |
| `frames.fullscreenRefreshRate` | user | float | `0` | 0 to 1000 | client |
| `frames.fullscreenWidth` | user | int | `0` | 0 to 16384 | client |
| `frames.mode` | user | enum | `windowed` | `windowed`, `borderless`, `exclusive` | client |
| `frames.monitor` | user | int | `0` | 0 to 63 | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `frames.backend` | The name of the window backend, or `auto` for the first one registered. A change applies at the next start. |
| `frames.mode` | `windowed`, `borderless` (full screen at the resolution of the desktop), or `exclusive` (full screen at a video mode). |
| `frames.monitor` | The monitor, by position in `Monitors`; 0 is the primary monitor. A monitor that is not connected gives the primary monitor, with a warning. |
| `frames.fullscreenWidth`, `frames.fullscreenHeight` | The resolution of exclusive full screen, in pixels; 0 for the resolution of the desktop. A resolution the monitor does not have gives the nearest in pixel count, with a warning. |
| `frames.fullscreenRefreshRate` | The refresh rate of exclusive full screen, in hertz; 0 for the highest of the resolution, otherwise the nearest. |
| `frames.backgroundFps` | The highest number of frames per second while the window has no focus or is minimized. |

The display keys apply when the window is created and again at the start of
the frame that follows a change, without a restart. When the operating
system refuses a display, the window keeps the previous one and a warning
is logged.

## Resources

None.

## Constraints

- Client only: a dedicated server runs without nFrames.
- Use the service and the window from the loop thread of the client
  context. nFrames pumps the events of the backend there, at the start of
  each frame, before the first phase; it is the input phase of the loop
  whatever its name.
- While the window has no focus or is minimized, nFrames waits at the start
  of each frame so that frames are at least `1 / frames.backgroundFps`
  seconds apart; events wake it, and the wait ends when the window gets the
  focus back. The simulation keeps its tick: elapsed time still feeds it.
  The profiler counts the wait in the start of the frame of nFrames.
- Reading raw input allocates nothing. The buffer grows when a frame has
  more events than any frame before.
- SDL keeps one state per process: the SDL2 backend initializes the video
  subsystem of SDL when nFrames first uses it, refuses to start when another
  user already did, and shuts it down when nFrames is released. It sets SDL
  so that the core keeps handling Ctrl+C.

## Known limitations

- One window: the API treats the window as an object, but nFrames creates
  only one.
- Windows only: the native surface of other platforms is `None`.
- No custom cursor image; system shapes only.
- Keys outside the keyboard page of the USB HID standard, such as media
  keys, arrive as `Scancode.Unknown`.
- The title holds no build number: the build does not provide one.
- The window takes its icon from the executable: a host that is not the
  executable of the game, such as a test, shows the default icon.
- The windowed size is 1280 by 720 logical units at start-up; it is not
  remembered between sessions.
- Dropping files on the window is not reported.
- Handlers of the close request cannot cancel it through the event bus.
- SDL2 is the only backend. The tests of nFrames.SDL2 open a real window and
  change the display mode of the monitor.
