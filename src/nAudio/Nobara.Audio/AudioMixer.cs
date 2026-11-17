// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <summary>Whether the game is paused, read from nGame when it is present.</summary>
internal interface IPauseSource
{
    bool Paused { get; }
}

/// <summary>
/// The service of nAudio in a context: the registry of backends, the device,
/// and the voices. At every frame, in the phase <c>Render</c>, it advances the
/// voices, scores them, gives the voices of the device to the most important,
/// and sends their parameters, relative to the listener, to the device.
/// </summary>
internal sealed partial class AudioMixer : IAudioSystem, IAudioBackends, IPhaseHandler
{
    /// <summary>The name of the phase where the mixer runs.</summary>
    public const string Phase = "Render";

    /// <summary>The gain under which a sound is not heard, about -60 dB.</summary>
    internal const float AudibleGain = 0.001f;

    /// <summary>The time constant of the smoothing of the emission parameters and of the reverberation, in seconds.</summary>
    internal const float SmoothingSeconds = 0.05f;

    /// <summary>The factor of the gain of a voice that is real already, so that two voices of close scores do not swap at every frame.</summary>
    internal const float Hysteresis = 1.25f;

    // The blocks of a stream queued on a voice of the device, at most.
    private const int QueuedBlocks = 3;

    private readonly ModuleContext _context;
    private readonly SoundRegistry _sounds;
    private readonly List<(ModuleContext Module, IAudioBackend Backend)> _backends = [];
    private readonly List<SoundEmitter> _emitters = [];
    private readonly List<ProcessorEntry> _processors = [];
    private readonly List<ReverbBus> _reverbs = [];
    private readonly Dictionary<SoundClip, AudioBuffer> _buffers = [];
    private readonly EmissionContext _emission = new();
    private readonly List<int> _pendingStops = [];
    private readonly bool _client;
    private Voice[] _voices = new Voice[64];
    private Candidate[] _candidates = new Candidate[64];
    private int[] _free = new int[64];
    private int _freeCount;
    private int _highWater;
    private int[] _realOwner = [];
    private bool[] _realWanted = [];
    private bool[] _reverbSlotUsed = [];
    private int[] _instances = [];
    private int[] _pending = new int[16];
    private int _pendingCount;
    private float[] _busVolume = [];
    private float[] _busEffective = [];
    private bool[] _busFollowsPause = [];
    private int[] _busOrder = [];
    private int[] _busParent = [];
    private AudioDevice? _device;
    private IAudioBackend? _backend;
    private IPauseSource? _pause;
    private AudioListener? _listener;
    private AudioVolumes? _volumes;
    private ulong _lastTick;
    private ulong _sequence;
    private uint _random = 0x9E3779B9;
    private int _realLimit;
    private int _defaultMaxInstances;
    private bool _interpolates;
    private bool _started;
    private bool _gamePaused;
    private bool _warnedNoListener;

    public AudioMixer(ModuleContext context, SoundRegistry sounds)
    {
        _context = context;
        _sounds = sounds;
        _client = context.Side == Side.Client;
        for (int i = _voices.Length - 1; i >= 0; i--)
        {
            _free[_freeCount++] = i;
        }
    }

    public bool IsActive => _device is not null;

    public string? BackendName => _device is null ? null : _backend!.Name;

    public AudioCapabilities? Capabilities => _device?.Capabilities;

    public int RealVoiceLimit => _device is null ? 0 : _realLimit;

    public AudioStatistics Statistics { get; private set; }

    public AudioListener? Listener => _listener;

    public IReadOnlyList<SoundEmitter> Emitters => _emitters;

    public string VolumesFilePath => Path.Combine(_context.Paths.Config, AudioVolumes.FileName);

    /// <summary>Gets the device, for the debug window and the tests.</summary>
    internal AudioDevice? Device => _device;

    /// <summary>Gets the processors, for the debug window.</summary>
    internal IReadOnlyList<ProcessorEntry> Processors => _processors;

    /// <summary>Gets the reverberation buses, for the debug window.</summary>
    internal IReadOnlyList<ReverbBus> ReverbBuses => _reverbs;

    /// <summary>Gets the slots in use, from 0 to this bound excluded, for the debug window.</summary>
    internal int HighWater => _highWater;

    // ---------------------------------------------------------------- lifecycle

    public void Register(ModuleContext module, IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(backend);
        if (_started)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' registers audio backend '{backend.Name}' after nAudio started; backends register during initialization.");
        }

        foreach ((ModuleContext other, IAudioBackend existing) in _backends)
        {
            if (string.Equals(existing.Name, backend.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modules '{other.Module.Id}' and '{module.Module.Id}' both register an audio backend named '{backend.Name}'.");
            }
        }

        _backends.Add((module, backend));
        _context.Logger.Debug($"Audio backend '{backend.Name}' registered by module '{module.Module.Id}'.");
    }

