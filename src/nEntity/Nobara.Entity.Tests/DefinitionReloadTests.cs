// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Entity.Tests.Support;
using static Nobara.Entity.Tests.Support.EntityHarness;

namespace Nobara.Entity.Tests;

public sealed class DefinitionReloadTests : IDisposable
{
    private const string Animals = "example.animals";
    private const string PigId = "entity:example.animals.pig";

    private readonly EntityHarness _harness = new() { DevelopmentMode = true };
    private ComponentType<Stats> _stats;
    private ComponentType<Voice> _voice;

    public DefinitionReloadTests()
    {
        _harness.Module(
            Animals,
            p => p.OnInitialize += probe =>
            {
                _stats = probe.Worlds.RegisterComponent<Stats>(probe.Context, "example.animals.stats");
                _voice = probe.Worlds.RegisterComponent<Voice>(probe.Context, "example.animals.voice");
            },
            nof: Pig(12));
    }

    public void Dispose() => _harness.Dispose();

    private static string Pig(string health, string extra = "", string more = "") => Nof("Example/Animals", $"""
        <Entity><Name>Pig</Name><Content>
          <Component ref="example.animals.stats"><Health>{health}</Health></Component>
          {extra}
        </Content></Entity>
        {more}
        """);

    private static string Pig(int health, string extra = "", string more = "") =>
        Pig(health.ToString(System.Globalization.CultureInfo.InvariantCulture), extra, more);

    private Probe Probe => _harness.Probes[Animals];

    private void Reload(Engine engine, string nof)
    {
        engine.ReloadResources(Animals, Bundle(Animals, nof).ToByteArray());
        _harness.Tick(engine);
    }

    private void ReloadWorld(Engine engine)
    {
        _harness.Owner.Sessions.End();
        _harness.Tick(engine);
        _harness.Owner.Sessions.Start();
        _harness.Tick(engine);
        Assert.Equal(SessionState.Active, _harness.Owner.Sessions.State);
    }

    private bool Refused(string text) =>
        _harness.Log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(PigId, StringComparison.Ordinal) && e.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void AChangedValueAppliesToTheEntitiesCreatedLater()
    {
        using Engine engine = _harness.StartSession();
        EntityDefinition pig = Probe.Worlds.GetDefinition(PigId);
        EntityWorld world = Probe.World();
        EntityId before = world.Instantiate(pig);

        Reload(engine, Pig(20));
        EntityId after = world.Instantiate(pig);

        Assert.Same(pig, Probe.Worlds.GetDefinition(PigId));
        Assert.Equal(20, pig.GetValue(_stats).Health);
        Assert.Equal(12, world.Read(before, _stats).Health);
        Assert.Equal(20, world.Read(after, _stats).Health);
        Assert.Same(world.GetArchetypeOf(before), world.GetArchetypeOf(after));
    }

    [Fact]
    public void AChangeOfComponentsIsReportedAndAppliesOnceTheWorldIsReloaded()
    {
        using Engine engine = _harness.StartSession();
        EntityDefinition pig = Probe.Worlds.GetDefinition(PigId);

        Reload(engine, Pig(20, """<Component ref="example.animals.voice"><Volume>0.5</Volume></Component>"""));

        Assert.True(Refused("its components change (added example.animals.voice)"));
        Assert.False(pig.Has(_voice));
        Assert.Equal(12, pig.GetValue(_stats).Health);
        EntityId old = Probe.World().Instantiate(pig);
        Assert.False(Probe.World().Has(old, _voice));

        ReloadWorld(engine);
        EntityId reloaded = Probe.World().Instantiate(pig);

        Assert.True(pig.Has(_voice));
        Assert.Equal(20, Probe.World().Read(reloaded, _stats).Health);
        Assert.Equal(0.5f, Probe.World().Read(reloaded, _voice).Volume);
    }

    [Fact]
    public void AnInvalidValueIsRefusedAndTheDefinitionKeepsItsValues()
    {
        using Engine engine = _harness.StartSession();
        EntityDefinition pig = Probe.Worlds.GetDefinition(PigId);

        Reload(engine, Pig("many"));

        Assert.True(Refused("<Health> is 'many', expected a whole number."));
        Assert.Equal(12, pig.GetValue(_stats).Health);
    }

    [Fact]
    public void AnAddedDefinitionCanBeInstantiatedAndARemovedOneStays()
    {
        using Engine engine = _harness.StartSession();

        Reload(engine, Nof("Example/Animals", """
            <Entity><Name>Cow</Name><Content><Component ref="example.animals.stats"><Health>30</Health></Component></Content></Entity>
            """));

        EntityDefinition cow = Probe.Worlds.GetDefinition("entity:example.animals.cow");
        Assert.Equal(30, Probe.World().Read(Probe.World().Instantiate(cow), _stats).Health);
        Assert.True(Refused("a removed entity definition stays loaded until the context restarts"));
        Assert.Equal(12, Probe.Worlds.GetDefinition(PigId).GetValue(_stats).Health);
    }
}
