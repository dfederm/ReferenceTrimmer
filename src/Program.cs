using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CommandLine;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace ReferenceReducer
{
    using System;

    public static class Program
    {
        public static void Main(string[] args)
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
            }

            var msbuildToolsPath = options.MsBuildToolsPath ?? FindOnPath("msbuild.exe");
            if (string.IsNullOrEmpty(msbuildToolsPath))
            {
                Console.WriteLine($"Could not find MsBuild. Ensure it is on the PATH or provide the {nameof(options.MsBuildToolsPath)} option.");
                return;
            }

            /* A couple bugs make this approach impossible:
             * https://github.com/Microsoft/msbuild/issues/3001
             * https://github.com/Microsoft/msbuild/issues/3002
             * 
             * Try using an assembly resolver to resolve directly to the msbuild dlls in the msbuildToolsPath
             */

            // TODO: May want to base off of: https://daveaglick.com/posts/running-a-design-time-build-with-msbuild-apis
            var projectCollection = ProjectCollection.GlobalProjectCollection;
            var msbuildToolsVersion = projectCollection.DefaultToolsVersion;
            projectCollection.RemoveAllToolsets();
            projectCollection.AddToolset(new Toolset(msbuildToolsVersion, msbuildToolsPath, projectCollection, msbuildToolsPath));
            projectCollection.DefaultToolsVersion = msbuildToolsVersion;

            var logger = new FileLogger();
            logger.Parameters = "LOGFILE=foo.log";
            logger.Verbosity = LoggerVerbosity.Diagnostic;
            projectCollection.RegisterLogger(logger);

            // Required for design-time builds.
            projectCollection.SetGlobalProperty("DesignTimeBuild", "true");
            projectCollection.SetGlobalProperty("BuildProjectReferences", "false");
            projectCollection.SetGlobalProperty("SkipCompilerExecution", "true");
            projectCollection.SetGlobalProperty("ProvideCommandLineArgs", "true");

            var root = options.Root ?? Directory.GetCurrentDirectory();
            var projectFiles = Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories);
            var results = new ConcurrentDictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

            var projectMap = new Dictionary<string, Project>();
            foreach (var projectFile in projectFiles)
            {
                var project = projectCollection.LoadProject(projectFile);
                projectMap.Add(projectFile, project);
            }

            //Parallel.ForEach(projectFiles, projectFile =>
            foreach (var pair in projectMap)
            {
                var projectFile = pair.Key;
                var project = pair.Value;
                //var project = projectCollection.LoadProject(projectFile);

                var assemblyFile = project.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
                if (assemblyFile == null)
                {
                    // Not all projects may produce an assembly
                    continue;
                    //return;
                }

                var assemblyFileFullPath = Path.Combine(Path.GetDirectoryName(projectFile), assemblyFile);
                var assemblyReferences = GetAssemblyReferences(assemblyFileFullPath);

                var projectInstance = project.CreateProjectInstance();
                if (!projectInstance.Build("CompileDesignTime", projectCollection.Loggers))
                {
                    Console.WriteLine($"Failed to get references for project {Path.GetFileName(projectFile)}");
                    //continue;
                    return;
                }

                var projectReferences = new HashSet<string>(projectInstance.GetItems("ReferencePathWithRefAssemblies")
                    .Select(reference => reference.EvaluatedInclude), StringComparer.OrdinalIgnoreCase);

                var removeableReferences = projectReferences.Except(assemblyReferences).ToList();
                if (removeableReferences.Count > 0)
                {
                    results.TryAdd(projectFile, removeableReferences);
                }

                // Debug output
                foreach (string assemblyReference in assemblyReferences)
                {
                    Console.WriteLine($"Assembly {Path.GetFileName(assemblyFileFullPath)} referenced {assemblyReference}");
                }

                foreach (string projectReference in projectReferences)
                {
                    Console.WriteLine($"Project {Path.GetFileName(projectFile)} referenced {projectReference}");
                }
            }//);

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
                return new HashSet<string>(assembly
                    .GetReferencedAssemblies()
                    .Select(name => name.Name), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                Console.WriteLine(assemblyFile);
                Console.WriteLine(e);
                Console.WriteLine();
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string FindOnPath(string file)
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Where(Directory.Exists)
                .Select(i => Path.Combine(i, file))
                .Where(File.Exists)
                .FirstOrDefault();
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
