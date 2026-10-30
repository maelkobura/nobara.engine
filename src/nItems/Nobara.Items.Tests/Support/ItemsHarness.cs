// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.Items.Tests.Schemas;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler.Plugins;
using F = Nobara.Formats;

namespace Nobara.Items.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real modules nGame and nItems,
/// a module of art types that items reference, and test modules whose
/// resources are compiled by the tests and whose steps run the code a test
/// gives them.
/// </summary>
internal sealed class ItemsHarness : IDisposable
{
    public const string Art = "example.art";

    private readonly List<TestModule> _modules = [];

    public ItemsHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-items-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders and of the compilations.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets what the modules did, in order.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Gets or sets whether the engine runs in development mode, which reloads resources.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets or sets the value that <c>engine.nobara</c> sets for <c>items.defaultMaxStack</c>, or <see langword="null"/>.</summary>
    public int? DefaultMaxStack { get; set; }

    /// <summary>Gets the nItems module, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Items { get; } = CompiledModule.FromAssembly(typeof(ItemsModule).Assembly.Location)!;

    /// <summary>Gets the art module: meshes and textures, and the type Gem whose plugin derives items.</summary>
    public static CompiledModule ArtModule { get; } = CompileArt();

    /// <summary>Gets the schemas of the art module.</summary>
    public static byte[] ArtSchemas { get; } = DescriptorSet(ArtReflection.Descriptor);

    /// <summary>Wraps resource elements in a root element.</summary>
    public static string Nof(string ns, string body) => $"<Nobara version=\"1.0.0\" namespace=\"{ns}\">\n{body}\n</Nobara>";

