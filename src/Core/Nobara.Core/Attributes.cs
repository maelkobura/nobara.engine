// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core;

/// <summary>
/// Marks code that runs often enough for allocations to matter. The engine
/// analyzers forbid managed allocations, LINQ, capturing closures, and boxing
/// in marked code, and require calls into the same project to target marked
/// code.
/// </summary>
/// <remarks>
/// A mark on a type covers its methods, except its static constructor, and
/// not its nested types. A mark on an interface method, a virtual method, or
/// a type covers the implementations and overrides.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface
    | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    Inherited = false)]
public sealed class HotPathAttribute : Attribute
{
}

/// <summary>
/// Marks code that a phase of the loop runs. The engine analyzers forbid
/// <see langword="async"/> methods, lambdas, and local functions in marked
/// code.
/// </summary>
/// <remarks>
/// The mark covers the same members as <see cref="HotPathAttribute"/>. The
/// interfaces through which a module joins the loop carry it, so their
/// implementations need no mark of their own.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method,
    Inherited = false)]
public sealed class LoopCallbackAttribute : Attribute
{
}

/// <summary>
/// Marks an isolation class: the only place where a module uses the types of
/// one of its optional dependencies, so that the module loads when that
/// dependency is absent.
/// </summary>
/// <remarks>
/// Call an isolation class only after checking that the dependency is active,
/// with <see cref="ModuleContext.IsModuleActive(string)"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesOptionalDependencyAttribute : Attribute
{
    /// <summary>Initializes the attribute for one optional dependency.</summary>
    /// <param name="moduleId">The identifier of the optional dependency, as declared in <c>module.nobara</c>.</param>
    public UsesOptionalDependencyAttribute(string moduleId)
    {
        ModuleId = moduleId;
    }

    /// <summary>Gets the identifier of the optional dependency.</summary>
    public string ModuleId { get; }
}

/// <summary>
/// Names the class that the core instantiates, once per context, to run the
/// code of the module that the assembly contains.
/// </summary>
/// <remarks>
/// The class implements <see cref="IModule"/> and has a public parameterless
/// constructor. An assembly without this attribute is a module without code:
/// the core loads it and calls nothing.
/// </remarks>
/// <example>
/// <code>
/// [assembly: ModuleEntry(typeof(ExampleModule))]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ModuleEntryAttribute : Attribute
{
    /// <summary>Initializes the attribute with the class of the module.</summary>
    /// <param name="moduleType">The class that implements <see cref="IModule"/>.</param>
    public ModuleEntryAttribute(Type moduleType)
    {
        ModuleType = moduleType;
    }

    /// <summary>Gets the class that implements <see cref="IModule"/>.</summary>
    public Type ModuleType { get; }
}
