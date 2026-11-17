// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Nobara.Audio.Build;
using Nobara.Audio.Tests.Support;
using Nobara.AudioToolkit;
using Nobara.ResourceCompiler;
using Xunit.Abstractions;

namespace Nobara.Audio.Tests;

/// <summary>The build of sounds and buses: the checks of the plugin and the conversion of WAV and FLAC sources.</summary>
public sealed class CompilationTests : IDisposable
{
    private readonly AudioHarness _harness = new();
    private readonly ITestOutputHelper _output;
    private readonly string _resources;

    public CompilationTests(ITestOutputHelper output)
    {
        _output = output;
        _resources = _harness.CopyResources();
    }

    public void Dispose() => _harness.Dispose();

    private string Nof => Path.Combine(_resources, "game.nof");

    private void Edit(Func<string, string> change) => File.WriteAllText(Nof, change(File.ReadAllText(Nof)));

    private void Add(string resources) => Edit(text => text.Replace("</Nobara>", resources + "\n</Nobara>", StringComparison.Ordinal));

    private int LineOf(string text) => File.ReadAllLines(Nof).ToList().FindIndex(l => l.Contains(text, StringComparison.Ordinal)) + 1;

    private Diagnostic Error(string? code = null)
    {
        CompilationResult result = AudioHarness.Compile(_resources);
        Assert.False(result.Success);
        Diagnostic? error = result.Diagnostics.FirstOrDefault(d => (code is null || d.Code == code) && d.Severity == DiagnosticSeverity.Error);
        Assert.True(error is not null, $"Expected error {code}, got:{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics)}");
        return error;
    }

    [Fact]
    public void CompilesSoundsAndABusAndEmbedsOnlyOggVorbis()
    {
        CompilationResult result = AudioHarness.Compiled(_resources);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(6, result.Bundle!.Resources.Count(r => r.Type == "sound"));
        Assert.Contains(result.Bundle.Resources, r => r.ObjectId == "bus:example.sounds.machines");
        Dictionary<string, byte[]> files = AudioHarness.Files(result);

        // An Ogg source is embedded as it is; the others are converted.
        Assert.Equal(File.ReadAllBytes(Path.Combine(_resources, "Sounds", "click.ogg")), files["Sounds/click.ogg"]);
        Assert.All(files.Values, f => Assert.Equal(AudioFormat.Ogg, AudioFile.Detect(f)));
    }

    [Theory]
    [InlineData("Sounds/step.wav", 1)]
    [InlineData("Sounds/bell.flac", 1)]
    [InlineData("Sounds/wind.wav", 2)]
    public void ConvertsWavAndFlacSourcesWithTheirLengthAndRate(string path, int channels)
    {
        Dictionary<string, byte[]> files = AudioHarness.Files(AudioHarness.Compiled(_resources));
        PcmAudio source = AudioFile.Decode(File.ReadAllBytes(Path.Combine(_resources, path)));

        float[] decoded = OggVorbis.DecodeAll(files[path], out OggInfo info);

        // step.wav is mono; bell.flac is stereo, mixed to mono by <Mono/> of Step; wind.wav stays stereo.
        Assert.Equal(channels, info.Channels);
        Assert.Equal(source.SampleRate, info.SampleRate);
        Assert.Equal(source.Frames, info.Frames);
        Assert.Equal(source.Frames * channels, decoded.Length);

        PcmAudio expected = channels == 1 ? source.ToMono() : source;
        double signal = 0, noise = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            signal += expected.Samples[i] * expected.Samples[i];
            double error = decoded[i] - expected.Samples[i];
            noise += error * error;
        }

