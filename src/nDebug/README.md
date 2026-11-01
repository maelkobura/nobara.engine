# nDebug

This page is the module sheet of nDebug, the module of the debug user
interface. It describes what the module provides to other modules and the
rules it enforces. Terms are defined in [the
glossary](../../docs/explanation/glossary.md).

## Purpose

nDebug is the user interface for debugging and for the tools of the engine,
built on Dear ImGui. It owns the context of Dear ImGui, feeds it from the
input layer of highest priority of [nInput](../nInput/README.md), and draws it
at the insertion point of the user interface of
[nRenderer](../nRenderer/README.md), through nRenderer only. Modules register
their own debug windows and draw them with Dear ImGui directly. nDebug
provides two windows: the profiler of the core, and the settings. Until the
user interface of the game exists, the menus of a game are screens of nDebug
too.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.frames` | `^1.0` | Required |
| `nobara.graphics` | `^1.0` | Required |
| `nobara.input` | `^1.0` | Required |
| `nobara.renderer` | `^1.0` | Required |

Third-party libraries: `ImGui.NET` 1.91.6.1.
<!-- nobara:generated:end -->

nRenderer draws the frames of Dear ImGui in the pass of the user interface,
and provides the shader resource; nGraphics creates the font atlas, the
buffers, and the pipeline; nInput gives the input layer and the action that
opens the tools; nFrames gives the size, the scale, and the cursor of the
window, and the names of the keys. Third-party library: `ImGui.NET` (MIT), the
.NET bindings of Dear ImGui (MIT) through its C wrapper cimgui (MIT), shipped
as a native library for each platform. Modules that draw debug windows call
`ImGuiNET.ImGui` directly.

## Entry points

| Member | Use |
| --- | --- |
| `IDebugUi` | The service, resolved with `context.Resolve<IDebugUi>()`: whether the tools are available (`ToolsEnabled`) and open (`IsOpen`), the windows (`Windows`, `AddWindow`), and whether Dear ImGui used the mouse or the keyboard in the last frame (`WantsMouse`, `WantsKeyboard`). |
| `IDebugWindow` | The content of a window: nDebug calls `Draw` once per frame while the window shows. |
| `DebugWindow` | The handle of a registered window: its title, kind, and module, whether it is open (`IsOpen`), and `Dispose`, which removes it. |
| `DebugWindowOptions`, `DebugWindowKind`, `DebugWindowPlacement` | How a window is registered: its title, a tool or a screen, free, centered, or over the whole display, its first size, and whether it is open. |
| `DebugSettings` | The names and values of the configuration key. |
| `DebugModule` | The identifier of the module and the rank of its pass at the insertion point of the user interface. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Debug windows

A module registers a window with `AddWindow`, from its initialization on,
and keeps the handle that removes it. While the window shows, nDebug begins
it, calls `IDebugWindow.Draw`, and ends it; `Draw` draws the content with
Dear ImGui.

```csharp
public void Start(ModuleContext context)
{
    _window = context.Resolve<IDebugUi>().AddWindow(
        context,
        new DebugWindowOptions { Title = "Crops", Size = new Vector2(300, 200) },
        new CropsWindow(_crops));
}

public void Stop(ModuleContext context) => _window?.Dispose();

private sealed class CropsWindow(CropField crops) : IDebugWindow
{
    public void Draw(DebugWindow window)
    {
        ImGui.Text($"Crops growing: {crops.Count}");
        if (ImGui.Button("Ripen all"))
        {
            crops.RipenAll();
        }
    }
}
```

nDebug calls the windows while nRenderer draws the frame, before the pass of
the user interface begins: after the simulation and every other phase of the
frame, on the loop thread. The time of each window is measured as a scope of
its module, `Debug window <title>`, and the time of the whole frame of Dear
ImGui as the scope `Debug frame` of nDebug. An exception of a window is
charged to its module, under its error policy; nDebug ends the window, and
Dear ImGui closes what the content left open at the end of the frame. The
windows of a module that is no longer active are not drawn.

### Add a debug window to a module

A module that must run without nDebug depends on it optionally, and keeps its
debug code in an isolation class that it calls only when nDebug is active:

1. In `module.nobara`, declare `<Module id="nobara.debug" version="^1.0"
   optional="true" />`.
2. Write the debug code in a class marked
   `[UsesOptionalDependency("nobara.debug")]`. Only this class names the types
   of nDebug and of Dear ImGui, and its members that other classes call do not
   expose them.
3. Create the class only once `context.IsModuleActive("nobara.debug")` is
   true, and dispose its window when the module stops.

```csharp
public void Start(ModuleContext context)
{
    if (context.IsModuleActive("nobara.debug"))
    {
        _debug = new CropsDebug(context, _crops);
    }
}

public void Stop(ModuleContext context) => _debug?.Dispose();

[UsesOptionalDependency("nobara.debug")]
internal sealed class CropsDebug : IDebugWindow, IDisposable
{
    private readonly CropField _crops;
    private readonly DebugWindow _window;

    public CropsDebug(ModuleContext context, CropField crops)
    {
        _crops = crops;
        _window = context.Resolve<IDebugUi>().AddWindow(context, new DebugWindowOptions { Title = "Crops" }, this);
    }

    public void Draw(DebugWindow window) => ImGui.Text($"Crops growing: {_crops.Count}");

