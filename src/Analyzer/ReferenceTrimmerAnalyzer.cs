using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ReferenceTrimmer.Shared;

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
        "PackageReference {0} can be removed{1}",
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
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(DumpUsedReferences);
    }

    private static void DumpUsedReferences(CompilationAnalysisContext context)
    {
        try
        {
            DumpUsedReferencesCore(context);
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

    private static void DumpUsedReferencesCore(CompilationAnalysisContext context)
    {
        AdditionalText? declaredReferencesFile = GetDeclaredReferencesFile(context);
        if (declaredReferencesFile == null)
        {
            // Reference Trimmer is disabled
            return;
        }

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

        if (!compilation.Options.Errors.IsEmpty)
        {
            return;
        }

        HashSet<string> usedReferences = new(StringComparer.OrdinalIgnoreCase);
        foreach (MetadataReference metadataReference in compilation.GetUsedAssemblyReferences())
        {
            if (metadataReference.Display != null)
            {
                usedReferences.Add(metadataReference.Display);
            }
        }

        var globalOptions = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        if (globalOptions.TryGetValue("build_property.EnableReferenceTrimmerDiagnostics", out string? enableDiagnostics)
            && string.Equals(enableDiagnostics, "true", StringComparison.OrdinalIgnoreCase))
        {
            HashSet<string> unusedReferences = new(StringComparer.OrdinalIgnoreCase);
            foreach (MetadataReference metadataReference in compilation.References)
            {
                if (metadataReference.Display != null && !usedReferences.Contains(metadataReference.Display))
                {
                    unusedReferences.Add(metadataReference.Display);
                }
            }

            DumpReferencesInfo(usedReferences, unusedReferences, declaredReferencesFile.Path);
        }

        Dictionary<string, List<string>> packageAssembliesDict = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> topLevelPackageAssembliesDict = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredReference declaredReference in ReadDeclaredReferences(sourceText))
        {
            switch (declaredReference.Kind)
            {
                case DeclaredReferenceKind.Reference:
                {
                    if (!usedReferences.Contains(declaredReference.AssemblyPath))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RT0001Descriptor, Location.None, declaredReference.Spec));
                    }

                    break;
                }
                case DeclaredReferenceKind.ProjectReference:
                {
                    if (!usedReferences.Contains(declaredReference.AssemblyPath))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RT0002Descriptor, Location.None, declaredReference.Spec));
                    }

                    break;
                }
                case DeclaredReferenceKind.PackageReference:
                {
                    if (!packageAssembliesDict.TryGetValue(declaredReference.Spec, out List<string> packageAssemblies))
                    {
                        packageAssemblies = [];
                        packageAssembliesDict.Add(declaredReference.Spec, packageAssemblies);
                    }

                    packageAssemblies.Add(declaredReference.AssemblyPath);

                    bool isTopLevelPackageAssembly = string.Equals(declaredReference.Spec, declaredReference.AdditionalSpec, StringComparison.OrdinalIgnoreCase);
                    if (isTopLevelPackageAssembly)
                    {
                        if (!topLevelPackageAssembliesDict.TryGetValue(declaredReference.Spec, out List<string> topLevelPackageAssemblies))
                        {
                            topLevelPackageAssemblies = [];
                            topLevelPackageAssembliesDict.Add(declaredReference.Spec, topLevelPackageAssemblies);
                        }

                        topLevelPackageAssemblies.Add(declaredReference.AssemblyPath);
                    }
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
                context.ReportDiagnostic(Diagnostic.Create(RT0003Descriptor, Location.None, packageName, string.Empty));
            }
            else if (!topLevelPackageAssembliesDict[packageName].Any(usedReferences.Contains))
            {
                context.ReportDiagnostic(Diagnostic.Create(RT0003Descriptor, Location.None, packageName, " (though some of its transitive dependent packages may be used)"));
            }
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

    private static AdditionalText? GetDeclaredReferencesFile(CompilationAnalysisContext context)
    {
        foreach (AdditionalText additionalText in context.Options.AdditionalFiles)
        {
            if (Path.GetFileName(additionalText.Path).Equals(DeclaredReferencesFileName, StringComparison.Ordinal))
            {
                return additionalText;
            }
        }

        return null;
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