    /// <summary>Prepares the buses and the volumes of the player, once the registry is frozen.</summary>
    public void Initialize(IPauseSource? pause)
    {
        _pause = pause;
        IReadOnlyList<AudioBus> buses = _sounds.Buses;
        _busVolume = new float[buses.Count];
        _busEffective = new float[buses.Count];
        _busFollowsPause = new bool[buses.Count];
        _busParent = [.. buses.Select(b => b.Parent?.Index ?? -1)];
        Array.Fill(_busVolume, 1f);
        Array.Fill(_busEffective, 1f);
        var order = new List<int>(buses.Count);
        var queue = new Queue<AudioBus>();
        queue.Enqueue(_sounds.Master);
        while (queue.Count > 0)
        {
            AudioBus bus = queue.Dequeue();
            order.Add(bus.Index);
            _busFollowsPause[bus.Index] = bus.Parent is null || (_busFollowsPause[bus.Parent.Index] && !AudioRules.IgnoresPause(bus.ObjectId));
            foreach (AudioBus child in bus.Children)
            {
                queue.Enqueue(child);
            }
        }

        _busOrder = [.. order];
        _instances = new int[Math.Max(1, _sounds.Sounds.Count)];
        _interpolates = _context.Loop.Phases.Any(p => p.IsFixed);
        if (_client)
        {
            _volumes = new AudioVolumes(VolumesFilePath, _context.Logger);
            _volumes.Load(_sounds, _busVolume);
        }
    }

    /// <summary>Forgets nGame, disabled after an error: nothing pauses any more.</summary>
    public void DropPause() => _pause = null;

    /// <summary>Chooses the backend and opens the device; on a server, does nothing.</summary>
    public void Start()
    {
        _started = true;
        if (!_client)
        {
            return;
        }

        _backend = ChooseBackend();
        var options = new AudioDeviceOptions(AudioSettings.ParseHrtf(_context.Settings.GetEnum(AudioSettings.Hrtf)), _context.Logger);
        try
        {
            _device = _backend.Open(options);
        }
        catch (NotSupportedException exception)
        {
            _context.Logger.Error($"Audio backend '{_backend.Name}' cannot open a device, so the game runs without sound: {exception.Message}");
            return;
        }

        AudioCapabilities capabilities = _device.Capabilities;
        _realLimit = Math.Max(1, Math.Min(_context.Settings.GetInt(AudioSettings.RealVoices), capabilities.MaxVoices));
        _defaultMaxInstances = _context.Settings.GetInt(AudioSettings.MaxInstances);
        _realOwner = new int[_realLimit];
        _realWanted = new bool[_realLimit];
        Array.Fill(_realOwner, -1);
        _reverbSlotUsed = new bool[Math.Max(0, capabilities.MaxReverbSlots)];
        foreach (ReverbBus reverb in _reverbs)
        {
            AssignSlot(reverb);
        }

        foreach (ProcessorEntry processor in _processors)
        {
            CheckFeatures(processor);
        }

        _context.Logger.Info($"Audio device opened by backend '{_backend.Name}': {_device.Description}; {_realLimit} of {capabilities.MaxVoices} voices; features {capabilities.Features}; HRTF {(_device.HrtfEnabled ? "on" : "off")}.");
    }

    /// <summary>Stops every voice and closes the device.</summary>
    public void Release()
    {
        for (int i = 0; i < _highWater; i++)
        {
            if (_voices[i].Live)
            {
                End(i);
            }
        }

        if (_device is null)
        {
            return;
        }

        foreach (int real in _pendingStops)
        {
            _device.Stop(real);
        }

        _pendingStops.Clear();
        foreach (AudioBuffer buffer in _buffers.Values)
        {
            _device.DestroyBuffer(buffer);
        }

        _buffers.Clear();
        _device.Dispose();
        _device = null;
    }

    /// <summary>Applies a change of a key of nAudio.</summary>
    public void SettingChanged(string key)
    {
        switch (key)
        {
            case AudioSettings.Hrtf when _device is not null:
                _device.SetHrtf(AudioSettings.ParseHrtf(_context.Settings.GetEnum(AudioSettings.Hrtf)));
                break;
            case AudioSettings.Backend:
                _context.Logger.Info($"'{AudioSettings.Backend}' changed; the audio backend changes at the next start of the game.");
                break;
        }
    }

    // ---------------------------------------------------------------- listener, buses, registrations

