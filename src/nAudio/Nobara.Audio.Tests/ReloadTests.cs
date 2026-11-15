// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.HotReload;

namespace Nobara.Audio.Tests;

/// <summary>Hot reload in a development build: a changed sound, or its file, takes effect at the next frame.</summary>
public sealed class ReloadTests : IDisposable
{
    private readonly AudioHarness _harness = new() { DevelopmentMode = true };
    private readonly string _resources;
    private readonly Engine _engine;
    private readonly ResourceWatcher _watcher;

    public ReloadTests()
    {
        _resources = _harness.CopyResources();
        _engine = _harness.WithGame(AudioHarness.Compiled(_resources)).Start();
        _watcher = _harness.Watcher(_engine, _resources);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _engine.Dispose();
        _harness.Dispose();
    }

    private ISoundRegistry Sounds => _harness.Sounds;

    private string Nof => Path.Combine(_resources, "game.nof");

    private bool Reload()
    {
        bool compiled = _watcher.Compile(AudioHarness.Game);
        _engine.MainContext.RunFrame();
        return compiled;
    }

    private string Logs() => string.Join(Environment.NewLine, _harness.Log.Entries.Select(e => $"{e.Level}: {e.Message}"));

    [Fact]
    public void AChangedSoundKeepsItsIndexAndTakesItsNewSettings()
    {
        SoundEvent before = Sounds.Get("sound:example.sounds.click");
        File.WriteAllText(Nof, File.ReadAllText(Nof).Replace("<Volume value=\"0.8\"/>", "<Volume value=\"0.5\"/>", StringComparison.Ordinal));

        Assert.True(Reload());

        SoundEvent after = Sounds.Get("sound:example.sounds.click");
        Assert.NotSame(before, after);
        Assert.Equal(before.Index, after.Index);
        Assert.Same(after, Sounds.Sounds[after.Index]);
        Assert.Equal(new SoundRange(0.5f, 0.5f), after.Volume);
    }

    [Fact]
    public void AChangedFileIsConvertedAndDecodedAgain()
    {
        Sounds.CompleteDecoding();
        SoundVariant before = Sounds.Get("sound:example.sounds.step").Variants[0];
        File.Copy(Path.Combine(_resources, "Sounds", "wind.wav"), Path.Combine(_resources, "Sounds", "step.wav"), overwrite: true);

        Assert.True(Reload(), Logs());
        Sounds.CompleteDecoding();

        SoundVariant after = Sounds.Get("sound:example.sounds.step").Variants[0];
        Assert.NotEqual(before.Frames, after.Frames);
        Assert.Equal(44100, after.SampleRate);
        Assert.Equal(1, after.Channels);
        Assert.True(Sounds.TryGetClip(after, out SoundClip? clip));
        Assert.Equal(after.Frames, clip.Frames);
        Assert.False(Sounds.TryGetClip(before, out _));
    }

    [Fact]
    public void ANewSoundAndAChangedBusWaitForTheNextStart()
    {
        File.WriteAllText(Nof, File.ReadAllText(Nof)
            .Replace("<DisplayName>audio.bus.machines</DisplayName>", "<DisplayName>audio.bus.engines</DisplayName>", StringComparison.Ordinal)
            .Replace("</Nobara>", "<Sound><Name>Beep</Name><Content><Category>bus:nobara.audio.ui</Category><Variants><Variant file=\"Sounds/click.ogg\"/></Variants></Content></Sound></Nobara>", StringComparison.Ordinal));

        Assert.True(Reload());

        Assert.False(Sounds.TryGet("sound:example.sounds.beep", out _));
        Assert.Equal("audio.bus.machines", Sounds.GetBus("bus:example.sounds.machines").DisplayName);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("sound:example.sounds.beep", StringComparison.Ordinal));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("bus:example.sounds.machines", StringComparison.Ordinal));
    }
}
