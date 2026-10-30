// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Nobara.Core;

namespace Nobara.Items;

/// <summary>One reason why an item cannot be loaded.</summary>
/// <param name="ItemId">The object identifier of the item.</param>
/// <param name="Message">What is wrong, naming the component and the value.</param>
/// <param name="Source">Where the problem is written in the <c>.nof</c> file: the value, the component, or the item.</param>
public sealed record ItemProblem(string ItemId, string Message, SourcePosition Source)
{
    /// <summary>Returns the problem as a compiler writes it.</summary>
    /// <returns>For example <c>Resources/food.nof(9,9): item:example.food.steak: ...</c>.</returns>
    public override string ToString() =>
        string.IsNullOrEmpty(Source.File) ? $"{ItemId}: {Message}" : $"{Source}: {ItemId}: {Message}";
}

/// <summary>
/// Thrown when nItems starts and the items cannot be loaded: an unknown
/// component, a value of the wrong type, a missing value, or a field that the
/// component does not have. The start of the context fails.
/// </summary>
/// <remarks>Every problem names the item, the component, and the position in the <c>.nof</c> file.</remarks>
public sealed class ItemLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public ItemLoadException(IReadOnlyList<ItemProblem> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<ItemProblem> Problems { get; }

    private static string Describe(IReadOnlyList<ItemProblem> problems)
    {
        var text = new StringBuilder("The items cannot be loaded:");
        foreach (ItemProblem problem in problems)
        {
            text.AppendLine().Append("- ").Append(problem.ToString());
        }

        return text.ToString();
    }
}
