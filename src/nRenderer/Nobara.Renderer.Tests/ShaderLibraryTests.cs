// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Nobara.Renderer.Build;

namespace Nobara.Renderer.Tests;

public sealed class ShaderLibraryTests
{
    [Fact]
    public void AnIncludeInsertsTheLibraryAndKeepsTheLinesThatFollow()
    {
        const string Source = "#version 460\n#include <nobara/renderer/chunk.glsl>\nvoid main()\n{\n    undefined_name = 1;\n}\n";
        using var compiler = new GlslCompiler();
        GlslResult result = compiler.Compile(Encoding.UTF8.GetBytes(Source), "test.vert", "vertex", []);

        Assert.Null(result.SpirV);
        GlslMessage error = Assert.Single(result.Messages, m => m.IsError && m.Text.Contains("undefined_name", StringComparison.Ordinal));
        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void AFileIsIncludedOnce()
    {
        byte[] once = GlslCompiler.ExpandIncludes(Encoding.UTF8.GetBytes("#include <nobara/renderer/chunk.glsl>\n"))!;
        byte[] twice = GlslCompiler.ExpandIncludes(Encoding.UTF8.GetBytes("#include <nobara/renderer/chunk.glsl>\n#include \"nobara/renderer/chunk.glsl\"\n"))!;
        Assert.Contains("nobara_decodeChunkVertex", Encoding.UTF8.GetString(once), StringComparison.Ordinal);
        Assert.Equal(1, CountOf(Encoding.UTF8.GetString(twice), "NobaraChunkVertex nobara_decodeChunkVertex"));
    }

    [Fact]
    public void AnIncludeOutsideTheLibraryIsAnErrorOnItsLine()
    {
        using var compiler = new GlslCompiler();
        GlslResult result = compiler.Compile(Encoding.UTF8.GetBytes("#version 460\n\n#include <common/lighting.glsl>\nvoid main() {}\n"), "test.frag", "fragment", []);

        Assert.Null(result.SpirV);
        GlslMessage error = Assert.Single(result.Messages);
        Assert.Equal(3, error.Line);
        Assert.Contains("common/lighting.glsl", error.Text, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
