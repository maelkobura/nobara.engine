// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Schemas;
using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Google.Protobuf;
using F = Nobara.Formats;

namespace Nobara.Audio.Tests;

/// <summary>The registry of one context: the tree of buses, the numbered sounds, and the draw of variants.</summary>
public sealed class RegistryTests : IDisposable
{
    private readonly AudioHarness _harness = new();
    private readonly string _resources;

    public RegistryTests()
    {
        _resources = _harness.CopyResources();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void BuildsTheDefaultTreeWithTheBusOfAModule()
    {
        using Engine engine = _harness.WithGame(AudioHarness.Compiled(_resources)).Start();
        ISoundRegistry sounds = _harness.Sounds;

        AudioBus master = sounds.Master;
        Assert.Equal(AudioModule.MasterBus, master.ObjectId);
        Assert.Equal(0, master.Index);
        Assert.Null(master.Parent);
        Assert.Equal(
            ["bus:nobara.audio.ambient", "bus:nobara.audio.effects", "bus:nobara.audio.music", "bus:nobara.audio.ui"],
            master.Children.Select(b => b.ObjectId));
        AudioBus effects = sounds.GetBus("bus:nobara.audio.effects");
        Assert.Equal(
            ["bus:example.sounds.machines", "bus:nobara.audio.effects.blocks", "bus:nobara.audio.effects.entities", "bus:nobara.audio.effects.players"],
            effects.Children.Select(b => b.ObjectId));
        Assert.All(sounds.Buses, b => Assert.Same(b, sounds.Buses[b.Index]));

        AudioBus machines = sounds.GetBus("bus:example.sounds.machines");
        Assert.True(machines.Adjustable);
        Assert.Equal("audio.bus.machines", machines.DisplayName);
        Assert.False(sounds.GetBus("bus:nobara.audio.effects.blocks").Adjustable);
    }

    [Fact]
    public void NumbersSoundsByIdentifierAndReadsTheirSettings()
    {
        using Engine engine = _harness.WithGame(AudioHarness.Compiled(_resources)).Start();
        ISoundRegistry sounds = _harness.Sounds;

        Assert.Equal(
            ["sound:example.sounds.click", "sound:example.sounds.hum", "sound:example.sounds.music", "sound:example.sounds.pop", "sound:example.sounds.step", "sound:example.sounds.wind"],
            sounds.Sounds.Select(s => s.ObjectId));
        Assert.All(sounds.Sounds, s => Assert.Same(s, sounds.Sounds[s.Index]));

        SoundEvent click = sounds.Get("sound:example.sounds.click");
        Assert.Equal(new SoundRange(0.8f, 0.8f), click.Volume);
        Assert.Equal(new SoundRange(1f, 1f), click.Pitch);
        Assert.Null(click.Spatial);
        Assert.Equal(SoundPriority.Normal, click.Priority);
        Assert.Equal("bus:nobara.audio.ui", click.Bus.ObjectId);

        SoundEvent step = sounds.Get("sound:example.sounds.step");
        Assert.Equal(new SoundSpatial(2, 16, SoundRolloff.Linear), step.Spatial);
        Assert.Equal(new SoundRange(0.9f, 1.1f), step.Pitch);
        Assert.Equal(SoundPriority.Low, step.Priority);
        Assert.Equal(4, step.MaxInstances);
        Assert.Equal([1f, 3f], step.Variants.Select(v => v.Weight));
        Assert.Equal(0.5f, step.Variants[1].Volume);
        Assert.All(step.Variants, v => Assert.Equal(1, v.Channels));

        SoundEvent music = sounds.Get("sound:example.sounds.music");
        Assert.True(music.Loop);
        Assert.True(music.Streamed);
        SoundVariant track = Assert.Single(music.Variants);
        Assert.Equal((2, 48000, 72000L), (track.Channels, track.SampleRate, track.Frames));
        Assert.Equal(TimeSpan.FromSeconds(1.5), track.Duration);
    }

    [Fact]
    public void DrawsVariantsByWeightTheSameWayForTheSameSeed()
    {
        using Engine engine = _harness.WithGame(AudioHarness.Compiled(_resources)).Start();
        SoundEvent step = _harness.Sounds.Get("sound:example.sounds.step");

        int[] counts = new int[2];
        for (uint seed = 0; seed < 4000; seed++)
        {
            SoundVariant variant = step.PickVariant(seed);
            Assert.Same(variant, step.PickVariant(seed));
            counts[variant.Index]++;
        }

        // Weights 1 and 3.
        Assert.InRange(counts[0], 850, 1150);
        Assert.InRange(counts[1], 2850, 3150);
    }

    [Fact]
    public void RejectsABusWhoseParentIsUnknownAtLoad()
    {
        _harness.WithGame(AudioHarness.Compiled(_resources));

        AudioLoadException error = LoadError(() => _harness.Start(bundle: b => SetParent(b, "bus:example.sounds.machines", "bus:example.sounds.gone")));

        AudioProblem problem = Assert.Single(error.Problems);
        Assert.Equal("bus:example.sounds.machines", problem.ObjectId);
        Assert.Contains("bus:example.sounds.gone", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsACycleOfBuses()
    {
        File.AppendAllText(Path.Combine(_resources, "cycle.nof"), """
            <Nobara version="1.0.0" namespace="Example/Loop">
              <Bus><Name>A</Name><Content><Parent>bus:example.loop.b</Parent></Content></Bus>
              <Bus><Name>B</Name><Content><Parent>bus:example.loop.a</Parent></Content></Bus>
            </Nobara>
            """);
        _harness.WithGame(AudioHarness.Compiled(_resources));

        AudioLoadException error = LoadError(() => _harness.Start());

        AudioProblem problem = Assert.Single(error.Problems);
        Assert.Contains("cycle", problem.Message, StringComparison.Ordinal);
        Assert.Contains("bus:example.loop.a", problem.Message, StringComparison.Ordinal);
        Assert.Contains("bus:example.loop.b", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsOnAServerWithoutDecoding()
    {
        using Engine engine = _harness.WithGame(AudioHarness.Compiled(_resources)).Start(Side.Server);
        ISoundRegistry sounds = _harness.Sounds;

        Assert.False(sounds.Decodes);
        Assert.Equal(6, sounds.Sounds.Count);
        Assert.Equal(0, ((SoundRegistry)sounds).PendingCount);
        Assert.False(sounds.TryGetClip(sounds.Get("sound:example.sounds.click").Variants[0], out _));
        Assert.Throws<InvalidOperationException>(() => sounds.OpenStream(sounds.Get("sound:example.sounds.music").Variants[0], loop: false));
    }

    // The engine stops, with the exception of nAudio inside.
    private static AudioLoadException LoadError(Func<Engine> start)
    {
        EngineStopException stop = Assert.Throws<EngineStopException>(() => start().Dispose());
        return Assert.IsType<AudioLoadException>(stop.InnerException);
    }

    private static void SetParent(F.ResourceBundle bundle, string bus, string parent)
    {
        F.Resource resource = bundle.Resources.Single(r => r.ObjectId == bus);
        Bus content = Bus.Parser.ParseFrom(resource.Content);
        content.Parent = parent;
        resource.Content = content.ToByteString();
    }
}
