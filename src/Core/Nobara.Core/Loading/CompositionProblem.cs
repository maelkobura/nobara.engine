// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace Nobara.Core.Loading;

/// <summary>Why the core refuses a composition of modules.</summary>
public enum CompositionProblemKind
{
    /// <summary>The distribution manifest is missing, unreadable, or of an unsupported format.</summary>
    InvalidDistribution,

    /// <summary>The assembly of a module is not where the distribution expects it.</summary>
    MissingAssembly,

    /// <summary>The assembly of a module differs from the one the distribution was built with.</summary>
    IntegrityMismatch,

    /// <summary>The manifest of a module is missing, unreadable, or does not match the distribution.</summary>
    InvalidManifest,

    /// <summary>A required dependency is not in the distribution, or does not run on a side where its dependent runs.</summary>
    MissingDependency,

    /// <summary>A dependency is present in a version outside the declared range.</summary>
    VersionOutOfRange,

    /// <summary>Modules depend on each other in a cycle.</summary>
    DependencyCycle,

    /// <summary>A platform module depends on a business module.</summary>
    PlatformDependsOnBusiness,

    /// <summary>Two active modules declare a conflict.</summary>
    Conflict,

    /// <summary>Two active modules reserve the same exclusive resource.</summary>
    DisputedReservation,

    /// <summary>A base module has no active backend on a side where it requires one.</summary>
    MissingBackend,

    /// <summary>A configuration key is declared twice or has an invalid default, or the distribution gives it an invalid value.</summary>
    InvalidConfiguration,
}

/// <summary>One reason why the core refuses a composition, naming the module at fault.</summary>
/// <param name="Kind">The cause.</param>
/// <param name="ModuleId">The module at fault, or an empty string for the distribution itself.</param>
/// <param name="Message">A message that names the module and the cause.</param>
public sealed record CompositionProblem(CompositionProblemKind Kind, string ModuleId, string Message);

/// <summary>
/// Thrown when the modules of a distribution cannot run together. No code of
/// any module has run.
/// </summary>
public sealed class ModuleCompositionException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public ModuleCompositionException(IReadOnlyList<CompositionProblem> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<CompositionProblem> Problems { get; }

    private static string Describe(IReadOnlyList<CompositionProblem> problems)
    {
        var text = new StringBuilder("The modules of the distribution cannot run together:");
        foreach (CompositionProblem problem in problems)
        {
            text.AppendLine().Append("- ").Append(problem.Message);
        }

        return text.ToString();
    }
}
