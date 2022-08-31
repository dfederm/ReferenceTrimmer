using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ReferenceTrimmer;

internal record ProjectReference(ParsedProject Project, string UnevaluatedInclude);

internal record class ParsedProject(
    string Name,
    string AssemblyName,
    HashSet<string> AssemblyReferences,
    List<string> References,
    List<ProjectReference> ProjectReferences,
    List<string> PackageReferences,
    Dictionary<string, List<string>> PackageAssemblies)
{
    private static readonly Dictionary<string, ParsedProject?> ParsedProjectCache = new Dictionary<string, ParsedProject?>(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RestoreTargets = { "Restore" };
    private static readonly string[] CompileTargets = { "Compile" };

    public static ParsedProject? Create(
        string projectFile,
        Arguments arguments,
        BuildManager buildManager,
        ILogger logger)
    {
        if (!ParsedProjectCache.TryGetValue(projectFile, out ParsedProject? project))
        {
            project = CreateInternal(projectFile, arguments, buildManager, logger);
            ParsedProjectCache.Add(projectFile, project);
        }

        return project;
    }

    private static ParsedProject? CreateInternal(
        string projectFile,
        Arguments arguments,
        BuildManager buildManager,
        ILogger logger)
    {
        var relativeProjectFile = Path.GetRelativePath(arguments.Path.FullName, projectFile);
        try
        {
            var project = new Project(projectFile);

            var assemblyFile = project.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
            if (string.IsNullOrEmpty(assemblyFile))
            {
                // Not all projects may produce an assembly. Just avoid these sorts of projects.
                return null;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile);
            if (projectDirectory == null)
            {
                return null;
            }

            var assemblyFileFullPath = Path.GetFullPath(Path.Combine(projectDirectory, assemblyFile));
            var assemblyFileRelativePath = Path.GetRelativePath(arguments.Path.FullName, assemblyFileFullPath);

            // Compile the assembly if needed
            if (!File.Exists(assemblyFileFullPath))
            {
                if (arguments.CompileIfNeeded)
                {
                    logger.LogDebug($"Assembly {assemblyFileRelativePath} does not exist. Compiling {relativeProjectFile}...");
                    var projectInstance = project.CreateProjectInstance();

                    // Compile usually requires a restore as well
                    if (arguments.RestoreIfNeeded)
                    {
                        var restoreResult = ExecuteRestore(projectInstance, buildManager);
                        if (restoreResult.OverallResult != BuildResultCode.Success)
                        {
                            logger.LogError($"Project failed to restore: {relativeProjectFile}");
                            return null;
                        }
                    }

                    var compileResult = ExecuteCompile(projectInstance, buildManager);
                    if (compileResult.OverallResult != BuildResultCode.Success)
                    {
                        logger.LogError($"Project failed to compile: {relativeProjectFile}");
                        return null;
                    }
                }
                else
                {
                    // Can't analyze this project since it hasn't been built
                    logger.LogError($"Assembly {assemblyFileRelativePath} did not exist. Ensure you've previously built it, or set the --CompileIfNeeded flag. Project: {relativeProjectFile}");
                    return null;
                }
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
                    logger.LogError($"{assemblyFileRelativePath} is not an assembly");
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

            ICollection<ProjectItem> referenceItems = project.GetItems("Reference");
            List<string> references = new(referenceItems.Count);
            foreach (ProjectItem referenceItem in referenceItems)
            {
                if (!referenceItem.UnevaluatedInclude.Equals("@(_SDKImplicitReference)", StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(referenceItem.EvaluatedInclude);
                }
            }

            ICollection<ProjectItem> projectReferenceItems = project.GetItems("ProjectReference");
            List<ProjectReference> projectReferences = new(projectReferenceItems.Count);
            foreach (ProjectItem projectReferenceItem in projectReferenceItems)
            {
                ParsedProject? parsedProject = Create(Path.GetFullPath(Path.Combine(projectDirectory, projectReferenceItem.EvaluatedInclude)), arguments, buildManager, logger);
                if (parsedProject != null)
                {
                    projectReferences.Add(new ProjectReference(parsedProject, projectReferenceItem.UnevaluatedInclude));
                }
            }

            ICollection<ProjectItem> packageReferenceItems = project.GetItems("PackageReference");
            List<string> packageReferences = new(packageReferenceItems.Count);
            foreach (ProjectItem packageReferenceItem in packageReferenceItems)
            {
                packageReferences.Add(packageReferenceItem.EvaluatedInclude);
            }

            // Certain project types may require references simply to copy them to the output folder to satisfy transitive dependencies.
            if (NeedsTransitiveAssemblyReferences(project))
            {
                projectReferences.ForEach(projectReference => assemblyReferences.UnionWith(projectReference.Project.AssemblyReferences));
            }

            // Collect package assemblies
            var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (packageReferences.Count > 0)
            {
                var projectAssetsFile = project.GetPropertyValue("ProjectAssetsFile");
                if (string.IsNullOrEmpty(projectAssetsFile))
                {
                    logger.LogError($"Project with PackageReferences missing ProjectAssetsFile property: {relativeProjectFile}");
                    return null;
                }

                // TODO: Combine with the restore above.
                var projectAssetsFileFullPath = Path.GetFullPath(Path.Combine(projectDirectory, projectAssetsFile));
                var projectAssetsFileRelativePath = Path.GetRelativePath(arguments.Path.FullName, projectAssetsFileFullPath);
                if (!File.Exists(projectAssetsFileFullPath))
                {
                    if (arguments.RestoreIfNeeded)
                    {
                        logger.LogDebug($"ProjectAssetsFile {projectAssetsFileRelativePath} did not exist. Restoring {relativeProjectFile}...");
                        var projectInstance = project.CreateProjectInstance();

                        var restoreResult = ExecuteRestore(projectInstance, buildManager);
                        if (restoreResult.OverallResult != BuildResultCode.Success)
                        {
                            logger.LogError($"Project failed to restore: {relativeProjectFile}");
                            return null;
                        }
                    }
                    else
                    {
                        // Can't analyze this project since it hasn't been restored
                        logger.LogError($"ProjectAssetsFile {projectAssetsFileRelativePath} did not exist. Ensure you've previously built it, or set the --RestoreIfNeeded flag. Project: {relativeProjectFile}");
                        return null;
                    }
                }

                var lockFile = LockFileUtilities.GetLockFile(projectAssetsFileFullPath, NullLogger.Instance);
                if (lockFile == null)
                {
                    logger.LogError($"{projectAssetsFileRelativePath} is not a valid assets file");
                    return null;
                }

                var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

                var nuGetTargetMoniker = project.GetPropertyValue("NuGetTargetMoniker");
                var runtimeIdentifier = project.GetPropertyValue("RuntimeIdentifier");

                var nugetTarget = lockFile.GetTarget(NuGetFramework.Parse(nuGetTargetMoniker), runtimeIdentifier);
                var nugetLibraries = nugetTarget.Libraries
                    .Where(nugetLibrary => nugetLibrary.Type.Equals("Package", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Compute the hierarchy of packages.
                // Keys are packages and values are packages which depend on that package.
                var nugetDependants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var nugetLibrary in nugetLibraries)
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
                foreach (var nugetLibrary in nugetLibraries)
                {
                    List<string> nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                        .Select(item => item.Path)
                        .Where(path => !path.EndsWith("_._", StringComparison.Ordinal)) // Ignore special packages
                        .Select(path =>
                        {
                            var packageFolderRelativePath = Path.Combine(nugetLibrary.Name, nugetLibrary.Version.ToNormalizedString(), path);
                            var fullPath = packageFolders
                                .Select(packageFolder => Path.Combine(packageFolder, packageFolderRelativePath))
                                .First(File.Exists);
                            return System.Reflection.AssemblyName.GetAssemblyName(fullPath).Name!;
                        })
                        .ToList();

                    // Walk up to add assemblies to all packages which directly or indirectly depend on this one.
                    var seenDependants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var queue = new Queue<string>();
                    queue.Enqueue(nugetLibrary.Name);
                    while (queue.Count > 0)
                    {
                        var packageId = queue.Dequeue();

                        // Add this package's assemblies, if there are any
                        if (nugetLibraryAssemblies.Count > 0)
                        {
                            if (!packageAssemblies.TryGetValue(packageId, out var assemblies))
                            {
                                assemblies = new List<string>();
                                packageAssemblies.Add(packageId, assemblies);
                            }

                            assemblies.AddRange(nugetLibraryAssemblies);
                        }

                        // Recurse though dependants
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

            return new ParsedProject(projectFile, assemblyName, assemblyReferences, references, projectReferences, packageReferences, packageAssemblies);
        }
        catch (Exception e)
        {
            logger.LogError($"Exception while trying to load: {relativeProjectFile}. Exception: {e}");
            return null;
        }
    }

    private static bool NeedsTransitiveAssemblyReferences(Project projectInstance)
    {
        var outputType = projectInstance.GetPropertyValue("OutputType");
        if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // Based on MSBuild.exe's restore logic when using /restore. https://github.com/Microsoft/msbuild/blob/master/src/MSBuild/XMake.cs#L1242
    private static BuildResult ExecuteRestore(ProjectInstance projectInstance, BuildManager buildManager)
    {
        const string UniqueProperty = "MSBuildRestoreSessionId";

        // Set a property with a random value to ensure that restore happens under a different evaluation context
        // If the evaluation context is not different, then projects won't be re-evaluated after restore
        projectInstance.SetProperty(UniqueProperty, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

        // Create a new request with a Restore target only and specify:
        //  - BuildRequestDataFlags.ClearCachesAfterBuild to ensure the projects will be reloaded from disk for subsequent builds
        //  - BuildRequestDataFlags.SkipNonexistentTargets to ignore missing targets since Restore does not require that all targets exist
        //  - BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports to ignore imports that don't exist, are empty, or are invalid because restore might
        //     make available an import that doesn't exist yet and the <Import /> might be missing a condition.
        var request = new BuildRequestData(
            projectInstance,
            targetsToBuild: RestoreTargets,
            hostServices: null,
            flags: BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports);

        var result = ExecuteBuild(buildManager, request);

        // Revert the property
        projectInstance.RemoveProperty(UniqueProperty);

        return result;
    }

    private static BuildResult ExecuteCompile(ProjectInstance projectInstance, BuildManager buildManager) => ExecuteBuild(buildManager, new BuildRequestData(projectInstance, CompileTargets));

    private static BuildResult ExecuteBuild(BuildManager buildManager, BuildRequestData request) => buildManager.PendBuildRequest(request).Execute();
}
