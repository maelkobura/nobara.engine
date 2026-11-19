// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nobara.Audio.OpenAL.Tests.Support;
using Nobara.Core;

namespace Nobara.Audio.OpenAL.Tests;

/// <summary>Where the backend finds the native OpenAL Soft library.</summary>
public sealed class LibraryTests : IDisposable
{
    // Under the output folder: a copy of the library that stays loaded is cleaned with the build, not left in the temporary folder.
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "library-tests", Guid.NewGuid().ToString("N"));

    public LibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The copy of the library stays loaded until the process ends.
        }
    }

    [Fact]
    public void Finds_the_library_in_a_folder_that_holds_it()
    {
        // An installed game: the native library and the bindings side by side in lib/.
        string lib = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        File.WriteAllBytes(Path.Combine(lib, OpenALLibrary.FileName), []);

        Assert.Equal(Path.Combine(lib, OpenALLibrary.FileName), OpenALLibrary.FindIn(lib));
    }

    [Fact]
    public void Leaves_the_search_to_the_bindings_when_the_folder_lacks_the_library()
    {
        Assert.Null(OpenALLibrary.FindIn(_root));
        Assert.Null(OpenALLibrary.FindIn(null));
        Assert.Null(OpenALLibrary.FindIn(string.Empty));
    }

    [Fact]
    public void The_file_names_are_those_of_the_native_package()
    {
        Assert.Equal("soft_oal.dll", OpenALLibrary.GetFileName(windows: true, macOS: false));
        Assert.Equal("libopenal.dylib", OpenALLibrary.GetFileName(windows: false, macOS: true));
        Assert.Equal("libopenal.so", OpenALLibrary.GetFileName(windows: false, macOS: false));
    }

    [Fact]
    public void Opens_the_device_with_the_library_of_a_folder_out_of_the_search_of_the_bindings()
    {
        // The library of the native package, copied where Silk.NET would not look for it.
        string package = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", OpenALLibrary.FileName);
        Assert.True(File.Exists(package), $"the native package ships no library at {package}");
        string lib = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        File.Copy(package, Path.Combine(lib, OpenALLibrary.FileName));
        string? path = OpenALLibrary.FindIn(lib);
        Assert.NotNull(path);

        using var harness = new OpenALHarness(library: path);
        using Engine engine = harness.Start();

        // The extensions resolve through the same library: EFX gives the effects.
        Assert.True(harness.System.IsActive);
        ALDevice device = harness.Backend.Opened!;
        Assert.True(device.Capabilities.Features.HasFlag(AudioFeatures.Effects));
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(
                Process.GetCurrentProcess().Modules.Cast<ProcessModule>(),
                module => string.Equals(module.FileName, path, StringComparison.OrdinalIgnoreCase));
        }
    }
}
