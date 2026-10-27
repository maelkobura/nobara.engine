// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Cli.Tests;

/// <summary>
/// Compositions that <c>nobara build</c> refuses before it compiles anything,
/// each with a message that names the module or the file at fault.
/// </summary>
public sealed class RefusalTests : IDisposable
{
    private readonly GameFolder _game = new();

    public RefusalTests()
    {
        // Only an empty local source: a package that a test asks for is never found.
        Directory.CreateDirectory(Path.Combine(_game.Root, "feed"));
        _game.Write("NuGet.config", "<configuration><packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources></configuration>");
        _game.Engine().Build();
    }

    public void Dispose() => _game.Dispose();

    private static string Depends(string id, bool optional = false) =>
        $"""<Dependencies><Module id="{id}" version="^1.0" optional="{(optional ? "true" : "false")}" /></Dependencies>""";

    private GameFolder Modules(params (string Id, string Body, string Level, string Side)[] modules)
    {
        _game.Project(string.Concat(modules.Select(m => $"""<Local path="src/{m.Id}" />""")));
        foreach ((string id, string body, string level, string side) in modules)
        {
            _game.Module($"src/{id}", id, body, level, side);
        }

        return _game;
    }

    private string Refused(string target, params string[] words)
    {
        CliResult result = _game.Run("build", target);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"error: target '{target}' was not built.", result.Errors, StringComparison.Ordinal);
        foreach (string word in words)
        {
            Assert.Contains(word, result.Errors, StringComparison.Ordinal);
        }

