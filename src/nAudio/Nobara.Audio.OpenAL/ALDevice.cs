// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nobara.Core.Logging;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Nobara.Audio.OpenAL;

/// <summary>
/// An OpenAL Soft output device with its context: real voices are sources,
/// sounds in memory are buffers, a stream is a queue of buffers, filters and
/// reverberation slots come from EFX. OpenAL Soft mixes on a thread of its own,
/// the one exception to the threads of the engine (one thread, owned by the
/// library, that only reads what this class sets); every call here is made on
/// the loop thread, during the phase <c>Render</c>.
/// </summary>
internal sealed unsafe partial class ALDevice : AudioDevice
{
    /// <summary>The number of reverberation slots the device offers at most.</summary>
    internal const int MaxSlots = 4;

    // How often the connection of the device is read, in ticks of a stopwatch.
    private static readonly long ConnectionInterval = Stopwatch.Frequency / 4;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly SoftAlc _soft;
    private readonly Logger _logger;
    private readonly Action<Action>? _post;
    private readonly bool _loopback;
    private readonly string _description;
    private Device* _device;
    private Context* _context;
    private AudioCapabilities _capabilities = new() { Features = AudioFeatures.None, MaxVoices = 1, MaxReverbSlots = 0 };
    private int _errorsLogged;
    private long _nextConnectionCheck;
    private bool _disposed;

    // Set by a thread of OpenAL Soft when the default output changes; read on the loop thread.
    private int _defaultChanged;
    private GCHandle _self;

    private ALDevice(ALContext alc, AL al, SoftAlc soft, Device* device, Logger logger, Action<Action>? post, bool loopback)
    {
        _alc = alc;
        _al = al;
        _soft = soft;
        _device = device;
        _logger = logger;
        _post = post;
        _loopback = loopback;
        string? name = loopback ? null : _alc.GetContextProperty(device, GetContextString.DeviceSpecifier);
        _description = loopback ? "OpenAL Soft loopback" : $"OpenAL Soft: {name ?? "default output"}";
    }

    /// <inheritdoc/>
    public override AudioCapabilities Capabilities => _capabilities;

    /// <inheritdoc/>
    public override string Description => _description;

    /// <inheritdoc/>
    public override bool HrtfEnabled => QueryHrtf() == 1;

