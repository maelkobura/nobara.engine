// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Compression;
using System.Text;

namespace Nobara.Cli;

/// <summary>
/// The native launcher of the game. <c>dotnet publish</c> writes it next to
/// the entry assembly; the installed game keeps it at its root and the
/// runtime in <c>bin/</c>, so the launcher is changed to start
/// <c>bin/&lt;entry&gt;.dll</c>.
/// </summary>
internal static class AppHost
{
    // The launcher holds the path of the application in a zero-filled block of 1024 bytes.
    private const int PathCapacity = 1024;
    private const int Padding = 32;

    /// <summary>Copies a launcher and makes it start the entry assembly in <c>bin/</c>.</summary>
    /// <returns>Why it cannot, or <see langword="null"/> on success.</returns>
    public static string? Install(string source, string target, string entryAssembly)
    {
        byte[] content = File.ReadAllBytes(source);
        byte[] current = Encoding.UTF8.GetBytes(entryAssembly);
        byte[] search = [.. current, .. new byte[Padding]];
        int index = content.AsSpan().IndexOf(search);
        if (index < 0 || content.AsSpan(index + 1).IndexOf(search) >= 0)
        {
            return $"'{source}' does not hold the path '{entryAssembly}' once";
        }

        byte[] replacement = Encoding.UTF8.GetBytes("bin/" + entryAssembly);
        if (replacement.Length >= PathCapacity)
        {
            return $"the path 'bin/{entryAssembly}' is too long for the launcher";
        }

        replacement.CopyTo(content, index);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
        return null;
    }
}

/// <summary>Packs an installed game into a zip archive.</summary>
internal static class Packager
{
    /// <summary>Writes the archive: every file of the folder, under one root folder.</summary>
    public static void Zip(string folder, string archive, string rootName)
    {
        if (File.Exists(archive))
        {
            File.Delete(archive);
        }

        using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            zip.CreateEntryFromFile(file, rootName + "/" + Names.Relative(folder, file), CompressionLevel.Optimal);
        }
    }
}
