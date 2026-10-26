// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis;

namespace Nobara.Analyzers;

/// <summary>Every diagnostic reported by the engine analyzers. All are errors.</summary>
/// <remarks>
/// Each descriptor is built with literal arguments so that the release
/// tracking of Roslyn can read its category and severity.
/// </remarks>
internal static class Descriptors
{
    private const string HotPath = "Nobara.HotPath";
    private const string StaticState = "Nobara.StaticState";
    private const string Async = "Nobara.Async";
    private const string OptionalDependency = "Nobara.OptionalDependency";
    private const string Help = "https://github.com/maelkobura/nobara.engine/blob/main/docs/reference/analyzers.md#";

    public static readonly DiagnosticDescriptor HotPathAllocation = new(
        "NOB101",
        "Managed allocation in hot path code",
        "Hot path code allocates managed memory: {0}",
        HotPath,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Code marked [HotPath] must not allocate managed memory. Use structs, spans, stackalloc, pools, or unmanaged memory.",
        Help + "nob101");

    public static readonly DiagnosticDescriptor HotPathLinq = new(
        "NOB102",
        "LINQ in hot path code",
        "Hot path code uses LINQ: {0}",
        HotPath,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Code marked [HotPath] must not use LINQ, which allocates enumerators and delegates. Write the loop explicitly.",
        Help + "nob102");

    public static readonly DiagnosticDescriptor HotPathClosure = new(
        "NOB103",
        "Capturing closure in hot path code",
        "Hot path code creates a closure: {0} captures '{1}'",
        HotPath,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Code marked [HotPath] must not use lambdas, anonymous methods, or local functions that capture variables or 'this'. Make them static and pass state explicitly.",
        Help + "nob103");

    public static readonly DiagnosticDescriptor HotPathBoxing = new(
        "NOB104",
        "Boxing in hot path code",
        "Hot path code boxes a value: {0}",
        HotPath,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Code marked [HotPath] must not box value types. Use generics, overloads for the value type, or override the object methods of the struct.",
        Help + "nob104");

    public static readonly DiagnosticDescriptor HotPathUnmarkedCall = new(
        "NOB105",
        "Hot path code calls unmarked code of the same project",
        "Hot path code calls '{0}', which is declared in this project and is not marked [HotPath]",
        HotPath,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A method, constructor, accessor, or operator declared in the same project and called from hot path code must be marked [HotPath], directly or through its type, so that the analyzer checks it too.",
        Help + "nob105");

    public static readonly DiagnosticDescriptor StaticField = new(
        "NOB201",
        "Mutable static field",
        "Static field '{0}' is mutable static state",
        StaticState,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Contexts of the same process share static state. Store state in a module instance, or make the field const or static readonly with an immutable type.",
        Help + "nob201");

    public static readonly DiagnosticDescriptor StaticAssignableMember = new(
        "NOB202",
        "Assignable static property or event",
        "Static {0} '{1}' can be assigned and is mutable static state",
        StaticState,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Contexts of the same process share static state. Remove the setter, or store the value in a module instance.",
        Help + "nob202");

    public static readonly DiagnosticDescriptor StaticMutableObject = new(
        "NOB203",
        "Static readonly member of a mutable type",
        "Static readonly {0} '{1}' holds a mutable '{2}'",
        StaticState,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A static readonly field or get-only auto-property of a mutable type, such as an array or a collection, is mutable static state. Use an immutable or frozen collection, or store the object in a module instance.",
        Help + "nob203");

    public static readonly DiagnosticDescriptor AsyncInLoop = new(
        "NOB301",
        "async in loop code",
        "{0} is async, which is not allowed in {1}",
        Async,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Code marked [HotPath] or [LoopCallback] must not be async. Run computations as jobs, and post the results of asynchronous I/O to the synchronization point of the context.",
        Help + "nob301");

    public static readonly DiagnosticDescriptor OptionalTypeOutsideIsolation = new(
        "NOB401",
        "Optional dependency used outside an isolation class",
        "'{0}' belongs to the optional dependency '{1}' and is used outside a class marked [UsesOptionalDependency(\"{1}\")]",
        OptionalDependency,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A type of an optional dependency may only be used inside a class marked [UsesOptionalDependency] with the identifier of that dependency, so that the module loads when the dependency is absent.",
        Help + "nob401");

    public static readonly DiagnosticDescriptor OptionalTypeExposed = new(
        "NOB402",
        "Isolation class exposes an optional dependency",
        "'{0}' exposes '{1}' of the optional dependency '{2}' outside its isolation class",
        OptionalDependency,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "The base types and the non-private member signatures of an isolation class must not mention types of an optional dependency, because code outside the class would load them.",
        Help + "nob402");

    public static readonly DiagnosticDescriptor UnknownOptionalDependency = new(
        "NOB403",
        "Isolation class names an unknown optional dependency",
        "'{0}' is not an optional dependency of this module",
        OptionalDependency,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "The identifier given to [UsesOptionalDependency] must be declared as an optional dependency in module.nobara.",
        Help + "nob403");
}
