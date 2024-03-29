using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
        "PackageReference {0} can be removed",
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
            RT0003Descriptor);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(DumpUsedReferences);
    }

    private static void DumpUsedReferences(CompilationAnalysisContext context)
    {
        string? declaredReferencesPath = GetDeclaredReferencesPath(context);
        if (declaredReferencesPath == null)
        {
            // Reference Trimmer is disabled
            return;
        }

        DeclaredReferences declaredReferences = DeclaredReferences.ReadFromFile(declaredReferencesPath);
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

            DumpReferencesInfo(usedReferences, unusedReferences, declaredReferencesPath);
        }

        Dictionary<string, List<string>> packageAssembliesDict = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredReference declaredReference in declaredReferences.References)
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
            if (!packageAssemblies.Any(usedReferences.Contains))
            {
                context.ReportDiagnostic(Diagnostic.Create(RT0003Descriptor, Location.None, packageName));
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

    private static string? GetDeclaredReferencesPath(CompilationAnalysisContext context)
    {
        foreach (AdditionalText additionalText in context.Options.AdditionalFiles)
        {
            if (Path.GetFileName(additionalText.Path).Equals(DeclaredReferencesFileName, StringComparison.Ordinal))
            {
                return additionalText.Path;
            }
        }

        return null;
    }
}
