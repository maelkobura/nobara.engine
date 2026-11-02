// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Silk.NET.SDL;

namespace Nobara.Frames.SDL2;

/// <summary>
/// Finds and loads the native SDL2 library. An installed game keeps it in
/// <c>lib/</c>, next to the bindings, where the library loader of Silk.NET
/// does not look: the backend gives it the full path when the file is there.
/// </summary>
internal static class Sdl2Library
{
    /// <summary>Gets the file name of the native library on the running platform.</summary>
    public static string FileName => GetFileName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    /// <summary>Returns the file name of the native library, as the native package of SDL2 ships it.</summary>
    /// <param name="windows">Whether the platform is Windows.</param>
    /// <param name="macOS">Whether the platform is macOS; Linux otherwise.</param>
    public static string GetFileName(bool windows, bool macOS) =>
        windows ? "SDL2.dll" : macOS ? "libSDL2-2.0.dylib" : "libSDL2-2.0.so";

    /// <summary>Loads the bindings: from the folder of the bindings when the library is there, otherwise by the search of Silk.NET.</summary>
    public static Sdl Load()
    {
        string? path = FindIn(Path.GetDirectoryName(typeof(Sdl).Assembly.Location));
        return path is null ? Sdl.GetApi() : new Sdl(Sdl.CreateDefaultContext([path]));
    }

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
}
