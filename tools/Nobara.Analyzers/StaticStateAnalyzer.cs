// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nobara.Analyzers;

/// <summary>
/// Forbids mutable static state, which every context of a process would
/// share: static fields that are not readonly, static properties with a
/// setter, static field-like events, and static readonly members of a known
/// mutable type.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticStateAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Mutable types recognized by name. A class is mutable when it, or one of
    /// its base classes, is listed. An interface is mutable when it is listed.
    /// </summary>
    internal static readonly ImmutableHashSet<string> MutableTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedList`2",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.PriorityQueue`2",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.ISet`1",
        "System.Collections.ArrayList",
        "System.Collections.BitArray",
        "System.Collections.Hashtable",
        "System.Collections.Queue",
        "System.Collections.SortedList",
        "System.Collections.Stack",
        "System.Collections.IList",
        "System.Collections.IDictionary",
        "System.Collections.Concurrent.BlockingCollection`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.KeyedCollection`2",
        "System.Collections.ObjectModel.ObservableCollection`1",
        "System.IO.Stream",
        "System.Random",
        "System.Text.StringBuilder",
        "System.Threading.AsyncLocal`1",
        "System.Threading.ThreadLocal`1");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.StaticField,
        Descriptors.StaticAssignableMember,
        Descriptors.StaticMutableObject);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
        context.RegisterSymbolAction(AnalyzeEvent, SymbolKind.Event);
    }

    /// <summary>Returns the mutable type that makes <paramref name="type"/> mutable, or null when it is not known to be mutable.</summary>
    internal static ITypeSymbol? FindMutableType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
        {
            return type;
        }

        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        if (named.TypeKind == TypeKind.Interface)
        {
            return MutableTypes.Contains(KnownNames.MetadataName(named)) ? named : null;
        }

        for (INamedTypeSymbol? current = named; current is not null; current = current.BaseType)
        {
            if (MutableTypes.Contains(KnownNames.MetadataName(current)))
            {
                return current;
            }
        }

        return null;
    }

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (!field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
        {
            return;
        }

        if (!field.IsReadOnly || IsThreadStatic(field))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticField, field.Locations[0], Display(field)));
        }
        else if (FindMutableType(field.Type) is { } mutable)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticMutableObject, field.Locations[0], "field", Display(field), Display(mutable)));
        }
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!property.IsStatic || property.IsAbstract)
        {
            return;
        }

        if (property.SetMethod is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticAssignableMember, property.Locations[0], "property", Display(property)));
        }
        else if (IsAutoProperty(property) && FindMutableType(property.Type) is { } mutable)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticMutableObject, property.Locations[0], "property", Display(property), Display(mutable)));
        }
    }

    private static void AnalyzeEvent(SymbolAnalysisContext context)
    {
        var @event = (IEventSymbol)context.Symbol;
        if (!@event.IsStatic || @event.IsAbstract)
        {
            return;
        }

        // A field-like event stores its handlers in a static field. An event
        // with explicit accessors stores them elsewhere, where that storage is checked.
        foreach (SyntaxReference reference in @event.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax })
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.StaticAssignableMember, @event.Locations[0], "event", Display(@event)));
                return;
            }
        }
    }

    private static bool IsThreadStatic(IFieldSymbol field)
    {
        foreach (AttributeData attribute in field.GetAttributes())
        {
            if (attribute.AttributeClass is { Name: "ThreadStaticAttribute", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAutoProperty(IPropertySymbol property)
    {
        foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is PropertyDeclarationSyntax { AccessorList.Accessors: var accessors } && accessors.Count > 0)
            {
                foreach (AccessorDeclarationSyntax accessor in accessors)
                {
                    if (accessor.Body is not null || accessor.ExpressionBody is not null)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }
}
