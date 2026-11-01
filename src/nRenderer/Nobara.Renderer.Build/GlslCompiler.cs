// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Core.Contexts;
using Silk.NET.Shaderc;

namespace Nobara.Renderer.Build;

/// <summary>A message of shaderc about a GLSL source.</summary>
/// <param name="IsError">Whether it is an error.</param>
/// <param name="Line">The line of the source, from 1, or 0 when shaderc gives none.</param>
/// <param name="Text">The message.</param>
internal sealed record GlslMessage(bool IsError, int Line, string Text);

/// <summary>The outcome of a compilation: the SPIR-V, or <see langword="null"/> on error, and the messages.</summary>
internal sealed record GlslResult(byte[]? SpirV, IReadOnlyList<GlslMessage> Messages);

/// <summary>Compiles GLSL to SPIR-V for OpenGL with shaderc.</summary>
/// <remarks>
/// The native library of shaderc is loaded from the folder of the plugin, or
/// from <c>runtimes/&lt;rid&gt;/native/</c> in it, where the build copies it.
/// </remarks>
internal sealed unsafe partial class GlslCompiler : IDisposable
{
    private readonly Shaderc _shaderc;
    private readonly Compiler* _compiler;

    public GlslCompiler()
    {
        _shaderc = new Shaderc(new LibraryContext(LoadLibrary()));
        _compiler = _shaderc.CompilerInitialize();
    }

    public GlslResult Compile(byte[] source, string name, string stage, IReadOnlyList<string> defines)
    {
        CompileOptions* options = _shaderc.CompileOptionsInitialize();
        try
        {
            _shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Opengl, (uint)EnvVersion.Opengl45);
            _shaderc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            foreach (string define in defines)
            {
                int equals = define.IndexOf('=', StringComparison.Ordinal);
                byte[] macro = Encoding.UTF8.GetBytes(equals < 0 ? define : define[..equals]);
                byte[] value = Encoding.UTF8.GetBytes(equals < 0 ? "1" : define[(equals + 1)..]);
                fixed (byte* m = macro)
                fixed (byte* v = value)
                {
                    _shaderc.CompileOptionsAddMacroDefinition(options, m, (nuint)macro.Length, v, (nuint)value.Length);
                }
            }

            ShaderKind kind = stage switch
            {
                "vertex" => ShaderKind.VertexShader,
                "fragment" => ShaderKind.FragmentShader,
                _ => ShaderKind.ComputeShader,
            };
            CompilationResult* result;
            fixed (byte* text = source)
            {
                result = _shaderc.CompileIntoSpv(_compiler, text, (nuint)source.Length, kind, name, "main", options);
            }

            try
            {
                List<GlslMessage> messages = Parse(_shaderc.ResultGetErrorMessageS(result) ?? string.Empty, name);
                if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    if (!messages.Any(m => m.IsError))
                    {
                        messages.Add(new GlslMessage(true, 0, $"shaderc cannot compile the {stage} stage ({_shaderc.ResultGetCompilationStatus(result)})"));
                    }

                    return new GlslResult(null, messages);
                }

                return new GlslResult(new ReadOnlySpan<byte>(_shaderc.ResultGetBytes(result), (int)_shaderc.ResultGetLength(result)).ToArray(), messages);
            }
            finally
            {
                _shaderc.ResultRelease(result);
            }
        }
        finally
        {
            _shaderc.CompileOptionsRelease(options);
        }
    }

    public void Dispose()
    {
        _shaderc.CompilerRelease(_compiler);
        _shaderc.Dispose();
    }

    // Lines such as "Shaders/lit.frag:12: error: 'x' : undeclared identifier".
    internal static List<GlslMessage> Parse(string log, string name)
    {
        var messages = new List<GlslMessage>();
        foreach (string raw in log.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || ErrorCount().IsMatch(line))
            {
                continue;
            }

            Match match = Located().Match(line);
            if (match.Success && match.Groups["file"].Value == name)
            {
                messages.Add(new GlslMessage(match.Groups["kind"].Value == "error", int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture), match.Groups["text"].Value.Trim()));
                continue;
            }

            match = Unlocated().Match(line);
            messages.Add(match.Success
                ? new GlslMessage(match.Groups["kind"].Value == "error", 0, match.Groups["text"].Value.Trim())
                : new GlslMessage(true, 0, line.Trim()));
        }

        return messages;
    }

    private static nint LoadLibrary()
    {
        // A plugin loaded by the compiler finds it through its load context;
        // a plugin loaded in the default context, such as in tests, next to its assembly.
        if (NativeLibrary.TryLoad("shaderc_shared", typeof(GlslCompiler).Assembly, null, out nint handle))
        {
            return handle;
        }

        string directory = AppContext.BaseDirectory;
        string file = OperatingSystem.IsWindows() ? "shaderc_shared.dll" : "libshaderc_shared.so";
        foreach (string path in new[] { Path.Combine(directory, file), Path.Combine(directory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file) })
        {
            if (NativeLibrary.TryLoad(path, out handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException("The native library of shaderc (shaderc_shared) is not next to the renderer plugin: build the plugin again.");
    }

    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+): (?<kind>error|warning): (?<text>.*)$")]
    private static partial Regex Located();

    [GeneratedRegex(@"^(?:(?<file>.+?): )?(?<kind>error|warning): (?<text>.*)$")]
    private static partial Regex Unlocated();

    [GeneratedRegex(@"^\d+ (?:errors?|warnings?)(?: and \d+ (?:errors?|warnings?))? generated\.$")]
    private static partial Regex ErrorCount();

    // Gives shaderc the functions of a library loaded by handle.
    private sealed class LibraryContext(nint handle) : INativeContext
    {
        public nint GetProcAddress(string proc, int? slot = null) =>
            TryGetProcAddress(proc, out nint address, slot) ? address : throw new EntryPointNotFoundException($"shaderc has no function '{proc}'.");

        public bool TryGetProcAddress(string proc, out nint address, int? slot = null) => NativeLibrary.TryGetExport(handle, proc, out address);

        public void Dispose()
        {
            // The library stays loaded for the life of the process, as shaderc expects.
        }
    }
}
