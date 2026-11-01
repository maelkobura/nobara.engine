// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Nobara.Graphics.OpenGL.Tests.Support;
using Xunit.Abstractions;

namespace Nobara.Graphics.OpenGL.Tests;

/// <summary>
/// The SPIR-V bench: representative SPIR-V shaders run on the driver of the
/// machine and compared with the image or the values they must give. It
/// writes a report to <c>artifacts/spirv-bench/</c> at the root of the
/// repository, to compare drivers.
/// </summary>
/// <remarks>Run it with <c>dotnet test src/nGraphics/Nobara.Graphics.OpenGL.Tests --filter "FullyQualifiedName~SpirVBench"</c>.</remarks>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class SpirVBenchTests(ITestOutputHelper output)
{
    private static readonly (string Name, string Covers, Action<GraphicsDevice> Run)[] Cases =
    [
        ("Textured triangle", "vertex and fragment shaders, vertex and index buffers, uniform buffer, sampled 2D texture", Scenes.TexturedTriangle),
        ("Texture array", "sampler2DArray, one layer per stripe", Scenes.TextureArray),
        ("3D texture", "sampler3D, one slice per stripe", Scenes.Texture3D),
        ("Storage buffer in a fragment shader", "read-only std430 buffer", Scenes.StorageBufferInFragment),
        ("Compute shader writing a storage buffer", "compute shader, read-write std430 buffer, uniform buffer, read-back", Scenes.ComputeStorageBuffer),
        ("Compute shader writing a storage texture", "compute shader, rgba8 image2D, read-back", Scenes.ComputeStorageTexture),
        ("Indirect draws and dispatch", "draw, indexed draw, and dispatch arguments read from a buffer, gl_BaseInstance", Scenes.Indirect),
    ];

    [Fact]
    public void Runs_every_case_on_the_driver_and_writes_the_report()
    {
        using var harness = new GLHarness();
        GraphicsDevice device = harness.Device;
        var report = new StringBuilder();
        report.AppendLine("# SPIR-V bench").AppendLine();
        report.AppendLine($"- GPU: {device.Info.Renderer}");
        report.AppendLine($"- Vendor: {device.Info.Vendor}");
        report.AppendLine($"- Driver: {device.Info.Version}");
        report.AppendLine($"- System: {Environment.OSVersion.VersionString}");
        report.AppendLine($"- Date: {DateTimeOffset.UtcNow:yyyy-MM-dd}").AppendLine();
        report.AppendLine("| Case | Covers | Result |").AppendLine("| --- | --- | --- |");

        var failures = new List<string>();
        foreach ((string name, string covers, Action<GraphicsDevice> run) in Cases)
        {
            string result;
            try
            {
                run(device);
                result = "pass";
            }
            catch (Exception exception)
            {
                result = $"FAIL: {exception.Message.ReplaceLineEndings(" ")}";
                failures.Add($"{name}: {exception.Message}");
            }

            report.AppendLine($"| {name} | {covers} | {result} |");
        }

        string text = report.ToString();
        output.WriteLine(text);
        string folder = Path.Combine(RepositoryRoot(), "artifacts", "spirv-bench");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"{Sanitize(device.Info.Renderer)}.md");
        File.WriteAllText(file, text);
        output.WriteLine($"Report written to {file}");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Nobara.slnx")))
            {
                return folder.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder();
        foreach (char c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return builder.ToString().Trim('-');
    }
}
