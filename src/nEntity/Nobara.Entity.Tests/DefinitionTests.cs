// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Core.Resources;
using Nobara.Entity.Tests.Support;
using static Nobara.Entity.Tests.Support.EntityHarness;
using F = Nobara.Formats;

namespace Nobara.Entity.Tests;

public sealed class DefinitionTests : IDisposable
{
    private const string Animals = "example.animals";
    private const string PigId = "entity:example.animals.pig";

    private readonly EntityHarness _harness = new() { DevelopmentMode = true };
    private ComponentType<Stats> _stats;
    private ComponentType<Voice> _voice;
    private ComponentType<Body> _body;

    public void Dispose() => _harness.Dispose();

    /// <summary>A file with the pig: the stats on line 6, more components from line 8, and free properties.</summary>
    private static string Pig(string stats = "<Health>12</Health>", string extra = "", string properties = "") =>
        Nof("Example/Animals", PigEntity(stats, extra, properties));

    private static string PigEntity(string stats = "<Health>12</Health>", string extra = "", string properties = "") => $"""
        <Entity>
          <Name>Pig</Name>
          <Content>
            <Component ref="example.animals.stats">
              {stats}
            </Component>
            {extra}
          </Content>{properties}
        </Entity>
        """;

    private static Voice ReadVoice(DefinitionValues values, Voice fallback)
    {
        double volume = values.GetFloat("Volume", fallback.Volume);
        return volume is >= 0 and <= 1
            ? new Voice { Volume = (float)volume }
            : throw values.Error(FormattableString.Invariant($"<Volume> is {volume}, expected 0 to 1."), values.GetElement("Volume"));
    }

