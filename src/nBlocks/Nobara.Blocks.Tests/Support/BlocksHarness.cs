// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Reflection;
using Google.Protobuf;
using Nobara.Blocks.Build;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Game;
using Nobara.Items;
using Nobara.Items.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Blocks.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real modules nGame, nItems,
/// and nBlocks, and test modules whose resources are compiled by the tests
/// and whose steps run the code a test gives them.
/// </summary>
internal sealed class BlocksHarness : IDisposable
{
    private readonly List<TestModule> _modules = [];

    public BlocksHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-blocks-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets what the modules did, in order.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Gets or sets whether the engine runs in development mode, which reloads resources.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets or sets the value that <c>engine.nobara</c> sets for <c>blocks.maxStatesPerType</c>, or <see langword="null"/>.</summary>
    public int? MaxStatesPerType { get; set; }

    /// <summary>Gets the nItems module, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Items { get; } = CompiledModule.FromAssembly(typeof(ItemsModule).Assembly.Location)!;

    /// <summary>Gets the nBlocks module, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Blocks { get; } = CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!;

    /// <summary>Wraps resource elements in a root element.</summary>
    public static string Nof(string ns, string body) => $"<Nobara version=\"1.0.0\" namespace=\"{ns}\">\n{body}\n</Nobara>";

    /// <summary>Wraps the content of one block named <c>Lamp</c> in a root element of namespace <c>Example/Blocks</c>.</summary>
    public static string Lamp(string content, string extra = "") => Nof("Example/Blocks", $"""
        <Block>
          <Name>Lamp</Name>
          <Content>
            {content}
          </Content>
        </Block>
        {extra}
        """);

    /// <summary>Compiles the resources of a module that depends on nItems and nBlocks.</summary>
    /// <param name="moduleId">The module.</param>
    /// <param name="nof">The content of its single <c>.nof</c> file, <c>blocks.nof</c>.</param>
    /// <returns>The result, with its diagnostics.</returns>
    public static CompilationResult Compile(string moduleId, string nof)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-blocks-tests", "nof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), nof);
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = moduleId,
                ResourcesDirectory = root,
                Dependencies = [Items, Blocks],
                Plugins = [new ItemPlugin(), new BlockPlugin()],
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

    /// <summary>Adds a business module that depends on nItems and nBlocks.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="nof">Its resources, or <see langword="null"/> for none.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="shortName">The short name of the module, or empty.</param>
    /// <param name="policy">The error policy of the module, or <see langword="null"/> for the default.</param>
    public BlocksHarness Module(string id, string? nof = null, Action<Probe>? behavior = null, string shortName = "", F.Policy? policy = null)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both, ShortName = shortName };
        foreach (string dependency in new[] { ItemsModule.ModuleId, BlocksModule.ModuleId })
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        _modules.Add(new TestModule(manifest, nof is null ? null : Bundle(id, nof).ToByteArray(), behavior, policy));
        return this;
    }

    /// <summary>Loads the engine: steps 0 and 1.</summary>
    public Engine Load(Side side = Side.Server)
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
        foreach (string id in new[] { "nobara.game", ItemsModule.ModuleId, BlocksModule.ModuleId }.Concat(_modules.Select(m => m.Manifest.Id)))
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach (TestModule module in _modules.Where(m => m.Policy is not null))
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = module.Manifest.Id, Policy = module.Policy!.Value });
        }

        if (MaxStatesPerType is { } value)
        {
            distribution.Settings.Add(new F.SettingValue { Key = BlocksModule.MaxStatesPerTypeKey, Operation = F.SettingValue.Types.Operation.Set, Value = value.ToString(CultureInfo.InvariantCulture) });
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
    public Engine Start(Side side = Side.Server)
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

    private static byte[] Embedded(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{assembly.GetName().Name} embeds no {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed record TestModule(F.ModuleManifest Manifest, byte[]? Resources, Action<Probe>? Behavior, F.Policy? Policy);

    private sealed class Source(BlocksHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.game" => Embedded(typeof(GameModule).Assembly, "nobara/module.pb"),
                ItemsModule.ModuleId => Embedded(typeof(ItemsModule).Assembly, "nobara/module.pb"),
                BlocksModule.ModuleId => Embedded(typeof(BlocksModule).Assembly, "nobara/module.pb"),
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
                case BlocksModule.ModuleId:
                    return new LoadedModule(typeof(BlocksModule).Assembly, () => new BlocksModule());
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

/// <summary>A test module whose steps run the code a test gives it, with the services of nBlocks at hand.</summary>
internal sealed class Probe(string id, List<string> journal) : IModule
{
    public string Id { get; } = id;

    public ModuleContext Context { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IBlockHooks Hooks { get; private set; } = null!;

    public IItemRegistry Items { get; private set; } = null!;

    public Action<Probe>? OnRegister { get; set; }

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public void Add(string line)
    {
        lock (journal)
        {
            journal.Add(line);
        }
    }

    public void Register(ModuleContext context)
    {
        Context = context;
        OnRegister?.Invoke(this);
    }

    public void Initialize(ModuleContext context)
    {
        Context = context;
        Blocks = context.Resolve<IBlockRegistry>();
        Hooks = context.Resolve<IBlockHooks>();
        Items = context.Resolve<IItemRegistry>();
        OnInitialize?.Invoke(this);
    }

    public void Start(ModuleContext context) => OnStart?.Invoke(this);
}

/// <summary>A subscriber of a batch hook that writes what it sees and does what the test asks.</summary>
internal sealed class BatchRecorder(List<string> calls, string name, Action<BlockBatchView>? act = null) : IBlockBatchHandler
{
    public void Handle(BlockBatchView batch)
    {
        var seen = new List<string>();
        for (int i = 0; i < batch.Count; i++)
        {
            if (!batch.IsRemoved(i))
            {
                BlockPosition p = batch[i].Position;
                seen.Add(p.X.ToString(CultureInfo.InvariantCulture));
            }
        }

        calls.Add($"{name}:{string.Join(",", seen)}");
        act?.Invoke(batch);
    }
}

/// <summary>A subscriber of a unit hook that writes what it sees and answers what the test asks.</summary>
internal sealed class UnitRecorder<TArgs>(List<string> calls, string name, bool answer = false, Action? act = null) : IBlockHandler<TArgs>
    where TArgs : struct
{
    public bool Handle(in TArgs args)
    {
        calls.Add(name);
        act?.Invoke();
        return answer;
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
