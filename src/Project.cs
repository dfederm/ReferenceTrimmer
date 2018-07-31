// <copyright file="Project.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using Buildalyzer;
    using Buildalyzer.Environment;
    using Microsoft.Extensions.Logging;
    using NuGet.Common;
    using NuGet.Frameworks;
    using NuGet.ProjectModel;
    using ILogger = Microsoft.Extensions.Logging.ILogger;
    using MsBuildProject = Microsoft.Build.Evaluation.Project;

    internal sealed class Project
    {
        private static readonly Dictionary<string, Project> Projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        private Project()
        {
        }

        public string Name { get; private set; }

        public string AssemblyName { get; private set; }

        public HashSet<string> AssemblyReferences { get; private set; }

        public List<string> References { get; private set; }

        public List<ProjectReference> ProjectReferences { get; private set; }

        public List<string> PackageReferences { get; private set; }

        public Dictionary<string, List<string>> PackageAssemblies { get; private set; }

        public static Project GetProject(
            AnalyzerManager manager,
            BuildEnvironment buildEnvironment,
            ILogger logger,
            string projectFile,
            bool useBinlog)
        {
            if (!Projects.TryGetValue(projectFile, out var project))
            {
                project = Create(manager, buildEnvironment, logger, projectFile, useBinlog);
                Projects.Add(projectFile, project);
            }

            return project;
        }

        private static Project Create(
            AnalyzerManager analyzerManager,
            BuildEnvironment buildEnvironment,
            ILogger logger,
            string projectFile,
            bool useBinlog)
        {
            try
            {
                var projectAnalyzer = analyzerManager.GetProject(projectFile);

                if (buildEnvironment == null)
                {
                    buildEnvironment = projectAnalyzer.EnvironmentFactory.GetBuildEnvironment();
                }

                var msBuildProject = projectAnalyzer.Load(buildEnvironment);

                var assemblyFile = msBuildProject.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
                if (string.IsNullOrEmpty(assemblyFile))
                {
                    // Not all projects may produce an assembly
                    return null;
                }

                var projectDirectory = Path.GetDirectoryName(projectFile);
                var assemblyFileFullPath = Path.GetFullPath(Path.Combine(projectDirectory, assemblyFile));
                if (!File.Exists(assemblyFileFullPath))
                {
                    // Can't analyze this project since it hasn't been built
                    logger.LogError($"Assembly did not exist. Ensure you've previously built it. Assembly: {assemblyFileFullPath}");
                    return null;
                }

                // Read metadata from the assembly, such as the assembly name and its references
                string assemblyName;
                var assemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var stream = File.OpenRead(assemblyFileFullPath))
                using (var peReader = new PEReader(stream))
                {
                    var metadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
                    if (!metadata.IsAssembly)
                    {
                        logger.LogError($"{assemblyFileFullPath} is not an assembly");
                        return null;
                    }

                    assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);

                    foreach (var assemblyReferenceHandle in metadata.AssemblyReferences)
                    {
                        var reference = metadata.GetAssemblyReference(assemblyReferenceHandle);
                        var name = metadata.GetString(reference.Name);
                        if (!string.IsNullOrEmpty(name))
                        {
                            assemblyReferences.Add(name);
                        }
                    }
                }

                var references = msBuildProject
                    .GetItems("Reference")
                    .Where(reference => !reference.UnevaluatedInclude.Equals("@(_SDKImplicitReference)", StringComparison.OrdinalIgnoreCase))
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                var projectReferences = msBuildProject
                    .GetItems("ProjectReference")
                    .Select(reference => new ProjectReference(
                        GetProject(analyzerManager, buildEnvironment, logger, Path.GetFullPath(Path.Combine(projectDirectory, reference.EvaluatedInclude)), useBinlog),
                        reference.UnevaluatedInclude))
                    .Where(projectReference => projectReference.Project != null)
                    .ToList();

                var packageReferences = msBuildProject
                    .GetItems("PackageReference")
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                // Certain project types may require references simply to copy them to the output folder to satisfy transitive dependencies.
                if (NeedsTransitiveAssemblyReferences(msBuildProject))
                {
                    projectReferences.ForEach(projectReference => assemblyReferences.UnionWith(projectReference.Project.AssemblyReferences));
                }

                // Collect package assemblies
                var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (packageReferences.Count > 0)
                {
                    var projectAssetsFile = msBuildProject.GetPropertyValue("ProjectAssetsFile");
                    if (!string.IsNullOrEmpty(projectAssetsFile) && File.Exists(projectAssetsFile))
                    {
                        var lockFile = LockFileUtilities.GetLockFile(projectAssetsFile, NullLogger.Instance);

                        var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

                        var nuGetTargetMoniker = msBuildProject.GetPropertyValue("NuGetTargetMoniker");
                        var runtimeIdentifier = msBuildProject.GetPropertyValue("RuntimeIdentifier");

                        var nugetTarget = lockFile.GetTarget(NuGetFramework.Parse(nuGetTargetMoniker), runtimeIdentifier);

                        // Compute the hierarchy of packages.
                        // Keys are packages and values are packages which depend on that package.
                        var nugetDependants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var nugetLibrary in nugetTarget.Libraries)
                        {
                            var packageId = nugetLibrary.Name;
                            foreach (var dependency in nugetLibrary.Dependencies)
                            {
                                if (!nugetDependants.TryGetValue(dependency.Id, out var parents))
                                {
                                    parents = new List<string>();
                                    nugetDependants.Add(dependency.Id, parents);
                                }

                                parents.Add(packageId);
                            }
                        }

                        // Get the transitive closure of assemblies included by each package
                        foreach (var nugetLibrary in nugetTarget.Libraries)
                        {
                            var nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                                .Select(item => item.Path)
                                .Where(path => !path.EndsWith("_._", StringComparison.Ordinal)) // Ignore special packages
                                .Select(path =>
                                {
                                    var packageFolderRelativePath = Path.Combine(nugetLibrary.Name, nugetLibrary.Version.ToNormalizedString(), path);
                                    var fullPath = packageFolders
                                        .Select(packageFolder => Path.Combine(packageFolder, packageFolderRelativePath))
                                        .First(File.Exists);
                                    return System.Reflection.AssemblyName.GetAssemblyName(fullPath).Name;
                                })
                                .ToList();

                            // Walk up to add assemblies to all packages which directly or indirectly depend on this one.
                            var seenDependants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var queue = new Queue<string>();
                            queue.Enqueue(nugetLibrary.Name);
                            while (queue.Count > 0)
                            {
                                var packageId = queue.Dequeue();

                                if (!packageAssemblies.TryGetValue(packageId, out var assemblies))
                                {
                                    assemblies = new List<string>();
                                    packageAssemblies.Add(packageId, assemblies);
                                }

                                assemblies.AddRange(nugetLibraryAssemblies);

                                if (nugetDependants.TryGetValue(packageId, out var dependants))
                                {
                                    foreach (var dependant in dependants)
                                    {
                                        if (seenDependants.Add(dependant))
                                        {
                                            queue.Enqueue(dependant);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return new Project
                {
                    Name = projectFile,
                    AssemblyName = assemblyName,
                    AssemblyReferences = assemblyReferences,
                    References = references,
                    ProjectReferences = projectReferences,
                    PackageReferences = packageReferences,
                    PackageAssemblies = packageAssemblies,
                };
            }
            catch (Exception e)
            {
                logger.LogError($"Exception while trying to load: {projectFile}. Exception: {e}");
                return null;
            }
        }

        private static bool NeedsTransitiveAssemblyReferences(MsBuildProject msBuildProject)
        {
            var outputType = msBuildProject.GetPropertyValue("OutputType");
            if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