    /// <summary>Compiles the resources of a module that depends on nItems and on the art module.</summary>
    /// <param name="moduleId">The module.</param>
    /// <param name="nof">The content of its single <c>.nof</c> file, <c>food.nof</c>.</param>
    /// <returns>The result, with its diagnostics.</returns>
    public static CompilationResult Compile(string moduleId, string nof)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-items-tests", "nof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "food.nof"), nof);
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = moduleId,
                ResourcesDirectory = root,
                Dependencies = [Items, ArtModule],
                Plugins = [new ItemPlugin(), new GemPlugin()],
            });
            return compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Compiles resources that must compile.</summary>
    public static F.ResourceBundle Bundle(string moduleId, string nof)
    {
        CompilationResult result = Compile(moduleId, nof);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Bundle!;
    }

    /// <summary>Adds a business module that depends on nItems, nGame, and the art module.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="nof">Its resources, or <see langword="null"/> for none.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="policy">The error policy of the module, or <see langword="null"/> for the default.</param>
    /// <param name="side">The sides of the module.</param>
    /// <param name="dependsOn">Other modules of the test it depends on.</param>
    public ItemsHarness Module(string id, string? nof = null, Action<Probe>? behavior = null, F.Policy? policy = null, F.Side side = F.Side.Both, params string[] dependsOn)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = side };
        foreach (string dependency in new[] { "nobara.game", ItemsModule.ModuleId, Art }.Concat(dependsOn))
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        _modules.Add(new TestModule(manifest, nof is null ? null : Bundle(id, nof).ToByteArray(), behavior, policy));
        return this;
    }

    /// <summary>Loads the engine: steps 0 and 1.</summary>
    public Engine Load(Side side = Side.Client)
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = StandardLoop(),
        };
        foreach (string id in new[] { "nobara.game", ItemsModule.ModuleId, Art }.Concat(_modules.Select(m => m.Manifest.Id)))
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach (TestModule module in _modules.Where(m => m.Policy is not null))
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = module.Manifest.Id, Policy = module.Policy!.Value });
        }

        if (DefaultMaxStack is { } value)
        {
            distribution.Settings.Add(new F.SettingValue { Key = ItemsModule.DefaultMaxStackKey, Operation = F.SettingValue.Types.Operation.Set, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        return Engine.Load(new EngineOptions
        {
            Side = side,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            DevelopmentMode = DevelopmentMode,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
    }

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start(Side side = Side.Client)
    {
        Engine engine = Load(side);
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs frames that each last one tick at 30 ticks per second.</summary>
    public void Tick(Engine engine, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            engine.MainContext.RunFrame();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static F.Loop StandardLoop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    private static CompiledModule CompileArt()
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-items-tests", "art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "art.nof"), Nof("Example/Art", """
                <Mesh><Name>Sword</Name><Content><File>sword.glb</File></Content></Mesh>
                <Texture><Name>Steak</Name><Content><File>steak.png</File></Content></Texture>
                """));
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = Art,
                ResourcesDirectory = root,
                Schemas = DescriptorSet(ArtReflection.Descriptor),
            });
            CompilationResult result = compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return new CompiledModule(ArtManifest(), DescriptorSet(ArtReflection.Descriptor), result.Bundle!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static F.ModuleManifest ArtManifest() =>
        new() { Format = 1, Id = Art, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };

    private static byte[] DescriptorSet(params FileDescriptor[] files)
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(FileDescriptor file)
        {
            foreach (FileDescriptor dependency in file.Dependencies)
            {
                Add(dependency);
            }

            if (seen.Add(file.Name))
            {
                set.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
            }
        }

        foreach (FileDescriptor file in files)
        {
            Add(file);
        }

        return set.ToByteArray();
    }

    private static byte[] Embedded(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{assembly.GetName().Name} embeds no {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed record TestModule(F.ModuleManifest Manifest, byte[]? Resources, Action<Probe>? Behavior, F.Policy? Policy);

    private sealed class Source(ItemsHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.game" => Embedded(typeof(GameModule).Assembly, "nobara/module.pb"),
                ItemsModule.ModuleId => Embedded(typeof(ItemsModule).Assembly, "nobara/module.pb"),
                Art => ArtManifest().ToByteArray(),
                _ => harness._modules.Single(p => p.Manifest.Id == module.Id).Manifest.ToByteArray(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.game":
                    return new LoadedModule(typeof(GameModule).Assembly, () => new GameModule());
                case ItemsModule.ModuleId:
                    return new LoadedModule(typeof(ItemsModule).Assembly, () => new ItemsModule());
                case Art:
                    byte[] schemas = ArtSchemas;
                    byte[] resources = ArtModule.Resources.ToByteArray();
                    return new LoadedModule(null, null, name => name switch
                    {
                        "nobara/schemas.pb" => new MemoryStream(schemas),
                        "nobara/resources.pb" => new MemoryStream(resources),
                        _ => null,
                    });
            }

            // Every test module runs code of the test assembly; only a module with an error policy of
            // its own owns it, so that the core charges the exceptions of the test code to that module.
            TestModule test = harness._modules.Single(p => p.Manifest.Id == image.Id);
            return new LoadedModule(
                test.Policy is null ? null : typeof(Probe).Assembly,
                () =>
                {
                    var probe = new Probe(image.Id, harness.Journal);
                    test.Behavior?.Invoke(probe);
                    return probe;
                },
                name => name == "nobara/resources.pb" && test.Resources is not null ? new MemoryStream(test.Resources) : null);
        }
    }
}

/// <summary>A test module whose steps run the code a test gives it, with the services of nItems at hand.</summary>
internal sealed class Probe(string id, List<string> journal) : IModule
{
    public string Id { get; } = id;

    public ModuleContext Context { get; private set; } = null!;

    public IItemRegistry Registry { get; private set; } = null!;

    public IItemUsage Usage { get; private set; } = null!;

    public IGameTime Time { get; private set; } = null!;

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public void Add(string line)
    {
        lock (journal)
        {
            journal.Add(line);
        }
    }

    public void Initialize(ModuleContext context)
    {
        Context = context;
        Registry = context.Resolve<IItemRegistry>();
        Usage = context.Resolve<IItemUsage>();
        Time = context.Resolve<IGameTime>();
        OnInitialize?.Invoke(this);
    }

    public void Start(ModuleContext context) => OnStart?.Invoke(this);
}

/// <summary>The plugin of the test type Gem: a gem with <c>&lt;CreateItem/&gt;</c> derives an item that has a socket component.</summary>
public sealed class GemPlugin : ResourcePlugin
{
    public override IReadOnlyCollection<string> ResourceTypes { get; } = ["Gem"];

    public override IEnumerable<DerivedResource> Derive(ResourceContext context)
    {
        Gem gem = context.Parse(Gem.Parser);
        if (gem.CreateItem)
        {
            yield return DerivedItem.Create(
                maxStack: 8,
                components: [DerivedItem.Component("example.gems.socket", new System.Xml.Linq.XElement("Hardness", gem.Hardness))]);
        }
    }
}

/// <summary>A clock that moves only when the test advances it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}

/// <summary>Keeps the messages logged by the engine.</summary>
internal sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(in LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public void Flush()
    {
    }
}
