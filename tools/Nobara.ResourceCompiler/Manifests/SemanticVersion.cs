// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>A semantic version: major, minor, and patch numbers, and an optional pre-release suffix.</summary>
/// <remarks>Build metadata (after <c>+</c>) is kept in <see cref="Text"/> and ignored when comparing.</remarks>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Full = new Regex(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(-(?<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?(\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Partial = new Regex(
        @"^(?<major>\d+)(\.(?<minor>\d+))?(\.(?<patch>\d+))?(-(?<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?$",
        RegexOptions.CultureInvariant);

    private SemanticVersion(long major, long minor, long patch, string prerelease, string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Text = text;
    }

    /// <summary>Gets the major number.</summary>
    public long Major { get; }

    /// <summary>Gets the minor number.</summary>
    public long Minor { get; }

    /// <summary>Gets the patch number.</summary>
    public long Patch { get; }

    /// <summary>Gets the pre-release suffix without its dash, or an empty string.</summary>
    public string Prerelease { get; }

    /// <summary>Gets the version as written.</summary>
    public string Text { get; }

    /// <summary>Parses a full version: <c>1.2.0</c>, <c>2.0.0-beta.1</c>, <c>1.0.0+7</c>.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="version">The version, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a semantic version.</returns>
    public static bool TryParse(string text, out SemanticVersion? version)
    {
        version = null;
        Match match = Full.Match(text);
        if (!match.Success || !TryNumbers(match, out long major, out long minor, out long patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, match.Groups["pre"].Value, text);
        return true;
    }

    /// <summary>
    /// Parses a version whose minor and patch numbers may be omitted, as in a
    /// range: <c>1</c> and <c>1.2</c> stand for <c>1.0.0</c> and <c>1.2.0</c>.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="version">The version, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a version.</returns>
    public static bool TryParsePartial(string text, out SemanticVersion? version)
    {
        version = null;
        Match match = Partial.Match(text);
        if (!match.Success || !TryNumbers(match, out long major, out long minor, out long patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, match.Groups["pre"].Value, text);
        return true;
    }

    /// <summary>Creates a release version from its numbers.</summary>
    /// <param name="major">The major number, zero or more.</param>
    /// <param name="minor">The minor number, zero or more.</param>
    /// <param name="patch">The patch number, zero or more.</param>
    /// <returns>The version <c>major.minor.patch</c>.</returns>
    public static SemanticVersion Of(long major, long minor, long patch) =>
        new SemanticVersion(major, minor, patch, string.Empty, $"{major}.{minor}.{patch}");

    /// <inheritdoc/>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Returns the version as written.</summary>
    /// <returns><see cref="Text"/>.</returns>
    public override string ToString() => Text;

    // A version without a pre-release suffix is greater than the same
    // version with one; identifiers compare numerically when both are numbers.
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == 0 ? 1 : -1;
        }

        string[] a = left.Split('.');
        string[] b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumber = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long x);
            bool bNumber = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long y);
            int result = aNumber && bNumber ? x.CompareTo(y)
                : aNumber ? -1
                : bNumber ? 1
                : string.CompareOrdinal(a[i], b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static bool TryNumbers(Match match, out long major, out long minor, out long patch)
    {
        minor = 0;
        patch = 0;
        return long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && (!match.Groups["minor"].Success || long.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            && (!match.Groups["patch"].Success || long.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch));
    }
}
