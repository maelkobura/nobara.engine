// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class RebindingTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private Probe? _probe;

    public RebindingTests()
    {
        _harness.Module(Module, GameInput.Nof, p => p.OnStart = probe => _probe = probe);
    }

    public void Dispose() => _harness.Dispose();

    private IInputSystem Input => _probe!.Input;

    private IInputRebinding Rebinding => Input.Rebinding;

    private InputLayer AddLayer() => Input.AddLayer(_probe!.Context, new InputLayerOptions { Name = "game" });

    [Fact]
    public void ARebindingIsWrittenThenReadAgainWithoutFreezingTheOtherActions()
    {
        using (Engine engine = _harness.Start())
        {
            Rebinding.SetBindings(Input.GetAction(Jump), [InputBinding.Key(InputControl.Key(Scancode.J))]);
            Rebinding.SetInteraction(Input.GetAction(Sprint), InputInteraction.Toggle);
            Rebinding.Save();
        }

        XElement root = XDocument.Load(_harness.ConfigFile).Root!;
        Assert.Equal("InputBindings", root.Name.LocalName);
        Assert.Equal(["inputaction:example.input.jump", "inputaction:example.input.sprint"], root.Elements("Action").Select(a => a.Attribute("id")!.Value));
        Assert.Equal("Key.J", root.Elements("Action").First().Element("Bindings")!.Element("Key")!.Value);
        Assert.Null(root.Elements("Action").First().Element("Interaction"));
        Assert.NotNull(root.Elements("Action").Last().Element("Interaction")!.Element("Toggle"));

        using (Engine engine = _harness.Start())
        {
            InputAction jump = Input.GetAction(Jump);
            Assert.True(jump.HasCustomBindings);
            Assert.Equal([InputBinding.Key(InputControl.Key(Scancode.J))], jump.Bindings);
            Assert.Equal(InteractionKind.Toggle, Input.GetAction(Sprint).Interaction.Kind);
            Assert.False(Input.GetAction(Move).HasCustomBindings);

            InputLayer layer = AddLayer();
            _harness.Key(Scancode.J, true).Key(Scancode.Space, true);
            _harness.Frame(engine);
            Assert.True(layer.Read(jump).WasPressed);
            _harness.Key(Scancode.J, false).Key(Scancode.Space, false).Key(Scancode.Space, true);
            _harness.Frame(engine);
            Assert.False(layer.Read(jump).WasPressed);
        }
    }

    [Fact]
    public void BindingsEqualToTheDefaultsAreNotWritten()
    {
        using Engine engine = _harness.Start();
        InputAction jump = Input.GetAction(Jump);
        Rebinding.Rebind(jump, 0, InputBinding.Key(InputControl.Key(Scancode.J)));
        Rebinding.Rebind(jump, 0, InputBinding.Key(InputControl.Key(Scancode.Space)));
        Rebinding.SetInteraction(Input.GetAction(Sprint), InputInteraction.Hold(0.3));
        Rebinding.Save();

        Assert.False(jump.HasCustomBindings);
        Assert.Empty(XDocument.Load(_harness.ConfigFile).Root!.Elements());
    }

    [Fact]
    public void BindingsAreAddedRemovedAndResetToTheDefaults()
    {
        using Engine engine = _harness.Start();
        InputAction jump = Input.GetAction(Jump);
        Rebinding.Rebind(jump, 1, InputBinding.Key(InputControl.Mouse(MouseControl.Middle)));
        Assert.Equal(2, jump.Bindings.Count);
        Rebinding.RemoveBinding(jump, 0);
        Assert.Equal("Mouse.Middle", jump.Bindings.Single().ToString());
        Rebinding.RemoveBinding(jump, 0);
        Assert.Empty(jump.Bindings);
        Assert.True(jump.HasCustomBindings);

        Rebinding.ResetToDefault(jump);
        Assert.Equal(jump.DefaultBindings, jump.Bindings);
        Rebinding.SetBindings(jump, []);
        Rebinding.ResetAllToDefault();
        Assert.False(jump.HasCustomBindings);

        Assert.Throws<ArgumentException>(() => Rebinding.SetBindings(jump, [InputBinding.Axis(InputControl.Mouse(MouseControl.Delta))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rebinding.Rebind(jump, 3, InputBinding.Key(InputControl.Key(Scancode.J))));
        Assert.Throws<ArgumentException>(() => Rebinding.SetInteraction(Input.GetAction(Move), InputInteraction.Toggle));
    }

    [Fact]
    public void AnUnbindableEntryIsIgnoredWithAWarningAndAnUnknownActionIsKept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_harness.ConfigFile)!);
        File.WriteAllText(_harness.ConfigFile, """
            <InputBindings format="1">
              <Action id="inputaction:other.mod.fly"><Bindings><Key>Key.F</Key></Bindings></Action>
              <Action id="inputaction:example.input.jump"><Bindings><Key>Key.Nope</Key></Bindings></Action>
              <Action id="inputaction:example.input.move"><Bindings><Key>Key.W</Key></Bindings></Action>
              <Action id="inputaction:example.input.dodge"><Interaction><Hold duration="0"/></Interaction></Action>
              <Action id="inputaction:example.input.attack"><Bindings><Key>Key.K</Key></Bindings></Action>
            </InputBindings>
            """);

        using Engine engine = _harness.Start();

        Assert.False(Input.GetAction(Jump).HasCustomBindings);
        Assert.False(Input.GetAction(Move).HasCustomBindings);
        Assert.False(Input.GetAction(Dodge).HasCustomInteraction);
        Assert.Equal("Key.K", Input.GetAction(Attack).Bindings.Single().ToString());
        LogEntry[] warnings = _harness.Log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("input.xml", StringComparison.Ordinal)).ToArray();
        Assert.Contains(warnings, w => w.Message.Contains("inputaction:other.mod.fly", StringComparison.Ordinal) && w.Message.Contains("(2)", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("'Key.Nope'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("Axis2D", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("dodge", StringComparison.Ordinal));

        Rebinding.Save();
        XElement root = XDocument.Load(_harness.ConfigFile).Root!;
        Assert.Equal(["inputaction:example.input.attack", "inputaction:other.mod.fly"], root.Elements("Action").Select(a => a.Attribute("id")!.Value));
    }

    [Fact]
    public void AFileThatIsNotXmlLeavesTheDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_harness.ConfigFile)!);
        File.WriteAllText(_harness.ConfigFile, "<InputBindings format=\"1\"><Action");

        using Engine engine = _harness.Start();

        Assert.False(Input.GetAction(Jump).HasCustomBindings);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("defaults apply", StringComparison.Ordinal));
    }

    [Fact]
    public void ConflictsAreActionsOfMapsActiveTogetherOnTheSameInput()
    {
        using Engine engine = _harness.Start();
        InputAction jump = Input.GetAction(Jump);

        // W moves on foot, and accelerates in the vehicle map, which is not active.
        BindingConflict conflict = Assert.Single(Rebinding.FindConflicts(jump, InputBinding.Key(InputControl.Key(Scancode.W))));
        Assert.Equal(Move, conflict.Action.Id);

        Input.GetMap(Vehicle).IsActive = true;
        Assert.Equal([Accelerate, Move], Rebinding.FindConflicts(jump, InputBinding.Key(InputControl.Key(Scancode.W))).Select(c => c.Action.Id));

        // A combination does not conflict with its last key alone, but with the same combination.
        Assert.Equal([Hotbar4], Rebinding.FindConflicts(jump, InputBinding.Key(InputControl.Key(Scancode.Digit4))).Select(c => c.Action.Id));
        Assert.Equal([Preset4], Rebinding.FindConflicts(jump, InputBinding.Combo(InputControl.Key(Scancode.LeftControl), InputControl.Key(Scancode.Digit4))).Select(c => c.Action.Id));
    }

    [Fact]
    public void ListeningCapturesTheNextKeyWithItsModifiers()
    {
        using Engine engine = _harness.Start();
        InputLayer layer = AddLayer();
        var captured = new List<InputBinding?>();
        InputListening listening = Rebinding.Listen(InputValueKind.Button, captured.Add);

        _harness.Key(Scancode.LeftControl, true).Key(Scancode.J, true);
        _harness.Frame(engine);

        Assert.Equal([InputBinding.Combo(InputControl.Key(Scancode.LeftControl), InputControl.Key(Scancode.J))], captured);
        Assert.False(listening.IsListening);

        // While it listened, the keys reached no layer.
        _harness.Key(Scancode.LeftControl, false).Key(Scancode.J, false).Key(Scancode.Digit4, true);
        _harness.Frame(engine);
        Assert.False(layer.Read(Input.GetAction(Preset4)).WasPressed);
        Assert.True(layer.Read(Input.GetAction(Hotbar4)).WasPressed);
    }

    [Fact]
    public void ListeningTakesAModifierReleasedAloneTheWheelTheMouseOrACancel()
    {
        using Engine engine = _harness.Start();
        var captured = new List<InputBinding?>();

        Rebinding.Listen(InputValueKind.Button, captured.Add);
        _harness.Key(Scancode.LeftShift, true).Key(Scancode.LeftShift, false);
        _harness.Frame(engine);

        Rebinding.Listen(InputValueKind.Axis1D, captured.Add);
        _harness.Wheel(0, 1);
        _harness.Frame(engine);

        Rebinding.Listen(InputValueKind.Axis2D, captured.Add);
        _harness.Key(Scancode.A, true).Move(5, 5).Move(20, 0);
        _harness.Frame(engine);

        Rebinding.Listen(InputValueKind.Button, captured.Add);
        _harness.Key(Scancode.Escape, true);
        _harness.Frame(engine);

        Assert.Equal(
            [
                InputBinding.Key(InputControl.Key(Scancode.LeftShift)),
                InputBinding.Axis(InputControl.Mouse(MouseControl.Wheel)),
                InputBinding.Axis(InputControl.Mouse(MouseControl.Delta)),
                null,
            ],
            captured);
    }
}