    private static EntityLoadException StartFails(Engine engine)
    {
        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);
        Assert.Equal("nobara.entity", stop.ModuleId);
        Assert.Equal(LifecycleStep.Start, stop.Step);
        return Assert.IsType<EntityLoadException>(stop.InnerException);
    }

    private EntityHarness WithAnimals(string nof, Action<Probe>? more = null) =>
        _harness.Module(
            Animals,
            p =>
            {
                p.OnInitialize += probe =>
                {
                    _stats = probe.Worlds.RegisterComponent<Stats>(probe.Context, "example.animals.stats", defaultValue: new Stats { Health = 10, Speed = 1, Size = Vector3.One, Diet = Diet.Grass, Tame = false, Litter = 0 });
                    _voice = probe.Worlds.RegisterComponent<Voice>(probe.Context, "example.animals.voice", ReadVoice, defaultValue: new Voice { Volume = 1 });
                };
                more?.Invoke(p);
            },
            nof: nof);

    /// <summary>Adds module <c>example.motion</c>: it reads the free property <c>Weight</c> and sets its body component.</summary>
    private EntityHarness WithMotion(Action<EntityConversion>? also = null, bool revoke = false) =>
        _harness.Module(
            "example.motion",
            p =>
            {
                FreeProperty weight = null!;
                p.OnRegister = probe => weight = probe.Context.Resources.DeclareProperty("Weight", PropertyType.Float);
                p.OnInitialize += probe =>
                {
                    _body = probe.Worlds.RegisterComponent<Body>(probe.Context, "example.motion.body");
                    EntityConverterHandle handle = probe.Worlds.AddConverter(probe.Context, conversion =>
                    {
                        if (weight.TryGetFloat(conversion.Resource, out double mass))
                        {
                            conversion.Set(_body, new Body { Mass = (float)mass });
                        }

                        also?.Invoke(conversion);
                    });
                    if (revoke)
                    {
                        handle.Dispose();
                    }
                };
            },
            shortName: "Motion");

    [Fact]
    public void ADefinitionIsLoadedOnceAndInstantiatedTenThousandTimesWithoutAllocating()
    {
        WithAnimals(Pig(
            """<Health>12</Health><Speed>1.5</Speed><Size><X>1</X><Y>0.75</Y><Z>1.5</Z></Size><Diet>Meat</Diet><Tame>true</Tame>""",
            """
            <Component ref="example.animals.voice"><Volume>0.25</Volume></Component>
                <Component ref="nobara.entity.transform"><Scale><X>0.5</X><Y>0.5</Y><Z>0.5</Z></Scale></Component>
            """));
        using Engine engine = _harness.StartSession();
        Probe probe = _harness.Probes[Animals];

        EntityDefinition pig = probe.Worlds.GetDefinition(PigId);

        Assert.Same(pig, probe.Worlds.GetDefinition(PigId));
        Stats stats = pig.GetValue(_stats);
        Assert.Equal((12, 1.5f, new Vector3(1, 0.75f, 1.5f), Diet.Meat, true, (byte)0), (stats.Health, stats.Speed, stats.Size, stats.Diet, stats.Tame, stats.Litter));
        Assert.Equal(3, pig.Components.Count);

        EntityWorld world = probe.World();
        EntityId first = world.Instantiate(pig);
        var entities = new EntityId[10_000];
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.Instantiate(pig, entities);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(10_001, world.EntityCount);
        EntityArchetype archetype = world.GetArchetypeOf(first);
        Assert.Same(archetype, world.GetArchetypeOf(entities[9_999]));
        Assert.Equal(10_001, archetype.EntityCount);
        Assert.All(entities, e => Assert.Equal(12, world.Read(e, _stats).Health));
        Assert.Equal(0.25f, world.Read(entities[5_000], _voice).Volume);
        Assert.Equal(new Vector3(0.5f), world.Read(entities[9_999], probe.Worlds.WorldTransform).Scale);
        Assert.Equal(Quaternion.Identity, world.Read(entities[9_999], probe.Worlds.WorldTransform).Rotation);
    }

    [Fact]
    public void AComponentWithoutValuesTakesItsDefaultValue()
    {
        WithAnimals(Pig(string.Empty, """<Component ref="example.animals.voice"/>"""));
        using Engine engine = _harness.StartSession();

        EntityDefinition pig = _harness.Probes[Animals].Worlds.GetDefinition(PigId);

        Assert.Equal(10, pig.GetValue(_stats).Health);
        Assert.Equal(1f, pig.GetValue(_voice).Volume);
    }

    [Fact]
    public void AnOptionalComponentOfAnInactiveModuleIsIgnoredWithoutAMessage()
    {
        _harness.Module("example.server", null, side: F.Side.Server);
        WithAnimals(Pig(extra: """<Component ref="example.server.loot" optional="true"><Count>2</Count></Component>"""));
        using Engine engine = _harness.StartSession(Side.Client);

        EntityDefinition pig = _harness.Probes[Animals].Worlds.GetDefinition(PigId);

        Assert.Equal([_stats.Untyped], pig.Components);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Message.Contains("example.server.loot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("example.animals.loot", "true", "module 'example.animals' registers no entity component 'example.animals.loot'.")]
    [InlineData("example.unknown.loot", "true", "the component 'example.unknown.loot' names no module of the distribution.")]
    [InlineData("example.animals.loot", "false", "module 'example.animals' registers no entity component 'example.animals.loot'.")]
    [InlineData("example.server.loot", "false", "the component 'example.server.loot' belongs to module 'example.server', which is not active; mark it optional=\"true\" if the entity works without it.")]
    public void AComponentThatCannotBeFoundFailsUnlessItsModuleIsInactive(string name, string optional, string message)
    {
        _harness.Module("example.server", null, side: F.Side.Server);
        WithAnimals(Pig(extra: $"""<Component ref="{name}" optional="{optional}"/>"""));
        using Engine engine = _harness.Load(Side.Client);

        EntityProblem problem = Assert.Single(StartFails(engine).Problems);

        Assert.Equal(PigId, problem.DefinitionId);
        Assert.Equal(message, problem.Message);
        Assert.Equal(8, problem.Source.Line);
    }

    [Fact]
    public void ATypingErrorInAComponentNameStopsTheStartWithTheFileAndPosition()
    {
        WithAnimals(Pig().Replace("example.animals.stats", "example.animals.stat", StringComparison.Ordinal));
        using Engine engine = _harness.Load();

        EntityLoadException exception = StartFails(engine);

        EntityProblem problem = Assert.Single(exception.Problems);
        Assert.Equal("module 'example.animals' registers no entity component 'example.animals.stat'.", problem.Message);
        Assert.EndsWith("animals.nof", problem.Source.File, StringComparison.Ordinal);
        Assert.Equal(5, problem.Source.Line);
        Assert.True(problem.Source.Column > 0);
        Assert.Contains($"animals.nof(5,{problem.Source.Column}): {PigId}: module 'example.animals'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Healt>3</Healt>", "<Healt> is not a field of the component; the fields of 'Stats' are Health, Speed, Size, Diet, Tame, Litter.")]
    [InlineData("<Health>many</Health>", "<Health> is 'many', expected a whole number.")]
    [InlineData("<Health>1.5</Health>", "<Health> is '1.5', expected a whole number.")]
    [InlineData("<Speed>fast</Speed>", "<Speed> is 'fast', expected a number.")]
    [InlineData("<Litter>300</Litter>", "<Litter> is 300, which does not fit in a Byte.")]
    [InlineData("<Tame>yes</Tame>", "<Tame> is 'yes', expected true or false.")]
    [InlineData("<Diet>Rocks</Diet>", "<Diet> is 'Rocks', expected one of Grass, Meat.")]
    [InlineData("<Size>3</Size>", "<Size> is '3', expected child elements: the fields X, Y, Z.")]
    [InlineData("<Size><X>1</X><W>2</W></Size>", "<W> is not a field of the component; the fields of 'Vector3' are X, Y, Z.")]
    [InlineData("<Health>1</Health><Health>2</Health>", "<Health> is written twice.")]
    public void AValueThatDoesNotFitTheComponentFailsWithItsPosition(string stats, string message)
    {
        WithAnimals(Pig(stats));
        using Engine engine = _harness.Load();

        EntityProblem problem = Assert.Single(StartFails(engine).Problems);

        Assert.Equal("component 'example.animals.stats': " + message, problem.Message);
        Assert.EndsWith("animals.nof", problem.Source.File, StringComparison.Ordinal);
        Assert.Equal(6, problem.Source.Line);
    }

    [Fact]
    public void AReaderOfTheModuleChecksItsOwnRules()
    {
        WithAnimals(Pig(extra: """<Component ref="example.animals.voice"><Volume>2</Volume></Component>"""));
        using Engine engine = _harness.Load();

        EntityProblem problem = Assert.Single(StartFails(engine).Problems);

        Assert.Equal("component 'example.animals.voice': <Volume> is 2, expected 0 to 1.", problem.Message);
        Assert.Equal(8, problem.Source.Line);
    }

    [Fact]
    public void ARotationOfLengthZeroIsRefused()
    {
        WithAnimals(Pig(extra: """<Component ref="nobara.entity.transform"><Rotation><X>0</X><Y>0</Y><Z>0</Z><W>0</W></Rotation></Component>"""));
        using Engine engine = _harness.Load();

        Assert.Contains("a rotation is a quaternion of length 1", Assert.Single(StartFails(engine).Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideDevelopmentModeADefinitionIsCheckedTheFirstTimeItIsAskedFor()
    {
        _harness.DevelopmentMode = false;
        WithAnimals(Nof("Example/Animals", PigEntity() + """
            <Entity><Name>Cow</Name><Content><Component ref="example.animals.horns"/></Content></Entity>
            """));
        using Engine engine = _harness.StartSession();
        IEntityWorlds worlds = _harness.Probes[Animals].Worlds;

        Assert.Equal(12, worlds.GetDefinition(PigId).GetValue(_stats).Health);
        EntityLoadException exception = Assert.Throws<EntityLoadException>(() => worlds.GetDefinition("entity:example.animals.cow"));
        Assert.Contains("example.animals.horns", Assert.Single(exception.Problems).Message, StringComparison.Ordinal);
        Assert.Throws<EntityLoadException>(() => worlds.TryGetDefinition("entity:example.animals.cow", out _));
        Assert.False(worlds.TryGetDefinition("entity:example.animals.horse", out _));
        Assert.Throws<KeyNotFoundException>(() => worlds.GetDefinition("entity:example.animals.horse"));
    }

    [Fact]
    public void DefinitionsAreReadOnceTheEntityModuleStarted()
    {
        Exception? early = null;
        WithAnimals(Pig(), p => p.OnInitialize += probe => early = Record.Exception(() => probe.Worlds.GetDefinition(PigId)));
        using Engine engine = _harness.StartSession();

        Assert.IsType<InvalidOperationException>(early);
    }

    [Fact]
    public void AConverterAddsAComponentFromAFreeProperty()
    {
        int healthSeen = 0;
        WithAnimals(Pig(properties: "<Properties><Weight>80</Weight></Properties>"));
        WithMotion(conversion =>
        {
            Assert.Equal(PigId, conversion.DefinitionId);
            if (conversion.TryGet(_stats, out Stats stats))
            {
                healthSeen = stats.Health;
            }
        });
        using Engine engine = _harness.StartSession();
        Probe probe = _harness.Probes[Animals];

        EntityDefinition pig = probe.Worlds.GetDefinition(PigId);
        EntityId entity = probe.World().Instantiate(pig);

        Assert.Equal(12, healthSeen);
        Assert.Equal(80f, pig.GetValue(_body).Mass);
        Assert.Equal(80f, probe.World().Read(entity, _body).Mass);
        Assert.Equal(2, probe.World().GetArchetypeOf(entity).Components.Count);
    }

    [Fact]
    public void WithoutTheReaderModuleTheDefinitionHasNoComponentForTheProperty()
    {
        WithAnimals(Pig(properties: "<Properties><Weight>80</Weight></Properties>"));
        using Engine engine = _harness.StartSession();
        Probe probe = _harness.Probes[Animals];

        EntityId entity = probe.World().Instantiate(probe.Worlds.GetDefinition(PigId));

        Assert.False(probe.Worlds.TryGetComponent("example.motion.body", out _));
        Assert.Equal([_stats.Untyped], probe.World().GetArchetypeOf(entity).Components);
    }

    [Fact]
    public void ARevokedConverterIsNotCalled()
    {
        WithAnimals(Pig(properties: "<Properties><Weight>80</Weight></Properties>"));
        WithMotion(revoke: true);
        using Engine engine = _harness.StartSession();

        Assert.False(_harness.Probes[Animals].Worlds.GetDefinition(PigId).Has(_body));
    }

    [Fact]
    public void AConverterErrorFailsTheDefinition()
    {
        WithAnimals(Pig(properties: "<Properties><Weight>-1</Weight></Properties>"));
        WithMotion(conversion =>
        {
            if (conversion.TryGet(_body, out Body body) && body.Mass < 0)
            {
                conversion.Error("Weight is -1, expected at least 0.");
            }
        });
        using Engine engine = _harness.Load();

        EntityProblem problem = Assert.Single(StartFails(engine).Problems);

        Assert.Equal("the converter of module 'example.motion': Weight is -1, expected at least 0.", problem.Message);
        Assert.Equal(2, problem.Source.Line);
    }

    [Fact]
    public void AConverterSetsTheComponentsOfItsOwnModuleOnly()
    {
        WithAnimals(Pig());
        WithMotion(conversion => conversion.Set(_stats, new Stats()));
        using Engine engine = _harness.Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Exception inner = Assert.IsType<ArgumentException>(stop.InnerException);
        Assert.Contains("a converter sets the components of its own module", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConverterIsAddedBeforeTheEntityModuleStarts()
    {
        Exception? late = null;
        WithAnimals(Pig(), p => p.OnStart = probe => late = Record.Exception(() => probe.Worlds.AddConverter(probe.Context, _ => { })));
        using Engine engine = _harness.StartSession();

        Assert.IsType<InvalidOperationException>(late);
    }

    [Fact]
    public void ADefinitionOfAnotherContextIsRefused()
    {
        WithAnimals(Pig());
        using Engine engine = _harness.StartSession();
        Probe probe = _harness.Probes[Animals];
        EntityDefinition pig = probe.Worlds.GetDefinition(PigId);

        using Engine other = _harness.StartSession();

        Assert.Throws<ArgumentException>(() => _harness.Probes[Animals].World().Instantiate(pig));
    }

    [Fact]
    public void AWorldIsLockedWhileASystemRuns()
    {
        Exception? locked = null;
        WithAnimals(Pig());
        _harness.Module("example.locks", p => p.OnInitialize += probe => probe.Worlds.AddSystem(
            probe.Context,
            "example.locks.spawn",
            new ActionSystem((world, _) => locked ??= Record.Exception(() => world.Instantiate(probe.Worlds.GetDefinition(PigId))))));
        using Engine engine = _harness.StartSession();
        _harness.Tick(engine);

        Assert.IsType<InvalidOperationException>(locked);
    }

    [Fact]
    public void TheEntityModuleLogsHowManyDefinitionsItChecked()
    {
        WithAnimals(Pig());
        using Engine engine = _harness.Start();

        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Information && e.Message == "Entity definitions loaded and checked: 1.");
    }
}
