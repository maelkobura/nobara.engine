// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Google.Protobuf;
using Nobara.Audio;
using Nobara.Audio.Build;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Items;
using Nobara.Items.Build;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler;
using F = Nobara.Formats;

namespace Nobara.Voxel.Sound.Tests.Support;

/// <summary>
/// A client engine with nVoxel, nAudio on a backend that plays nothing,
/// nVoxel.Sound, and a test game: blocks with sounds, and two grids besides
/// the terrain, one at the origin and one moved and turned. The listener
/// stands at the origin.
/// </summary>
internal sealed class SoundHarness : IDisposable
{
    public const string Game = "example.world";
    public const string PlayState = "state:example.world.play";

    private static readonly string[] ModuleIds =
    [
        "nobara.game", "nobara.context", "nobara.entity", "nobara.items", "nobara.blocks", "nobara.voxel",
        AudioModule.ModuleId, "example.audio", VoxelSoundModule.ModuleId, Game,
    ];

    private readonly List<string> _folders = [];

    public SoundHarness()
    {
        Root = Folder("data");
    }

    public string Root { get; }

    /// <summary>Gets or sets the blocks of the test game, in place of those of <c>Resources/blocks.nof</c>.</summary>
    public string? Blocks { get; set; }

    /// <summary>Gets or sets whether the distribution has nVoxel.Sound; it does by default.</summary>
    public bool WithSound { get; set; } = true;

    /// <summary>Gets or sets whether the distribution has nAudio and a backend; they are there by default.</summary>
    public bool WithAudio { get; set; } = true;

    public MemoryLogSink Log { get; } = new();

    public ManualClock Clock { get; } = new();

    public TestAudioBackend Backend { get; } = new("test");

    public WorldProbe Probe { get; private set; } = null!;

    public Engine Engine { get; private set; } = null!;

    public TestAudioDevice Device => Backend.Device!;

    public IAudioSystem Audio => Probe.Context.Resolve<IAudioSystem>();

    /// <summary>Gets how many sounds started on the device since it opened.</summary>
    public int Plays => Device.Voices.Sum(v => v.Plays);

    public Grid Terrain
    {
        get
        {
            Assert.True(Probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
            return terrain!;
        }
    }

    public Engine Start()
    {
        (byte[] bundle, Dictionary<string, byte[]> files) = Compile();
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = Loop(),
            States = new F.InitialStates { Client = PlayState },
        };
        foreach (string id in ModuleIds.Where(id => (WithAudio || id is not (AudioModule.ModuleId or "example.audio" or VoxelSoundModule.ModuleId)) && (WithSound || id != VoxelSoundModule.ModuleId)))
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this, bundle, files),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        try
        {
            Engine.MainContext.Start();
        }
        catch
        {
            Engine.Dispose();
            throw;
        }

        if (WithAudio)
        {
            Probe.Context.Resolve<ISoundRegistry>().CompleteDecoding();
        }

