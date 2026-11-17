// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Audio;

/// <summary>
/// The only place where nAudio uses nDebug, nRenderer, and nGraphics, its
/// optional dependencies: the "Audio" tool window, which shows the device, the voices
/// with their scores, the volumes of the buses (adjustable), the emitters, the
/// reverberation buses, and the processors; and, when nRenderer is present,
/// the drawing of the sounds in the world.
/// </summary>
[UsesOptionalDependency(DebugModuleId)]
[UsesOptionalDependency(RendererModuleId)]
[UsesOptionalDependency(GraphicsModuleId)]
internal sealed class AudioDebugWindow : IDisposable
{
    /// <summary>The identifier of nDebug.</summary>
    public const string DebugModuleId = "nobara.debug";

    /// <summary>The identifier of nRenderer.</summary>
    public const string RendererModuleId = "nobara.renderer";

    /// <summary>The identifier of nGraphics, whose colors the drawing uses.</summary>
    public const string GraphicsModuleId = "nobara.graphics";

    private const int ListedVoices = 64;

    private readonly AudioMixer _mixer;
    private readonly SoundRegistry _sounds;
    private readonly IDebugDraw? _draw;
    private DebugWindow? _window;
    private bool _drawSounds;

    private AudioDebugWindow(AudioMixer mixer, SoundRegistry sounds, IDebugDraw? draw) => (_mixer, _sounds, _draw) = (mixer, sounds, draw);

    /// <summary>Registers the window. Call it only when nDebug is active.</summary>
    /// <returns>The window, which the module disposes when it is released; <see langword="null"/> without the service of nDebug.</returns>
    public static AudioDebugWindow? Add(ModuleContext context, AudioMixer mixer, SoundRegistry sounds, bool hasRenderer)
    {
        if (!context.TryResolve(out IDebugUi? debug))
        {
            return null;
        }

        IDebugDraw? draw = hasRenderer && context.TryResolve(out IDebugDraw? found) && found.IsEnabled ? found : null;
        var window = new AudioDebugWindow(mixer, sounds, draw);
        window._window = debug.AddWindow(context, new DebugWindowOptions { Title = "Audio", Open = false }, new Content(window));
        return window;
    }

    /// <summary>Removes the window.</summary>
    public void Dispose() => _window?.Dispose();

    private void Draw()
    {
        DrawDevice();
        if (ImGui.CollapsingHeader("Voices", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawVoices();
        }

        if (ImGui.CollapsingHeader("Buses", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBus(_sounds.Master, 0);
        }

        if (ImGui.CollapsingHeader("Emitters"))
        {
            foreach (SoundEmitter emitter in _mixer.Emitters)
            {
                string state = emitter.IsSuspended ? "suspended" : emitter.IsActive ? emitter.Playing.IsReal ? "real" : emitter.Playing.IsActive ? "virtual" : "waiting" : "off";
                Text($"{emitter.Sound.ObjectId} ({emitter.Module.Module.Id}): {state}, volume {emitter.Volume:F2}, pitch {emitter.Pitch:F2}");
            }
        }

        if (ImGui.CollapsingHeader("Reverberation and processors"))
        {
            foreach (ReverbBus reverb in _mixer.ReverbBuses)
            {
                ReverbParameters p = reverb.Current;
                Text($"Reverb '{reverb.Name}' ({reverb.Module.Module.Id}): {(reverb.IsAvailable ? $"slot {reverb.Slot}" : "unavailable")}, decay {p.DecayTime:F2} s, gain {p.Gain:F2}");
            }

            foreach (AudioMixer.ProcessorEntry entry in _mixer.Processors)
            {
                Text($"Processor '{entry.Processor.Name}' ({entry.Registration.Module.Module.Id}){(entry.Disabled ? ", disabled" : string.Empty)}");
            }
        }
    }

    private void DrawDevice()
    {
        if (_mixer.Device is not { } device)
        {
            ImGui.TextDisabled("No audio device: nothing plays.");
            return;
        }

        AudioCapabilities capabilities = device.Capabilities;
        AudioStatistics statistics = _mixer.Statistics;
        Text($"Backend '{_mixer.BackendName}': {device.Description}");
        Text($"Features: {capabilities.Features}; HRTF {(device.HrtfEnabled ? "on" : "off")}; {capabilities.MaxReverbSlots} reverberation slots");
        Text($"Voices: {statistics.RealVoices} real of {_mixer.RealVoiceLimit}, {statistics.VirtualVoices} virtual; {statistics.Emitters} emitters");
        Text($"Listener: {(_mixer.Listener is { IsPlaced: true } listener ? listener.Position.ToString() : "none, spatial sounds are not heard")}");
        if (_draw is not null)
        {
            ImGui.Checkbox("Draw the sounds in the world", ref _drawSounds);
        }
    }

    private void DrawVoices()
    {
        int listed = 0;
        foreach (AudioMixer.VoiceView voice in _mixer.Voices())
        {
            if (listed++ < ListedVoices)
            {
                string place = voice.Spatial ? string.Create(CultureInfo.InvariantCulture, $", {voice.Distance:F1} blocks") : string.Empty;
                Text($"{(voice.Real ? "real   " : "virtual")} {voice.Sound.ObjectId}: {voice.Sound.Priority}, gain {voice.Gain:F3}{place}{(voice.Paused ? ", paused" : string.Empty)}");
            }

            if (_drawSounds && _draw is not null && voice.Spatial)
            {
                ColorF color = voice.Real ? new ColorF(0.2f, 1f, 0.3f, 1f) : new ColorF(1f, 0.6f, 0.1f, 0.6f);
                _draw.Sphere(voice.Position, 0.25f, color);
                _draw.Sphere(voice.Position, voice.Sound.Spatial!.Value.MaxDistance, new ColorF(color.R, color.G, color.B, 0.15f));
            }
        }

        if (listed > ListedVoices)
        {
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"... and {listed - ListedVoices} more"));
        }
    }

    private void DrawBus(AudioBus bus, int depth)
    {
        string name = new string(' ', depth * 2) + bus.ObjectId;
        if (bus.Adjustable)
        {
            float volume = _mixer.GetBusVolume(bus);
            if (ImGui.SliderFloat(name, ref volume, 0, 1))
            {
                _mixer.SetBusVolume(bus, volume);
            }
        }
        else
        {
            Text($"{name}: {_mixer.GetEffectiveVolume(bus):F2}");
        }

        foreach (AudioBus child in bus.Children)
        {
            DrawBus(child, depth + 1);
        }
    }

    private static void Text(FormattableString text) => ImGui.TextUnformatted(text.ToString(CultureInfo.InvariantCulture));

    // Only the isolation class creates it, so the types of nDebug load only when nDebug is active.
    private sealed class Content(AudioDebugWindow owner) : IDebugWindow
    {
        public void Draw(DebugWindow window) => owner.Draw();
    }
}
