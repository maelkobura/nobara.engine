// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Silk.NET.Core.Contexts;
using Silk.NET.OpenAL;

namespace Nobara.Audio.OpenAL;

/// <summary>
/// Finds and loads the native OpenAL Soft library. An installed game keeps it
/// in <c>lib/</c>, next to the bindings, where the library loader of Silk.NET
/// does not look: the backend gives it the full path when the file is there.
/// </summary>
internal static class OpenALLibrary
{
    /// <summary>Gets the file name of the native library on the running platform.</summary>
    public static string FileName => GetFileName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    /// <summary>Returns the file name of the native library, as the native package of OpenAL Soft ships it.</summary>
    /// <param name="windows">Whether the platform is Windows.</param>
    /// <param name="macOS">Whether the platform is macOS; Linux otherwise.</param>
    public static string GetFileName(bool windows, bool macOS) =>
        windows ? "soft_oal.dll" : macOS ? "libopenal.dylib" : "libopenal.so";

    /// <summary>Returns the full path of the native library in the folder of the bindings, or <see langword="null"/> when it is not there.</summary>
    public static string? Find() => FindIn(Path.GetDirectoryName(typeof(AL).Assembly.Location));

    /// <summary>Returns the full path of the native library in a folder, or <see langword="null"/> when it is not there.</summary>
    /// <param name="directory">The folder, or <see langword="null"/>.</param>
    public static string? FindIn(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string path = Path.Combine(directory, FileName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>Loads the context API: from a full path, otherwise by the search of Silk.NET.</summary>
    /// <param name="path">The full path of the native library, or <see langword="null"/>.</param>
    public static unsafe ALContext LoadContext(string? path)
    {
        if (path is null)
        {
            return ALContext.GetApi(soft: true);
        }

        // As ALContext.GetApi does: the functions that the library does not
        // export are asked from the device of the current context.
        var native = new MultiNativeContext(ALContext.CreateDefaultContext([path]), null);
        var alc = new ALContext(native);
        native.Contexts[1] = new LamdaNativeContext(name =>
            name.EndsWith("GetProcAddress", StringComparison.Ordinal)
            || name.EndsWith("GetContextsDevice", StringComparison.Ordinal)
            || name.EndsWith("GetCurrentContext", StringComparison.Ordinal)
                ? default
                : (nint)alc.GetProcAddress(alc.GetContextsDevice(alc.GetCurrentContext()), name));
        return alc;
    }

    /// <summary>Loads the API of the sources and buffers: from a full path, otherwise by the search of Silk.NET.</summary>
    /// <param name="path">The full path of the native library, or <see langword="null"/>.</param>
    public static AL LoadAL(string? path)
    {
        if (path is null)
        {
            return AL.GetApi(soft: true);
        }

        // As AL.GetApi does: the functions that the library does not export
        // are asked from alGetProcAddress.
        var native = new MultiNativeContext(AL.CreateDefaultContext([path]), null);
        var al = new AL(native);
        native.Contexts[1] = new LamdaNativeContext(name =>
            name.EndsWith("GetProcAddress", StringComparison.Ordinal) ? default : al.GetProcAddress(name));
        return al;
    }
}
