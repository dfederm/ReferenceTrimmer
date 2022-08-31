using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;

namespace ReferenceTrimmer;

internal static class ReferenceTrimmerRunner
{
    public static void Run(Arguments arguments, ILogger logger)
    {
        var buildManager = BuildManager.DefaultBuildManager;
        var parameters = new BuildParameters(ProjectCollection.GlobalProjectCollection);

        if (arguments.UseBinaryLogger)
        {
            const string BinaryLoggerFilename = "msbuild.binlog";
            logger.LogInformation($"Binary logging enabled and will be written to {BinaryLoggerFilename}");
            parameters.Loggers = new[]
            {
                new BinaryLogger { Parameters = Path.Combine(arguments.Path.FullName, BinaryLoggerFilename) },
            };
        }

        buildManager.BeginBuild(parameters);

        IEnumerable<FileInfo> projectFiles = arguments.Path.EnumerateFiles("*.*proj", SearchOption.AllDirectories);
        foreach (FileInfo projectFile in projectFiles)
        {
            var project = ParsedProject.Create(projectFile.FullName, arguments, buildManager, logger);
            if (project == null)
            {
                continue;
            }

            var relativeProjectFile = Path.GetRelativePath(arguments.Path.FullName, projectFile.FullName);

            foreach (var reference in project.References)
            {
                if (!project.AssemblyReferences.Contains(reference))
                {
                    logger.LogInformation($"Reference {reference} can be removed from {relativeProjectFile}");
                }
            }

            foreach (var projectReference in project.ProjectReferences)
            {
                var projectReferenceAssemblyName = projectReference.Project.AssemblyName;
                if (!project.AssemblyReferences.Contains(projectReferenceAssemblyName))
                {
                    logger.LogInformation($"ProjectReference {projectReference.UnevaluatedInclude} can be removed from {relativeProjectFile}");
                }
            }

            foreach (var packageReference in project.PackageReferences)
            {
                if (!project.PackageAssemblies.TryGetValue(packageReference, out var packageAssemblies))
                {
                    // These are likely Analyzers, tools, etc.
                    continue;
                }

                if (!packageAssemblies.Any(packageAssembly => project.AssemblyReferences.Contains(packageAssembly)))
                {
                    logger.LogInformation($"PackageReference {packageReference} can be removed from {relativeProjectFile}");
                }
            }
        }

        buildManager.EndBuild();
    }
}
