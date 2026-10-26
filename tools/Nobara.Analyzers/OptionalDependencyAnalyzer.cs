// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nobara.Analyzers;

/// <summary>
/// Confines the types of optional dependencies to isolation classes, the
/// classes marked <c>[UsesOptionalDependency("id")]</c>, so that a module
/// loads when an optional dependency is absent.
/// </summary>
/// <remarks>
/// The identifiers of the optional dependencies come from the
/// <c>NobaraOptionalModules</c> build property that <c>Nobara.Sdk</c> sets.
/// The assembly of a dependency is the referenced assembly whose name equals
/// the identifier, compared without case.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptionalDependencyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.OptionalTypeOutsideIsolation,
        Descriptors.OptionalTypeExposed,
        Descriptors.UnknownOptionalDependency);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            ImmutableHashSet<string> identifiers = ReadIdentifiers(start.Options);
            var assemblies = new Dictionary<IAssemblySymbol, string>(SymbolEqualityComparer.Default);
            foreach (IAssemblySymbol referenced in start.Compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (identifiers.TryGetValue(referenced.Name, out string? identifier))
                {
                    assemblies[referenced] = identifier;
                }
            }

            var state = new State(identifiers, assemblies);
            start.RegisterSymbolAction(state.AnalyzeType, SymbolKind.NamedType);
            if (assemblies.Count > 0)
            {
                start.RegisterSemanticModelAction(state.AnalyzeUses);
            }
        });
    }

    private static ImmutableHashSet<string> ReadIdentifiers(AnalyzerOptions options)
    {
        if (!options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(KnownNames.OptionalModulesProperty, out string? value))
        {
            return ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        }

        return value.Split(';')
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private sealed class State(ImmutableHashSet<string> identifiers, Dictionary<IAssemblySymbol, string> assemblies)
    {
        private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableHashSet<string>> _isolations = new(SymbolEqualityComparer.Default);

        /// <summary>Checks the attributes of an isolation class and what the class exposes.</summary>
        public void AnalyzeType(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            foreach (AttributeData attribute in type.GetAttributes())
            {
                if (KnownNames.Is(attribute, KnownNames.UsesOptionalDependency) && !identifiers.Contains(IdentifierOf(attribute) ?? string.Empty))
                {
                    Location location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations[0];
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnknownOptionalDependency, location, IdentifierOf(attribute) ?? string.Empty));
                }
            }

            if (assemblies.Count == 0 || IsolationsOf(type).IsEmpty)
            {
                return;
            }

            if (type.BaseType is { } baseType)
            {
                ReportExposure(context, type, baseType);
            }

            foreach (INamedTypeSymbol @interface in type.Interfaces)
            {
                ReportExposure(context, type, @interface);
            }

            if (!IsExposed(type))
            {
                return;
            }

            foreach (ISymbol member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.DeclaredAccessibility == Accessibility.Private || member is INamedTypeSymbol)
                {
                    continue;
                }

                foreach (ITypeSymbol signatureType in SignatureTypes(member))
                {
                    if (ReportExposure(context, member, signatureType))
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>Reports, once per statement or declaration, each use of a type of an optional dependency outside its isolation class.</summary>
        public void AnalyzeUses(SemanticModelAnalysisContext context)
        {
            SemanticModel model = context.SemanticModel;
            var reported = new HashSet<(SyntaxNode Anchor, string Identifier)>();
            foreach (SyntaxNode node in model.SyntaxTree.GetRoot(context.CancellationToken).DescendantNodes())
            {
                if (node is not SimpleNameSyntax name || IsIgnored(name, model))
                {
                    continue;
                }

                // The type arguments of a generic name are names of their own, reported where they are written.
                ISymbol? symbol = model.GetSymbolInfo(name, context.CancellationToken).Symbol;
                if (symbol is INamedTypeSymbol generic && name is GenericNameSyntax)
                {
                    symbol = generic.OriginalDefinition;
                }

                if (symbol is null || OptionalTypeOf(symbol) is not { } found)
                {
                    continue;
                }

                string identifier = assemblies[found.ContainingAssembly];
                if (IsInIsolation(name, model, identifier, context.CancellationToken) || !reported.Add((AnchorOf(name), identifier)))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(Descriptors.OptionalTypeOutsideIsolation, name.GetLocation(), Display(found), identifier));
            }
        }

        private bool ReportExposure(SymbolAnalysisContext context, ISymbol exposer, ITypeSymbol type)
        {
            if (FindOptionalType(type) is not { } found)
            {
                return false;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.OptionalTypeExposed, exposer.Locations[0], Display(exposer), Display(found), assemblies[found.ContainingAssembly]));
            return true;
        }

        /// <summary>Returns the identifiers that the type, or a type that contains it, isolates.</summary>
        private ImmutableHashSet<string> IsolationsOf(INamedTypeSymbol type) => _isolations.GetOrAdd(type, t =>
        {
            ImmutableHashSet<string> own = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AttributeData attribute in t.GetAttributes())
            {
                if (KnownNames.Is(attribute, KnownNames.UsesOptionalDependency) && IdentifierOf(attribute) is { } identifier)
                {
                    own = own.Add(identifier);
                }
            }

            return t.ContainingType is { } outer ? own.Union(IsolationsOf(outer)) : own;
        });

        private bool IsInIsolation(SyntaxNode node, SemanticModel model, string identifier, CancellationToken cancellation)
        {
            BaseTypeDeclarationSyntax? declaration = node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
            return declaration is not null
                && model.GetDeclaredSymbol(declaration, cancellation) is { } type
                && IsolationsOf(type).Contains(identifier);
        }

        /// <summary>Returns the type of an optional dependency that a symbol refers to, or null.</summary>
        private ITypeSymbol? OptionalTypeOf(ISymbol symbol) => symbol switch
        {
            ITypeSymbol type => FindOptionalType(type),
            IAliasSymbol alias => FindOptionalType(alias.Target as ITypeSymbol),
            ILocalSymbol local => FindOptionalType(local.Type),
            IParameterSymbol parameter => FindOptionalType(parameter.Type),
            IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } => null,
            IMethodSymbol method => FindOptionalType(method.ContainingType) ?? FindOptionalType(method.ReturnType),
            IPropertySymbol property => FindOptionalType(property.ContainingType) ?? FindOptionalType(property.Type),
            IFieldSymbol field => FindOptionalType(field.ContainingType) ?? FindOptionalType(field.Type),
            IEventSymbol @event => FindOptionalType(@event.ContainingType) ?? FindOptionalType(@event.Type),
            _ => null,
        };

        /// <summary>Returns <paramref name="type"/>, or a type it is built from, when it belongs to an optional dependency.</summary>
        private ITypeSymbol? FindOptionalType(ITypeSymbol? type)
        {
            switch (type)
            {
                case null:
                    return null;
                case IArrayTypeSymbol array:
                    return FindOptionalType(array.ElementType);
                case IPointerTypeSymbol pointer:
                    return FindOptionalType(pointer.PointedAtType);
                case INamedTypeSymbol named:
                    if (named.ContainingAssembly is { } assembly && assemblies.ContainsKey(assembly))
                    {
                        return named.OriginalDefinition;
                    }

                    foreach (ITypeSymbol argument in named.TypeArguments)
                    {
                        if (FindOptionalType(argument) is { } found)
                        {
                            return found;
                        }
                    }

                    return named.ContainingType is { } outer ? FindOptionalType(outer) : null;
                default:
                    return null;
            }
        }

        private static string? IdentifierOf(AttributeData attribute) =>
            attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string identifier ? identifier : null;

        /// <summary>Returns whether a type can be named from outside the outermost isolation class that contains it.</summary>
        private bool IsExposed(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            {
                if (current.ContainingType is null || current.ContainingType is { } outer && IsolationsOf(outer).IsEmpty)
                {
                    // The outermost isolation class: what it exposes is visible outside.
                    return true;
                }

                if (current.DeclaredAccessibility == Accessibility.Private)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol member)
        {
            switch (member)
            {
                case IFieldSymbol field:
                    yield return field.Type;
                    break;
                case IPropertySymbol property:
                    yield return property.Type;
                    foreach (IParameterSymbol parameter in property.Parameters)
                    {
                        yield return parameter.Type;
                    }

                    break;
                case IEventSymbol @event:
                    yield return @event.Type;
                    break;
                case IMethodSymbol { AssociatedSymbol: null } method:
                    yield return method.ReturnType;
                    foreach (IParameterSymbol parameter in method.Parameters)
                    {
                        yield return parameter.Type;
                    }

                    break;
            }
        }

        private static bool IsIgnored(SimpleNameSyntax name, SemanticModel model)
        {
            foreach (SyntaxNode ancestor in name.Ancestors())
            {
                switch (ancestor)
                {
                    case UsingDirectiveSyntax:
                    case NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax when ancestor is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(name.Span):
                        return true;
                    case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } invocation
                        when model.GetOperation(invocation)?.Kind == Microsoft.CodeAnalysis.OperationKind.NameOf:
                        return true;
                    case MemberDeclarationSyntax or StatementSyntax:
                        return false;
                }
            }

            return false;
        }

        /// <summary>Returns the statement or declaration that a diagnostic about <paramref name="node"/> is reported once for.</summary>
        private static SyntaxNode AnchorOf(SyntaxNode node)
        {
            foreach (SyntaxNode ancestor in node.Ancestors())
            {
                if (ancestor is StatementSyntax and not BlockSyntax || ancestor is MemberDeclarationSyntax || ancestor is AttributeListSyntax)
                {
                    return ancestor;
                }
            }

            return node.SyntaxTree.GetRoot();
        }
    }
}
