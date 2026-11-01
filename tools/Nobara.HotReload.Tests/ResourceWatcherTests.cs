// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Resources;
using F = Nobara.Formats;
using Nobara.HotReload.Tests.Schemas;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;

namespace Nobara.HotReload.Tests;

/// <summary>A module whose <c>.nof</c> files change on disk while an engine runs.</summary>
public sealed class ResourceWatcherTests : IDisposable
{
    private const string ModuleId = "example.gadgets";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nobara-hotreload-tests", Guid.NewGuid().ToString("N"));
    private readonly MemoryLogSink _log = new();
    private readonly byte[] _schemas = DescriptorSet(GadgetsReflection.Descriptor);
    private readonly F.ModuleManifest _manifest = new() { Format = 1, Id = ModuleId, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };
    private readonly List<ResourceReload> _reloads = [];
    private int _power;
    private string _manual = string.Empty;

    public ResourceWatcherTests()
    {
        Directory.CreateDirectory(Resources);
        File.WriteAllText(ManualFile, "Plug it in.");
        Write(42);
    }

    private string Resources => Path.Combine(_root, "Resources");

    private string GadgetFile => Path.Combine(Resources, "gadgets.nof");

    private string ManualFile => Path.Combine(Resources, "manual.txt");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher may still hold the folder; it is disposable.
        }
    }

    private void Write(int power, string extra = "") => File.WriteAllText(GadgetFile, $"""
        <Nobara version="1.0.0" namespace="Example/Gadgets">
          <Gadget><Name>Lamp</Name><Content><Power>{power}</Power><Manual>manual.txt</Manual></Content></Gadget>
          {extra}
        </Nobara>
        """);

    private F.ResourceBundle CompileNow()
    {
        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions { ModuleId = ModuleId, ResourcesDirectory = Resources, Schemas = _schemas });
        CompilationResult result = compiler.Compile(Directory.EnumerateFiles(Resources, "*.nof"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Bundle!;
    }

    // An engine with one module that reads the power of the lamp, and reloads it in place.
    private Engine Load(F.ResourceBundle bundle)
    {
        var distribution = new F.DistributionManifest { Format = 1, Id = "example", Name = "Example", Version = "0.1.0", ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop } };
        distribution.Modules.Add(new F.DistributionModule { Id = ModuleId, Version = "1.0.0" });
        var data = new Dictionary<string, byte[]> { ["nobara/resources.pb"] = bundle.ToByteArray(), ["nobara/schemas.pb"] = _schemas, ["nobara/files/manual.txt"] = File.ReadAllBytes(ManualFile) };
        var source = new MemorySource(_manifest.ToByteArray(), () => new GadgetModule(this), data);
        return Engine.Load(new EngineOptions
        {
            Side = Side.Server,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = source,
            Paths = EnginePaths.At(Path.Combine(_root, "data")),
            DevelopmentMode = true,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [_log],
        });
    }

    // The installed module keeps the resources it was built with.
    private ResourceWatcher Watch(Engine engine, F.ResourceBundle installed) =>
        new(engine, [new WatchedModule(ModuleId, Resources, [])], id => id == ModuleId ? new CompiledModule(_manifest, _schemas, installed) : null)
        {
            Delay = TimeSpan.FromMilliseconds(50),
        };

    private void WaitForLog(string text)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(15);
        while (!_log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < limit, $"No log contains '{text}':{Environment.NewLine}{string.Join(Environment.NewLine, _log.Entries.Select(e => e.Message))}");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Applies_a_changed_file_at_the_next_frame()
    {
        F.ResourceBundle installed = CompileNow();
        using Engine engine = Load(installed);
        using ResourceWatcher watcher = Watch(engine, installed);
        engine.MainContext.Start();
        watcher.Start();
        Assert.Equal(42, _power);

        Write(7);
        WaitForLog("Hot reload: compiled 1 files of 'example.gadgets'");
        Assert.Equal(42, _power);
        engine.MainContext.RunFrame();

        Assert.Equal(7, _power);
        Assert.Equal(["gadget:example.gadgets.lamp"], Assert.Single(_reloads).Changed.Select(r => r.ObjectId));
    }

    [Fact]
    public void Reloads_a_changed_binary_file_with_the_resources_that_reference_it()
    {
        F.ResourceBundle installed = CompileNow();
        using Engine engine = Load(installed);
        using ResourceWatcher watcher = Watch(engine, installed);
        engine.MainContext.Start();
        watcher.Start();
        Assert.Equal("Plug it in.", _manual);

        File.WriteAllText(ManualFile, "Unplug it first.");
        WaitForLog("with 1 binary files");
        engine.MainContext.RunFrame();

        Assert.Equal("Unplug it first.", _manual);
        Assert.Equal(["gadget:example.gadgets.lamp"], Assert.Single(_reloads).Changed.Select(r => r.ObjectId));

        // A change of the .nof alone hands no binary file again.
        Write(8);
        Assert.True(watcher.Compile(ModuleId));
        engine.MainContext.RunFrame();
        Assert.Equal(8, _power);
        Assert.Equal("Unplug it first.", _manual);
        Assert.Contains(_log.Entries, e => e.Message.Contains("with 0 binary files", StringComparison.Ordinal));
    }

    [Fact]
    public void Signals_a_change_that_the_module_cannot_apply()
    {
        F.ResourceBundle installed = CompileNow();
        using Engine engine = Load(installed);
        using ResourceWatcher watcher = Watch(engine, installed);
        engine.MainContext.Start();

        Write(42, "<Gadget><Name>Fan</Name><Content><Power>3</Power></Content></Gadget>");
        Assert.True(watcher.Compile(ModuleId));
        engine.MainContext.RunFrame();

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message == "The change of 'gadget:example.gadgets.fan' is not applied while the game runs: a new gadget needs a restart");
    }

    [Fact]
    public void Logs_the_errors_of_a_file_and_keeps_the_previous_resources()
    {
        F.ResourceBundle installed = CompileNow();
        using Engine engine = Load(installed);
        using ResourceWatcher watcher = Watch(engine, installed);
        engine.MainContext.Start();

        File.WriteAllText(GadgetFile, """
            <Nobara version="1.0.0" namespace="Example/Gadgets">
              <Gadget><Name>Lamp</Name><Content><Power>many</Power></Content></Gadget>
            </Nobara>
            """);
        Assert.False(watcher.Compile(ModuleId));
        engine.MainContext.RunFrame();

        Assert.Equal(42, _power);
        Assert.Empty(_reloads);
        LogEntry error = _log.Entries.First(e => e.Level == LogLevel.Error);
        Assert.StartsWith("Hot reload: " + GadgetFile + "(2,", error.Message, StringComparison.Ordinal);
        Assert.Contains(_log.Entries, e => e.Message == "Hot reload: the resources of 'example.gadgets' do not compile; the game keeps the previous ones.");
    }

    [Fact]
    public void Reads_the_watched_modules_of_a_development_build()
    {
        string json = """{"modules":[{"id":"example.gadgets","resources":"C:/game/src/Example.Gadgets/Resources","plugins":["C:/game/src/Example.Base.Build/bin/Example.Base.Build.dll"]}]}""";

        WatchedModule module = Assert.Single(HotReloadHost.ReadMap(new MemoryStream(Encoding.UTF8.GetBytes(json))));

        Assert.Equal(("example.gadgets", "C:/game/src/Example.Gadgets/Resources"), (module.Id, module.ResourcesDirectory));
        Assert.Equal(["C:/game/src/Example.Base.Build/bin/Example.Base.Build.dll"], module.Plugins);
        Assert.True(HotReloadHost.IsRequested(["--hot-reload"]));
        Assert.False(HotReloadHost.IsRequested([]));
    }

    // The schemas and everything they import, each file once.
    private static byte[] DescriptorSet(FileDescriptor file)
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(FileDescriptor current)
        {
            foreach (FileDescriptor dependency in current.Dependencies)
            {
                Add(dependency);
            }

            if (seen.Add(current.Name))
            {
                set.File.Add(FileDescriptorProto.Parser.ParseFrom(current.SerializedData));
            }
        }

        Add(file);
        return set.ToByteArray();
    }

    private sealed class GadgetModule(ResourceWatcherTests test) : IModule
    {
        public void Register(ModuleContext context)
        {
            context.Resources.RegisterDecoder("gadget", data => Gadget.Parser.ParseFrom(data.Content.Span));
            context.Resources.RegisterReloadHandler("gadget", reload =>
            {
                test._reloads.Add(reload);
                foreach (ResourceData added in reload.Added)
                {
                    reload.Refuse(added.ObjectId, "a new gadget needs a restart");
                }

                Read(context);
            });
        }

        public void Initialize(ModuleContext context) => Read(context);

        private void Read(ModuleContext context)
        {
            Gadget lamp = context.Resources.Get<Gadget>("gadget:example.gadgets.lamp");
            test._power = lamp.Power;
            using var reader = new StreamReader(context.Resources.OpenFile(context.Resources.Find("gadget:example.gadgets.lamp")!, lamp.Manual));
            test._manual = reader.ReadToEnd();
        }
    }

    private sealed class MemorySource(byte[] manifest, Func<IModule> factory, Dictionary<string, byte[]> data) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module) => new(module.Id, "memory", manifest);

        public LoadedModule Load(ModuleImage image) =>
            new(null, factory, name => data.TryGetValue(name, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null);
    }
}
