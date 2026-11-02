// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Compression;
using System.Text.Json;

namespace Nobara.Cli.Tests;

/// <summary>The sample game, built in release for its client and server targets.</summary>
public sealed class ReleaseBuild : IDisposable
{
    public ReleaseBuild()
    {
        Game = new SampleGameCopy();
        Result = Game.Run("build");
    }

    internal SampleGameCopy Game { get; }

    internal CliResult Result { get; }

    public void Dispose() => Game.Dispose();
}

/// <summary><c>nobara build</c> and <c>nobara package</c> on the sample game, and the built game itself.</summary>
public sealed class EndToEndTests : IClassFixture<ReleaseBuild>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly ReleaseBuild _build;

    public EndToEndTests(ReleaseBuild build)
    {
        _build = build;
        Assert.True(build.Result.ExitCode == 0, build.Result.ToString());
    }

    private string Out(string relative) => _build.Game.Path_(Path.Combine("out", relative));

    [Fact]
    public void Arranges_each_target_in_the_folders_of_an_installed_game()
    {
        string[] client = Directory.EnumerateFiles(Out("client"), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Out("client"), f).Replace('\\', '/')).ToArray();
        Assert.Contains("Sample Game.exe", client);
        Assert.Contains("bin/SampleGame.dll", client);
        Assert.Contains("bin/Nobara.Core.dll", client);
        Assert.Contains("bin/Google.Protobuf.dll", client);
        Assert.Contains("bin/System.Private.CoreLib.dll", client);
        Assert.Contains("game/Example.Ticker.dll", client);
        Assert.Contains("game/Example.Clock.dll", client);
        Assert.Contains("lib/System.IO.Hashing.dll", client);
        Assert.DoesNotContain(client, f => f.StartsWith("bin/Example.", StringComparison.Ordinal) || f.StartsWith("bin/System.IO.Hashing", StringComparison.Ordinal));
        Assert.DoesNotContain(client, f => f.Contains("Nobara.HotReload", StringComparison.Ordinal) || f.Contains("Nobara.ResourceCompiler", StringComparison.Ordinal));

        // The server ships no client module.
        Assert.True(File.Exists(Out("server/Sample GameServer.exe")));
        Assert.True(File.Exists(Out("server/game/Example.Ticker.dll")));
        Assert.False(File.Exists(Out("server/game/Example.Clock.dll")));

        // The dependency file of bin/ only names what bin/ holds.
        string deps = File.ReadAllText(Out("client/bin/SampleGame.deps.json"));
        Assert.DoesNotContain("Example.Ticker", deps, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.IO.Hashing", deps, StringComparison.Ordinal);
        using JsonDocument _ = JsonDocument.Parse(deps);
    }

    [Fact]
    public void Packs_each_target_into_an_archive()
    {
        using (ZipArchive server = ZipFile.OpenRead(Out("server.zip")))
        {
            Assert.Contains(server.Entries, e => e.FullName == "Sample GameServer/Sample GameServer.exe");
            Assert.Contains(server.Entries, e => e.FullName == "Sample GameServer/game/Example.Ticker.dll");
        }

        using (ZipArchive client = ZipFile.OpenRead(Out("client.zip")))
        {
            Assert.Contains(client.Entries, e => e.FullName == "Sample Game/Sample Game.exe");
        }

        File.Delete(Out("client.zip"));
        CliResult result = _build.Game.Run("package", "client");
        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(File.Exists(Out("client.zip")));
    }

    [Fact]
    public void Runs_the_server_a_few_ticks_then_stops_it()
    {
        string install = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "server-" + Guid.NewGuid().ToString("N"));
        SampleGameCopy.Copy(Out("server"), install);

        using RunningGame server = SampleGameCopy.Start(Path.Combine(install, "Sample GameServer.exe"));
        int exit = server.Exit(Timeout);

        Assert.True(exit == 0, server.Output);
        Assert.Contains("library hash", server.Output, StringComparison.Ordinal);
        Assert.Contains("speed 3", server.Output, StringComparison.Ordinal);
        Assert.Contains("stopping after 5 ticks", server.Output, StringComparison.Ordinal);
        Assert.Contains("profiler off", server.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Turns_on_the_profiler_with_a_launch_option()
    {
        string install = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "server-" + Guid.NewGuid().ToString("N"));
        SampleGameCopy.Copy(Out("server"), install);

        using RunningGame server = SampleGameCopy.Start(Path.Combine(install, "Sample GameServer.exe"), "--profile");
        int exit = server.Exit(Timeout);

        Assert.True(exit == 0, server.Output);
        Assert.Contains("profiler on", server.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_start_when_an_assembly_changed_after_the_build()
    {
        string install = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "server-" + Guid.NewGuid().ToString("N"));
        SampleGameCopy.Copy(Out("server"), install);
        File.AppendAllText(Path.Combine(install, "game", "Example.Ticker.dll"), " ");
        File.AppendAllText(Path.Combine(install, "lib", "System.IO.Hashing.dll"), " ");

        using RunningGame server = SampleGameCopy.Start(Path.Combine(install, "Sample GameServer.exe"));
        int exit = server.Exit(Timeout);

        Assert.Equal(1, exit);
        Assert.Contains(Path.Combine(install, "game", "Example.Ticker.dll") + "' differs from the assembly the distribution was built with", server.Output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(install, "lib", "System.IO.Hashing.dll") + "' differs from the file the distribution was built with", server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stopping after", server.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_hot_reload_in_a_release_build()
    {
        using RunningGame client = SampleGameCopy.Start(Out("client/Sample Game.exe"), "--hot-reload");

        Assert.Equal(1, client.Exit(Timeout));
        Assert.Contains("--hot-reload needs a development build: nobara build --development.", client.Output, StringComparison.Ordinal);
    }
}

/// <summary>A development build of the server, whose tuning changes on disk while it runs.</summary>
public sealed class HotReloadEndToEndTests : IDisposable
{
    private readonly SampleGameCopy _game = new();

    public void Dispose() => _game.Dispose();

    private void WriteTuning(int speed, int stopAfter, string extra = "") => File.WriteAllText(_game.Path_("src/Example.Ticker/Resources/tuning.nof"), $"""
        <Nobara version="1.0.0" namespace="Example/Ticker">
          <Tuning><Name>Main</Name><Content><Speed>{speed}</Speed><StopAfterTicks>{stopAfter}</StopAfterTicks></Content></Tuning>
          {extra}
        </Nobara>
        """);

    [Fact]
    public void Applies_a_changed_resource_at_the_next_tick_and_signals_what_it_cannot_apply()
    {
        // Long enough for the change: the module stops as soon as its speed is 7.
        WriteTuning(3, 3000);
        CliResult build = _game.Run("build", "server", "--development");
        Assert.True(build.ExitCode == 0, build.ToString());
        Assert.False(File.Exists(_game.Path_("out/server.zip")));

        using RunningGame server = SampleGameCopy.Start(_game.Path_("out/server/Sample GameServer.exe"), "--hot-reload");
        server.WaitFor("Hot reload: watching the resources of 1 modules.", TimeSpan.FromMinutes(1));
        server.WaitFor("speed 3", TimeSpan.FromMinutes(1));

        WriteTuning(7, 3000, "<Tuning><Name>Spare</Name><Content><Speed>1</Speed></Content></Tuning>");
        int exit = server.Exit(TimeSpan.FromMinutes(1));

        Assert.True(exit == 0, server.Output);
        Assert.Contains("Hot reload: compiled 1 files of 'example.ticker'", server.Output, StringComparison.Ordinal);
        Assert.Contains("speed 7", server.Output, StringComparison.Ordinal);
        Assert.Contains("The change of 'tuning:example.ticker.spare' is not applied while the game runs: a new tuning needs a restart of the context", server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stopping after 3000 ticks", server.Output, StringComparison.Ordinal);
    }
}
