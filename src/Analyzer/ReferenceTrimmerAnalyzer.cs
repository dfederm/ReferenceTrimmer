using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ReferenceTrimmer.Shared;

namespace ReferenceTrimmer.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
public class ReferenceTrimmerAnalyzer : DiagnosticAnalyzer
{
    private const string DeclaredReferencesFileName = "_ReferenceTrimmer_DeclaredReferences.tsv";
    private const string UsedReferencesFileName = "_ReferenceTrimmer_UsedReferences.tsv";

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
                string assemblyName = AssemblyName.GetAssemblyName(metadataReference.Display).Name;
                usedReferences.Add(assemblyName);
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(declaredReferencesPath), UsedReferencesFileName), usedReferences);

        Dictionary<string, List<string>> packageAssembliesDict = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredReference declaredReference in declaredReferences.References)
        {
            switch (declaredReference.Kind)
            {
                case DeclaredReferenceKind.Reference:
                {
                    if (!usedReferences.Contains(declaredReference.AssemblyName))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(RT0001Descriptor, Location.None, declaredReference.Spec));
                    }

                    break;
                }
                case DeclaredReferenceKind.ProjectReference:
                {
                    if (!usedReferences.Contains(declaredReference.AssemblyName))
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

                    packageAssemblies.Add(declaredReference.AssemblyName);
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