        // The session starts on the first frames; the sounds of the blocks are heard from the origin.
        Frames(3);
        Assert.Equal(SessionState.Active, Probe.Sessions.State);
        return Engine;
    }

    /// <summary>Runs frames of one tick each.</summary>
    public void Frames(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            Engine.MainContext.RunFrame();
        }
    }

    public uint State(string block) => Probe.Blocks.Get("block:example.world." + block).DefaultState;

    /// <summary>
    /// Runs frames and gives the voices of the device that started in them, by
    /// the order of their slot: their sound still plays when the frames end.
    /// </summary>
    public List<TestVoice> Started(Action action, int frames = 1)
    {
        int[] before = [.. Device.Voices.Select(v => v.Plays)];
        action();
        Frames(frames);
        return [.. Device.Voices.Where((v, i) => v.Plays > before[i])];
    }

    /// <summary>Gets the state of the lamp, lit or not.</summary>
    public uint Lamp(bool lit)
    {
        BlockType type = Probe.Blocks.Get("block:example.world.lamp");
        return Probe.Blocks.With(type.DefaultState, type.GetProperty("lit"), lit ? 1 : 0);
    }

    /// <summary>Places blocks with a hand batch, applied at the next tick.</summary>
    public VoxelBatchHandle Place(Grid grid, params (BlockCoordinate Position, uint State)[] blocks) =>
        Probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [.. blocks.Select(b => new BlockPlacement(b.Position, b.State))]);

    /// <summary>Breaks blocks with a hand batch, applied at the next tick.</summary>
    public VoxelBatchHandle Break(Grid grid, params BlockCoordinate[] positions) =>
        Probe.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Hand, positions);

    public void Dispose()
    {
        Engine?.Dispose();
        foreach (string folder in _folders.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file still open; the temporary folder is left behind.
            }
        }
    }

    private static F.Loop Loop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        loop.Phases.Add(new F.Phase { Name = "Render" });
        return loop;
    }

    private (byte[] Bundle, Dictionary<string, byte[]> Files) Compile()
    {
        string resources = Folder("resources");
        string source = Path.Combine(AppContext.BaseDirectory, "Resources");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(resources, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        if (!WithAudio)
        {
            File.Delete(Path.Combine(resources, "sounds.nof"));
        }

        if (Blocks is not null)
        {
            File.WriteAllText(Path.Combine(resources, "blocks.nof"), $"""<Nobara version="1.0.0" namespace="Example/World">{Blocks}</Nobara>""");
        }

        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = Game,
            ResourcesDirectory = resources,
            Dependencies =
            [
                CompiledModule.FromAssembly(typeof(ItemsModule).Assembly.Location)!,
                CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!,
                CompiledModule.FromAssembly(typeof(AudioModule).Assembly.Location)!,
                CompiledModule.FromAssembly(typeof(VoxelSoundModule).Assembly.Location)!,
            ],
            Plugins = [new ItemPlugin(), new BlockPlugin(), new AudioPlugin()],
        });
        CompilationResult result = compiler.Compile(Directory.EnumerateFiles(resources, "*.nof"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (result.Bundle!.ToByteArray(), result.Files.ToDictionary(f => f.Path, f => f.Content?.ToArray() ?? File.ReadAllBytes(f.SourcePath), StringComparer.Ordinal));
    }

    private string Folder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nobara-voxel-sound-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(folder);
        _folders.Add(Path.GetDirectoryName(folder)!);
        return folder;
    }

    private static byte[] Embedded(Type module)
    {
        using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private byte[] GameManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Game, Version = "1.0.0", Level = F.Level.Game, Side = F.Side.Client };
        foreach (string dependency in new[] { "nobara.game", "nobara.context", "nobara.items", "nobara.blocks", "nobara.voxel" })
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        if (WithAudio)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = AudioModule.ModuleId, Range = "^1.0" });
        }

        if (WithSound)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = VoxelSoundModule.ModuleId, Range = "^1.0" });
        }

        manifest.States.Add(new F.StateDeclaration { Id = PlayState });
        return manifest.ToByteArray();
    }

    private static byte[] BackendManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = "example.audio", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = AudioModule.ModuleId };
        manifest.Dependencies.Add(new F.Dependency { Id = AudioModule.ModuleId, Range = "^1.0" });
        return manifest.ToByteArray();
    }

    private sealed class Source(SoundHarness harness, byte[] bundle, Dictionary<string, byte[]> files) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.game" => Embedded(typeof(GameModule)),
                "nobara.context" => Embedded(typeof(ContextModule)),
                "nobara.entity" => Embedded(typeof(EntityModule)),
                "nobara.items" => Embedded(typeof(ItemsModule)),
                "nobara.blocks" => Embedded(typeof(BlocksModule)),
                "nobara.voxel" => Embedded(typeof(VoxelModule)),
                AudioModule.ModuleId => Embedded(typeof(AudioModule)),
                VoxelSoundModule.ModuleId => Embedded(typeof(VoxelSoundModule)),
                "example.audio" => BackendManifest(),
                _ => harness.GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.game" => new LoadedModule(typeof(GameModule).Assembly, () => new GameModule()),
            "nobara.context" => new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule()),
            "nobara.entity" => new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule()),
            "nobara.items" => new LoadedModule(typeof(ItemsModule).Assembly, () => new ItemsModule()),
            "nobara.blocks" => new LoadedModule(typeof(BlocksModule).Assembly, () => new BlocksModule()),
            "nobara.voxel" => new LoadedModule(typeof(VoxelModule).Assembly, () => new VoxelModule()),
            AudioModule.ModuleId => new LoadedModule(typeof(AudioModule).Assembly, () => new AudioModule()),
            VoxelSoundModule.ModuleId => new LoadedModule(typeof(VoxelSoundModule).Assembly, () => new VoxelSoundModule()),
            "example.audio" => new LoadedModule(null, () => new BackendModule(harness.Backend)),
            _ => new LoadedModule(null, () => harness.Probe = new WorldProbe(harness), Open),
        };

        private Stream? Open(string name)
        {
            if (name == "nobara/resources.pb")
            {
                return new MemoryStream(bundle, writable: false);
            }

            const string Prefix = "nobara/files/";
            return name.StartsWith(Prefix, StringComparison.Ordinal) && files.TryGetValue(name[Prefix.Length..], out byte[]? file) ? new MemoryStream(file, writable: false) : null;
        }
    }

    private sealed class BackendModule(TestAudioBackend backend) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IAudioBackends>().Register(context, backend);
    }
}

