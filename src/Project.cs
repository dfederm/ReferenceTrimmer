// <copyright file="Project.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceReducer
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
        private static Dictionary<string, Project> projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        private Project()
        {
        }

        public string AssemblyName { get; private set; }

        public IReadOnlyList<string> AssemblyReferences { get; private set; }

        public IReadOnlyList<string> References { get; private set; }

        public IReadOnlyList<Project> ProjectReferences { get; private set; }

        public IReadOnlyList<string> PackageReferences { get; private set; }

        public static Project GetProject(AnalyzerManager manager, Options options, string projectFile)
        {
            if (!projects.TryGetValue(projectFile, out var project))
            {
                project = Create(manager, options, projectFile);
                projects.Add(projectFile, project);
            }

            return project;
        }

        private static Project Create(AnalyzerManager manager, Options options, string projectFile)
        {
            var analyzer = manager.GetProject(projectFile);
            var msBuildProject = analyzer.Load();

            var assemblyFile = msBuildProject.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
            if (assemblyFile == null)
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
                .ToList();

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
                projectReferences.ForEach(projectReference => assemblyReferences.AddRange(projectReference.AssemblyReferences));
            }

            return new Project
            {
                AssemblyName = assembly.GetName().Name,
                AssemblyReferences = assemblyReferences.AsReadOnly(),
                References = references.AsReadOnly(),
                ProjectReferences = projectReferences.AsReadOnly(),
                PackageReferences = packageReferences.AsReadOnly(),
            };
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