    public void Dispose() => _window.Dispose();
}
```

Without nDebug in the distribution, the module loads and runs: `CropsDebug`
is never loaded, so neither are the assemblies of nDebug and of Dear ImGui.
The [engine analyzers](../../docs/reference/analyzers.md#optional-dependency-rules)
check the isolation class.

### Tools and screens

| Kind | Shows | Cursor |
| --- | --- | --- |
| `DebugWindowKind.Tool` | While the tools are open, if the tools are available. The menu bar of the tools lists it and opens it again after its close button closed it. | Shown while the tools are open. |
| `DebugWindowKind.Screen` | Whenever it is open, tools available or not. | Shown while it is open. |

The action `inputaction:nobara.debug.toggle`, on F1 by default, opens and
closes the tools when they are available; the player may bind it to another
key like any action. `IDebugUi.IsOpen` does the same in code. The tools are
available when `debug.tools` is `always`, or `development` and the engine
runs in [development mode](../../docs/explanation/glossary.md#development-mode).
Screens show in every build: a menu of the game registers a screen when its
state enters and disposes it when the state exits.

```csharp
public override void Enter()
{
    _menu = _debug.AddWindow(_context, new DebugWindowOptions
    {
        Title = "Pause",
        Kind = DebugWindowKind.Screen,
        Placement = DebugWindowPlacement.Fullscreen,
    }, _pauseMenu);
}

public override void Exit() => _menu?.Dispose();
```

A `Fullscreen` window covers the display without title bar or border, behind
the other windows; a `Center` window appears at the center of the display.
Screens are drawn first, so the tools stay above them.

### Input

nDebug registers the input layer `nobara.debug`, of priority
`InputLayerPriority.Debug`, above every other layer, which receives the raw
input. While nothing shows, it lets everything through. While a window shows:

- the mouse goes to Dear ImGui, and is consumed while the cursor is over one
  of its windows or drags one: a click in a window does not reach the game;
- the keyboard goes to Dear ImGui, and is consumed while one of its widgets
  is active;
- a text field that is active takes the text focus of nInput: typed text goes
  to the field, and no keyboard action triggers in any layer until the field
  is left;
- the layer asks for the cursor, which takes the mouse out of relative mode.

Letter keys reach Dear ImGui as the layout of the player labels them, so the
shortcuts of text fields are the keys printed so; the other keys keep their
position. Dear ImGui handles the repetition of held keys itself.

### Profiler

The window `Profiler` shows the measures of the
[profiler of the core](../Core/README.md#profiler), for every context, one
row per marker: context, module, kind, name, calls, total, mean, and longest
time, managed memory allocated, and pauses of the garbage collector. A click
on a header sorts the rows; the filter keeps the rows whose context, module,
kind, or name contains its text. `Enabled` turns the profiler on and off,
`Reset` sets the measures back to zero, and `Paused` stops the reading. The
window reads the profiler twice a second while it shows, and never turns it on
by itself. Turn the profiler on from start-up with the launch option
`--profile` of the executable of the game.

### Settings

The window `Settings` lists the configuration keys that nDebug may read: its
own and those of nFrames, nInput, nGraphics, and nRenderer. It shows the
value, the scope, and the default of each key, and edits the keys of scope
`user` that the distribution does not lock: a check box, a slider between the
bounds, a number, a list of values, or a text field. A change applies at once
through the notifications of the core, which the modules of the key receive at
the next frame, and the settings file of the player is saved, so the value
holds at the next start. `Reset` gives a key its default back. A value out of
bounds is refused with a message.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `debug.tools` | distribution | enum | `development` | `development`, `always`, `never` | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `debug.tools` | When the tools are available: `development` in development mode only, `always` in every build, `never` in no build. Read when nDebug starts. Screens show whatever its value. |

A distribution that ships no debug interface at all leaves nDebug out of its
build; it then has no screen either.

## Resources

nDebug provides two resources:

| Resource | Use |
| --- | --- |
| `shader:nobara.debug.imgui` | The shader of the draw lists of Dear ImGui: 2D vertices with a color, and the font atlas at sampled texture binding 0. It decodes the sRGB colors of Dear ImGui when the target encodes colors itself or keeps linear values. |
| `inputaction:nobara.debug.toggle` | The action that opens and closes the tools, on F1. |

## Constraints

- Client only: a dedicated server runs without nDebug.
- Use the service and the windows on the loop thread of the client context.
  Windows draw on that thread, during the phase `Render`.
- Dear ImGui keeps its current context in a native global: nDebug makes its
  context current before each call, so two client contexts of one process
  must not draw Dear ImGui on two threads at once.
- nDebug draws only through nRenderer and nGraphics: it never calls OpenGL.
- A frame that shows nothing costs a test per window, and allocates nothing.
  A frame that shows windows allocates what their content and Dear ImGui.NET
  allocate.
- nDebug writes nothing to the working folder: no `imgui.ini`, no log.

## Known limitations

- Dear ImGui draws with its default font only, rasterized at the scale of the
  monitor of the window when it changes.
- The positions and sizes of windows are not kept from one start to the next.
- Only the font atlas is a texture of Dear ImGui: `ImGui.Image` cannot show a
  texture of nRenderer.
- The compositions of input methods are not shown in text fields, and the
  candidate window of an input method is not placed next to the field.
- The clipboard of the system is not connected: copy and paste work inside
  Dear ImGui only.
- A press and a release in the same frame reach Dear ImGui over two frames,
  and the mouse is consumed from what Dear ImGui wanted in the frame before:
  a click in the frame where the cursor first enters a window reaches the
  game.
- The window `Settings` shows the keys of nDebug and of its dependencies only.
- nFrames, nInput, nGraphics, and nRenderer have no debug window: they cannot
  depend on nDebug, which depends on them.
- Keyboard and gamepad navigation of Dear ImGui are off.
