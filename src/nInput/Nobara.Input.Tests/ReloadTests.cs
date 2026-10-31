// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using Nobara.ResourceCompiler.Nof;
using static Nobara.Input.Tests.Support.InputHarness;

namespace Nobara.Input.Tests;

public sealed class ReloadTests : IDisposable
{
    private const string Module = "example.game";
    private const string Jump = "inputaction:example.input.jump";

    private readonly InputHarness _harness = new() { DevelopmentMode = true };
    private Probe? _probe;

    public void Dispose() => _harness.Dispose();

    private static string Actions(string jumpKey, string extra = "") => Nof("Example/Input", $"""
        <InputMap><Name>OnFoot</Name><Content><Active>true</Active></Content></InputMap>
        <InputAction><Name>Jump</Name><Content><Value>Button</Value><Map>inputmap:example.input.onfoot</Map><Bindings><Key>{jumpKey}</Key></Bindings></Content></InputAction>
        <InputAction><Name>Crouch</Name><Content><Value>Button</Value><Bindings><Key>Key.C</Key></Bindings></Content></InputAction>
        {extra}
        """);

    [Fact]
    public void AnotherModuleOverridesTheBindingsOfAnAction()
    {
        CompiledModule game = Compiled(Module, Actions("Key.Space"));
        _harness.Module(Module, Actions("Key.Space"));
        _harness.Module("example.mod", Nof("Example/Input", """
            <InputAction override="true">
              <Name>Jump</Name>
              <Content>
                <Query type="Bindings"><Replace><Bindings><Key>Key.J</Key><Key>Mouse.Middle</Key></Bindings></Replace></Query>
              </Content>
            </InputAction>
            """), p => p.OnStart = probe => _probe = probe, (Module, game));

        using Engine engine = _harness.Start();

        Assert.Equal(["Key.J", "Mouse.Middle"], _probe!.Input.GetAction(Jump).DefaultBindings.Select(b => b.ToString()));
    }

    [Fact]
    public void AChangedDefaultBindingAppliesWhileTheGameRunsAndKeepsTheChoicesOfThePlayer()
    {
        _harness.Module(Module, Actions("Key.Space"), p => p.OnStart = probe => _probe = probe);
        using Engine engine = _harness.Start();
        IInputSystem input = _probe!.Input;
        InputLayer layer = input.AddLayer(_probe.Context, new InputLayerOptions { Name = "game" });
        input.Rebinding.SetBindings(input.GetAction("inputaction:example.input.crouch"), [InputBinding.Key(InputControl.Key(Scancode.LeftControl))]);

        engine.ReloadResources(Module, Bundle(Module, Actions("Key.J", "<InputAction><Name>Fly</Name><Content><Value>Button</Value></Content></InputAction>")).ToByteArray());
        _harness.Frame(engine);

        InputAction jump = input.GetAction(Jump);
        Assert.Equal("Key.J", jump.Bindings.Single().ToString());
        Assert.Equal("Key.LeftControl", input.GetAction("inputaction:example.input.crouch").Bindings.Single().ToString());
        Assert.False(input.TryGetAction("inputaction:example.input.fly", out _));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("inputaction:example.input.fly", StringComparison.Ordinal) && e.Message.Contains("restart", StringComparison.Ordinal));

        _harness.Key(Scancode.J, true);
        _harness.Frame(engine);
        Assert.True(layer.Read(jump).WasPressed);
    }

    [Fact]
    public void AChangedKindOfValueIsRefused()
    {
        _harness.Module(Module, Actions("Key.Space"), p => p.OnStart = probe => _probe = probe);
        using Engine engine = _harness.Start();

        engine.ReloadResources(Module, Bundle(Module, Actions("Key.Space").Replace("<Value>Button</Value><Map>", "<Value>Float</Value><Map>", StringComparison.Ordinal)).ToByteArray());
        _harness.Frame(engine);

        Assert.Equal(InputValueKind.Button, _probe!.Input.GetAction(Jump).ValueKind);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("kind of value", StringComparison.Ordinal));
    }
}
