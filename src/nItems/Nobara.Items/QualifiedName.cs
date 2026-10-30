// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Items;

/// <summary>The names qualified by the identifier of a module, such as <c>example.food.edible</c>.</summary>
internal static class QualifiedName
{
    /// <summary>Checks that a name is the identifier of a module, a dot, and lowercase names separated by dots.</summary>
    /// <param name="moduleId">The module that names the thing.</param>
    /// <param name="name">The name.</param>
    /// <param name="what">What is named, for the message.</param>
    /// <exception cref="ArgumentException">The name is not of that form.</exception>
    public static void Check(string moduleId, string name, string what)
    {
        string prefix = moduleId + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !IsLowercasePath(name.AsSpan(prefix.Length)))
        {
            throw new ArgumentException(
                $"The {what} '{name}' of module '{moduleId}' must be named '{prefix}' followed by lowercase letters, digits, and underscores, such as '{prefix}example'.",
                nameof(name));
        }
    }

    /// <summary>Finds the module of a distribution whose identifier prefixes a name.</summary>
    /// <param name="name">A qualified name.</param>
    /// <param name="modules">The identifiers of the modules of the distribution.</param>
    /// <returns>The longest identifier followed by a dot at the start of the name, or <see langword="null"/>.</returns>
    public static string? FindOwner(string name, IEnumerable<string> modules) =>
        modules
            .Where(m => name.Length > m.Length + 1 && name.StartsWith(m, StringComparison.Ordinal) && name[m.Length] == '.')
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();

    private static bool IsLowercasePath(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text[0] == '.' || text[^1] == '.')
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool valid = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' || (c == '.' && text[i - 1] != '.');
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
