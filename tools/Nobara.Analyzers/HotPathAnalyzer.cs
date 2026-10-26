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
/// Checks code marked <c>[HotPath]</c>: no managed allocation, no LINQ, no
/// capturing closure, no boxing, and no call to unmarked code of the same
/// project.
/// </summary>
/// <remarks>
/// The rules apply to the bodies of marked methods, constructors, accessors,
/// and operators, including the lambdas and local functions they contain.
/// Code that only runs to throw an exception, and the operand of
/// <c>nameof</c>, are exempt. Calls into other assemblies are not followed.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> LinqTypes = ImmutableHashSet.Create(
        "System.Linq.Enumerable",
        "System.Linq.Queryable",
        "System.Linq.ParallelEnumerable",
        "System.Linq.AsyncEnumerable");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.HotPathAllocation,
        Descriptors.HotPathLinq,
        Descriptors.HotPathClosure,
        Descriptors.HotPathBoxing,
        Descriptors.HotPathUnmarkedCall);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var marks = new Marks(KnownNames.HotPath);
            IAssemblySymbol assembly = start.Compilation.Assembly;

            start.RegisterSymbolAction(symbol => AnalyzeIterator(symbol, marks), SymbolKind.Method);
            start.RegisterOperationBlockStartAction(block =>
            {
                if (!marks.IsMarkedCode(block.OwningSymbol))
                {
                    return;
                }

                var rules = new Rules(marks, assembly);
                block.RegisterOperationAction(rules.ObjectCreation, OperationKind.ObjectCreation);
                block.RegisterOperationAction(rules.Allocation, OperationKind.AnonymousObjectCreation, OperationKind.TypeParameterObjectCreation, OperationKind.ArrayCreation, OperationKind.CollectionExpression, OperationKind.With);
                block.RegisterOperationAction(rules.StringBuilding, OperationKind.Binary, OperationKind.CompoundAssignment, OperationKind.InterpolatedString);
                block.RegisterOperationAction(rules.DelegateCreation, OperationKind.DelegateCreation);
                block.RegisterOperationAction(rules.Closure, OperationKind.AnonymousFunction, OperationKind.LocalFunction);
                block.RegisterOperationAction(rules.Invocation, OperationKind.Invocation);
                block.RegisterOperationAction(rules.Query, OperationKind.TranslatedQuery);
                block.RegisterOperationAction(rules.Conversion, OperationKind.Conversion);
                block.RegisterOperationAction(rules.ForEach, OperationKind.Loop);
                block.RegisterOperationAction(rules.PropertyReference, OperationKind.PropertyReference);
                block.RegisterOperationAction(rules.Operator, OperationKind.Unary, OperationKind.Increment, OperationKind.Decrement);
            });
        });
    }

    private static void AnalyzeIterator(SymbolAnalysisContext context, Marks marks)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsIterator && marks.IsMarked(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.HotPathAllocation, method.Locations[0], $"iterator method '{method.Name}'"));
        }
    }

    /// <summary>
    /// Returns whether an operation does not run on the hot path: an exception
    /// being thrown, the operand of <c>nameof</c>, or an attribute argument.
    /// </summary>
    private static bool IsCold(IOperation operation)
    {
        for (IOperation? current = operation; current is not null; current = current.Parent)
        {
            if (current.Kind is OperationKind.Throw or OperationKind.NameOf or OperationKind.Attribute)
            {
                return true;
            }
        }

        return false;
    }

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private static bool IsSpan(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Span" or "ReadOnlySpan", Arity: 1, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

    private sealed class Rules(Marks marks, IAssemblySymbol assembly)
    {
        public void ObjectCreation(OperationAnalysisContext context)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (IsCold(creation) || creation.Type is null)
            {
                return;
            }

            if (creation.Type.IsReferenceType)
            {
                Report(context, Descriptors.HotPathAllocation, creation, $"new '{Display(creation.Type)}'");
            }
            else if (creation.Constructor is { } constructor)
            {
                CheckCall(context, constructor, creation);
            }
        }

        public void Allocation(OperationAnalysisContext context)
        {
            IOperation operation = context.Operation;
            if (IsCold(operation))
            {
                return;
            }

            string? construct = operation switch
            {
                IAnonymousObjectCreationOperation => "anonymous type",
                IWithOperation { Type: { IsReferenceType: true } type } => $"'with' expression that copies '{Display(type)}'",
                ITypeParameterObjectCreationOperation { Type: { IsValueType: false } type } => $"new '{Display(type)}', a type parameter not constrained to a struct",
                IArrayCreationOperation array => ArrayAllocation(array),
                ICollectionExpressionOperation collection => CollectionAllocation(collection),
                _ => null,
            };

            if (construct is not null)
            {
                Report(context, Descriptors.HotPathAllocation, operation, construct);
            }
        }

        private static string? ArrayAllocation(IArrayCreationOperation array)
        {
            string element = array.Type is IArrayTypeSymbol arrayType ? Display(arrayType.ElementType) : "?";
            if (array.IsImplicit && array.Parent is IArgumentOperation { ArgumentKind: ArgumentKind.ParamArray, Parameter.ContainingSymbol: { } method })
            {
                // The compiler passes Array.Empty<T>() for an empty params array.
                return array.Initializer is { ElementValues.Length: 0 } ? null : $"params array of '{element}' for '{Display(method)}'";
            }

            return $"array of '{element}'";
        }

        private static string? CollectionAllocation(ICollectionExpressionOperation collection)
        {
            if (IsSpan(collection.Type))
            {
                return null;
            }

            // An empty collection expression of an array type is Array.Empty<T>().
            if (collection.Elements.Length == 0 && collection.Type is IArrayTypeSymbol)
            {
                return null;
            }

            return $"collection expression of type '{(collection.Type is null ? "?" : Display(collection.Type))}'";
        }

        public void StringBuilding(OperationAnalysisContext context)
        {
            IOperation operation = context.Operation;
            if (operation.ConstantValue.HasValue || IsCold(operation))
            {
                return;
            }

            switch (operation)
            {
                case IBinaryOperation binary:
                    if (binary.OperatorKind == BinaryOperatorKind.Add && binary.Type?.SpecialType == SpecialType.System_String
                        && binary.Parent is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String })
                    {
                        Report(context, Descriptors.HotPathAllocation, binary, "string concatenation");
                    }
                    else if (binary.OperatorMethod is { } binaryOperator)
                    {
                        CheckCall(context, binaryOperator, binary);
                    }

                    break;

                case ICompoundAssignmentOperation compound:
                    if (compound.OperatorKind == BinaryOperatorKind.Add && compound.Target.Type?.SpecialType == SpecialType.System_String)
                    {
                        Report(context, Descriptors.HotPathAllocation, compound, "string concatenation");
                    }
                    else if (compound.OperatorMethod is { } compoundOperator)
                    {
                        CheckCall(context, compoundOperator, compound);
                    }

                    break;

                case IInterpolatedStringOperation interpolated:
                    // An interpolated string handler of the callee, such as a
                    // logging handler, builds no string; its own code is checked
                    // where it is declared.
                    if (interpolated.Parent is not IInterpolatedStringHandlerCreationOperation)
                    {
                        Report(context, Descriptors.HotPathAllocation, interpolated, "interpolated string");
                    }

                    break;
            }
        }

        public void DelegateCreation(OperationAnalysisContext context)
        {
            var creation = (IDelegateCreationOperation)context.Operation;
            if (IsCold(creation) || creation.Target is not IMethodReferenceOperation reference)
            {
                // Lambdas are checked for captures; one that captures nothing is cached by the compiler.
                return;
            }

            // Since C# 11 the compiler caches delegates created from a static method group.
            LanguageVersion version = ((CSharpParseOptions)creation.Syntax.SyntaxTree.Options).LanguageVersion;
            if (reference.Method.IsStatic && version >= LanguageVersion.CSharp11)
            {
                return;
            }

            Report(context, Descriptors.HotPathAllocation, creation, $"delegate created from the method group '{Display(reference.Method)}'");
        }

        public void Closure(OperationAnalysisContext context)
        {
            IOperation operation = context.Operation;
            (IMethodSymbol function, SyntaxNode? syntax, string kind) = operation switch
            {
                IAnonymousFunctionOperation lambda => (lambda.Symbol, lambda.Syntax, lambda.Syntax is AnonymousMethodExpressionSyntax ? "anonymous method" : "lambda"),
                ILocalFunctionOperation local => (local.Symbol, local.Syntax, $"local function '{local.Symbol.Name}'"),
                _ => (null!, null, string.Empty),
            };

            if (syntax is null || function.IsStatic || IsCold(operation) || operation.SemanticModel is not { } model)
            {
                return;
            }

            DataFlowAnalysis? flow = syntax switch
            {
                AnonymousFunctionExpressionSyntax expression => model.AnalyzeDataFlow(expression),
                LocalFunctionStatementSyntax statement => model.AnalyzeDataFlow(statement),
                _ => null,
            };

            if (flow is not { Succeeded: true })
            {
                return;
            }

            foreach (ISymbol captured in flow.CapturedInside)
            {
                if (!flow.VariablesDeclared.Contains(captured))
                {
                    string name = captured is IParameterSymbol { IsThis: true } ? "this" : captured.Name;
                    Report(context, Descriptors.HotPathClosure, operation, kind, name);
                    return;
                }
            }
        }

        public void Invocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (IsCold(invocation))
            {
                return;
            }

            IMethodSymbol target = invocation.TargetMethod;
            INamedTypeSymbol container = target.ContainingType;
            if (LinqTypes.Contains(KnownNames.MetadataName(container)))
            {
                if (!IsInQuery(invocation))
                {
                    Report(context, Descriptors.HotPathLinq, invocation, $"'{container.Name}.{target.Name}'");
                }

                return;
            }

            if (invocation.Instance?.Type is { IsValueType: true, TypeKind: not TypeKind.TypeParameter } instanceType
                && container.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum
                && !Overrides(instanceType, target))
            {
                Report(context, Descriptors.HotPathBoxing, invocation, $"call to '{Display(target)}', inherited by '{Display(instanceType)}'");
                return;
            }

            CheckCall(context, target, invocation);
        }

        public void Query(OperationAnalysisContext context)
        {
            if (!IsCold(context.Operation))
            {
                Report(context, Descriptors.HotPathLinq, context.Operation, "query expression");
            }
        }

        public void Conversion(OperationAnalysisContext context)
        {
            var conversion = (IConversionOperation)context.Operation;
            if (IsCold(conversion))
            {
                return;
            }

            if (conversion.Language == LanguageNames.CSharp && conversion.GetConversion().IsBoxing && conversion.Operand.Type is { } from && conversion.Type is { } to)
            {
                Report(context, Descriptors.HotPathBoxing, conversion, $"conversion from '{Display(from)}' to '{Display(to)}'");
            }
            else if (conversion.OperatorMethod is { } userDefined)
            {
                CheckCall(context, userDefined, conversion);
            }
        }

        public void ForEach(OperationAnalysisContext context)
        {
            if (context.Operation is not IForEachLoopOperation { IsAsynchronous: false } loop
                || loop.Syntax is not CommonForEachStatementSyntax syntax
                || loop.SemanticModel is not { } model
                || IsCold(loop))
            {
                return;
            }

            ITypeSymbol? collection = loop.Collection is IConversionOperation { IsImplicit: true } implicitConversion
                ? implicitConversion.Operand.Type
                : loop.Collection.Type;

            // The compiler lowers a foreach over an array, a string, or a span to an indexed loop.
            if (collection is null or IArrayTypeSymbol || collection.SpecialType == SpecialType.System_String || IsSpan(collection))
            {
                return;
            }

            if (model.GetForEachStatementInfo(syntax).GetEnumeratorMethod?.ReturnType is { IsReferenceType: true } enumerator)
            {
                Report(context, Descriptors.HotPathAllocation, loop.Collection, $"foreach enumerator of type '{Display(enumerator)}'");
            }
        }

        public void PropertyReference(OperationAnalysisContext context)
        {
            var reference = (IPropertyReferenceOperation)context.Operation;
            if (IsCold(reference))
            {
                return;
            }

            IPropertySymbol property = reference.Property;
            bool reads = true;
            bool writes = false;
            switch (reference.Parent)
            {
                case ISimpleAssignmentOperation assignment when assignment.Target == reference:
                    reads = false;
                    writes = true;
                    break;
                case ICompoundAssignmentOperation compound when compound.Target == reference:
                case ICoalesceAssignmentOperation coalesce when coalesce.Target == reference:
                case IIncrementOrDecrementOperation:
                    writes = true;
                    break;
            }

            if (reads && property.GetMethod is { } getter)
            {
                CheckCall(context, getter, reference);
            }

            if (writes && property.SetMethod is { } setter)
            {
                CheckCall(context, setter, reference);
            }
        }

        public void Operator(OperationAnalysisContext context)
        {
            IOperation operation = context.Operation;
            IMethodSymbol? method = operation switch
            {
                IUnaryOperation unary => unary.OperatorMethod,
                IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                _ => null,
            };

            if (method is not null && !IsCold(operation))
            {
                CheckCall(context, method, operation);
            }
        }

        /// <summary>Reports a call to a method of this project that is not marked.</summary>
        private void CheckCall(OperationAnalysisContext context, IMethodSymbol target, IOperation operation)
        {
            IMethodSymbol method = (target.ReducedFrom ?? target).OriginalDefinition;
            if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.StaticConstructor
                || method.IsImplicitlyDeclared
                || method.IsExtern
                || (method.IsPartialDefinition && method.PartialImplementationPart is null)
                || !SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, assembly)
                || (!method.IsAbstract && IsAutoAccessor(method))
                || marks.IsMarked(method))
            {
                return;
            }

            Report(context, Descriptors.HotPathUnmarkedCall, operation, Display(method));
        }

        private static bool IsAutoAccessor(IMethodSymbol method)
        {
            if (method.AssociatedSymbol is not IPropertySymbol property)
            {
                return false;
            }

            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is AccessorDeclarationSyntax { Body: null, ExpressionBody: null })
                {
                    return true;
                }
            }

            // A positional record property is declared by a parameter and has no accessor syntax.
            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is ParameterSyntax)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Overrides(ITypeSymbol type, IMethodSymbol method)
        {
            foreach (ISymbol member in type.GetMembers(method.Name))
            {
                for (IMethodSymbol? current = member as IMethodSymbol; current is not null; current = current.OverriddenMethod)
                {
                    if (current.IsOverride && SymbolEqualityComparer.Default.Equals(current.OverriddenMethod?.OriginalDefinition, method.OriginalDefinition))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsInQuery(IOperation operation)
        {
            for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
            {
                if (current.Kind == OperationKind.TranslatedQuery)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, IOperation operation, params object[] arguments) =>
            context.ReportDiagnostic(Diagnostic.Create(descriptor, operation.Syntax.GetLocation(), arguments));
    }
}
