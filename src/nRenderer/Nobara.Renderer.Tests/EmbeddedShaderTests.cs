// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Renderer.Build;

namespace Nobara.Renderer.Tests;

public sealed class EmbeddedShaderTests
{
    // Set to 1 to write the SPIR-V of the sources next to them, after changing a shader of nRenderer; build again to embed it.
    private const string UpdateVariable = "NOBARA_UPDATE_RENDERER_SHADERS";

    [Theory]
    [InlineData("debug-draw.vert", "vertex")]
    [InlineData("debug-draw.frag", "fragment")]
    public void TheEmbeddedSpirVIsTheSourceCompiled(string file, string stage)
    {
        string source = Path.Combine(SourceFolder(), file);
        using var compiler = new GlslCompiler();
        GlslResult result = compiler.Compile(File.ReadAllBytes(source), file, stage, []);
        Assert.True(result.SpirV is not null, string.Join(Environment.NewLine, result.Messages));

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllBytes(source + ".spv", result.SpirV);
        }

        Assert.True(
            result.SpirV.AsSpan().SequenceEqual(EmbeddedShaders.Read(file + ".spv")),
            $"The embedded SPIR-V of '{file}' is not its source compiled: run the tests with {UpdateVariable}=1, then build again.");
    }

    private static string SourceFolder()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "src", "nRenderer", "Nobara.Renderer", "Shaders");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The shaders of nRenderer are not found above the folder of the tests.");
    }
}
