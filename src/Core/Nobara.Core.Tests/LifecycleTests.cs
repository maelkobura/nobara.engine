// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Logging;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Steps 2 to 7 in one context.</summary>
public sealed class LifecycleTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private TestDistribution ThreeModules() => _distribution
        .Module("example.time", m => m.Level = F.Level.Platform)
        .Module("example.creatures", m => m.Requires("example.time"))
        .Module("example.mobs", m => m.Requires("example.creatures"));

    [Fact]
    public void Runs_each_step_for_every_module_in_load_order_and_stops_in_reverse_order()
    {
        using Engine engine = ThreeModules().Load();

        engine.MainContext.Start();
        Assert.Equal(ContextState.Running, engine.MainContext.State);
        engine.MainContext.Stop();

        string[] order = ["example.time", "example.creatures", "example.mobs"];
        string[] reverse = [.. order.Reverse()];
        Assert.Equal(
            [.. order.Select(m => m + ":Registration"), .. order.Select(m => m + ":Initialization"), .. order.Select(m => m + ":Start"),
             .. reverse.Select(m => m + ":Stop"), .. reverse.Select(m => m + ":Release")],
            _distribution.Journal.Lines);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Fact]
    public void Logs_each_step_with_its_context_and_phase()
    {
        using Engine engine = ThreeModules().Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        var steps = _distribution.Log.Entries.Where(e => e.Source == "core" && e.Context == "client" && e.Level == LogLevel.Information).Select(e => e.Phase).Distinct();
        Assert.Equal(["", "Registration", "Initialization", "Start", "Stop", "Release"], steps);
        Assert.Contains(_distribution.Log.Entries, e => e.Phase == "Initialization" && e.Message == "Initialization: example.creatures.");
    }

    [Fact]
    public void Creates_one_instance_per_context_and_none_before_registration()
    {
        using Engine engine = ThreeModules().Load();
        Assert.Equal(0, _distribution.Instances);

        engine.MainContext.Start();

        Assert.Equal(3, _distribution.Instances);
    }

    [Fact]
    public void Gives_each_module_the_side_of_its_context()
    {
        Side seen = Side.None;
        _distribution.Module("example.time", behavior: m => m.OnRegister = c => seen = c.Side);
        using Engine engine = _distribution.Load(Side.Server);

        engine.MainContext.Start();

        Assert.Equal(Side.Server, seen);
    }

    [Fact]
    public void Starts_a_context_once()
    {
        using Engine engine = ThreeModules().Load();
        engine.MainContext.Start();

        Assert.Throws<InvalidOperationException>(engine.MainContext.Start);
    }

    [Fact]
    public void Stops_running_contexts_when_the_engine_is_disposed()
    {
        Engine engine = ThreeModules().Load();
        engine.MainContext.Start();

        engine.Dispose();

        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
        Assert.Equal(["example.mobs", "example.creatures", "example.time"], _distribution.Journal.Of("Release"));
    }

    [Fact]
    public void Logs_an_exception_thrown_while_stopping_and_continues()
    {
        _distribution
            .Module("example.time", m => m.Level = F.Level.Platform)
            .Module("example.mobs", m => m.Requires("example.time"), m => m.OnStop = _ => throw new InvalidOperationException("save failed"));
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        engine.MainContext.Stop();

        Assert.Equal(["example.mobs", "example.time"], _distribution.Journal.Of("Release"));
        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Error && e.Exception?.Message == "save failed");
    }
}
