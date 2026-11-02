// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames.SDL2.Tests;

/// <summary>Where the backend finds the native SDL2 library; no window is opened.</summary>
public sealed class Sdl2LibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nobara-sdl2-library", Guid.NewGuid().ToString("N"));

    public Sdl2LibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_the_library_in_a_folder_that_holds_it()
    {
        // An installed game: the native library and the bindings side by side in lib/.
        string lib = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        File.WriteAllBytes(Path.Combine(lib, Sdl2Library.FileName), []);

        Assert.Equal(Path.Combine(lib, Sdl2Library.FileName), Sdl2Library.FindIn(lib));
    }

    [Fact]
    public void Leaves_the_search_to_the_bindings_when_the_folder_lacks_the_library()
    {
        Assert.Null(Sdl2Library.FindIn(_root));
        Assert.Null(Sdl2Library.FindIn(null));
        Assert.Null(Sdl2Library.FindIn(string.Empty));
    }

    [Fact]
    public void The_file_names_are_those_of_the_native_package()
    {
        Assert.Equal("SDL2.dll", Sdl2Library.GetFileName(windows: true, macOS: false));
        Assert.Equal("libSDL2-2.0.dylib", Sdl2Library.GetFileName(windows: false, macOS: true));
        Assert.Equal("libSDL2-2.0.so", Sdl2Library.GetFileName(windows: false, macOS: false));
    }
}
