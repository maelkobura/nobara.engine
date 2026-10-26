// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System.Text.RegularExpressions;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>
/// A range of module versions, as written in the dependencies of
/// <c>module.nobara</c>: caret (<c>^1.2</c>), tilde (<c>~1.2</c>), one
/// comparator (<c>&gt;= 2.0</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>), or a
/// lower and an upper comparator separated by spaces (<c>&gt;=1.0 &lt;2.0</c>).
/// </summary>
/// <remarks>A bare version such as <c>1.2.3</c> is not a range.</remarks>
public sealed class VersionRange
{
    /// <summary>The forms accepted, for error messages.</summary>
    public const string SupportedForms = "'^1.2', '~1.2', '>= 1.2', '> 1.2', '<= 2.0', '< 2.0', or '>=1.0 <2.0'";

    private const string VersionPattern = @"(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?<pre>-[0-9A-Za-z][0-9A-Za-z.-]*)?";

    private static readonly Regex Prefixed = new Regex(@"^(?<op>[\^~])\s*" + VersionPattern + "$", RegexOptions.CultureInvariant);

    private static readonly Regex Comparison = new Regex(
        @"^(?<op1>>=|>|<=|<)\s*(?<v1>[0-9][0-9A-Za-z.-]*)(?:\s+(?<op2>>=|>|<=|<)\s*(?<v2>[0-9][0-9A-Za-z.-]*))?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Plain = new Regex("^" + VersionPattern + "$", RegexOptions.CultureInvariant);

    private VersionRange(string text, string nuGet, SemanticVersion? lower, bool lowerInclusive, SemanticVersion? upper, bool upperInclusive)
    {
        Text = text;
        NuGet = nuGet;
        Lower = lower;
        LowerInclusive = lowerInclusive;
        Upper = upper;
        UpperInclusive = upperInclusive;
    }

    /// <summary>Gets the range as written.</summary>
    public string Text { get; }

    /// <summary>Gets the same range in NuGet interval notation, for example <c>[1.0,2.0.0)</c>.</summary>
    public string NuGet { get; }

    /// <summary>Gets the lower bound, or <see langword="null"/> when there is none.</summary>
    public SemanticVersion? Lower { get; }

    /// <summary>Gets whether <see cref="Lower"/> is part of the range.</summary>
    public bool LowerInclusive { get; }

    /// <summary>Gets the upper bound, or <see langword="null"/> when there is none.</summary>
    public SemanticVersion? Upper { get; }

    /// <summary>Gets whether <see cref="Upper"/> is part of the range.</summary>
    public bool UpperInclusive { get; }

    /// <summary>Parses a range.</summary>
    /// <param name="text">The range as written.</param>
    /// <param name="range">The range, or <see langword="null"/>.</param>
    /// <param name="error">The reason the range is invalid, or an empty string.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a valid range.</returns>
    public static bool TryParse(string text, out VersionRange? range, out string error)
    {
        range = null;
        error = string.Empty;
        string trimmed = text.Trim();

        Match match = Prefixed.Match(trimmed);
        if (match.Success)
        {
            string lowerText = trimmed.Substring(1).Trim();
            if (!SemanticVersion.TryParsePartial(lowerText, out SemanticVersion? lower))
            {
                error = $"the version in '{text}' is too large";
                return false;
            }

            bool hasMinor = match.Groups["minor"].Success;
            bool hasPatch = match.Groups["patch"].Success;
            SemanticVersion upper = match.Groups["op"].Value == "^"
                ? CaretUpper(lower!, hasMinor, hasPatch)
                : TildeUpper(lower!, hasMinor);
            range = new VersionRange(text, $"[{lowerText},{upper.Text})", lower, true, upper, false);
            return true;
        }

        match = Comparison.Match(trimmed);
        if (match.Success)
        {
            string v1 = match.Groups["v1"].Value;
            if (!Plain.IsMatch(v1) || !SemanticVersion.TryParsePartial(v1, out SemanticVersion? first))
            {
                error = $"'{v1}' is not a version";
                return false;
            }

            string op1 = match.Groups["op1"].Value;
            if (!match.Groups["op2"].Success)
            {
                range = op1 switch
                {
                    ">=" => new VersionRange(text, $"[{v1},)", first, true, null, false),
                    ">" => new VersionRange(text, $"({v1},)", first, false, null, false),
                    "<=" => new VersionRange(text, $"(,{v1}]", null, false, first, true),
                    _ => new VersionRange(text, $"(,{v1})", null, false, first, false),
                };
                return true;
            }

            string v2 = match.Groups["v2"].Value;
            string op2 = match.Groups["op2"].Value;
            if (!Plain.IsMatch(v2) || !SemanticVersion.TryParsePartial(v2, out SemanticVersion? second))
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

            string loOp = firstIsLower ? op1 : op2;
            string hiOp = firstIsLower ? op2 : op1;
            string lo = firstIsLower ? v1 : v2;
            string hi = firstIsLower ? v2 : v1;
            SemanticVersion low = firstIsLower ? first! : second!;
            SemanticVersion high = firstIsLower ? second! : first!;
            string nuGet = (loOp == ">=" ? "[" : "(") + lo + "," + hi + (hiOp == "<=" ? "]" : ")");
            range = new VersionRange(text, nuGet, low, loOp == ">=", high, hiOp == "<=");
            return true;
        }

        error = Plain.IsMatch(trimmed)
            ? $"'{text}' is a bare version, not a range; use one of {SupportedForms}"
            : $"'{text}' is not a version range; use one of {SupportedForms}";
        return false;
    }

    /// <summary>Tells whether a version is in the range.</summary>
    /// <param name="version">The version to test.</param>
    /// <returns><see langword="true"/> when <paramref name="version"/> satisfies both bounds.</returns>
    /// <remarks>Versions are ordered as in NuGet: <c>[1.0,2.0.0)</c> contains <c>2.0.0-beta</c>.</remarks>
    public bool Contains(SemanticVersion version)
    {
        if (Lower is not null)
        {
            int low = version.CompareTo(Lower);
            if (low < 0 || (low == 0 && !LowerInclusive))
            {
                return false;
            }
        }

        if (Upper is not null)
        {
            int high = version.CompareTo(Upper);
            if (high > 0 || (high == 0 && !UpperInclusive))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the range as written.</summary>
    /// <returns><see cref="Text"/>.</returns>
    public override string ToString() => Text;

    // The upper bound excludes the next version that may break compatibility:
    // the leftmost non-zero component is the one incremented.
    private static SemanticVersion CaretUpper(SemanticVersion lower, bool hasMinor, bool hasPatch)
    {
        if (lower.Major > 0)
        {
            return SemanticVersion.Of(lower.Major + 1, 0, 0);
        }

        if (!hasMinor)
        {
            return SemanticVersion.Of(1, 0, 0);
        }

        if (lower.Minor > 0)
        {
            return SemanticVersion.Of(0, lower.Minor + 1, 0);
        }

        return hasPatch ? SemanticVersion.Of(0, 0, lower.Patch + 1) : SemanticVersion.Of(0, 1, 0);
    }

    private static SemanticVersion TildeUpper(SemanticVersion lower, bool hasMinor) =>
        hasMinor ? SemanticVersion.Of(lower.Major, lower.Minor + 1, 0) : SemanticVersion.Of(lower.Major + 1, 0, 0);
}
