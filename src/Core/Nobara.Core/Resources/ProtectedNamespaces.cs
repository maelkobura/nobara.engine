// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Resources;

/// <summary>
/// The namespaces that the distribution protects. A module loaded
/// dynamically, such as a mod, can neither define nor override a resource in a
/// protected namespace; the modules of the distribution itself are not
/// concerned.
/// </summary>
/// <remarks>
/// A pattern is a namespace, such as <c>Example/Blocks</c>, or a namespace
/// followed by <c>/*</c>, such as <c>Example/*</c>, which also covers the
/// namespace itself and every namespace under it. Namespaces are compared
/// without regard to case, like the object identifiers derived from them.
/// </remarks>
public sealed class ProtectedNamespaces
{
    private readonly string[] _patterns;

    /// <summary>Initializes the protection from the patterns of the distribution.</summary>
    /// <param name="patterns">The patterns, as <c>&lt;ProtectedNamespaces&gt;</c> lists them.</param>
    public ProtectedNamespaces(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }

    /// <summary>Gets the patterns, as the distribution lists them.</summary>
    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary>Tells whether a namespace is protected.</summary>
    /// <param name="ns">A namespace as written in a <c>.nof</c> file: <c>Example/Blocks</c>.</param>
    /// <returns><see langword="true"/> when a pattern covers <paramref name="ns"/>.</returns>
    public bool IsProtected(string ns)
    {
        ArgumentNullException.ThrowIfNull(ns);
        foreach (string pattern in _patterns)
        {
            if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                string prefix = pattern[..^2];
                if (string.Equals(ns, prefix, StringComparison.OrdinalIgnoreCase)
                    || (ns.Length > prefix.Length && ns[prefix.Length] == '/' && ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (string.Equals(ns, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