    /// <inheritdoc/>
    public override bool IsLost
    {
        get
        {
            if (Volatile.Read(ref _defaultChanged) != 0)
            {
                return true;
            }

            if (_loopback)
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            if (now < _nextConnectionCheck)
            {
                return false;
            }

            _nextConnectionCheck = now + ConnectionInterval;
            int connected = 1;
            _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.ConnectedAttribute, 1, &connected);
            return connected == 0;
        }
    }

    /// <summary>Gets the number of sources the device has created and not deleted yet.</summary>
    internal int LiveSources => _liveSources;

    /// <summary>Gets the number of buffers the device has created and not deleted yet.</summary>
    internal int LiveBuffers => _liveBuffers;

    /// <summary>Gets the number of filters, effects and effect slots the device has created and not deleted yet.</summary>
    internal int LiveEffectObjects => _liveEffectObjects;

    /// <summary>Opens the default output device of OpenAL Soft, or its loopback device for the tests.</summary>
    /// <param name="options">The options.</param>
    /// <param name="loopback">Whether to open the loopback device of OpenAL Soft, which plays nothing and renders on demand.</param>
    /// <returns>The device.</returns>
    /// <exception cref="NotSupportedException">The library cannot be loaded, or there is no output device.</exception>
    public static ALDevice Open(in AudioDeviceOptions options, bool loopback)
    {
        ALContext alc;
        AL al;
        Device* device;
        try
        {
            alc = ALContext.GetApi(soft: true);
            al = AL.GetApi(soft: true);
            SoftAlc global = SoftAlc.Load(alc, null);
            if (loopback)
            {
                if (global.LoopbackOpen == null)
                {
                    throw new NotSupportedException("this OpenAL Soft library has no loopback device");
                }

                device = global.LoopbackOpen(null);
            }
            else
            {
                device = alc.OpenDevice(null);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
        {
            throw new NotSupportedException($"the OpenAL Soft library cannot be loaded ({exception.Message})", exception);
        }

        if (device == null)
        {
            al.Dispose();
            alc.Dispose();
            throw new NotSupportedException("OpenAL Soft found no output device");
        }

        // The functions that need the device (HRTF) are loaded once it exists.
        var result = new ALDevice(alc, al, SoftAlc.Load(alc, device), device, options.Logger, options.Post, loopback);
        try
        {
            result.CreateContext(options.Hrtf);
            result.RegisterEvents();
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    /// <inheritdoc/>
    public override void Commit()
    {
        // Errors of the frame: the first few are logged, so that a fault shows without flooding the journal.
        AudioError error = _al.GetError();
        if (error != AudioError.NoError && _errorsLogged < 10)
        {
            _errorsLogged++;
            _logger.Warning($"OpenAL reported {error} during the frame.");
        }
    }

    /// <inheritdoc/>
    public override void SetHrtf(HrtfMode mode)
    {
        if (_soft.ResetDevice == null)
        {
            return;
        }

        int[] attributes = Attributes(mode);
        fixed (int* list = attributes)
        {
            if (_soft.ResetDevice(_device, list) == 0)
            {
                _logger.Warning($"OpenAL Soft cannot apply the HRTF mode {mode} to the device.");
                return;
            }
        }

        LogHrtf();
    }

    /// <summary>Renders frames of a loopback device: interleaved 32-bit floats, two channels.</summary>
    /// <param name="frames">The buffer, of two floats per frame.</param>
    internal void Render(Span<float> frames)
    {
        if (!_loopback)
        {
            throw new InvalidOperationException("Only a loopback device renders on demand.");
        }

        fixed (float* pointer = frames)
        {
            _soft.RenderSamples(_device, pointer, frames.Length / 2);
        }
    }

    /// <summary>Tells the device that the default output changed, as the library does from its own thread.</summary>
    internal void NotifyDefaultChanged() => Volatile.Write(ref _defaultChanged, 1);

    // From the thread of the library: the flag is set on the loop thread, at the synchronization point of the context.
    private void ReportDefaultChanged()
    {
        if (_post is null)
        {
            NotifyDefaultChanged();
            return;
        }

        _post(NotifyDefaultChanged);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterEvents();
        if (_context != null)
        {
            _alc.MakeContextCurrent(_context);
            ReleaseObjects();
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _context = null;
        }

        _alc.CloseDevice(_device);
        _device = null;
        _al.Dispose();
        _alc.Dispose();
    }

    private static int HrtfValue(HrtfMode mode) => mode switch
    {
        HrtfMode.On => 1,
        HrtfMode.Off => 0,
        _ => SoftAlc.DontCare,
    };

    /// <summary>Builds the list of attributes of the context: HRTF, one auxiliary send per source, and the format of the loopback.</summary>
    private int[] Attributes(HrtfMode hrtf)
    {
        var list = new List<int>(12);
        if (_soft.ResetDevice != null)
        {
            list.AddRange([SoftAlc.HrtfAttribute, HrtfValue(hrtf)]);
        }

        if (_sends > 0)
        {
            list.AddRange([SoftAlc.MaxAuxiliarySendsAttribute, _sends]);
        }

        if (_loopback)
        {
            list.AddRange([SoftAlc.FormatChannelsAttribute, SoftAlc.StereoChannels, SoftAlc.FormatTypeAttribute, SoftAlc.FloatType, SoftAlc.FrequencyAttribute, 48000]);
        }

        list.Add(0);
        return [.. list];
    }

    private void CreateContext(HrtfMode hrtf)
    {
        bool hasEfx = _alc.IsExtensionPresent(_device, "ALC_EXT_EFX");
        _sends = hasEfx ? 1 : 0;
        int[] attributes = Attributes(hrtf);
        fixed (int* list = attributes)
        {
            _context = _alc.CreateContext(_device, list);
        }

        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            throw new NotSupportedException("OpenAL Soft cannot create an audio context on the device");
        }

        // nAudio computes the gain of distance, buses and processors: the library applies it as it is.
        _hasSpatialize = _al.IsExtensionPresent("AL_SOFT_source_spatialize");
        _al.DistanceModel(DistanceModel.None);
        _al.SetListenerProperty(ListenerVector3.Position, 0, 0, 0);

        int mono = 0, stereo = 0;
        _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.MonoSourcesAttribute, 1, &mono);
        _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.StereoSourcesAttribute, 1, &stereo);
        int voices = Math.Max(1, mono + stereo);
        _voices = new VoiceState[voices];

        AudioFeatures features = _soft.ResetDevice != null ? AudioFeatures.Hrtf : AudioFeatures.None;
        int slots = 0;
        if (hasEfx && _al.TryGetExtension(out _efx))
        {
            int granted = 0;
            _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.MaxAuxiliarySendsAttribute, 1, &granted);
            _sends = granted;
            if (SupportsLowPass())
            {
                features |= AudioFeatures.Filters;
            }

            if (granted > 0)
            {
                features |= AudioFeatures.AuxiliarySends;
                slots = CreateSlots();
                if (slots > 0)
                {
                    features |= AudioFeatures.Effects;
                }
            }
        }
        else
        {
            _sends = 0;
        }

        _capabilities = new AudioCapabilities { Features = features, MaxVoices = voices, MaxReverbSlots = slots };
        LogHrtf();
    }

    private int QueryHrtf()
    {
        if (_soft.ResetDevice == null || _device == null)
        {
            return 0;
        }

        int enabled = 0;
        _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.HrtfAttribute, 1, &enabled);
        return enabled;
    }

    private void LogHrtf()
    {
        if (_soft.ResetDevice == null)
        {
            _logger.Debug("OpenAL Soft: this library has no HRTF.");
            return;
        }

        int status = 0;
        _alc.GetContextProperty(_device, (GetContextInteger)SoftAlc.HrtfStatusAttribute, 1, &status);
        string text = status switch
        {
            0 => "disabled",
            1 => "enabled",
            2 => "denied by the library",
            3 => "required by the output",
            4 => "enabled, headphones detected",
            5 => "not available for this output format",
            _ => $"status {status}",
        };
        _logger.Debug($"OpenAL Soft HRTF: {text}.");
    }

    /// <summary>
    /// Registers the callback with which OpenAL Soft reports a change of the
    /// default output. The library keeps one callback: the device opened last
    /// receives it, and the others read their connection instead.
    /// </summary>
    private void RegisterEvents()
    {
        if (_loopback || _soft.EventControl == null || _soft.EventCallback == null)
        {
            return;
        }

        _self = GCHandle.Alloc(this);
        _soft.EventCallback(&OnSystemEvent, (void*)GCHandle.ToIntPtr(_self));
        int type = SoftAlc.DefaultDeviceChangedEvent;
        if (_soft.EventControl(1, &type, 1) == 0)
        {
            _logger.Debug("OpenAL Soft does not report changes of the default output; nAudio reads the connection of the device instead.");
        }
    }

    // The library has one callback for the process: clearing it here would clear that of a device opened since.
    // nAudio closes a device before it opens the next one, so no other device holds it.
    private void UnregisterEvents()
    {
        if (!_self.IsAllocated)
        {
            return;
        }

        int type = SoftAlc.DefaultDeviceChangedEvent;
        _soft.EventControl(1, &type, 0);
        _soft.EventCallback(null, null);
        _self.Free();
    }

    // Called by a thread of OpenAL Soft: it only sets a flag, which the loop thread reads.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSystemEvent(int eventType, int deviceType, Device* device, int length, byte* message, void* userParam)
    {
        if (eventType == SoftAlc.DefaultDeviceChangedEvent && deviceType == SoftAlc.PlaybackDeviceType
            && GCHandle.FromIntPtr((IntPtr)userParam).Target is ALDevice self)
        {
            self.ReportDefaultChanged();
        }
    }
}
