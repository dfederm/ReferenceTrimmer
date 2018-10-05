// <copyright file="ReferenceTrimmer.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System.IO;
    using System.Linq;
    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Execution;
    using Microsoft.Build.Logging;
    using Microsoft.Extensions.Logging;
    using ILogger = Microsoft.Extensions.Logging.ILogger;

    internal static class ReferenceTrimmer
    {
        public static void Run(Arguments arguments, ILogger logger)
        {
            // Normalize the provided root param
            arguments.Path = arguments.Path == null
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(arguments.Path);

            var buildManager = BuildManager.DefaultBuildManager;
            var parameters = new BuildParameters(ProjectCollection.GlobalProjectCollection);

            if (arguments.UseBinaryLogger)
            {
                const string BinaryLoggerFilename = "msbuild.binlog";
                logger.LogInformation($"Binary logging enabled and will be written to {BinaryLoggerFilename}");
                parameters.Loggers = new[]
                {
                    new BinaryLogger { Parameters = BinaryLoggerFilename },
                };
            }

            buildManager.BeginBuild(parameters);

            var projectFiles = Directory.EnumerateFiles(arguments.Path, "*.*proj", SearchOption.AllDirectories);
            foreach (var projectFile in projectFiles)
            {
                var project = ParsedProject.Create(projectFile, arguments, buildManager, logger);
                if (project == null)
                {
                    continue;
                }

                var relativeProjectFile = projectFile.Substring(arguments.Path.Length + 1);

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
}
