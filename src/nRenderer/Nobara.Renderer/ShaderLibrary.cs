// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Renderer;

/// <summary>
/// The GLSL files that shaders include with
/// <c>#include &lt;nobara/renderer/name.glsl&gt;</c>, embedded in the assembly
/// of nRenderer. The plugin of nRenderer inserts them when it compiles a
/// shader.
/// </summary>
internal static class ShaderLibrary
{
    /// <summary>The prefix of the paths of the files: <c>nobara/renderer/</c>.</summary>
    public const string PathPrefix = "nobara/renderer/";

    private const string ResourcePrefix = "nobara.renderer.include.";

    /// <summary>Reads a file of the library.</summary>
    /// <param name="path">The path, such as <c>nobara/renderer/chunk.glsl</c>.</param>
    /// <returns>The text, or <see langword="null"/> when the library has no such file.</returns>
    public static string? Read(string path)
    {
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal) || path.AsSpan(PathPrefix.Length).IndexOfAny('/', '\\') >= 0)
        {
            return null;
        }

        using Stream? stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(ResourcePrefix + path[PathPrefix.Length..]);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
