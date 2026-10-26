// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Logging;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>The check of declared states, and the logging of the core.</summary>
public sealed class StateAndLoggingTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private Action<TestModule> Verifying(params RegisteredState[] states) => m => m.OnStart = c => c.VerifyStates(states);

    private TestDistribution WithStates(Action<TestModule> behavior) => _distribution
        .Module("example.menus", m =>
        {
            m.States.Add(new F.StateDeclaration { Id = "state:example.menu" });
            m.States.Add(new F.StateDeclaration { Id = "state:example.lobby", Side = F.Side.Server });
        })
        .Module("example.states", m => m.Level = F.Level.Platform, behavior);

    [Fact]
    public void Accepts_registered_states_that_match_the_declarations_of_the_side()
    {
        using Engine engine = WithStates(Verifying(new RegisteredState("state:example.menu", "example.menus"))).Load();

        engine.MainContext.Start();

        Assert.Equal(ContextState.Running, engine.MainContext.State);
    }

    [Fact]
    public void Refuses_a_declared_state_that_is_not_registered()
    {
        using Engine engine = WithStates(Verifying()).Load();

        var exception = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        var mismatch = Assert.IsType<StateMismatchException>(exception.InnerException);
        Assert.Equal(["State 'state:example.menu' is declared by module 'example.menus' and not registered on the client side."], mismatch.Differences);
    }

    [Fact]
    public void Refuses_a_registered_state_that_no_module_declares_or_that_another_module_declares()
    {
        using Engine engine = WithStates(Verifying(
            new RegisteredState("state:example.menu", "example.states"),
            new RegisteredState("state:example.credits", "example.menus"))).Load();

        var mismatch = Assert.IsType<StateMismatchException>(Assert.Throws<EngineStopException>(engine.MainContext.Start).InnerException);

        Assert.Equal(
            [
                "State 'state:example.menu' is registered by module 'example.states', but module 'example.menus' declares it.",
                "State 'state:example.credits' is registered by module 'example.menus', but no active module declares it on the client side.",
            ],
            mismatch.Differences);
    }

    [Fact]
    public void Formats_nothing_and_allocates_nothing_for_a_disabled_level()
    {
        using Engine engine = _distribution.Module("example.time").Load();
        var formatted = new CountingValue();
        Logger logger = engine.MainContext.Logger;

        // Debug is enabled in these tests; Trace is not.
        logger.Trace($"value {formatted}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            logger.Trace($"value {formatted} {i}");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, formatted.Formats);
        Assert.Equal(0, allocated);
        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void Writes_structured_messages_with_the_module_and_the_context()
    {
        _distribution.Module("example.time", behavior: m => m.OnInitialize = c => c.Logger.Info($"tick rate {30}"));
        using Engine engine = _distribution.Load(Side.Server);

        engine.MainContext.Start();

        LogEntry entry = Assert.Single(_distribution.Log.Entries, e => e.Message == "tick rate 30");
        Assert.Equal("example.time", entry.Source);
        Assert.Equal("server", entry.Context);
        Assert.Equal("Initialization", entry.Phase);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.EndsWith("INF [server/example.time/Initialization] tick rate 30", entry.ToString());
    }

    [Fact]
    public void Writes_a_log_file_in_the_logs_folder()
    {
        using (Engine engine = _distribution.Module("example.time").Load(logToFile: true))
        {
            engine.MainContext.Start();
        }

        string file = Assert.Single(Directory.GetFiles(Path.Combine(_distribution.Root, "logs"), "*.log"));
        Assert.Contains("[client/core/Registration] Registration: 1 modules.", File.ReadAllText(file));
    }

    private sealed class CountingValue
    {
        public int Formats { get; private set; }

        public override string ToString()
        {
            Formats++;
            return "counted";
        }
    }
}
