﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nerdbank.MessagePack.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ConverterAnalyzers : DiagnosticAnalyzer
{
	public const string CallbackToTopLevelSerializerDiagnosticId = "NBMsgPack030";
	public const string NotExactlyOneStructureDiagnosticId = "NBMsgPack031";

	public static readonly DiagnosticDescriptor CallbackToTopLevelSerializerDescriptor = new(
		CallbackToTopLevelSerializerDiagnosticId,
		title: Strings.NBMsgPack030_Title,
		messageFormat: Strings.NBMsgPack030_MessageFormat,
		category: "Usage",
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	public static readonly DiagnosticDescriptor NotExactlyOneStructureDescriptor = new(
		NotExactlyOneStructureDiagnosticId,
		title: Strings.NBMsgPack031_Title,
		messageFormat: Strings.NBMsgPack031_MessageFormat,
		category: "Usage",
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
		CallbackToTopLevelSerializerDescriptor,
		NotExactlyOneStructureDescriptor,
	];

	public override void Initialize(AnalysisContext context)
	{
		if (!Debugger.IsAttached)
		{
			context.EnableConcurrentExecution();
		}

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

		context.RegisterCompilationStartAction(context =>
		{
			if (!ReferenceSymbols.TryCreate(context.Compilation, out ReferenceSymbols? referenceSymbols))
			{
				return;
			}

			INamedTypeSymbol unboundConverterBase = referenceSymbols.MessagePackConverter.ConstructUnboundGenericType();
			context.RegisterSymbolStartAction(
				context =>
				{
					INamedTypeSymbol target = (INamedTypeSymbol)context.Symbol;
					if (target.IsOrDerivedFrom(unboundConverterBase))
					{
						context.RegisterOperationBlockStartAction(context =>
						{
							context.RegisterOperationAction(
								context =>
								{
									switch (context.Operation)
									{
										case IObjectCreationOperation objectCreation:
											if (objectCreation.Type?.IsOrDerivedFrom(referenceSymbols.MessagePackSerializer) is true)
											{
												context.ReportDiagnostic(Diagnostic.Create(CallbackToTopLevelSerializerDescriptor, objectCreation.Syntax.GetLocation()));
											}

											break;
										case IInvocationOperation invocation:
											if (invocation.TargetMethod.ContainingSymbol is ITypeSymbol typeSymbol &&
												typeSymbol.IsOrDerivedFrom(referenceSymbols.MessagePackSerializer))
											{
												context.ReportDiagnostic(Diagnostic.Create(CallbackToTopLevelSerializerDescriptor, invocation.Syntax.GetLocation()));
											}

											break;
									}
								},
								OperationKind.ObjectCreation,
								OperationKind.Invocation);

							context.RegisterOperationBlockEndAction(context => this.AnalyzeStructureCounts(context, referenceSymbols));
						});
					}
				},
				SymbolKind.NamedType);
		});
	}

	private void AnalyzeStructureCounts(OperationBlockAnalysisContext context, ReferenceSymbols referenceSymbols)
	{
		if (context.OwningSymbol is not IMethodSymbol { IsOverride: true } method)
		{
			return;
		}

		int? GetVariableImpact(IInvocationOperation invocation) => invocation.Arguments.Length > 0 && invocation.Arguments[0].Value is ILiteralOperation { ConstantValue.Value: int count } ? -count : null;

		Func<IInvocationOperation, (int? Impact, bool Unconditional)> relevantMethodTest;
		switch (method.Name)
		{
			case "Serialize":
				relevantMethodTest = i =>
				{
					if (SymbolEqualityComparer.Default.Equals(referenceSymbols.MessagePackWriter, i.TargetMethod.ContainingSymbol))
					{
						return i.TargetMethod.Name switch
						{
							"WriteArrayHeader" => (1 + GetVariableImpact(i), true),
							"WriteMapHeader" => (1 + (GetVariableImpact(i) * 2), true),
							string t when t.StartsWith("Write", StringComparison.Ordinal) => (1, true),
							_ => (0, true),
						};
					}
					else if (i.TargetMethod.ContainingSymbol is ITypeSymbol s && s.IsOrDerivedFrom(referenceSymbols.MessagePackConverterUnbound))
					{
						return i.TargetMethod.Name switch
						{
							"Serialize" or "SerializeAsync" => (1, true),
							_ => (0, true),
						};
					}
					else
					{
						return (0, true);
					}
				};

				break;
			case "Deserialize":
				relevantMethodTest = i =>
				{
					if (SymbolEqualityComparer.Default.Equals(referenceSymbols.MessagePackReader, i.TargetMethod.ContainingSymbol))
					{
						return i.TargetMethod.Name switch
						{
							"ReadArrayHeader" or "ReadMapHeader" => (null, true), // Advanced case, which we'll just assume they're doing correctly.
							"TryReadArrayHeader" or "TryReadMapHeader" => (null, false), // Advanced case, which we'll just assume they're doing correctly.
							"TryReadNil" => (1, false),
							string t when t.StartsWith("Read", StringComparison.Ordinal) => (1, true),
							_ => (0, true),
						};
					}
					else if (i.TargetMethod.ContainingSymbol is ITypeSymbol s && s.IsOrDerivedFrom(referenceSymbols.MessagePackConverterUnbound))
					{
						return i.TargetMethod.Name switch
						{
							"Deserialize" or "DeserializeAsync" => (1, true),
							_ => (0, true),
						};
					}
					else
					{
						return (0, true);
					}
				};

				break;
			default:
				return;
		}

		foreach (IOperation block in context.OperationBlocks)
		{
			Debug.Assert(context.OperationBlocks.Length == 1, $"{context.OperationBlocks.Length} == 1");
			ControlFlowGraph flow = context.GetControlFlowGraph(block);
			if (flow.Blocks[0].FallThroughSuccessor?.Destination is { } initialBlock)
			{
				VisitBlock(initialBlock, 0, ImmutableHashSet<BasicBlock>.Empty);
			}

			void VisitBlock(BasicBlock basicBlock, int ops, ImmutableHashSet<BasicBlock> recursionGuard)
			{
				if (recursionGuard.Contains(basicBlock))
				{
					return;
				}

				recursionGuard = recursionGuard.Add(basicBlock);

				foreach (IOperation op in basicBlock.Operations.SelectMany(op => op.DescendantsAndSelf()))
				{
					if (op is IInvocationOperation invocation)
					{
						(int? impact, bool unconditional) = relevantMethodTest(invocation);
						if (impact.HasValue)
						{
							if (!unconditional)
							{
								// The code doesn't care whether it is actually reading a token or not, which is a bug.
								context.ReportDiagnostic(Diagnostic.Create(NotExactlyOneStructureDescriptor, op.Syntax.GetLocation()));
								return;
							}

							ops += impact.Value;
							if (ops > 1)
							{
								// Too many structures.
								context.ReportDiagnostic(Diagnostic.Create(NotExactlyOneStructureDescriptor, op.Syntax.GetLocation()));
								return;
							}
						}
						else
						{
							// Non-deterministic situation. Skip testing.
							return;
						}
					}
				}

				if (ops < 1 && basicBlock.Kind == BasicBlockKind.Exit)
				{
					// Insufficient structures.
					// Ideally the location should be the path that led to this point.
					Location location = ((MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)).Identifier.GetLocation();
					context.ReportDiagnostic(Diagnostic.Create(NotExactlyOneStructureDescriptor, location));
					return;
				}

				int? branchValueImpact = 0;
				bool branchValueUnconditional = true;
				if (basicBlock.BranchValue is IInvocationOperation conditionInvocation)
				{
					(branchValueImpact, branchValueUnconditional) = relevantMethodTest(conditionInvocation);
				}

				if (branchValueImpact is null)
				{
					return;
				}

				if (basicBlock.FallThroughSuccessor?.Destination is BasicBlock nextBlock)
				{
					VisitBlock(nextBlock, basicBlock.ConditionKind == ControlFlowConditionKind.WhenFalse || branchValueUnconditional ? ops + branchValueImpact.Value : ops, recursionGuard);
				}

				if (basicBlock.ConditionalSuccessor?.Destination is BasicBlock conditionalBlock)
				{
					VisitBlock(conditionalBlock, basicBlock.ConditionKind == ControlFlowConditionKind.WhenTrue || branchValueUnconditional ? ops + branchValueImpact.Value : ops, recursionGuard);
				}
			}
		}
	}
}