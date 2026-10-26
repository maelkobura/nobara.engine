// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nobara.Analyzers;

/// <summary>
/// Forbids <c>async</c> methods, lambdas, and local functions in code marked
/// <c>[HotPath]</c> or <c>[LoopCallback]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Descriptors.AsyncInLoop);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var hot = new Marks(KnownNames.HotPath);
            var loop = new Marks(KnownNames.LoopCallback);

            start.RegisterSymbolAction(
                symbol =>
                {
                    var method = (IMethodSymbol)symbol.Symbol;
                    if (method.IsAsync && Where(method, hot, loop) is { } where)
                    {
                        symbol.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncInLoop, AsyncKeyword(method), $"Method '{method.Name}'", where));
                    }
                },
                SymbolKind.Method);

            start.RegisterOperationBlockStartAction(block =>
            {
                string? where = block.OwningSymbol switch
                {
                    IMethodSymbol method => Where(method, hot, loop),
                    _ when hot.IsMarkedCode(block.OwningSymbol) => "hot path code",
                    _ => null,
                };

                if (where is null)
                {
                    return;
                }

                block.RegisterOperationAction(
                    operation =>
                    {
                        (IMethodSymbol function, string kind, SyntaxToken keyword) = operation.Operation switch
                        {
                            ILocalFunctionOperation local => (local.Symbol, $"Local function '{local.Symbol.Name}'", ((LocalFunctionStatementSyntax)local.Syntax).Modifiers.FirstOrDefault(SyntaxKind.AsyncKeyword)),
                            IAnonymousFunctionOperation lambda => (lambda.Symbol, "Lambda", (lambda.Syntax as AnonymousFunctionExpressionSyntax)?.AsyncKeyword ?? default),
                            _ => (null!, string.Empty, default),
                        };

                        if (function is { IsAsync: true })
                        {
                            Location location = keyword.IsKind(SyntaxKind.AsyncKeyword) ? keyword.GetLocation() : operation.Operation.Syntax.GetLocation();
                            operation.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncInLoop, location, kind, where));
                        }
                    },
                    OperationKind.LocalFunction,
                    OperationKind.AnonymousFunction);
            });
        });
    }

    private static string? Where(IMethodSymbol method, Marks hot, Marks loop) =>
        hot.IsMarked(method) ? "hot path code" : loop.IsMarked(method) ? "a loop callback" : null;

    private static Location AsyncKeyword(IMethodSymbol method)
    {
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            SyntaxTokenList modifiers = reference.GetSyntax() switch
            {
                BaseMethodDeclarationSyntax declaration => declaration.Modifiers,
                AccessorDeclarationSyntax accessor => accessor.Modifiers,
                _ => default,
            };

            SyntaxToken keyword = modifiers.FirstOrDefault(SyntaxKind.AsyncKeyword);
            if (keyword.IsKind(SyntaxKind.AsyncKeyword))
            {
                return keyword.GetLocation();
            }
        }

        return method.Locations[0];
    }
}

/// <summary>Helpers on token lists.</summary>
internal static class SyntaxTokenListExtensions
{
    /// <summary>Returns the first token of a kind, or the default token.</summary>
    public static SyntaxToken FirstOrDefault(this SyntaxTokenList tokens, SyntaxKind kind)
    {
        foreach (SyntaxToken token in tokens)
        {
            if (token.IsKind(kind))
            {
                return token;
            }
        }

        return default;
    }
}
