using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReferenceTrimmer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UsedAssemblyReferencesDumper : DiagnosticAnalyzer
    {
        internal static readonly string Title = "Unused references should be removed";
        internal static DiagnosticDescriptor Rule = new DiagnosticDescriptor("RT0001", Title, "'{0}'", "References", DiagnosticSeverity.Warning, true, customTags: WellKnownDiagnosticTags.Unnecessary);

        /// <summary>
        /// The supported diagnosticts.
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationAction(DumpUsedReferences);
        }

        private static void DumpUsedReferences(CompilationAnalysisContext context)
        {
            Compilation compilation = context.Compilation;
            if (compilation.Options.Errors.IsEmpty)
            {
                IEnumerable<MetadataReference> usedReferences = compilation.GetUsedAssemblyReferences();
                AdditionalText analyzerOutputFile = context.Options.AdditionalFiles.FirstOrDefault(file => file.Path.EndsWith("_ReferenceTrimmer_GetUsedAssemblyReferences.txt", StringComparison.OrdinalIgnoreCase));
                Directory.CreateDirectory(Path.GetDirectoryName(analyzerOutputFile.Path));
                File.WriteAllLines(analyzerOutputFile.Path, usedReferences.Select(reference => reference.Display));
            }
        }
    }
}