        Assert.False(_game.Exists($"out/{target}"));
        return result.Errors;
    }

    [Fact]
    public void Refuses_a_required_dependency_that_the_target_disables()
    {
        Modules(("example.mobs", "", "business", "both"), ("example.zombie", Depends("example.mobs"), "business", "both"));
        _game.Build(serverExtra: "<Disable module=\"example.mobs\" />");

        Refused("server", "error NRC201: module 'example.zombie' requires 'example.mobs', which is not active on the server side of target 'server'", Path.Combine(_game.Root, "src", "example.zombie", "module.nobara"));
    }

    [Fact]
    public void Refuses_a_required_dependency_that_no_source_provides()
    {
        Modules(("example.zombie", Depends("example.missing"), "business", "both"));

        // The resolution of the modules fails before any target is built; NuGet names the package.
        CliResult result = _game.Run("build", "client");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("example.missing", result.Output, StringComparison.Ordinal);
        Assert.Contains("error NCLI004: the module packages cannot be restored", result.Errors, StringComparison.Ordinal);
        Assert.Contains("error NCLI004: package 'example.missing' cannot be restored ('example.zombie' requires ^1.0)", result.Errors, StringComparison.Ordinal);
        Assert.False(_game.Exists("out/client"));
    }

    [Fact]
    public void Refuses_a_dependency_cycle()
    {
        Modules(("example.a", Depends("example.b"), "business", "both"), ("example.b", Depends("example.a"), "business", "both"));

        Refused("client", "error NRC204: the dependencies form a cycle", "example.a -> example.b -> example.a");
    }

    [Fact]
    public void Refuses_two_active_modules_in_conflict()
    {
        Modules(("example.a", "<Conflicts><Module id=\"example.b\" /></Conflicts>", "business", "both"), ("example.b", "", "business", "both"));

        Refused("client", "error NRC205: module 'example.a' conflicts with 'example.b'");
    }

    [Fact]
    public void Refuses_a_disputed_reservation()
    {
        string reserve = "<Reserves><Resource name=\"window.main\" /></Reserves>";
        Modules(("example.a", reserve, "business", "client"), ("example.b", reserve, "business", "client"));

        Refused("client", "error NRC206: modules 'example.a' and 'example.b' both reserve 'window.main'");
    }

    [Fact]
    public void Refuses_an_error_policy_that_the_cascade_would_defeat()
    {
        Modules(("example.stats", "", "business", "both"), ("example.hud", Depends("example.stats"), "business", "both"));
        _game.Engine("<ErrorPolicy default=\"stop\"><Module id=\"example.stats\" policy=\"disable\" /></ErrorPolicy>");

        Refused("client", "error NRC215: 'example.stats' has the error policy 'disable', but 'example.hud' requires it", Path.Combine(_game.Root, "engine.nobara"));
    }

    [Fact]
    public void Refuses_a_base_module_without_a_backend_on_the_side_of_the_target()
    {
        Modules(
            ("example.audio", "<RequiresBackend side=\"client\" />", "platform", "both"),
            ("example.audio.openal", Depends("example.audio") + "<BackendOf module=\"example.audio\" />", "platform", "client"));
        _game.Build(clientExtra: "<Disable module=\"example.audio.openal\" />");

        Refused("client", "error NRC211: module 'example.audio' requires a backend on the client side");
        Assert.Equal(0, _game.Run("generate").ExitCode);
    }

    [Fact]
    public void Refuses_an_initial_state_that_no_active_module_provides()
    {
        Modules(("example.menu", "<States><State id=\"state:example.menu\" side=\"client\" /></States>", "business", "both"));
        _game.Engine("<States client=\"state:example.menu\" server=\"state:example.boot\" />");

        Refused("server", "error NRC210: the initial server state 'state:example.boot' is not provided by an active module", Path.Combine(_game.Root, "engine.nobara"));
    }

    [Fact]
    public void Refuses_a_required_module_that_the_target_disables()
    {
        _game.Project("""<Local path="src/example.debug" required="true" />""").Module("src/example.debug", "example.debug");
        _game.Build(serverExtra: "<Disable module=\"example.debug\" />");

        Refused("server", "error NRC217: target 'server' disables the required module 'example.debug'", Path.Combine(_game.Root, "build.nobara"));
    }

    [Fact]
    public void Refuses_an_unknown_configuration_key_in_the_engine_file()
    {
        Modules(("example.mobs", "<Settings><Setting key=\"example.mobs.count\" scope=\"distribution\" type=\"int\" default=\"1\" /></Settings>", "business", "both"));
        _game.Engine("<Settings><Set key=\"example.mobs.cuont\" value=\"2\" /></Settings>");

        Refused("client", "error NRC212: <Set> names the unknown key 'example.mobs.cuont'", Path.Combine(_game.Root, "engine.nobara"));
    }

    [Fact]
    public void Refuses_two_modules_that_need_different_versions_of_a_library()
    {
        Modules(
            ("example.a", "<Libraries><Package id=\"Example.Compression\" version=\"[1.0.0]\" /></Libraries>", "business", "both"),
            ("example.b", "<Libraries><Package id=\"Example.Compression\" version=\"2.0.0\" /></Libraries>", "business", "both"));

        Refused("client", "error NCLI002: modules 'example.a' and 'example.b' need incompatible versions of library 'Example.Compression': [1.0.0] and 2.0.0", Path.Combine(_game.Root, "src", "example.b", "module.nobara"));
    }

    [Fact]
    public void Refuses_a_platform_that_this_version_does_not_build()
    {
        Modules(("example.mobs", "", "business", "both"));
        _game.Write("build.nobara", """<NobaraBuild format="1"><Target name="linux"><Platform>linux-x64</Platform><Side>server</Side></Target></NobaraBuild>""");

        Refused("linux", "error NCLI001: target 'linux' is for 'linux-x64'; this version builds 'win-x64' only", Path.Combine(_game.Root, "build.nobara"));
    }

    [Fact]
    public void Refuses_an_unknown_target()
    {
        Modules(("example.mobs", "", "business", "both"));

        CliResult result = _game.Run("build", "console");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("target 'console' is not declared; targets: client, server", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_pack_a_target_that_is_not_built()
    {
        Modules(("example.mobs", "", "business", "both"));

        CliResult result = _game.Run("package", "client");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("target 'client' is not built: run nobara build client first.", result.Errors, StringComparison.Ordinal);
    }
}
