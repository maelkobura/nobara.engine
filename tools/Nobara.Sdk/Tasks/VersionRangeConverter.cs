// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Nobara.Sdk.Tasks;

/// <summary>
/// Converts the version ranges written in <c>module.nobara</c> to NuGet
/// interval notation.
/// </summary>
/// <remarks>
/// Supported forms: caret (<c>^1.2</c>), tilde (<c>~1.2</c>), one comparator
/// (<c>&gt;= 2.0</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>) and a lower plus an upper comparator
/// separated by spaces (<c>&gt;=1.0 &lt;2.0</c>). A bare version is not a range.
/// </remarks>
internal static class VersionRangeConverter
{
    /// <summary>The forms accepted, for error messages.</summary>
    public const string SupportedForms = "'^1.2', '~1.2', '>= 1.2', '> 1.2', '<= 2.0', '< 2.0', or '>=1.0 <2.0'";

    private const string VersionPattern = @"(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?<pre>-[0-9A-Za-z][0-9A-Za-z.-]*)?";

    private static readonly Regex Prefixed = new(@"^(?<op>[\^~])\s*" + VersionPattern + "$", RegexOptions.CultureInvariant);

    private static readonly Regex Comparison = new(
        @"^(?<op1>>=|>|<=|<)\s*(?<v1>[0-9][0-9A-Za-z.-]*)(?:\s+(?<op2>>=|>|<=|<)\s*(?<v2>[0-9][0-9A-Za-z.-]*))?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Plain = new("^" + VersionPattern + "$", RegexOptions.CultureInvariant);

    /// <summary>Converts <paramref name="range"/> to NuGet notation.</summary>
    /// <param name="range">The range as written in the manifest.</param>
    /// <param name="nuget">The NuGet interval, for example <c>[1.0,2.0.0)</c>.</param>
    /// <param name="error">The reason the range is invalid, or an empty string.</param>
    /// <returns><see langword="true"/> when the range is valid.</returns>
    public static bool TryConvert(string range, out string nuget, out string error)
    {
        nuget = string.Empty;
        error = string.Empty;
        string text = range.Trim();

        Match match = Prefixed.Match(text);
        if (match.Success)
        {
            if (!TryParts(match, out long major, out long? minor))
            {
                error = $"the version in '{range}' is too large";
                return false;
            }

            string lower = text.Substring(1).Trim();
            string upper = match.Groups["op"].Value == "^"
                ? CaretUpper(major, minor, match.Groups["patch"].Success ? long.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture) : null)
                : TildeUpper(major, minor);
            nuget = $"[{lower},{upper})";
            return true;
        }

        match = Comparison.Match(text);
        if (match.Success)
        {
            string v1 = match.Groups["v1"].Value;
            if (!Plain.IsMatch(v1))
            {
                error = $"'{v1}' is not a version";
                return false;
            }

            string op1 = match.Groups["op1"].Value;
            if (!match.Groups["op2"].Success)
            {
                nuget = op1 switch
                {
                    ">=" => $"[{v1},)",
                    ">" => $"({v1},)",
                    "<=" => $"(,{v1}]",
                    _ => $"(,{v1})",
                };
                return true;
            }

            string v2 = match.Groups["v2"].Value;
            string op2 = match.Groups["op2"].Value;
            if (!Plain.IsMatch(v2))
            {
                error = $"'{v2}' is not a version";
                return false;
            }

            bool firstIsLower = op1[0] == '>';
            bool secondIsLower = op2[0] == '>';
            if (firstIsLower == secondIsLower)
            {
                error = "a range needs one lower and one upper comparator";
                return false;
            }

            (string loOp, string lo, string hiOp, string hi) = firstIsLower ? (op1, v1, op2, v2) : (op2, v2, op1, v1);
            nuget = (loOp == ">=" ? "[" : "(") + lo + "," + hi + (hiOp == "<=" ? "]" : ")");
            return true;
        }

        error = Plain.IsMatch(text)
            ? $"'{range}' is a bare version, not a range; use one of {SupportedForms}"
            : $"'{range}' is not a version range; use one of {SupportedForms}";
        return false;
    }

    private static bool TryParts(Match match, out long major, out long? minor)
    {
        minor = null;
        if (!long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major))
        {
            return false;
        }

        if (match.Groups["minor"].Success)
        {
            if (!long.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long m))
            {
                return false;
            }

            minor = m;
        }

        return !match.Groups["patch"].Success
            || long.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    // The upper bound excludes the next version that may break compatibility:
    // the leftmost non-zero component is the one incremented.
    private static string CaretUpper(long major, long? minor, long? patch)
    {
        if (major > 0)
        {
            return $"{major + 1}.0.0";
        }

        if (minor is null)
        {
            return "1.0.0";
        }

        if (minor > 0)
        {
            return $"0.{minor + 1}.0";
        }

        if (patch is null)
        {
            return "0.1.0";
        }

        return $"0.0.{patch + 1}";
    }

    private static string TildeUpper(long major, long? minor)
    {
        return minor is null ? $"{major + 1}.0.0" : $"{major}.{minor + 1}.0";
    }
}
