// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Nobara.Analyzers;

/// <summary>
/// Tells whether a method carries a marking attribute such as
/// <c>[HotPath]</c>, directly or through the members it inherits its contract
/// from. One instance lives for one compilation and caches its answers.
/// </summary>
/// <remarks>
/// A method is marked when one of these carries the attribute:
/// <list type="bullet">
/// <item>the method itself;</item>
/// <item>the property it is an accessor of;</item>
/// <item>its containing type, unless the method is a static constructor;</item>
/// <item>a method it overrides or an interface method it implements.</item>
/// </list>
/// Nested types are not covered by the mark of their containing type.
/// </remarks>
internal sealed class Marks(string attributeName)
{
    private readonly ConcurrentDictionary<IMethodSymbol, bool> _methods = new(SymbolEqualityComparer.Default);

    /// <summary>Returns whether <paramref name="symbol"/> itself carries the attribute.</summary>
    public bool HasOwnMark(ISymbol symbol) => KnownNames.HasAttribute(symbol, attributeName);

    /// <summary>Returns whether <paramref name="method"/> is marked.</summary>
    public bool IsMarked(IMethodSymbol method) => _methods.GetOrAdd(Normalize(method), Compute);

    /// <summary>
    /// Returns whether the code of <paramref name="owner"/>, the symbol that
    /// owns an operation block, is marked: a marked method, or an instance
    /// field or property initializer of a marked type.
    /// </summary>
    public bool IsMarkedCode(ISymbol owner) => owner switch
    {
        IMethodSymbol method => IsMarked(method),
        IFieldSymbol { IsStatic: false } field => HasOwnMark(field.ContainingType),
        IPropertySymbol { IsStatic: false } property => HasOwnMark(property.ContainingType),
        _ => false,
    };

    private static IMethodSymbol Normalize(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition;

    private bool Compute(IMethodSymbol method)
    {
        if (HasOwnMark(method))
        {
            return true;
        }

        if (method.AssociatedSymbol is IPropertySymbol property && HasOwnMark(property))
        {
            return true;
        }

        if (method.MethodKind != MethodKind.StaticConstructor && method.ContainingType is { } type && HasOwnMark(type))
        {
            return true;
        }

        if (method.OverriddenMethod is { } overridden && IsMarked(overridden))
        {
            return true;
        }

        foreach (IMethodSymbol implemented in method.ExplicitInterfaceImplementations)
        {
            if (IsMarked(implemented))
            {
                return true;
            }
        }

        return !method.IsStatic && method.ContainingType is { TypeKind: not TypeKind.Interface } container && ImplementsMarkedInterfaceMethod(container, method);
    }

    private bool ImplementsMarkedInterfaceMethod(INamedTypeSymbol type, IMethodSymbol method)
    {
        foreach (INamedTypeSymbol @interface in type.AllInterfaces)
        {
            foreach (ISymbol member in @interface.GetMembers(method.Name))
            {
                if (member is IMethodSymbol { IsStatic: false } candidate
                    && IsMarked(candidate)
                    && type.FindImplementationForInterfaceMember(candidate) is IMethodSymbol implementation
                    && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
