# Show the state of a module in a debug window

This guide adds a debug window to a module, so that a developer sees what the
module does while the game runs: counters, lists, buttons that trigger an
action. The window is drawn by [nDebug](../../src/nDebug/README.md) with Dear
ImGui.

Prerequisites:

- a client module, built with `Nobara.Sdk`;
- a distribution that holds nDebug, run in development mode, or whose key
  `debug.tools` is `always`.

## Choose the dependency

A window for developers only is extra code: the module should still run in a
distribution without nDebug. Declare nDebug as an optional dependency of the
module, with `optional="true"` in its `module.nobara`.

A module that cannot run without its windows, such as the menus of a game
before its own user interface exists, declares a required dependency instead.

## Isolate the debug code

With an optional dependency, the types of nDebug and of Dear ImGui may be
absent at run time. Keep every use of them in one class marked
`[UsesOptionalDependency("nobara.debug")]`, and create that class only when
`context.IsModuleActive("nobara.debug")` is true. The engine analyzers refuse
a use of these types outside such a class.

## Register the window

In the isolation class, resolve `IDebugUi` and call `AddWindow` with the
module context, a title, and the content: an `IDebugWindow` whose `Draw`
calls Dear ImGui. Keep the handle it returns, and dispose it when the module
stops. A window of kind `Tool` shows while the debug tools are open: press F1,
then pick the window in the `Windows` menu.

The code of both steps is in [the module sheet of
nDebug](../../src/nDebug/README.md#add-a-debug-window-to-a-module).

## Check the result

Start the game in development mode, press F1, and open the window. Open the
`Profiler` window too: the row `Debug window <title>` of the module gives the
time its window takes at each frame.