    public AudioListener ClaimListener(ModuleContext module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_listener is not null)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' cannot claim the listener: module '{_listener.Module.Module.Id}' holds it. There is one listener per client.");
        }

        _listener = new AudioListener(module, () => _context.Loop.Tick, l =>
        {
            if (_listener == l)
            {
                _listener = null;
            }
        });
        _warnedNoListener = false;
        return _listener;
    }

    public float GetBusVolume(AudioBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return _busVolume[bus.Index];
    }

    public void SetBusVolume(AudioBus bus, float volume)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (!bus.Adjustable)
        {
            throw new InvalidOperationException($"The bus '{bus.ObjectId}' is not adjustable: its volume follows its parent.");
        }

        _busVolume[bus.Index] = float.IsNaN(volume) ? 1 : Math.Clamp(volume, 0, 1);
    }

    public float GetEffectiveVolume(AudioBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        float volume = 1;
        for (AudioBus? current = bus; current is not null; current = current.Parent)
        {
            volume *= _busVolume[current.Index];
        }

        return volume;
    }

    public bool IsPaused(AudioBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return (_pause?.Paused ?? false) && _busFollowsPause[bus.Index];
    }

    public void SaveBusVolumes()
    {
        if (_volumes is null)
        {
            throw new InvalidOperationException("Only a client keeps the volumes of the player.");
        }

        _volumes.Save(_sounds, _busVolume);
    }

    public AudioRegistration AddProcessor(ModuleContext module, IEmissionProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(processor);
        var registration = new AudioRegistration(module, r => _processors.RemoveAll(p => p.Registration == r));
        var entry = new ProcessorEntry(registration, processor);
        _processors.Add(entry);
        if (_device is not null)
        {
            CheckFeatures(entry);
        }

        return registration;
    }

    public ReverbBus CreateReverbBus(ModuleContext module, string name)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var reverb = new ReverbBus(module, name, RemoveReverb);
        _reverbs.Add(reverb);
        if (_device is not null)
        {
            AssignSlot(reverb);
        }

        return reverb;
    }

    public SoundEmitter CreateEmitter(ModuleContext module, SoundEvent sound, in WorldPosition position, EmitterRepeat repeat = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(sound);
        var emitter = new SoundEmitter(module, sound, repeat, NextRandom(), RemoveEmitter) { Position = position };
        _emitters.Add(emitter);
        return emitter;
    }

    public SoundEmitter CreateEmitter(ModuleContext module, SoundEvent sound, ISoundAnchor anchor, Vector3 offset, EmitterRepeat repeat = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(anchor);
        var emitter = new SoundEmitter(module, sound, repeat, NextRandom(), RemoveEmitter) { Anchor = anchor, Offset = offset };
        _emitters.Add(emitter);
        return emitter;
    }

    private void RemoveEmitter(SoundEmitter emitter)
    {
        _emitters.Remove(emitter);
        emitter.Playing.Stop();
        emitter.Playing = default;
    }

    private IAudioBackend ChooseBackend()
    {
        if (_backends.Count == 0)
        {
            throw new InvalidOperationException("No audio backend is registered: a client distribution needs a backend module of nobara.audio, such as nobara.audio.openal.");
        }

        string wanted = _context.Settings.GetString(AudioSettings.Backend);
        if (wanted.Length > 0 && !string.Equals(wanted, AudioSettings.AutomaticBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach ((_, IAudioBackend backend) in _backends)
            {
                if (string.Equals(backend.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return backend;
                }
            }

            _context.Logger.Warning($"'{AudioSettings.Backend}' names the audio backend '{wanted}', which no module registers; nAudio uses '{_backends[0].Backend.Name}'. Registered: {string.Join(", ", _backends.Select(b => b.Backend.Name))}.");
        }

        return _backends[0].Backend;
    }

    private void CheckFeatures(ProcessorEntry entry)
    {
        AudioFeatures missing = entry.Processor.RequiredFeatures & ~_device!.Capabilities.Features;
        if (missing != AudioFeatures.None)
        {
            entry.Disabled = true;
            _context.Logger.Warning($"Emission processor '{entry.Processor.Name}' of module '{entry.Registration.Module.Module.Id}' needs the audio features {missing}, which the device does not support; it is disabled.");
        }
    }

    private void AssignSlot(ReverbBus reverb)
    {
        if (!_device!.Capabilities.Supports(AudioFeatures.Effects))
        {
            _context.Logger.Warning($"Reverberation bus '{reverb.Name}' of module '{reverb.Module.Module.Id}' is ignored: the audio device has no effects.");
            return;
        }

        int slot = Array.IndexOf(_reverbSlotUsed, false);
        if (slot < 0)
        {
            _context.Logger.Warning($"Reverberation bus '{reverb.Name}' of module '{reverb.Module.Module.Id}' is ignored: the {_reverbSlotUsed.Length} reverberation slots of the audio device are taken.");
            return;
        }

        _reverbSlotUsed[slot] = true;
        reverb.Slot = slot;
        reverb.Current = reverb.Parameters;
        reverb.Changed = true;
    }

    private void RemoveReverb(ReverbBus reverb)
    {
        _reverbs.Remove(reverb);
        if (reverb.Slot >= 0)
        {
            _reverbSlotUsed[reverb.Slot] = false;
            _device?.ClearReverb(reverb.Slot);
            reverb.Slot = -1;
        }
    }

    /// <summary>A processor of the chain.</summary>
    internal sealed class ProcessorEntry(AudioRegistration registration, IEmissionProcessor processor)
    {
        public AudioRegistration Registration { get; } = registration;

        public IEmissionProcessor Processor { get; } = processor;

        public bool Disabled { get; set; }
    }
}
