// <copyright file="Project.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Buildalyzer;
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

        public List<Project> ProjectReferences { get; private set; }

        public List<string> PackageReferences { get; private set; }

        public Dictionary<string, List<string>> PackageAssemblies { get; private set; }

        public static Project GetProject(AnalyzerManager manager, Options options, string projectFile)
        {
            if (!Projects.TryGetValue(projectFile, out var project))
            {
                project = Create(manager, options, projectFile);
                Projects.Add(projectFile, project);
            }

            return project;
        }

        private static Project Create(AnalyzerManager manager, Options options, string projectFile)
        {
            var oldCurrentDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(Path.GetDirectoryName(projectFile));
            try
            {
                var analyzer = manager.GetProject(projectFile);
                var msBuildProject = analyzer.Load();

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
                    Console.WriteLine($"Assembly did not exist. Ensure you've previously built it. Assembly: {assemblyFileFullPath}");
                    return null;
                }

                var assembly = LoadAssembly(assemblyFileFullPath);
                if (assembly == null)
                {
                    // Can't analyze this project since we couldn't load its assembly
                    Console.WriteLine($"Assembly could not be loaded. Assembly: {assemblyFileFullPath}");
                    return null;
                }

                var assemblyReferences = assembly
                    .GetReferencedAssemblies()
                    .Select(name => name.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var references = msBuildProject
                    .GetItems("Reference")
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                var projectReferences = msBuildProject
                    .GetItems("ProjectReference")
                    .Select(reference => reference.EvaluatedInclude)
                    .Select(projectReference => GetProject(manager, options, Path.GetFullPath(Path.Combine(projectDirectory, projectReference))))
                    .Where(dependency => dependency != null)
                    .ToList();

                var packageReferences = msBuildProject
                    .GetItems("PackageReference")
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                // Certain project types may require references simply to copy them to the output folder to satisfy transitive dependencies.
                if (NeedsTransitiveAssemblyReferences(msBuildProject))
                {
                    projectReferences.ForEach(projectReference => assemblyReferences.UnionWith(projectReference.AssemblyReferences));
                }

                // Only bother doing a design-time build if there is a reason to
                var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (packageReferences.Count > 0)
                {
                    if (options.MsBuildBinlog)
                    {
                        analyzer.WithBinaryLog();
                    }

                    var msBuildCompiledProject = analyzer.Compile();

                    var packageParents = msBuildCompiledProject.GetItems("_ActiveTFMPackageDependencies")
                        .Where(package => !string.IsNullOrEmpty(package.GetMetadataValue("ParentPackage")))
                        .GroupBy(
                            package =>
                            {
                                var packageIdentity = package.EvaluatedInclude;
                                return packageIdentity.Substring(0, packageIdentity.IndexOf('/'));
                            },
                            package =>
                            {
                                var parentPackageIdentity = package.GetMetadataValue("ParentPackage");
                                return parentPackageIdentity.Substring(0, parentPackageIdentity.IndexOf('/'));
                            },
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.ToList());

                    var resolvedPackageReferences = msBuildCompiledProject.GetItems("Reference")
                        .Where(reference => reference.GetMetadataValue("NuGetSourceType").Equals("Package", StringComparison.OrdinalIgnoreCase));
                    foreach (var resolvedPackageReference in resolvedPackageReferences)
                    {
                        var assemblyName = Path.GetFileNameWithoutExtension(resolvedPackageReference.EvaluatedInclude);

                        // Add the assembly to the containing package and all parent packages.
                        var seenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var queue = new Queue<string>();
                        queue.Enqueue(resolvedPackageReference.GetMetadataValue("NuGetPackageId"));
                        while (queue.Count > 0)
                        {
                            var packageId = queue.Dequeue();

                            if (!packageAssemblies.TryGetValue(packageId, out var assemblies))
                            {
                                assemblies = new List<string>();
                                packageAssemblies.Add(packageId, assemblies);
                            }

                            assemblies.Add(assemblyName);

                            if (packageParents.TryGetValue(packageId, out var parents))
                            {
                                foreach (var parent in parents)
                                {
                                    if (seenParents.Add(parent))
                                    {
                                        queue.Enqueue(parent);
                                    }
                                }
                            }
                        }
                    }
                }

                return new Project
                {
                    Name = projectFile,
                    AssemblyName = assembly.GetName().Name,
                    AssemblyReferences = assemblyReferences,
                    References = references,
                    ProjectReferences = projectReferences,
                    PackageReferences = packageReferences,
                    PackageAssemblies = packageAssemblies,
                };
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception while trying to load: {projectFile}. Exception: {e}");
                return null;
            }
            finally
            {
                Directory.SetCurrentDirectory(oldCurrentDirectory);
            }
        }

        private static Assembly LoadAssembly(string assemblyFile)
        {
            try
            {
                return Assembly.LoadFile(assemblyFile);
            }
            catch (Exception e)
            {
                Console.WriteLine(assemblyFile);
                Console.WriteLine(e);
                Console.WriteLine();
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

            // TODO: Unit tests
            return false;
        }
    }
}