        Assert.True(10 * Math.Log10(signal / noise) > 10, $"signal to noise ratio {10 * Math.Log10(signal / noise):F1} dB");
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void ConvertsEveryWavEncoding(int bits, bool isFloat)
    {
        // The tone of step.wav, written again in another encoding.
        PcmAudio tone = AudioFile.Decode(File.ReadAllBytes(Path.Combine(_resources, "Sounds", "step.wav")));
        File.WriteAllBytes(Path.Combine(_resources, "Sounds", "step.wav"), Wav(tone, bits, isFloat));

        float[] decoded = OggVorbis.DecodeAll(AudioHarness.Files(AudioHarness.Compiled(_resources))["Sounds/step.wav"], out OggInfo info);

        Assert.Equal((1, tone.SampleRate, tone.Frames), (info.Channels, info.SampleRate, info.Frames));
        double signal = 0, noise = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            signal += tone.Samples[i] * tone.Samples[i];
            double error = decoded[i] - tone.Samples[i];
            noise += error * error;
        }

        // 8-bit samples carry their own noise, about 48 dB under full scale.
        Assert.True(10 * Math.Log10(signal / noise) > 10, $"signal to noise ratio {10 * Math.Log10(signal / noise):F1} dB");
    }

    [Fact]
    public void ReportsAStereoFileOfASpatialSound()
    {
        Edit(text => text.Replace("<Quality>0.2</Quality>", "<Quality>0.2</Quality><Spatial/>", StringComparison.Ordinal));

        CompilationResult result = AudioHarness.Compiled(_resources);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == AudioPlugin.StereoSpatial);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.EndsWith(Path.Combine("Sounds", "wind.wav"), warning.File, StringComparison.Ordinal);
        Assert.Contains("sound:example.sounds.wind is spatial", warning.Message, StringComparison.Ordinal);
        Assert.Contains("<Mono/>", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAnUnknownBusAtItsLine()
    {
        Edit(text => text.Replace("<Category>bus:nobara.audio.ui</Category>", "<Category>bus:nobara.audio.radio</Category>", StringComparison.Ordinal));

        Diagnostic error = Error();

        Assert.Equal(LineOf("bus:nobara.audio.radio"), error.Line);
        Assert.Contains("bus:nobara.audio.radio", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAMissingFile()
    {
        Edit(text => text.Replace("Sounds/click.ogg", "Sounds/missing.ogg", StringComparison.Ordinal));

        Diagnostic error = Error();

        Assert.Equal(LineOf("Sounds/missing.ogg"), error.Line);
    }

    [Fact]
    public void ReportsAFormatThatTheBuildDoesNotReadAtTheLineOfItsVariant()
    {
        File.Copy(Path.Combine(_resources, "Sounds", "step.wav"), Path.Combine(_resources, "Sounds", "step.mp3"));
        Edit(text => text.Replace("Sounds/step.wav", "Sounds/step.mp3", StringComparison.Ordinal));

        Diagnostic error = Error(AudioPlugin.UnsupportedFile);

        Assert.Equal(LineOf("Sounds/step.mp3"), error.Line);
        Assert.Contains(".ogg, .wav, or .flac", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAFileWhoseContentIsNotSound()
    {
        File.WriteAllText(Path.Combine(_resources, "Sounds", "step.wav"), "not a sound");

        Diagnostic error = Error(AudioPlugin.UnsupportedFile);

        Assert.EndsWith(Path.Combine("Sounds", "step.wav"), error.File, StringComparison.Ordinal);
        Assert.Contains("sound:example.sounds.step", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAnInvalidRangeAtItsLine()
    {
        Edit(text => text.Replace("<Pitch min=\"0.9\" max=\"1.1\"/>", "<Pitch min=\"1.2\" max=\"1.1\"/>", StringComparison.Ordinal));

        Diagnostic error = Error(AudioPlugin.InvalidSound);

        Assert.Equal(LineOf("<Pitch min=\"1.2\""), error.Line);
    }

    [Fact]
    public void ReportsABusWithoutParent()
    {
        Add("<Bus><Name>Loose</Name><Content/></Bus>");

        Diagnostic error = Error(AudioPlugin.InvalidBus);

        Assert.Contains("bus:nobara.audio.master", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAnAdjustableBusWithoutName()
    {
        Edit(text => text.Replace("<DisplayName>audio.bus.machines</DisplayName>", string.Empty, StringComparison.Ordinal));

        Assert.Contains("DisplayName", Error(AudioPlugin.InvalidBus).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotEncodeAnUnchangedSourceAgain()
    {
        string cache = Path.Combine(_resources, "..", "resources.cache");
        CompilationResult first = AudioHarness.Compiled(_resources, cache);

        CompilationResult second = AudioHarness.Compiled(_resources, cache);
        File.SetLastWriteTimeUtc(Path.Combine(_resources, "Sounds", "step.wav"), DateTime.UtcNow);
        CompilationResult touched = AudioHarness.Compiled(_resources, cache);

        Assert.Contains("Sounds/step.wav", first.ProcessedFiles);
        Assert.Empty(second.ProcessedFiles);
        Assert.Empty(touched.ProcessedFiles);
        Assert.Equal(AudioHarness.Files(first)["Sounds/step.wav"], AudioHarness.Files(second)["Sounds/step.wav"]);
    }

    [Fact]
    public void EncodesAgainWhenTheQualityChanges()
    {
        string cache = Path.Combine(_resources, "..", "resources.cache");
        CompilationResult first = AudioHarness.Compiled(_resources, cache);

        Edit(text => text.Replace("<Quality>0.2</Quality>", "<Quality>0.6</Quality>", StringComparison.Ordinal));
        CompilationResult second = AudioHarness.Compiled(_resources, cache);

        Assert.Equal(["Sounds/wind.wav"], second.ProcessedFiles);
        Assert.True(AudioHarness.Files(second)["Sounds/wind.wav"].Length > AudioHarness.Files(first)["Sounds/wind.wav"].Length);
    }

    [Fact]
    public void GivesTheSameBytesForTheSameSource()
    {
        byte[] first = AudioHarness.Files(AudioHarness.Compiled(_resources))["Sounds/bell.flac"];

        byte[] second = AudioHarness.Files(AudioHarness.Compiled(_resources))["Sounds/bell.flac"];

        Assert.Equal(first, second);
    }

    // A mono WAV file: integer PCM of 8 or 32 bits, or 32-bit float.
    private static byte[] Wav(PcmAudio audio, int bits, bool isFloat)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int bytes = bits / 8;
        writer.Write("RIFF"u8);
        writer.Write(36 + (audio.Samples.Length * bytes));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)(isFloat ? 3 : 1));
        writer.Write((ushort)1);
        writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * bytes);
        writer.Write((ushort)bytes);
        writer.Write((ushort)bits);
        writer.Write("data"u8);
        writer.Write(audio.Samples.Length * bytes);
        foreach (float sample in audio.Samples)
        {
            if (isFloat)
            {
                writer.Write(sample);
            }
            else if (bits == 8)
            {
                writer.Write((byte)Math.Clamp((int)Math.Round((sample * 128) + 128), 0, 255));
            }
            else
            {
                writer.Write((int)Math.Clamp(Math.Round(sample * 2147483648.0), int.MinValue, int.MaxValue));
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void MeasuresTheTimeOfOneMinuteOfStereo()
    {
        var samples = new float[48000 * 60 * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.3f * MathF.Sin(i * 0.013f) * MathF.Sin(i * 0.00007f);
        }

        var watch = Stopwatch.StartNew();
        byte[] ogg = VorbisEncoder.Encode(new PcmAudio(2, 48000, samples), AudioRules.DefaultQuality);
        watch.Stop();

        _output.WriteLine($"One minute of stereo at 48 kHz, quality {AudioRules.DefaultQuality}: {watch.Elapsed.TotalSeconds:F2} s, {ogg.Length / 1024} KiB ({VorbisEncoder.LibraryVersion})");
        Assert.Equal(48000 * 60, OggVorbis.ReadInfo(ogg).Frames);
    }
}
