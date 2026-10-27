// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Cli.Tests;

/// <summary><c>nobara docs</c>: the generated sections of the module sheets.</summary>
public sealed class DocsTests : IDisposable
{
    private const string Sheet = """
        # Voxel

        ## Level

        <!-- nobara:generated:begin level -->
        old
        <!-- nobara:generated:end -->

        ## Dependencies

        <!-- nobara:generated:begin dependencies -->
        <!-- nobara:generated:end -->

        Written by hand.

        ## Entry points

        <!-- nobara:generated:begin states -->
        <!-- nobara:generated:end -->

        ## Configuration

        <!-- nobara:generated:begin configuration -->
        <!-- nobara:generated:end -->

        ## Renderer

        <!-- nobara:generated:begin level example.voxel.renderer -->
        <!-- nobara:generated:end -->

        """;

    private readonly GameFolder _repository = new();

    public DocsTests()
    {
        _repository.Module("src/Voxel/Example.Voxel", "example.voxel", """
            <ShortName>Voxel</ShortName>
            <Dependencies>
              <Module id="example.blocks" version="^1.0" />
              <Module id="example.lighting" version="^1.2" optional="true" />
            </Dependencies>
            <Libraries><Package id="Example.Compression" version="1.3.8" /></Libraries>
            <States><State id="state:example.voxel.debug" side="client" /></States>
            <Settings>
              <Setting key="voxel.viewRadius" scope="user" type="int" min="32" max="1024" default="256" side="client" />
              <Setting key="voxel.mode" scope="distribution" type="enum" values="fast,exact" default="fast" />
            </Settings>
            <RequiresBackend side="client" />
            """, level: "platform");
        _repository.Module("src/Voxel/Example.Voxel.Renderer", "example.voxel.renderer", """<Dependencies><Module id="example.voxel" version="^1.0" /></Dependencies><BackendOf module="example.voxel" />""", level: "platform", side: "client");
        _repository.Write("src/Voxel/README.md", Sheet);
    }

    public void Dispose() => _repository.Dispose();

    [Fact]
    public void Writes_the_sections_from_the_manifest_of_the_main_module()
    {
        CliResult result = _repository.Run("docs");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal("""
            # Voxel

            ## Level

            <!-- nobara:generated:begin level -->
            Platform module, on the client and on the server. Requires an active backend on the client.
            <!-- nobara:generated:end -->

            ## Dependencies

            <!-- nobara:generated:begin dependencies -->
            | Module | Versions | Kind |
            | --- | --- | --- |
            | `example.blocks` | `^1.0` | Required |
            | `example.lighting` | `^1.2` | Optional |

            Third-party libraries: `Example.Compression` 1.3.8.
            <!-- nobara:generated:end -->

            Written by hand.

            ## Entry points

            <!-- nobara:generated:begin states -->
            States provided: `state:example.voxel.debug` (client).
            <!-- nobara:generated:end -->

            ## Configuration

            <!-- nobara:generated:begin configuration -->
            | Key | Scope | Type | Default | Values | Side |
            | --- | --- | --- | --- | --- | --- |
            | `voxel.mode` | distribution | enum | `fast` | `fast`, `exact` | both |
            | `voxel.viewRadius` | user | int | `256` | 32 to 1024 | client |
            <!-- nobara:generated:end -->

            ## Renderer

            <!-- nobara:generated:begin level example.voxel.renderer -->
            Platform module, on the client. Backend of `example.voxel`.
            <!-- nobara:generated:end -->

            """.ReplaceLineEndings("\n"), _repository.Read("src/Voxel/README.md"));
        Assert.Contains("Updated the generated sections of 1 module sheets.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_a_sheet_that_is_out_of_date_without_changing_it()
    {
        CliResult stale = _repository.Run("docs", "--check");

        Assert.Equal(1, stale.ExitCode);
        Assert.Contains($"{Path.Combine(_repository.Root, "src", "Voxel", "README.md")} is out of date", stale.Errors, StringComparison.Ordinal);
        Assert.Equal(Sheet.ReplaceLineEndings("\n"), _repository.Read("src/Voxel/README.md").ReplaceLineEndings("\n"));

        Assert.Equal(0, _repository.Run("docs").ExitCode);
        Assert.Equal(0, _repository.Run("docs", "--check").ExitCode);
    }

    [Fact]
    public void Refuses_an_unknown_section_or_module()
    {
        _repository.Write("src/Voxel/README.md", "<!-- nobara:generated:begin events -->\n<!-- nobara:generated:end -->\n<!-- nobara:generated:begin level example.other -->\n<!-- nobara:generated:end -->\n");

        CliResult result = _repository.Run("docs");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the section 'events' is unknown", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Describes_the_core_which_is_not_a_module()
    {
        _repository.Write("src/Core/Nobara.Core/Nobara.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _repository.Write("src/Core/README.md", "<!-- nobara:generated:begin dependencies -->\n<!-- nobara:generated:end -->\n");

        Assert.Equal(0, _repository.Run("docs").ExitCode);

        Assert.Equal("<!-- nobara:generated:begin dependencies -->\nNone. The core depends on no module.\n<!-- nobara:generated:end -->\n", _repository.Read("src/Core/README.md"));
    }
}
