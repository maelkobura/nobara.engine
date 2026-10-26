// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis;

namespace Nobara.Analyzers;

/// <summary>
/// Names of the attributes that the analyzers recognize. The core defines
/// them; the analyzers match them by full name, so they need no reference to
/// the core.
/// </summary>
internal static class KnownNames
{
    /// <summary>Namespace of every recognized attribute.</summary>
    public const string Namespace = "Nobara.Core";

    /// <summary><c>Nobara.Core.HotPathAttribute</c>: marks hot path code.</summary>
    public const string HotPath = "HotPathAttribute";

    /// <summary><c>Nobara.Core.LoopCallbackAttribute</c>: marks code that a loop phase runs.</summary>
    public const string LoopCallback = "LoopCallbackAttribute";

    /// <summary><c>Nobara.Core.UsesOptionalDependencyAttribute</c>: marks an isolation class.</summary>
    public const string UsesOptionalDependency = "UsesOptionalDependencyAttribute";

    /// <summary>Build property that holds the identifiers of the optional dependencies.</summary>
    public const string OptionalModulesProperty = "build_property.NobaraOptionalModules";

    /// <summary>Returns whether <paramref name="attribute"/> is the recognized attribute named <paramref name="name"/>.</summary>
    public static bool Is(AttributeData attribute, string name) =>
        attribute.AttributeClass is { } type
        && type.Name == name
        && type.ContainingNamespace is { Name: "Core" } core
        && core.ContainingNamespace is { Name: "Nobara" } nobara
        && nobara.ContainingNamespace is { IsGlobalNamespace: true };

    /// <summary>Returns whether <paramref name="symbol"/> carries the recognized attribute named <paramref name="name"/>.</summary>
    public static bool HasAttribute(ISymbol symbol, string name)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (Is(attribute, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the namespace-qualified metadata name of a type, such as <c>System.Collections.Generic.List`1</c>.</summary>
    public static string MetadataName(INamedTypeSymbol type)
    {
        INamedTypeSymbol definition = type.OriginalDefinition;
        string name = definition.ContainingType is { } outer ? MetadataName(outer) + "+" + definition.MetadataName : definition.MetadataName;
        return definition.ContainingType is null && definition.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }
}