/// <summary>The test game: two grids besides the terrain, and the listener at the origin.</summary>
internal sealed class WorldProbe(SoundHarness harness) : IModule
{
    /// <summary>Where the moved grid stands, and how it is turned: a quarter turn around the vertical axis.</summary>
    public static readonly WorldTransform MovedTransform = new(WorldPosition.FromBlocks(50, 10, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Vector3.One);

    private AudioListener? _listener;

    /// <summary>Gets the listener, once the module started on a client with audio.</summary>
    public AudioListener? Listener => _listener;

    public ModuleContext Context { get; private set; } = null!;

    public IVoxelWorlds Worlds { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IVoxelBatches Batches { get; private set; } = null!;

    public IWorldSessions Sessions { get; private set; } = null!;

    public IBlockHooks Hooks { get; private set; } = null!;

    /// <summary>Gets the grid at the origin that is not the terrain.</summary>
    public Grid Plain { get; private set; } = null!;

    /// <summary>Gets the grid that stands at <see cref="MovedTransform"/>.</summary>
    public Grid Moved { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Worlds = context.Resolve<IVoxelWorlds>();
        Blocks = context.Resolve<IBlockRegistry>();
        Batches = context.Resolve<IVoxelBatches>();
        Hooks = context.Resolve<IBlockHooks>();
        Sessions = context.Resolve<IWorldSessions>();
        context.Resolve<IStateStack>().Register(context, SoundHarness.PlayState, () => new PlayState(Sessions));
        Sessions.SubscribeStarted(context, _ =>
        {
            Plain = Worlds.CreateGrid(context, DimensionId.Default, new GridOptions { Transform = WorldTransform.Identity });
            Moved = Worlds.CreateGrid(context, DimensionId.Default, new GridOptions { Transform = MovedTransform });
        });
        Sessions.SubscribeEnded(context, _ =>
        {
            Worlds.Destroy(context, Plain);
            Worlds.Destroy(context, Moved);
        });
    }

    public void Start(ModuleContext context)
    {
        if (harness.WithAudio)
        {
            _listener = context.Resolve<IAudioSystem>().ClaimListener(context);
            _listener.Teleport(WorldPosition.FromBlocks(0, 0, 0), Quaternion.Identity);
        }
    }

    public void Stop(ModuleContext context) => _listener?.Dispose();

    private sealed class PlayState(IWorldSessions sessions) : ApplicationState
    {
        public override void Enter() => sessions.Start();
    }
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
