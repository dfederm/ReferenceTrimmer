using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ReferenceTrimmer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic, LanguageNames.FSharp)]
    public class UsedAssemblyReferencesDumper : DiagnosticAnalyzer
    {
        private static readonly string Title = "Enable documentation generation for accuracy of used references detection";
        private static readonly string Message = "Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection";
        private static DiagnosticDescriptor Rule = new DiagnosticDescriptor("DOC001", Title, Message, "Documentation", DiagnosticSeverity.Warning, true);

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
            if (compilation.SyntaxTrees.FirstOrDefault()?.Options.DocumentationMode == DocumentationMode.None)
            {
                string nameOrPath = compilation.Options.ModuleName;
                Location location = Location.None;
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, nameOrPath));
            }

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