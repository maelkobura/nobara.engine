// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Silk.NET.OpenAL;

namespace Nobara.Audio.OpenAL;

/// <summary>
/// The functions of the extensions of OpenAL Soft that the bindings of Silk.NET
/// do not wrap (HRTF, loopback, system events), loaded by name.
/// </summary>
internal sealed unsafe class SoftAlc
{
    // Attributes and values of ALC_SOFT_HRTF, ALC_SOFT_loopback, ALC_EXT_EFX, ALC_EXT_disconnect.
    public const int HrtfAttribute = 0x1992;
    public const int HrtfStatusAttribute = 0x1993;
    public const int DontCare = 2;
    public const int MaxAuxiliarySendsAttribute = 0x20003;
    public const int MonoSourcesAttribute = 0x1010;
    public const int StereoSourcesAttribute = 0x1011;
    public const int FrequencyAttribute = 0x1007;
    public const int ConnectedAttribute = 0x313;
    public const int FormatChannelsAttribute = 0x1990;
    public const int FormatTypeAttribute = 0x1991;
    public const int StereoChannels = 0x1501;
    public const int FloatType = 0x1406;

    // ALC_SOFT_system_events.
    public const int DefaultDeviceChangedEvent = 0x19D6;
    public const int PlaybackDeviceType = 0x19D4;

    // AL_SOFT_source_spatialize.
    public const int SourceSpatializeAttribute = 0x1214;

    public delegate* unmanaged[Cdecl]<Device*, int*, byte> ResetDevice;
    public delegate* unmanaged[Cdecl]<byte*, Device*> LoopbackOpen;
    public delegate* unmanaged[Cdecl]<Device*, void*, int, void> RenderSamples;
    public delegate* unmanaged[Cdecl]<int, int*, byte, byte> EventControl;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, Device*, int, byte*, void*, void>, void*, void> EventCallback;

    /// <summary>Loads the functions of the extensions that the library has.</summary>
    /// <param name="alc">The context API.</param>
    /// <param name="device">The device, or <see langword="null"/> to load the functions that do not need one.</param>
    public static SoftAlc Load(ALContext alc, Device* device)
    {
        var soft = new SoftAlc();
        if (alc.IsExtensionPresent(device, "ALC_SOFT_HRTF"))
        {
            soft.ResetDevice = (delegate* unmanaged[Cdecl]<Device*, int*, byte>)alc.GetProcAddress(device, "alcResetDeviceSOFT");
        }

        if (alc.IsExtensionPresent(null, "ALC_SOFT_loopback"))
        {
            soft.LoopbackOpen = (delegate* unmanaged[Cdecl]<byte*, Device*>)alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
            soft.RenderSamples = (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)alc.GetProcAddress(null, "alcRenderSamplesSOFT");
        }

        if (alc.IsExtensionPresent(null, "ALC_SOFT_system_events"))
        {
            soft.EventControl = (delegate* unmanaged[Cdecl]<int, int*, byte, byte>)alc.GetProcAddress(null, "alcEventControlSOFT");
            soft.EventCallback = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, Device*, int, byte*, void*, void>, void*, void>)alc.GetProcAddress(null, "alcEventCallbackSOFT");
        }

        return soft;
    }
}
