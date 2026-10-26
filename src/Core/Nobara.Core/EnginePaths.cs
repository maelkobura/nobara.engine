// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core;

/// <summary>
/// The data folders of a game. Modules never build a path themselves: they
/// use these folders, which point to the user data of the operating system on
/// a client and to the folder of the server on a server.
/// </summary>
/// <remarks>
/// The folders are not created in advance; code that writes to one creates it
/// when it is missing. The code folders of the game stay read-only.
/// </remarks>
public sealed class EnginePaths
{
    private EnginePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the folder that contains every data folder.</summary>
    public string Root { get; }

    /// <summary>Gets the folder of configuration files, such as the settings of the player.</summary>
    public string Config => Path.Combine(Root, "config");

    /// <summary>Gets the folder of saved worlds.</summary>
    public string Worlds => Path.Combine(Root, "worlds");

    /// <summary>Gets the folder of mods.</summary>
    public string Mods => Path.Combine(Root, "mods");

    /// <summary>Gets the folder of caches, which can be deleted at any time.</summary>
    public string Cache => Path.Combine(Root, "cache");

    /// <summary>Gets the folder of logs and crash reports.</summary>
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Gets the folder of player profiles.</summary>
    public string Profiles => Path.Combine(Root, "profiles");

    /// <summary>Gets the folders of a client: a folder named after the game in the user data of the operating system.</summary>
    /// <param name="gameName">The display name of the game.</param>
    /// <returns>The folders, for example under <c>%APPDATA%</c> on Windows.</returns>
    /// <exception cref="ArgumentException"><paramref name="gameName"/> is empty or is not a valid folder name.</exception>
    public static EnginePaths ForClient(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName) || gameName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{gameName}' is not a valid folder name.", nameof(gameName));
        }

        string userData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return new EnginePaths(Path.Combine(userData, gameName));
    }

    /// <summary>Gets the folders of a dedicated server: the folder of the server itself.</summary>
    /// <param name="serverDirectory">The folder where the server is installed.</param>
    /// <returns>The folders, such as <c>config/</c> and <c>worlds/</c> in <paramref name="serverDirectory"/>.</returns>
    public static EnginePaths ForServer(string serverDirectory) => new(serverDirectory);

    /// <summary>Gets folders under any root, for tools and tests.</summary>
    /// <param name="root">The folder that contains every data folder.</param>
    /// <returns>The folders.</returns>
    public static EnginePaths At(string root) => new(root);
}
