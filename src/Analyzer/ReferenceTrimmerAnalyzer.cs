using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using ReferenceTrimmer.Shared;
using CSharp = Microsoft.CodeAnalysis.CSharp;

namespace ReferenceTrimmer.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
public class ReferenceTrimmerAnalyzer : DiagnosticAnalyzer
{
    private const string DeclaredReferencesFileName = "_ReferenceTrimmer_DeclaredReferences.tsv";
    private const string UsedReferencesFileName = "_ReferenceTrimmer_UsedReferences.log";
    private const string UnusedReferencesFileName = "_ReferenceTrimmer_UnusedReferences.log";

    private static readonly DiagnosticDescriptor RT0000Descriptor = new(
        "RT0000",
        "Enable documentation generation for accuracy of used references detection",
        "Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection",
        "ReferenceTrimmer",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RT0001Descriptor = new(
        "RT0001",
        "Unnecessary reference",
        "Reference {0} can be removed",
        "ReferenceTrimmer",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RT0002Descriptor = new(
        "RT0002",
        "Unnecessary project reference",
        "ProjectReference {0} can be removed",
        "ReferenceTrimmer",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RT0003Descriptor = new(
        "RT0003",
        "Unnecessary package reference",
        "PackageReference {0} can be removed",
        "ReferenceTrimmer",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RT9999Descriptor = new(
        "RT9999",
        "ReferenceTrimmer internal error",
        "ReferenceTrimmer encountered an unexpected error: {0}. Please file a bug at https://github.com/dfederm/ReferenceTrimmer/issues",
        "ReferenceTrimmer",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly StringComparer PathComparer =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// The supported diagnostics.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(
            RT0000Descriptor,
            RT0001Descriptor,
            RT0002Descriptor,
            RT0003Descriptor,
            RT9999Descriptor);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.RegisterCompilationStartAction(CompilationStart);
    }

    private static void CompilationStart(CompilationStartAnalysisContext context)
    {
        // Check if ReferenceTrimmer is enabled
        AdditionalText? declaredReferencesFile = FindDeclaredReferencesFile(context.Options.AdditionalFiles);
        if (declaredReferencesFile == null)
        {
            return;
        }

        Compilation compilation = context.Compilation;

        if (!compilation.Options.Errors.IsEmpty)
        {
            return;
        }

        var globalOptions = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        bool useSymbolAnalysis =
            globalOptions.TryGetValue("build_property.ReferenceTrimmerUseSymbolAnalysis", out string? useSymbol)
            && string.Equals(useSymbol, "true", StringComparison.OrdinalIgnoreCase);

        if (useSymbolAnalysis)
        {
            InitializeSymbolBasedAnalysis(context, compilation, declaredReferencesFile);
        }
        else
        {
            context.RegisterCompilationEndAction(endContext => RunDefaultAnalysis(endContext, declaredReferencesFile));
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Default analysis path (GetUsedAssemblyReferences)
    // ──────────────────────────────────────────────────────────────────────

    private static void RunDefaultAnalysis(CompilationAnalysisContext context, AdditionalText declaredReferencesFile)
    {
        try
        {
            RunDefaultAnalysisCore(context, declaredReferencesFile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(RT9999Descriptor, Location.None, ex.Message));
        }
    }

    private static void RunDefaultAnalysisCore(CompilationAnalysisContext context, AdditionalText declaredReferencesFile)
    {
        SourceText? sourceText = declaredReferencesFile.GetText(context.CancellationToken);
        if (sourceText == null)
        {
            return;
        }

        Compilation compilation = context.Compilation;
        if (compilation.SyntaxTrees.FirstOrDefault()?.Options.DocumentationMode == DocumentationMode.None)
        {
            context.ReportDiagnostic(Diagnostic.Create(RT0000Descriptor, Location.None));
        }

        HashSet<string> usedReferences = new(PathComparer);
        foreach (MetadataReference metadataReference in compilation.GetUsedAssemblyReferences())
        {
            if (metadataReference.Display != null)
            {
                usedReferences.Add(metadataReference.Display);
            }
        }

        ReportUnusedReferences(context, declaredReferencesFile, sourceText, usedReferences, usedReferences);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Symbol-based analysis path (experimental, opt-in)
    // ──────────────────────────────────────────────────────────────────────

    private static void InitializeSymbolBasedAnalysis(
        CompilationStartAnalysisContext context,
        Compilation compilation,
        AdditionalText declaredReferencesFile)
    {
        // Build mappings from reference assembly identities to their metadata reference display paths.
        // These are used both for symbol tracking and for the transitive closure computation.
        var assemblyToPath = new Dictionary<AssemblyIdentity, string>();
        var pathToAssembly = new Dictionary<string, IAssemblySymbol>(PathComparer);
        foreach (MetadataReference reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm && reference.Display != null)
            {
                if (!assemblyToPath.ContainsKey(asm.Identity))
                {
                    assemblyToPath.Add(asm.Identity, reference.Display);
                }

                if (!pathToAssembly.ContainsKey(reference.Display))
                {
                    pathToAssembly.Add(reference.Display, asm);
                }
            }
        }

        int totalReferenceCount = assemblyToPath.Count;
        var usedReferencePaths = new ConcurrentDictionary<string, byte>(PathComparer);
        // Monotonically increasing counter. Once it reaches totalReferenceCount, all
        // callbacks short-circuit. A briefly stale read just means a few extra no-op lookups.
        int trackedCount = 0;

        // Tracks named types whose inheritance chain (base types + transitively-implemented
        // interfaces) has already been walked, to break self-referential cycles such as
        // `int` → `IComparable<int>` → typeArg `int` → ... and to avoid redundant work for
        // types used many times. Use SymbolEqualityComparer to match Roslyn semantics for
        // constructed generic types (e.g., distinct INamedTypeSymbol instances representing
        // the same `List<int>` are considered equal).
#pragma warning disable RS1024 // Compare symbols correctly (false positive: comparer is supplied explicitly)
        var inheritanceWalked = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024

        void TrackAssembly(IAssemblySymbol? assembly)
        {
            if (trackedCount >= totalReferenceCount)
            {
                return;
            }

            // Skip the compilation's own assembly — it's never an external reference.
            if (assembly == null || ReferenceEquals(assembly, compilation.Assembly))
            {
                return;
            }

            if (assemblyToPath.TryGetValue(assembly.Identity, out string? path)
                && usedReferencePaths.TryAdd(path, 0))
            {
                Interlocked.Increment(ref trackedCount);
            }
        }

        void TrackType(ITypeSymbol? type)
        {
            while (type != null)
            {
                if (trackedCount >= totalReferenceCount)
                {
                    return;
                }

                switch (type)
                {
                    case IArrayTypeSymbol array:
                        type = array.ElementType;
                        continue;
                    case IPointerTypeSymbol pointer:
                        type = pointer.PointedAtType;
                        continue;
                    case IFunctionPointerTypeSymbol funcPtr:
                        TrackType(funcPtr.Signature.ReturnType);
                        foreach (IParameterSymbol fpParam in funcPtr.Signature.Parameters)
                        {
                            TrackType(fpParam.Type);
                        }

                        return;
                    default:
                        TrackAssembly(type.ContainingAssembly);
                        if (type is INamedTypeSymbol named)
                        {
                            foreach (ITypeSymbol typeArg in named.TypeArguments)
                            {
                                TrackType(typeArg);
                            }

                            // When a named type is referenced, the C# compiler validates the entire
                            // inheritance chain at type-check time -- CS0012 fires when *any* base
                            // type or implemented interface is defined in an unreferenced assembly.
                            // Walk BaseType (recursively) and AllInterfaces (transitively) so every
                            // assembly along the chain is credited. AllInterfaces is broader than
                            // Interfaces and mirrors what the compiler actually validates. The
                            // visited-set guard breaks self-referential cycles (e.g. int ->
                            // IComparable<int> -> typeArg int -> ...) and avoids redundant work.
                            if (inheritanceWalked.TryAdd(named, 0))
                            {
                                for (INamedTypeSymbol? baseType = named.BaseType; baseType != null; baseType = baseType.BaseType)
                                {
                                    TrackAssembly(baseType.ContainingAssembly);
                                    foreach (ITypeSymbol typeArg in baseType.TypeArguments)
                                    {
                                        TrackType(typeArg);
                                    }
                                }

                                foreach (INamedTypeSymbol iface in named.AllInterfaces)
                                {
                                    TrackAssembly(iface.ContainingAssembly);
                                    foreach (ITypeSymbol typeArg in iface.TypeArguments)
                                    {
                                        TrackType(typeArg);
                                    }
                                }
                            }
                        }

                        return;
                }
            }
        }

        void TrackAttribute(AttributeData attr)
        {
            TrackType(attr.AttributeClass);
            foreach (TypedConstant arg in attr.ConstructorArguments)
            {
                TrackTypedConstant(arg);
            }

            foreach (KeyValuePair<string, TypedConstant> arg in attr.NamedArguments)
            {
                TrackTypedConstant(arg.Value);
            }
        }

        void TrackTypedConstant(TypedConstant constant)
        {
            TrackType(constant.Type);
            if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol typeValue)
            {
                TrackType(typeValue);
            }
            else if (constant.Kind == TypedConstantKind.Array && !constant.Values.IsDefault)
            {
                foreach (TypedConstant element in constant.Values)
                {
                    TrackTypedConstant(element);
                }
            }
        }

        void TrackPatternTypes(IPatternOperation pattern)
        {
            switch (pattern)
            {
                case ITypePatternOperation typePattern:
                    TrackType(typePattern.MatchedType);
                    break;
                case IDeclarationPatternOperation declPattern:
                    TrackType(declPattern.MatchedType);
                    break;
                case IRecursivePatternOperation recursivePattern:
                    TrackType(recursivePattern.MatchedType);
                    break;
                case INegatedPatternOperation negated:
                    TrackPatternTypes(negated.Pattern);
                    break;
                case IBinaryPatternOperation binary:
                    TrackPatternTypes(binary.LeftPattern);
                    TrackPatternTypes(binary.RightPattern);
                    break;
            }
        }

        // When a member is referenced via an `override`, the C# compiler validates the entire
        // override chain at compile time, so any assembly along the chain must remain a reference.
        // The IOperation only points at the override (the "near" symbol); without walking
        // OverriddenMethod/Property/Event we miss assemblies that declare the original or any
        // intermediate base member, producing false-positive RT0002 diagnostics whose removal
        // results in CS0012 errors.
        void TrackOverriddenChain(ISymbol? member)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    for (IMethodSymbol? overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                    {
                        TrackAssembly(overridden.ContainingAssembly);
                    }

                    break;
                case IPropertySymbol property:
                    for (IPropertySymbol? overridden = property.OverriddenProperty; overridden != null; overridden = overridden.OverriddenProperty)
                    {
                        TrackAssembly(overridden.ContainingAssembly);
                    }

                    break;
                case IEventSymbol evt:
                    for (IEventSymbol? overridden = evt.OverriddenEvent; overridden != null; overridden = overridden.OverriddenEvent)
                    {
                        TrackAssembly(overridden.ContainingAssembly);
                    }

                    break;
            }
        }

        // Track declaration-level type references: base types, interfaces, member signatures, attributes.
        context.RegisterSymbolAction(
            ctx =>
            {
                switch (ctx.Symbol)
                {
                    case INamedTypeSymbol namedType:
                        TrackType(namedType.BaseType);
                        foreach (INamedTypeSymbol iface in namedType.Interfaces)
                        {
                            TrackType(iface);
                        }

                        foreach (ITypeParameterSymbol typeParam in namedType.TypeParameters)
                        {
                            foreach (ITypeSymbol constraint in typeParam.ConstraintTypes)
                            {
                                TrackType(constraint);
                            }
                        }

                        foreach (AttributeData attr in namedType.GetAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        // For delegates, the parameter and return types live on the implicitly-declared
                        // Invoke method, which the SymbolKind.Method action does not fire for. Walk them here.
                        // Mirrors the IMethodSymbol case below (return type, parameter types, return-type
                        // attributes); type-parameter constraints are already covered above for the delegate
                        // type itself, and parameter attributes are not tracked for ordinary methods either.
                        if (namedType.TypeKind == TypeKind.Delegate && namedType.DelegateInvokeMethod is IMethodSymbol invoke)
                        {
                            TrackType(invoke.ReturnType);
                            foreach (IParameterSymbol param in invoke.Parameters)
                            {
                                TrackType(param.Type);
                            }

                            foreach (AttributeData attr in invoke.GetReturnTypeAttributes())
                            {
                                TrackAttribute(attr);
                            }
                        }

                        break;

                    case IMethodSymbol method:
                        TrackType(method.ReturnType);
                        foreach (IParameterSymbol param in method.Parameters)
                        {
                            TrackType(param.Type);
                        }

                        foreach (ITypeParameterSymbol typeParam in method.TypeParameters)
                        {
                            foreach (ITypeSymbol constraint in typeParam.ConstraintTypes)
                            {
                                TrackType(constraint);
                            }
                        }

                        foreach (AttributeData attr in method.GetAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        foreach (AttributeData attr in method.GetReturnTypeAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        break;

                    case IPropertySymbol property:
                        TrackType(property.Type);
                        foreach (AttributeData attr in property.GetAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        break;

                    case IFieldSymbol field:
                        TrackType(field.Type);
                        foreach (AttributeData attr in field.GetAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        break;

                    case IEventSymbol evt:
                        TrackType(evt.Type);
                        foreach (AttributeData attr in evt.GetAttributes())
                        {
                            TrackAttribute(attr);
                        }

                        break;
                }
            },
            SymbolKind.NamedType,
            SymbolKind.Method,
            SymbolKind.Property,
            SymbolKind.Field,
            SymbolKind.Event);

        // Track body-level references: method calls, member access, object creation, type checks, etc.
        context.RegisterOperationAction(
            ctx =>
            {
                IOperation operation = ctx.Operation;
                TrackType(operation.Type);

                switch (operation)
                {
                    case IInvocationOperation invocation:
                        TrackAssembly(invocation.TargetMethod.ContainingAssembly);
                        TrackOverriddenChain(invocation.TargetMethod);
                        foreach (ITypeSymbol typeArg in invocation.TargetMethod.TypeArguments)
                        {
                            TrackType(typeArg);
                        }

                        break;

                    case IObjectCreationOperation creation:
                        TrackAssembly(creation.Constructor?.ContainingAssembly);
                        break;

                    case IMemberReferenceOperation memberRef:
                        TrackAssembly(memberRef.Member.ContainingAssembly);
                        TrackOverriddenChain(memberRef.Member);
                        break;

                    case ITypeOfOperation typeOfOp:
                        TrackType(typeOfOp.TypeOperand);
                        break;

                    case IConversionOperation conversion:
                        TrackAssembly(conversion.OperatorMethod?.ContainingAssembly);
                        TrackType(conversion.Operand.Type);
                        break;

                    case IBinaryOperation binary:
                        TrackAssembly(binary.OperatorMethod?.ContainingAssembly);
                        break;

                    case IUnaryOperation unary:
                        TrackAssembly(unary.OperatorMethod?.ContainingAssembly);
                        break;

                    case ICompoundAssignmentOperation compound:
                        TrackAssembly(compound.OperatorMethod?.ContainingAssembly);
                        break;

                    case IIncrementOrDecrementOperation incDec:
                        TrackAssembly(incDec.OperatorMethod?.ContainingAssembly);
                        break;

                    case IIsTypeOperation isTypeOp:
                        TrackType(isTypeOp.TypeOperand);
                        break;

                    case IIsPatternOperation isPatternOp:
                        TrackPatternTypes(isPatternOp.Pattern);
                        break;

                    case ISwitchOperation switchOp:
                        foreach (ISwitchCaseOperation caseOp in switchOp.Cases)
                        {
                            foreach (ICaseClauseOperation clause in caseOp.Clauses)
                            {
                                if (clause is IPatternCaseClauseOperation patternClause)
                                {
                                    TrackPatternTypes(patternClause.Pattern);
                                }
                            }
                        }

                        break;

                    case ISwitchExpressionOperation switchExpr:
                        foreach (ISwitchExpressionArmOperation arm in switchExpr.Arms)
                        {
                            TrackPatternTypes(arm.Pattern);
                        }

                        break;

                    case ITypePatternOperation typePattern:
                        TrackType(typePattern.MatchedType);
                        break;

                    case IDeclarationPatternOperation declPattern:
                        TrackType(declPattern.MatchedType);
                        break;

                    case IRecursivePatternOperation recursivePattern:
                        TrackType(recursivePattern.MatchedType);
                        break;

                    case ICatchClauseOperation catchClause:
                        TrackType(catchClause.ExceptionType);
                        break;

                    case ISwitchExpressionArmOperation switchArm:
                        TrackPatternTypes(switchArm.Pattern);
                        break;

                    case IPatternCaseClauseOperation patternClause:
                        TrackPatternTypes(patternClause.Pattern);
                        break;

                    case ILocalFunctionOperation localFunc:
                        TrackType(localFunc.Symbol.ReturnType);
                        foreach (IParameterSymbol lfParam in localFunc.Symbol.Parameters)
                        {
                            TrackType(lfParam.Type);
                        }

                        break;

                    case IAnonymousFunctionOperation lambda:
                        foreach (IParameterSymbol lambdaParam in lambda.Symbol.Parameters)
                        {
                            TrackType(lambdaParam.Type);
                        }

                        break;

                    case IVariableDeclaratorOperation varDecl:
                        TrackType(varDecl.Symbol.Type);
                        break;

                    case ISizeOfOperation sizeOfOp:
                        TrackType(sizeOfOp.TypeOperand);
                        break;
                }
            },
            OperationKind.Invocation,
            OperationKind.ObjectCreation,
            OperationKind.FieldReference,
            OperationKind.PropertyReference,
            OperationKind.EventReference,
            OperationKind.MethodReference,
            OperationKind.TypeOf,
            OperationKind.DefaultValue,
            OperationKind.Conversion,
            OperationKind.Binary,
            OperationKind.Unary,
            OperationKind.CompoundAssignment,
            OperationKind.Increment,
            OperationKind.Decrement,
            OperationKind.IsType,
            OperationKind.IsPattern,
            OperationKind.Switch,
            OperationKind.SwitchExpression,
            OperationKind.TypePattern,
            OperationKind.DeclarationPattern,
            OperationKind.RecursivePattern,
            OperationKind.CatchClause,
            OperationKind.SwitchExpressionArm,
            OperationKind.CaseClause,
            OperationKind.LocalFunction,
            OperationKind.AnonymousFunction,
            OperationKind.SizeOf,
            OperationKind.VariableDeclarator);

        // Track nameof() and XML doc cref references via language-specific syntax actions.
        // These require syntax-level analysis because nameof is lowered to a string literal
        // and crefs live in documentation trivia — neither surfaces through IOperation.
        if (compilation.Language == LanguageNames.CSharp)
        {
            RegisterCSharpSyntaxTracking(context, TrackAssembly, TrackType);
        }

        context.RegisterCompilationEndAction(endContext =>
        {
            try
            {
                // Track assembly-level attributes
                foreach (AttributeData attr in compilation.Assembly.GetAttributes())
                {
                    TrackAttribute(attr);
                }

                SourceText? sourceText = declaredReferencesFile.GetText(endContext.CancellationToken);
                if (sourceText == null)
                {
                    return;
                }

                // Mark type-forwarding assemblies as used when the destination assembly is used.
                // E.g. a package may forward types to the runtime; the code uses the type (tracking the
                // runtime assembly) but the forwarder assembly must also be kept as a reference.
                foreach (KeyValuePair<string, IAssemblySymbol> kvp in pathToAssembly)
                {
                    if (usedReferencePaths.ContainsKey(kvp.Key))
                    {
                        continue;
                    }

                    foreach (INamedTypeSymbol forwardedType in kvp.Value.GetForwardedTypes())
                    {
                        if (forwardedType.ContainingAssembly != null
                            && assemblyToPath.TryGetValue(forwardedType.ContainingAssembly.Identity, out string? destPath)
                            && usedReferencePaths.ContainsKey(destPath))
                        {
                            usedReferencePaths.TryAdd(kvp.Key, 0);
                            break;
                        }
                    }
                }

                HashSet<string> usedReferences = new(usedReferencePaths.Keys, PathComparer);

                // For bare Reference items (RT0001), we always need a conservative "transitively used" set
                // because bare References control copy-to-output behavior directly and have no transitive
                // resolution. Removing a bare Reference needed at runtime would break the application.
                //
                // For ProjectReference items (RT0002), we also need the conservative set when
                // DisableTransitiveProjectReferences is enabled, since MSBuild won't propagate transitive
                // project dependencies in that case.
                HashSet<string> transitivelyUsedReferences = ComputeTransitivelyUsedReferences(assemblyToPath, pathToAssembly, usedReferences);

                ReportUnusedReferences(endContext, declaredReferencesFile, sourceText, usedReferences, transitivelyUsedReferences);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                endContext.ReportDiagnostic(Diagnostic.Create(RT9999Descriptor, Location.None, ex.Message));
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Shared reporting logic
    // ──────────────────────────────────────────────────────────────────────

    private static void ReportUnusedReferences(
        CompilationAnalysisContext context,
        AdditionalText declaredReferencesFile,
        SourceText sourceText,
        HashSet<string> usedReferences,
        HashSet<string> transitivelyUsedReferences)
    {
        Compilation compilation = context.Compilation;

        bool disableTransitiveProjectReferences =
            context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.DisableTransitiveProjectReferences", out string? disableTransitive)
            && string.Equals(disableTransitive, "true", StringComparison.OrdinalIgnoreCase);
        HashSet<string> projectReferenceUsedSet = disableTransitiveProjectReferences ? transitivelyUsedReferences : usedReferences;

        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.EnableReferenceTrimmerDiagnostics", out string? enableDiagnostics)
            && string.Equals(enableDiagnostics, "true", StringComparison.OrdinalIgnoreCase))
        {
            HashSet<string> unusedReferences = new(PathComparer);
            foreach (MetadataReference metadataReference in compilation.References)
            {
                if (metadataReference.Display != null && !usedReferences.Contains(metadataReference.Display))
                {
                    unusedReferences.Add(metadataReference.Display);
                }
            }

            DumpReferencesInfo(usedReferences, unusedReferences, declaredReferencesFile.Path);
        }

        Dictionary<string, List<string>> packageAssembliesDict = new(PathComparer);
        foreach (DeclaredReference declaredReference in ReadDeclaredReferences(sourceText))
        {
            switch (declaredReference.Kind)
            {
                case DeclaredReferenceKind.Reference:
                {
                    // Use the conservative transitively-used set for bare References
                    if (!transitivelyUsedReferences.Contains(declaredReference.AssemblyPath))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RT0001Descriptor, Location.None, declaredReference.Spec));
                    }

                    break;
                }
                case DeclaredReferenceKind.ProjectReference:
                {
                    if (!projectReferenceUsedSet.Contains(declaredReference.AssemblyPath))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RT0002Descriptor, Location.None, declaredReference.Spec));
                    }

                    break;
                }
                case DeclaredReferenceKind.PackageReference:
                {
                    if (!packageAssembliesDict.TryGetValue(declaredReference.Spec, out List<string> packageAssemblies))
                    {
                        packageAssemblies = new List<string>();
                        packageAssembliesDict.Add(declaredReference.Spec, packageAssemblies);
                    }

                    packageAssemblies.Add(declaredReference.AssemblyPath);
                    break;
                }
            }
        }

        // Do a second pass for package assemblies since if any assembly in the package is used, the package is used.
        foreach (KeyValuePair<string, List<string>> kvp in packageAssembliesDict)
        {
            string packageName = kvp.Key;
            List<string> packageAssemblies = kvp.Value;
            if (!usedReferences.Overlaps(packageAssemblies))
            {
                context.ReportDiagnostic(Diagnostic.Create(RT0003Descriptor, Location.None, packageName));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Language-specific syntax tracking (nameof, crefs)
    // ──────────────────────────────────────────────────────────────────────

    // Separate methods per language to avoid JIT-loading the wrong language assembly.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterCSharpSyntaxTracking(
        CompilationStartAnalysisContext context,
        Action<IAssemblySymbol?> trackAssembly,
        Action<ITypeSymbol?> trackType)
    {
        // nameof() — appears as InvocationExpression at the syntax level but is
        // lowered to a string literal in the IOperation tree.
        context.RegisterSyntaxNodeAction(ctx =>
        {
            if (ctx.Node is CSharp.Syntax.InvocationExpressionSyntax invocation
                && invocation.Expression is CSharp.Syntax.IdentifierNameSyntax id
                && id.Identifier.Text == "nameof"
                && invocation.ArgumentList.Arguments.Count > 0)
            {
                // Verify it is actually the nameof operator, not a method called "nameof".
                SymbolInfo invocationInfo = ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken);
                if (invocationInfo.Symbol is IMethodSymbol)
                {
                    return;
                }

                SymbolInfo argInfo = ctx.SemanticModel.GetSymbolInfo(invocation.ArgumentList.Arguments[0].Expression, ctx.CancellationToken);
                ISymbol? symbol = argInfo.Symbol ?? argInfo.CandidateSymbols.FirstOrDefault();
                if (symbol is ITypeSymbol typeSymbol)
                {
                    trackType(typeSymbol);
                }
                else if (symbol != null)
                {
                    trackAssembly(symbol.ContainingAssembly);
                }
            }
        }, CSharp.SyntaxKind.InvocationExpression);

        // Type qualifier in member access (e.g., `Foo.StaticMethod()` or `Foo.NestedType`).
        // For static method calls and static member references, the receiver type appears at
        // the syntax level as a qualifier — it isn't represented in IOperation (Instance is
        // null for static, and the operation tree only carries TargetMethod/Member which point
        // to the *defining* assembly, not the qualifier's assembly). Without this, calls like
        // `Derived.InheritedStaticMethod()` would only credit the base class's assembly and
        // the derived class's assembly would be wrongly flagged as removable.
        context.RegisterSyntaxNodeAction(ctx =>
        {
            if (ctx.Node is CSharp.Syntax.MemberAccessExpressionSyntax memberAccess)
            {
                SymbolInfo info = ctx.SemanticModel.GetSymbolInfo(memberAccess.Expression, ctx.CancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is ITypeSymbol typeSymbol)
                {
                    trackType(typeSymbol);
                }
            }
        }, CSharp.SyntaxKind.SimpleMemberAccessExpression);

        // XML doc <cref> — only relevant when documentation generation is enabled,
        // matching the behavior of GetUsedAssemblyReferences() in the legacy path.
        context.RegisterSyntaxNodeAction(ctx =>
        {
            if (ctx.SemanticModel.SyntaxTree.Options.DocumentationMode == DocumentationMode.None)
            {
                return;
            }

            if (ctx.Node is CSharp.Syntax.XmlCrefAttributeSyntax cref)
            {
                SymbolInfo symbolInfo = ctx.SemanticModel.GetSymbolInfo(cref.Cref, ctx.CancellationToken);
                ISymbol? symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (symbol is ITypeSymbol typeSymbol)
                {
                    trackType(typeSymbol);
                }
                else if (symbol != null)
                {
                    trackAssembly(symbol.ContainingAssembly);
                }
            }
        }, CSharp.SyntaxKind.XmlCrefAttribute);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static AdditionalText? FindDeclaredReferencesFile(ImmutableArray<AdditionalText> additionalFiles)
    {
        foreach (AdditionalText additionalText in additionalFiles)
        {
            if (Path.GetFileName(additionalText.Path).Equals(DeclaredReferencesFileName, StringComparison.Ordinal))
            {
                return additionalText;
            }
        }

        return null;
    }

    private static HashSet<string> ComputeTransitivelyUsedReferences(
        Dictionary<AssemblyIdentity, string> identityToPath,
        Dictionary<string, IAssemblySymbol> pathToAssembly,
        HashSet<string> usedReferences)
    {
        HashSet<string> transitivelyUsed = new(usedReferences, PathComparer);
        Queue<string> queue = new(usedReferences);
        while (queue.Count > 0)
        {
            string path = queue.Dequeue();
            if (pathToAssembly.TryGetValue(path, out IAssemblySymbol? asm))
            {
                foreach (IModuleSymbol module in asm.Modules)
                {
                    foreach (AssemblyIdentity dep in module.ReferencedAssemblies)
                    {
                        if (identityToPath.TryGetValue(dep, out string? depPath)
                            && transitivelyUsed.Add(depPath))
                        {
                            queue.Enqueue(depPath);
                        }
                    }
                }
            }
        }

        return transitivelyUsed;
    }

    private static void DumpReferencesInfo(HashSet<string> usedReferences, HashSet<string> unusedReferences, string declaredReferencesPath)
    {
        string dir = Path.GetDirectoryName(declaredReferencesPath);
        string filePath = Path.Combine(dir, UsedReferencesFileName);
        string text = string.Join(Environment.NewLine, usedReferences.OrderBy(s => s));
        WriteFile(filePath, text);
        filePath = Path.Combine(dir, UnusedReferencesFileName);
        text = string.Join(Environment.NewLine, unusedReferences.OrderBy(s => s));
        WriteFile(filePath, text);
    }

    private static void WriteFile(string filePath, string text)
    {
        try
        {
            if (File.Exists(filePath))
            {
                string oldText = File.ReadAllText(filePath);
                if (string.Equals(text, oldText, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            File.WriteAllText(filePath, text);
        }
        catch
        {
        }
    }

    // File format: tab-separated fields (AssemblyPath, Kind, Spec), one reference per line.
    // Keep in sync with SaveDeclaredReferences in CollectDeclaredReferencesTask.cs.
    private static IEnumerable<DeclaredReference> ReadDeclaredReferences(SourceText sourceText)
    {
        foreach (TextLine textLine in sourceText.Lines)
        {
            TextSpan lineSpan = textLine.Span;
            if (lineSpan.Length == 0)
            {
                continue;
            }

            // Find tab delimiters within the line span to avoid full-line ToString + Split.
            int start = lineSpan.Start;
            int end = lineSpan.End;

            int firstTab = -1;
            int secondTab = -1;
            for (int i = start; i < end; i++)
            {
                if (sourceText[i] == '\t')
                {
                    if (firstTab == -1)
                    {
                        firstTab = i;
                    }
                    else
                    {
                        secondTab = i;
                        break;
                    }
                }
            }

            if (firstTab == -1 || secondTab == -1)
            {
                yield break;
            }

            string assemblyPath = sourceText.ToString(TextSpan.FromBounds(start, firstTab));
            string spec = sourceText.ToString(TextSpan.FromBounds(secondTab + 1, end));

            // Determine kind without allocating a string. The three possible values are
            // "Reference" (len 9), "ProjectReference" (len 16), "PackageReference" (len 16).
            int kindLength = secondTab - firstTab - 1;
            DeclaredReferenceKind kind;
            if (kindLength == 9)
            {
                kind = DeclaredReferenceKind.Reference;
            }
            else if (kindLength == 16 && sourceText[firstTab + 1] == 'P' && sourceText[firstTab + 2] == 'r')
            {
                kind = DeclaredReferenceKind.ProjectReference;
            }
            else if (kindLength == 16 && sourceText[firstTab + 1] == 'P' && sourceText[firstTab + 2] == 'a')
            {
                kind = DeclaredReferenceKind.PackageReference;
            }
            else
            {
                continue;
            }

            yield return new DeclaredReference(assemblyPath, kind, spec);
        }
    }
}
