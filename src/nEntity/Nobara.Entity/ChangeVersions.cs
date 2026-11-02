// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// Compares change versions. A version is a 32-bit counter of a world that
/// goes up at each run of a system, and wraps around after 2^32 runs: compare
/// versions with these methods, never with <c>&gt;</c>.
/// </summary>
/// <remarks>
/// The world brings versions older than <see cref="MaxAge"/> up to that age
/// from time to time, so that a version stays comparable however long the
/// world lives. A reader that has not read for longer than
/// <see cref="MaxAge"/> versions sees every tracked value as changed.
/// </remarks>
[HotPath]
public static class ChangeVersions
{
    /// <summary>The age, in versions, beyond which versions are brought up to that age: 2^30.</summary>
    public const uint MaxAge = 1u << 30;

    /// <summary>Tells whether a version is newer than another.</summary>
    /// <param name="version">A version, such as the version of a value.</param>
    /// <param name="since">Another version, such as the version of the last read.</param>
    /// <returns><see langword="true"/> when <paramref name="version"/> comes after <paramref name="since"/>.</returns>
    public static bool IsNewer(uint version, uint since) => (int)(version - since) > 0;

    /// <summary>Tells whether a value written at <paramref name="version"/> changed since <paramref name="since"/>, in a world now at <paramref name="current"/>.</summary>
    /// <param name="version">The version of the value.</param>
    /// <param name="since">The version of the last read.</param>
    /// <param name="current">The current version of the world.</param>
    /// <returns><see langword="true"/> when the value changed after the last read, or the last read is older than <see cref="MaxAge"/>.</returns>
    public static bool ChangedSince(uint version, uint since, uint current) =>
        current - since >= MaxAge || IsNewer(version, since);

    /// <summary>Tells whether a version is older than another, both seen from the current version.</summary>
    internal static bool IsOlder(uint version, uint other, uint current) => current - version > current - other;
}
