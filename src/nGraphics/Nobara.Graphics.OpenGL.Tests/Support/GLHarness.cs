// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Frames;
using Nobara.Frames.SDL2;
using Silk.NET.Shaderc;
using F = Nobara.Formats;

namespace Nobara.Graphics.OpenGL.Tests.Support;

/// <summary>
/// An engine with the real window, the SDL2 backend, nGraphics, and the
/// OpenGL backend: it opens a window and an OpenGL 4.6 context.
/// </summary>
internal sealed class GLHarness : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nobara-opengl-tests", Guid.NewGuid().ToString("N"));

    public GLHarness()
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        distribution.Settings.Add(new F.SettingValue { Key = GraphicsSettings.VSync, Operation = F.SettingValue.Types.Operation.Default, Value = "off" });
        foreach (string id in new[] { "nobara.frames", "nobara.frames.sdl2", "nobara.graphics", "nobara.graphics.opengl", "example.game" })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(_root),
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
        });
        Engine.MainContext.Start();
    }

    public Engine Engine { get; }

    public Probe Game { get; private set; } = null!;

    public GraphicsDevice Device => Game.Context.Resolve<IGraphicsSystem>().Device;

    public GLDevice GL => (GLDevice)Device;

    public IWindowSystem Windows => Game.Context.Resolve<IWindowSystem>();

    public void Dispose()
    {
        Engine.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Source(GLHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Compiled(typeof(FramesModule)),
                "nobara.frames.sdl2" => Compiled(typeof(Sdl2Module)),
                "nobara.graphics" => Compiled(typeof(GraphicsModule)),
                "nobara.graphics.opengl" => Compiled(typeof(OpenGLModule)),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.frames" => new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule()),
            "nobara.frames.sdl2" => new LoadedModule(typeof(Sdl2Module).Assembly, () => new Sdl2Module()),
            "nobara.graphics" => new LoadedModule(typeof(GraphicsModule).Assembly, () => new GraphicsModule()),
            "nobara.graphics.opengl" => new LoadedModule(typeof(OpenGLModule).Assembly, () => new OpenGLModule()),
            _ => new LoadedModule(typeof(Probe).Assembly, () => harness.Game = new Probe()),
        };

        private static byte[] Compiled(Type module)
        {
            using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte[] GameManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "example.game", Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.graphics", Range = "^1.0" });
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
            return manifest.ToByteArray();
        }
    }
}

/// <summary>A module of the test assembly that keeps its context.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
}

/// <summary>Compiles GLSL to SPIR-V for OpenGL with shaderc, as the build of a game would.</summary>
internal static unsafe class SpirV
{
    public static byte[] Compile(ShaderStages stage, string glsl)
    {
        using Shaderc shaderc = Shaderc.GetApi();
        Compiler* compiler = shaderc.CompilerInitialize();
        CompileOptions* options = shaderc.CompileOptionsInitialize();
        try
        {
            shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Opengl, (uint)EnvVersion.Opengl45);
            ShaderKind kind = stage switch
            {
                ShaderStages.Vertex => ShaderKind.VertexShader,
                ShaderStages.Fragment => ShaderKind.FragmentShader,
                _ => ShaderKind.ComputeShader,
            };
            byte[] source = Encoding.UTF8.GetBytes(glsl);
            CompilationResult* result;
            fixed (byte* text = source)
            {
                result = shaderc.CompileIntoSpv(compiler, text, (nuint)source.Length, kind, "test.glsl", "main", options);
            }

            try
            {
                if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    throw new InvalidOperationException($"shaderc refused the {stage} shader: {shaderc.ResultGetErrorMessageS(result)}");
                }

                return new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), (int)shaderc.ResultGetLength(result)).ToArray();
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
        finally
        {
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }
    }

    public static Shader Load(GraphicsDevice device, ShaderStages stage, string glsl, string name) =>
        device.CreateShader(new ShaderDescription(stage, Compile(stage, glsl)) { Name = name });
}

/// <summary>SDL keeps one state per process: the tests that open a window run one at a time.</summary>
[CollectionDefinition(nameof(GpuCollection), DisableParallelization = true)]
public sealed class GpuCollection
{
}
