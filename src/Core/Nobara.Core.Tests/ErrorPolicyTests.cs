// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Logging;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Error policies: stop, disable with its cascade, and log.</summary>
public sealed class ErrorPolicyTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private sealed class Spawner : ISpawner
    {
        public void Spawn()
        {
        }
    }

    [Fact]
    public void Disables_a_failing_module_and_the_modules_that_require_it_and_keeps_the_game_running()
    {
        bool? spawnerFound = null;
        _distribution
            .Module("example.time", m => m.Level = F.Level.Platform)
            .Module("example.creatures", m => m.Requires("example.time"), m =>
            {
                m.OnRegister = c => c.Publish<ISpawner>(new Spawner());
                m.OnStart = _ => throw new InvalidOperationException("no creature fits");
            })
            .Module("example.mobs", m => m.Requires("example.creatures"))
            .Module("example.zombie", m => m.Requires("example.mobs"))
            .Module("example.sparks", m => m.Uses("example.creatures"), m => m.OnStart = c => spawnerFound = c.TryResolve(out ISpawner? _))
            .Policy("example.creatures", F.Policy.Disable)
            .Policy("example.mobs", F.Policy.Disable)
            .Policy("example.zombie", F.Policy.Log);
        using Engine engine = _distribution.Load();
        EngineContext context = engine.MainContext;

        context.Start();

        Assert.Equal(ContextState.Running, context.State);
        Assert.False(context.IsModuleActive("example.creatures"));
        Assert.False(context.IsModuleActive("example.mobs"));
        Assert.False(context.IsModuleActive("example.zombie"));
        Assert.True(context.IsModuleActive("example.time"));
        Assert.True(context.IsModuleActive("example.sparks"));

        // Disabled modules are released in reverse order; the optional dependent is told and works without them.
        Assert.Equal(["example.zombie", "example.mobs", "example.creatures"], _distribution.Journal.Of("Release"));
        Assert.Contains("example.sparks:lost example.creatures", _distribution.Journal.Lines);
        Assert.False(spawnerFound);
        Assert.Equal(["example.time", "example.sparks"], _distribution.Journal.Of("Start").Where(id => id != "example.creatures"));
        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'example.zombie' is disabled because it requires 'example.creatures'"));

        context.Stop();
        Assert.Equal(["example.sparks", "example.time"], _distribution.Journal.Of("Stop"));
    }

    [Fact]
    public void Stops_the_context_runs_the_emergency_hooks_and_writes_a_crash_report()
    {
        EmergencyStop? received = null;
        _distribution
            .Module("example.time", m => m.Level = F.Level.Platform, m => m.OnRegister = c => c.AddEmergencyHook(stop => received = stop))
            .Module("example.world", m => m.Requires("example.time"))
            .Module("example.mobs", m => m.Requires("example.world"), m => m.OnStart = _ => throw new InvalidOperationException("no spawn point"));
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal("example.mobs", exception.ModuleId);
        Assert.Equal(LifecycleStep.Start, exception.Step);
        Assert.Contains("'example.mobs' threw during start on the client side", exception.Message);
        Assert.Equal("example.mobs", received?.ModuleId);
        Assert.Equal("no spawn point", received?.Exception.Message);

        string report = File.ReadAllText(exception.CrashReport);
        Assert.StartsWith(Path.Combine(_distribution.Root, "logs"), exception.CrashReport);
        Assert.Contains("Module: example.mobs 1.0.0", report);
        Assert.Contains("Step: Start", report);
        Assert.Contains("no spawn point", report);

        // The modules that had started are stopped, then every initialized module is released, in reverse order.
        Assert.Equal(["example.world", "example.time"], _distribution.Journal.Of("Stop"));
        Assert.Equal(["example.mobs", "example.world", "example.time"], _distribution.Journal.Of("Release"));
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Theory]
    [InlineData(true, F.Level.Business)]
    [InlineData(false, F.Level.Platform)]
    public void Imposes_stop_on_required_and_platform_modules(bool required, F.Level level)
    {
        _distribution
            .Module("example.world", m => m.Level = level, m => m.OnInitialize = _ => throw new InvalidOperationException("broken"), required: required)
            .Policy("example.world", F.Policy.Disable);
        using Engine engine = _distribution.Load();

        Assert.Equal(ErrorPolicy.Stop, engine.Modules[0].ErrorPolicy);
        Assert.Equal("example.world", Assert.Throws<EngineStopException>(engine.MainContext.Start).ModuleId);
    }

    [Fact]
    public void Uses_the_default_policy_of_the_distribution()
    {
        _distribution.DefaultPolicy = F.Policy.Log;
        _distribution.Module("example.stats", behavior: m => m.OnInitialize = _ => throw new InvalidOperationException("no stats"));
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.Equal(ErrorPolicy.Log, engine.Modules[0].ErrorPolicy);
        Assert.True(engine.MainContext.IsModuleActive("example.stats"));
    }

    [Fact]
    public void Logs_the_exception_of_a_module_with_the_log_policy_and_continues()
    {
        _distribution
            .Module("example.stats", behavior: m => m.OnInitialize = _ => throw new InvalidOperationException("no stats"))
            .Policy("example.stats", F.Policy.Log);
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.True(engine.MainContext.IsModuleActive("example.stats"));
        Assert.Equal(["example.stats"], _distribution.Journal.Of("Start"));
        LogEntry error = Assert.Single(_distribution.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("example.stats", error.Source);
        Assert.Equal("Initialization", error.Phase);
        Assert.Equal("no stats", error.Exception?.Message);
    }

    [Fact]
    public void Stops_when_a_cascade_would_disable_a_module_whose_policy_is_stop()
    {
        _distribution
            .Module("example.stats", behavior: m => m.OnStart = _ => throw new InvalidOperationException("no stats"))
            .Module("example.hud", m => m.Requires("example.stats"))
            .Policy("example.stats", F.Policy.Disable);
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal("example.stats", exception.ModuleId);
        Assert.Contains(_distribution.Log.Entries, e => e.Message.Contains("would disable 'example.hud'"));
    }

    [Fact]
    public void Disables_a_module_that_fails_while_registering_and_withdraws_its_services()
    {
        bool? found = null;
        _distribution
            .Module("example.creatures", behavior: m => m.OnRegister = c =>
            {
                c.Publish<ISpawner>(new Spawner());
                throw new InvalidOperationException("half registered");
            })
            .Module("example.sparks", m => m.Uses("example.creatures"), m => m.OnInitialize = c => found = c.TryResolve(out ISpawner? _))
            .Policy("example.creatures", F.Policy.Disable);
        using Engine engine = _distribution.Load();

        engine.MainContext.Start();

        Assert.False(found);
        Assert.Empty(_distribution.Journal.Of("Release"));
    }
}
