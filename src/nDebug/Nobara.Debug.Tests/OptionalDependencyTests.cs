// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using ImGuiNET;
using Nobara.Core;
using Nobara.Debug.Tests.Support;

namespace Nobara.Debug.Tests;

/// <summary>A module that depends on nDebug optionally: its debug code lives in an isolation class, called only when nDebug is active.</summary>
public sealed class OptionalDependencyTests : IDisposable
{
    private const string Crops = "example.crops";

    private readonly DebugHarness _harness = new();
    private readonly CropsModule _crops = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void The_module_loads_and_runs_without_nDebug()
    {
        _harness.WithDebug = false;
        _harness.Module(Crops, debug: true, behavior: _crops.Attach);
        using Engine engine = _harness.Start();

        _harness.Frame(engine, 3);

        Assert.False(_crops.HasDebugWindow);
        Assert.Equal(3, _crops.Frames);
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void The_module_adds_its_window_when_nDebug_is_active()
    {
        _harness.Module(Crops, debug: true, behavior: _crops.Attach);
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;

        _harness.Frame(engine, 3);

        Assert.True(_crops.HasDebugWindow);
        Assert.Contains(_harness.Debug.Windows, w => w.Title == "Crops" && w.Module.Module.Id == Crops);
        Assert.Equal(3, _crops.Frames);
    }

    /// <summary>A module of crops, whose debug window shows how many crops grow.</summary>
    private sealed class CropsModule
    {
        private CropsDebug? _debug;

        public int Frames { get; private set; }

        public bool HasDebugWindow => _debug is not null;

        public void Attach(Probe probe)
        {
            probe.OnStart = p =>
            {
                // The isolation class is only touched once nDebug is known to be active.
                if (p.Context.IsModuleActive(DebugModule.ModuleId))
                {
                    _debug = new CropsDebug(p.Context, () => Frames);
                }
            };
            probe.OnStop = _ => _debug?.Dispose();
            probe.OnRegister = p => p.Context.Loop.Subscribe("Render", new CountFrames(this));
        }

        private sealed class CountFrames(CropsModule module) : Nobara.Core.Loop.IPhaseHandler
        {
            public void Run(in Nobara.Core.Loop.PhaseTime time) => module.Frames++;
        }
    }

    /// <summary>The debug code of the crops: the only class that names the types of nDebug and of Dear ImGui.</summary>
    [UsesOptionalDependency(DebugModule.ModuleId)]
    private sealed class CropsDebug : IDebugWindow, IDisposable
    {
        private readonly DebugWindow _window;
        private readonly Func<int> _count;

        public CropsDebug(ModuleContext context, Func<int> count)
        {
            _count = count;
            _window = context.Resolve<IDebugUi>().AddWindow(context, new DebugWindowOptions { Title = "Crops" }, this);
        }

        public void Draw(DebugWindow window) => ImGui.Text($"Crops growing: {_count()}");

        public void Dispose() => _window.Dispose();
    }
}
