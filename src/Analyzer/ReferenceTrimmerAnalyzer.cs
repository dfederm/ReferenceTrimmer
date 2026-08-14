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
        HashSet<AssemblyIdentity> usedAssemblyIdentities = new();
        foreach (MetadataReference metadataReference in compilation.GetUsedAssemblyReferences())
        {
            string? referencePath = GetReferencePath(metadataReference);
            if (referencePath is not null)
            {
                usedReferences.Add(referencePath);
            }

            AssemblyIdentity? identity = GetReferenceAssemblyIdentity(compilation, metadataReference);
            if (identity is not null)
            {
                usedAssemblyIdentities.Add(identity);
            }
        }

        ReportUnusedReferences(
            context,
            declaredReferencesFile,
            sourceText,
            usedReferences,
            usedReferences,
            usedAssemblyIdentities,
            usedAssemblyIdentities);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Symbol-based analysis path (experimental, opt-in)
    // ──────────────────────────────────────────────────────────────────────

    private static void InitializeSymbolBasedAnalysis(
        CompilationStartAnalysisContext context,
        Compilation compilation,
        AdditionalText declaredReferencesFile)
    {
        // Build mappings from reference assembly identities to their source symbols and file paths.
        // These are used both for symbol tracking and for the transitive closure computation.
        var referencesByIdentity = new Dictionary<AssemblyIdentity, ReferenceInfo>();
        foreach (MetadataReference reference in compilation.References)
        {
            IAssemblySymbol? asm = GetReferenceAssemblySymbol(compilation, reference);
            if (asm is not null)
            {
                if (!referencesByIdentity.TryGetValue(asm.Identity, out ReferenceInfo? referenceInfo))
                {
                    referenceInfo = new ReferenceInfo(asm);
                    referencesByIdentity.Add(asm.Identity, referenceInfo);
                }
                else
                {
                    referenceInfo.AddAssembly(asm);
                }

                string? referencePath = GetReferencePath(reference);
                if (referencePath is not null)
                {
                    referenceInfo.AddPath(referencePath);
                }
            }
        }

        int totalReferenceCount = referencesByIdentity.Count;
        var usedReferencePaths = new ConcurrentDictionary<string, byte>(PathComparer);
        var usedAssemblyIdentities = new ConcurrentDictionary<AssemblyIdentity, byte>();
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

        // Tracks (type, memberName) pairs whose overload-sibling walk has already been
        // performed, and (type) for whole-type indexer scans. Keyed by INamedTypeSymbol with
        // SymbolEqualityComparer to dedup across distinct INamedTypeSymbol instances that
        // refer to the same constructed type.
        var memberLookupWalked = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<string, byte>>(SymbolEqualityComparer.Default);
        var indexerLookupWalked = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
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

            if (!referencesByIdentity.TryGetValue(assembly.Identity, out ReferenceInfo? referenceInfo)
                || !usedAssemblyIdentities.TryAdd(assembly.Identity, 0))
            {
                return;
            }

            Interlocked.Increment(ref trackedCount);
            if (referenceInfo.Path is not null)
            {
                usedReferencePaths.TryAdd(referenceInfo.Path, 0);
                if (referenceInfo.AdditionalPaths is not null)
                {
                    foreach (string path in referenceInfo.AdditionalPaths)
                    {
                        usedReferencePaths.TryAdd(path, 0);
                    }
                }
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
                                // When the C# compiler resolves this type's metadata (for
                                // inheritance, attribute application, or constructor overload
                                // resolution at any base() call site), it loads ALL constructor
                                // signatures, not just the overload that's actually selected.
                                // Every parameter type's containing assembly must therefore be
                                // reachable, even if the overload is never called from source --
                                // otherwise CS0012 fires on the unused overload's parameter type.
                                // See dfederm/ReferenceTrimmer#146.
                                TrackInstanceConstructorParameters(named);

                                for (INamedTypeSymbol? baseType = named.BaseType; baseType != null; baseType = baseType.BaseType)
                                {
                                    TrackAssembly(baseType.ContainingAssembly);
                                    foreach (ITypeSymbol typeArg in baseType.TypeArguments)
                                    {
                                        TrackType(typeArg);
                                    }

                                    // The same metadata-closure concern applies up the inheritance
                                    // chain: when a derived class is declared, the compiler resolves
                                    // each base type's full constructor metadata to validate the
                                    // implicit/explicit base() call.
                                    TrackInstanceConstructorParameters(baseType);
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

        // When a named type is loaded by the C# compiler (because it is used as a base type,
        // applied as an attribute, or otherwise has its metadata resolved), the compiler must
        // resolve the parameter types of every constructor on the type, not just the overload
        // that source code actually selects. Overload resolution at any base() call site or
        // attribute application requires *all* candidates to be reachable so the compiler can
        // pick the best match -- a sibling overload that takes a type from an unreferenced
        // assembly produces CS0012 even when no source call selects it.
        // See dfederm/ReferenceTrimmer#146.
        void TrackInstanceConstructorParameters(INamedTypeSymbol type)
        {
            foreach (IMethodSymbol ctor in type.InstanceConstructors)
            {
                foreach (IParameterSymbol param in ctor.Parameters)
                {
                    TrackType(param.Type);
                }
            }
        }

        // When source code performs a name-based member lookup on a type -- e.g. an invocation
        // `p.Foo(x)`, a method-group reference `Action a = p.Foo`, or a static method call
        // `Provider.P.Foo(x)` -- the C# compiler resolves ALL members named `memberName` on
        // the receiver type AND its base chain / implemented interfaces in order to perform
        // overload resolution. Every sibling overload's signature must therefore be metadata-
        // resolvable, even ones source code never actually selects, otherwise CS0012 fires
        // on the unused overload's parameter or return type. The selected overload's assembly
        // is already credited via the existing TargetMethod tracking; this fills the gap for
        // siblings. Same family as the constructor-overload gap (#146) and the override-chain
        // gap (PR #143), specialized to per-call-site name resolution.
        void TrackOverloadSiblings(ITypeSymbol? receiverType, string? memberName)
        {
            if (receiverType is not INamedTypeSymbol named || string.IsNullOrEmpty(memberName))
            {
                return;
            }

            for (INamedTypeSymbol? t = named; t != null; t = t.BaseType)
            {
                TrackOverloadSiblingsOnType(t, memberName!);
            }

            foreach (INamedTypeSymbol iface in named.AllInterfaces)
            {
                TrackOverloadSiblingsOnType(iface, memberName!);
            }
        }

        void TrackOverloadSiblingsOnType(INamedTypeSymbol type, string memberName)
        {
            ConcurrentDictionary<string, byte> namesForType = memberLookupWalked.GetOrAdd(
                type,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            if (!namesForType.TryAdd(memberName, 0))
            {
                return;
            }

            foreach (ISymbol member in type.GetMembers(memberName))
            {
                switch (member)
                {
                    case IMethodSymbol method:
                        TrackType(method.ReturnType);
                        foreach (IParameterSymbol param in method.Parameters)
                        {
                            TrackType(param.Type);
                        }

                        break;
                    case IPropertySymbol property:
                        TrackType(property.Type);
                        foreach (IParameterSymbol param in property.Parameters)
                        {
                            TrackType(param.Type);
                        }

                        break;
                }
            }
        }

        // Indexer access (`p[k]`) performs name-based lookup keyed not on a single name but on
        // "any indexer on the type", so all indexers on the receiver type and its base chain /
        // interfaces participate in overload resolution and must have resolvable signatures.
        void TrackIndexerSiblings(ITypeSymbol? receiverType)
        {
            if (receiverType is not INamedTypeSymbol named)
            {
                return;
            }

            for (INamedTypeSymbol? t = named; t != null; t = t.BaseType)
            {
                TrackIndexerSiblingsOnType(t);
            }

            foreach (INamedTypeSymbol iface in named.AllInterfaces)
            {
                TrackIndexerSiblingsOnType(iface);
            }
        }

        void TrackIndexerSiblingsOnType(INamedTypeSymbol type)
        {
            if (!indexerLookupWalked.TryAdd(type, 0))
            {
                return;
            }

            foreach (ISymbol member in type.GetMembers())
            {
                if (member is IPropertySymbol property && property.IsIndexer)
                {
                    TrackType(property.Type);
                    foreach (IParameterSymbol param in property.Parameters)
                    {
                        TrackType(param.Type);
                    }
                }
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

                        // The compiler performs name-based lookup of `TargetMethod.Name` on
                        // the receiver type's member surface for overload resolution; sibling
                        // overloads on that type and its base chain must be metadata-resolvable.
                        TrackOverloadSiblings(
                            invocation.Instance?.Type ?? invocation.TargetMethod.ContainingType,
                            invocation.TargetMethod.Name);

                        // Extension method invocations (`p.Foo(arg)` where Foo is an extension)
                        // additionally resolve name lookup on the extension method's containing
                        // static class. The receiver type's lookup above misses this because the
                        // static class isn't part of the receiver's inheritance / interface
                        // surface. Sibling extension overloads on the static class must still be
                        // metadata-resolvable; without this, e.g. a sibling `Foo(this P, Dep.X)`
                        // sharing the same name would produce CS0012 on Dep.X's assembly when
                        // overload resolution inspects it.
                        if (invocation.TargetMethod.IsExtensionMethod)
                        {
                            TrackOverloadSiblings(
                                invocation.TargetMethod.ContainingType,
                                invocation.TargetMethod.Name);
                        }

                        break;

                    case IObjectCreationOperation creation:
                        TrackAssembly(creation.Constructor?.ContainingAssembly);
                        break;

                    case IMemberReferenceOperation memberRef:
                        TrackAssembly(memberRef.Member.ContainingAssembly);
                        TrackOverriddenChain(memberRef.Member);

                        // Method-group references (e.g. `Action a = p.Foo;`) trigger the same
                        // name-based overload resolution as IInvocationOperation; track sibling
                        // overloads of the referenced method on the receiver type.
                        if (memberRef is IMethodReferenceOperation methodRef)
                        {
                            TrackOverloadSiblings(
                                methodRef.Instance?.Type ?? methodRef.Method.ContainingType,
                                methodRef.Method.Name);

                            // Same extension-method special case as IInvocationOperation.
                            if (methodRef.Method.IsExtensionMethod)
                            {
                                TrackOverloadSiblings(
                                    methodRef.Method.ContainingType,
                                    methodRef.Method.Name);
                            }
                        }
                        else if (memberRef is IPropertyReferenceOperation propRef && propRef.Property.IsIndexer)
                        {
                            // Indexer access (`p[k]`) performs name-based lookup over all
                            // indexers on the receiver type; sibling indexers must have
                            // metadata-resolvable signatures.
                            TrackIndexerSiblings(propRef.Instance?.Type ?? propRef.Property.ContainingType);
                        }

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
                foreach (KeyValuePair<AssemblyIdentity, ReferenceInfo> kvp in referencesByIdentity)
                {
                    if (usedAssemblyIdentities.ContainsKey(kvp.Key))
                    {
                        continue;
                    }

                    IAssemblySymbol? forwardingAssembly = GetForwardingAssembly(kvp.Value, usedAssemblyIdentities);
                    if (forwardingAssembly is not null)
                    {
                        TrackAssembly(forwardingAssembly);
                    }
                }

                HashSet<string> usedReferences = new(usedReferencePaths.Keys, PathComparer);
                HashSet<AssemblyIdentity> usedIdentities = new(usedAssemblyIdentities.Keys);

                // For bare Reference items (RT0001), we always need a conservative "transitively used" set
                // because bare References control copy-to-output behavior directly and have no transitive
                // resolution. Removing a bare Reference needed at runtime would break the application.
                //
                // For ProjectReference items (RT0002), we also need the conservative set when
                // DisableTransitiveProjectReferences is enabled, since MSBuild won't propagate transitive
                // project dependencies in that case.
                HashSet<AssemblyIdentity> transitivelyUsedIdentities = ComputeTransitivelyUsedAssemblyIdentities(referencesByIdentity, usedIdentities);
                HashSet<string> transitivelyUsedReferences = GetReferencePaths(referencesByIdentity, transitivelyUsedIdentities, usedReferences);

                ReportUnusedReferences(
                    endContext,
                    declaredReferencesFile,
                    sourceText,
                    usedReferences,
                    transitivelyUsedReferences,
                    usedIdentities,
                    transitivelyUsedIdentities);
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
        HashSet<string> transitivelyUsedReferences,
        HashSet<AssemblyIdentity> usedAssemblyIdentities,
        HashSet<AssemblyIdentity> transitivelyUsedAssemblyIdentities)
    {
        Compilation compilation = context.Compilation;

        bool disableTransitiveProjectReferences =
            context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.DisableTransitiveProjectReferences", out string? disableTransitive)
            && string.Equals(disableTransitive, "true", StringComparison.OrdinalIgnoreCase);
        HashSet<string> projectReferenceUsedSet = disableTransitiveProjectReferences ? transitivelyUsedReferences : usedReferences;
        HashSet<AssemblyIdentity> projectReferenceUsedIdentitySet = disableTransitiveProjectReferences ? transitivelyUsedAssemblyIdentities : usedAssemblyIdentities;
        bool hasCompilationReferences = false;
        foreach (MetadataReference reference in compilation.References)
        {
            if (reference is CompilationReference)
            {
                hasCompilationReferences = true;
                break;
            }
        }

        HashSet<string>? portableExecutableReferencePaths = null;
        if (hasCompilationReferences)
        {
            portableExecutableReferencePaths = new HashSet<string>(PathComparer);
            foreach (MetadataReference reference in compilation.References)
            {
                string? referencePath = GetReferencePath(reference);
                if (referencePath is not null)
                {
                    portableExecutableReferencePaths.Add(referencePath);
                }
            }
        }

        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.EnableReferenceTrimmerDiagnostics", out string? enableDiagnostics)
            && string.Equals(enableDiagnostics, "true", StringComparison.OrdinalIgnoreCase))
        {
            HashSet<string> usedReferenceDiagnostics = new(PathComparer);
            HashSet<string> unusedReferences = new(PathComparer);
            foreach (MetadataReference metadataReference in compilation.References)
            {
                string? referencePath = GetReferencePath(metadataReference);
                AssemblyIdentity? referenceIdentity = GetReferenceAssemblyIdentity(compilation, metadataReference);
                string? diagnosticKey = referencePath ?? referenceIdentity?.GetDisplayName();
                if (diagnosticKey is null)
                {
                    continue;
                }

                bool isUsed =
                    (referencePath is not null && usedReferences.Contains(referencePath))
                    || (referenceIdentity is not null && usedAssemblyIdentities.Contains(referenceIdentity));
                if (isUsed)
                {
                    usedReferenceDiagnostics.Add(diagnosticKey);
                }
                else
                {
                    unusedReferences.Add(diagnosticKey);
                }
            }

            DumpReferencesInfo(usedReferenceDiagnostics, unusedReferences, declaredReferencesFile.Path);
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
                    bool isUsed = projectReferenceUsedSet.Contains(declaredReference.AssemblyPath);
                    bool hasAssemblyIdentity = false;
                    if (!isUsed && declaredReference.ProjectAssemblyIdentity.Length != 0)
                    {
                        hasAssemblyIdentity = AssemblyIdentity.TryParseDisplayName(
                            declaredReference.ProjectAssemblyIdentity,
                            out AssemblyIdentity? projectAssemblyIdentity);
                        isUsed = hasAssemblyIdentity
                            && projectReferenceUsedIdentitySet.Contains(projectAssemblyIdentity!);
                    }

                    bool hasPortableExecutableReferencePath =
                        portableExecutableReferencePaths?.Contains(declaredReference.AssemblyPath) == true;

                    if (!isUsed
                        && (hasAssemblyIdentity
                            || hasPortableExecutableReferencePath
                            || !hasCompilationReferences))
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

    private static HashSet<string> GetReferencePaths(
        Dictionary<AssemblyIdentity, ReferenceInfo> referencesByIdentity,
        HashSet<AssemblyIdentity> assemblyIdentities,
        HashSet<string> directlyUsedReferences)
    {
        HashSet<string> paths = new(directlyUsedReferences, PathComparer);
        foreach (AssemblyIdentity identity in assemblyIdentities)
        {
            if (referencesByIdentity.TryGetValue(identity, out ReferenceInfo? referenceInfo)
                && referenceInfo.Path is not null)
            {
                paths.Add(referenceInfo.Path);
                if (referenceInfo.AdditionalPaths is not null)
                {
                    foreach (string path in referenceInfo.AdditionalPaths)
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        return paths;
    }

    private static HashSet<AssemblyIdentity> ComputeTransitivelyUsedAssemblyIdentities(
        Dictionary<AssemblyIdentity, ReferenceInfo> referencesByIdentity,
        HashSet<AssemblyIdentity> usedAssemblyIdentities)
    {
        HashSet<AssemblyIdentity> transitivelyUsed = new(usedAssemblyIdentities);
        Queue<AssemblyIdentity> queue = new(usedAssemblyIdentities);
        while (queue.Count > 0)
        {
            AssemblyIdentity identity = queue.Dequeue();
            if (referencesByIdentity.TryGetValue(identity, out ReferenceInfo? referenceInfo))
            {
                AddReferencedAssemblies(referenceInfo.Assembly, referencesByIdentity, transitivelyUsed, queue);
                if (referenceInfo.AdditionalAssemblies is not null)
                {
                    foreach (IAssemblySymbol assembly in referenceInfo.AdditionalAssemblies)
                    {
                        AddReferencedAssemblies(assembly, referencesByIdentity, transitivelyUsed, queue);
                    }
                }
            }
        }

        return transitivelyUsed;
    }

    private static void AddReferencedAssemblies(
        IAssemblySymbol assembly,
        Dictionary<AssemblyIdentity, ReferenceInfo> referencesByIdentity,
        HashSet<AssemblyIdentity> transitivelyUsed,
        Queue<AssemblyIdentity> queue)
    {
        foreach (IModuleSymbol module in assembly.Modules)
        {
            foreach (AssemblyIdentity dependency in module.ReferencedAssemblies)
            {
                if (referencesByIdentity.ContainsKey(dependency)
                    && transitivelyUsed.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }
    }

    private static IAssemblySymbol? GetForwardingAssembly(
        ReferenceInfo referenceInfo,
        ConcurrentDictionary<AssemblyIdentity, byte> usedAssemblyIdentities)
    {
        if (ForwardsToUsedAssembly(referenceInfo.Assembly, usedAssemblyIdentities))
        {
            return referenceInfo.Assembly;
        }

        if (referenceInfo.AdditionalAssemblies is not null)
        {
            foreach (IAssemblySymbol assembly in referenceInfo.AdditionalAssemblies)
            {
                if (ForwardsToUsedAssembly(assembly, usedAssemblyIdentities))
                {
                    return assembly;
                }
            }
        }

        return null;
    }

    private static bool ForwardsToUsedAssembly(
        IAssemblySymbol assembly,
        ConcurrentDictionary<AssemblyIdentity, byte> usedAssemblyIdentities)
    {
        foreach (INamedTypeSymbol forwardedType in assembly.GetForwardedTypes())
        {
            if (forwardedType.ContainingAssembly is not null
                && usedAssemblyIdentities.ContainsKey(forwardedType.ContainingAssembly.Identity))
            {
                return true;
            }
        }

        return false;
    }

    private static IAssemblySymbol? GetReferenceAssemblySymbol(Compilation compilation, MetadataReference reference)
        => reference is CompilationReference compilationReference
            ? compilationReference.Compilation.Assembly
            : compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;

    private static AssemblyIdentity? GetReferenceAssemblyIdentity(Compilation compilation, MetadataReference reference)
        => GetReferenceAssemblySymbol(compilation, reference)?.Identity;

    private static string? GetReferencePath(MetadataReference reference)
        => (reference as PortableExecutableReference)?.FilePath;

    private sealed class ReferenceInfo(IAssemblySymbol assembly)
    {
        public IAssemblySymbol Assembly { get; } = assembly;

        public List<IAssemblySymbol>? AdditionalAssemblies { get; private set; }

        public string? Path { get; private set; }

        public List<string>? AdditionalPaths { get; private set; }

        public void AddAssembly(IAssemblySymbol assembly)
        {
            if (ReferenceEquals(Assembly, assembly))
            {
                return;
            }

            if (AdditionalAssemblies is not null)
            {
                foreach (IAssemblySymbol existing in AdditionalAssemblies)
                {
                    if (ReferenceEquals(existing, assembly))
                    {
                        return;
                    }
                }
            }
            else
            {
                AdditionalAssemblies = new List<IAssemblySymbol>();
            }

            AdditionalAssemblies.Add(assembly);
        }

        public void AddPath(string path)
        {
            if (Path is null)
            {
                Path = path;
                return;
            }

            if (PathComparer.Equals(Path, path))
            {
                return;
            }

            if (AdditionalPaths is not null)
            {
                foreach (string existing in AdditionalPaths)
                {
                    if (PathComparer.Equals(existing, path))
                    {
                        return;
                    }
                }
            }
            else
            {
                AdditionalPaths = new List<string>();
            }

            AdditionalPaths.Add(path);
        }
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

    // File format: tab-separated fields (AssemblyPath, Kind, Spec, optional ProjectAssemblyIdentity), one reference per line.
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
            int thirdTab = -1;
            for (int i = start; i < end; i++)
            {
                if (sourceText[i] == '\t')
                {
                    if (firstTab == -1)
                    {
                        firstTab = i;
                    }
                    else if (secondTab == -1)
                    {
                        secondTab = i;
                    }
                    else
                    {
                        thirdTab = i;
                        break;
                    }
                }
            }

            if (firstTab == -1 || secondTab == -1)
            {
                yield break;
            }

            string assemblyPath = sourceText.ToString(TextSpan.FromBounds(start, firstTab));
            int specEnd = thirdTab == -1 ? end : thirdTab;
            string spec = sourceText.ToString(TextSpan.FromBounds(secondTab + 1, specEnd));
            string projectAssemblyIdentity = thirdTab == -1
                ? string.Empty
                : sourceText.ToString(TextSpan.FromBounds(thirdTab + 1, end));

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

            yield return new DeclaredReference(assemblyPath, kind, spec, projectAssemblyIdentity);
        }
    }
}
