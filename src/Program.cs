// <copyright file="Program.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceReducer
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Buildalyzer;
    using CommandLine;
    using Microsoft.Build.Evaluation;

    internal static class Program
    {
        private static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run)
                .WithNotParsed(WriteErrors);
        }

        private static void Run(Options options)
        {
            if (options.Debug)
            {
                Console.WriteLine("Waiting for a debugger to attach");
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1000);
                }

                Debugger.Break();
            }

            // MsBuild will end up using the current working directory at time, so set it to the root.
            if (!string.IsNullOrEmpty(options.Root))
            {
                Directory.SetCurrentDirectory(options.Root);
            }

            var projectFiles = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.*proj", SearchOption.AllDirectories);
            var manager = new AnalyzerManager();
            var results = new ConcurrentDictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

            var projects = new Dictionary<string, Project>();
            foreach (var projectFile in projectFiles)
            {
                var analyzer = manager.GetProject(projectFile);

                if (options.MsBuildBinlog)
                {
                    // Put a binlog in the directory of each project
                    analyzer.WithBinaryLog();
                }

                var project = analyzer.Project;

                var assemblyFile = project.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
                if (assemblyFile == null)
                {
                    // Not all projects may produce an assembly
                    continue;
                }

                var assemblyFileFullPath = Path.Combine(Path.GetDirectoryName(projectFile), assemblyFile);
                var assemblyReferences = GetAssemblyReferences(assemblyFileFullPath);

                var projectReferences = new HashSet<string>(analyzer.GetReferences(), StringComparer.OrdinalIgnoreCase);

                var removeableReferences = projectReferences.Except(assemblyReferences).ToList();
                if (removeableReferences.Count > 0)
                {
                    results.TryAdd(projectFile, removeableReferences);
                }

                // Debug output
                foreach (var assemblyReference in assemblyReferences)
                {
                    Console.WriteLine($"Assembly {Path.GetFileName(assemblyFileFullPath)} referenced {assemblyReference}");
                }

                foreach (var projectReference in projectReferences)
                {
                    Console.WriteLine($"Project {Path.GetFileName(projectFile)} referenced {projectReference}");
                }
            }

            Console.WriteLine();

            foreach (var result in results)
            {
                Console.WriteLine("===" + result.Key + "===");
                foreach (var unnessesaryReference in result.Value)
                {
                    Console.WriteLine(unnessesaryReference);
                }

                Console.WriteLine();
            }
        }

        private static HashSet<string> GetAssemblyReferences(string assemblyFile)
        {
            if (!File.Exists(assemblyFile))
            {
                Console.WriteLine($"Assembly did not exist. Ensure you've previously built it. Assembly: {assemblyFile}");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var assembly = Assembly.LoadFile(assemblyFile);
                return new HashSet<string>(
                    assembly
                        .GetReferencedAssemblies()
                        .Select(name => name.Name),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                Console.WriteLine(assemblyFile);
                Console.WriteLine(e);
                Console.WriteLine();
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void WriteErrors(IEnumerable<Error> errors)
        {
            foreach (var error in errors)
            {
                Console.WriteLine(error.ToString());
            }
        }
    }
}
