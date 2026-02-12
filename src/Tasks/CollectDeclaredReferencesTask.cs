using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using ReferenceTrimmer.Shared;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace ReferenceTrimmer.Tasks;

public sealed class CollectDeclaredReferencesTask : MSBuildTask
{
    private static readonly HashSet<string> NugetAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        // Direct dependency
        "NuGet.ProjectModel",

        // Indirect dependencies
        "NuGet.Common",
        "NuGet.Frameworks",
        "NuGet.Packaging",
        "NuGet.Versioning",
    };

    private const string NoWarn = "NoWarn";
    private const string TreatAsUsed = "TreatAsUsed";

    [Required]
    public string? OutputFile { get; set; }

    [Required]
    public string? MSBuildProjectFile { get; set; }

    public ITaskItem[]? References { get; set; }

    public ITaskItem[]? ResolvedReferences { get; set; }

    public ITaskItem[]? ProjectReferences { get; set; }

    public ITaskItem[]? PackageReferences { get; set; }

    public ITaskItem[]? IgnorePackageBuildFiles { get; set; }

    public string? ProjectAssetsFile { get; set; }

    public string? TargetFrameworkMoniker { get; set; }

    public string? TargetPlatformMoniker { get; set; }

    public string? RuntimeIdentifier { get; set; }

    public string? NuGetRestoreTargets { get; set; }

    public ITaskItem[]? TargetFrameworkDirectories { get; set; }

    public string? NuGetPackageRoot { get; set; }

    public override bool Execute()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try
        {
            List<DeclaredReference> declaredReferences = new();

            if (References != null)
            {
                HashSet<string> targetFrameworkAssemblies = GetTargetFrameworkAssemblyNames();
                foreach (ITaskItem reference in References)
                {
                    // Ignore implicitly defined references (references which are SDK-provided)
                    if (reference.GetMetadata("IsImplicitlyDefined").Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping Reference '{0}' because it is implicitly defined (SDK-provided)", reference.ItemSpec);
                        continue;
                    }

                    // During the _HandlePackageFileConflicts target (ResolvePackageFileConflicts task), assembly conflicts may be
                    // resolved with an assembly from the target framework instead of a package. The package may be an indirect dependency,
                    // so the resulting reference would be unavoidable.
                    if (targetFrameworkAssemblies.Contains(reference.ItemSpec))
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping Reference '{0}' because it is a target framework assembly", reference.ItemSpec);
                        continue;
                    }

                    // Ignore references from packages. Those as handled later.
                    if (reference.GetMetadata("NuGetPackageId").Length != 0)
                    {
                        // Logs will be emitted for these references when processing the PackageReferences
                        continue;
                    }

                    // Ignore suppressions
                    if (IsSuppressed(reference, "RT0001"))
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping Reference '{0}' because it is suppressed via NoWarn=\"RT0001\" or <TreatAsUsed>", reference.ItemSpec);
                        continue;
                    }

                    var referenceSpec = reference.ItemSpec;
                    var referenceHintPath = reference.GetMetadata("HintPath");

                    string? referencePath;
                    if (!string.IsNullOrEmpty(referenceHintPath) && File.Exists(referenceHintPath))
                    {
                        referencePath = Path.GetFullPath(referenceHintPath);
                    }
                    else if (File.Exists(referenceSpec))
                    {
                        referencePath = Path.GetFullPath(referenceSpec);
                    }
                    else
                    {
                        var resolvedReference = ResolvedReferences.SingleOrDefault(rr => string.Equals(rr.GetMetadata("OriginalItemSpec"), referenceSpec, StringComparison.OrdinalIgnoreCase));
                        referencePath = resolvedReference is null ? null : resolvedReference.ItemSpec;
                    }

                    // If the reference is under the nuget package root, it's likely a Reference added in a package's props or targets.
                    if (NuGetPackageRoot != null && referencePath != null)
                    {
                        if (referencePath.StartsWith(NuGetPackageRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.LogMessage(MessageImportance.Low, "Skipping Reference '{0}' because its resolved path '{1}' is under the NuGet package root (likely added by a package's props/targets)", referenceSpec, referencePath);
                            continue;
                        }
                    }

                    if (referencePath is not null)
                    {
                        declaredReferences.Add(new DeclaredReference(referencePath, DeclaredReferenceKind.Reference, referenceSpec));
                    }
                }
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "No References to process");
            }

            if (ProjectReferences != null)
            {
                foreach (ITaskItem projectReference in ProjectReferences)
                {
                    // Ignore suppressions
                    if (IsSuppressed(projectReference, "RT0002"))
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping ProjectReference '{0}' because it is suppressed via NoWarn=\"RT0002\" or <TreatAsUsed>", projectReference.ItemSpec);
                        continue;
                    }

                    // Weirdly, NuGet restore is actually how transitive project references are determined and they're
                    // added to to project.assets.json and collected via the IncludeTransitiveProjectReferences target.
                    // This also adds the NuGetPackageId metadata, so use that as a signal that it's transitive.
                    bool isTransitiveDependency = !string.IsNullOrEmpty(projectReference.GetMetadata("NuGetPackageId"));
                    if (isTransitiveDependency)
                    {
                        // Ignore transitive project references since the project doesn't have direct control over them.
                        continue;
                    }

                    string projectReferenceAssemblyPath = Path.GetFullPath(projectReference.ItemSpec);
                    string referenceProjectFile = projectReference.GetMetadata("OriginalProjectReferenceItemSpec");

                    declaredReferences.Add(new DeclaredReference(projectReferenceAssemblyPath, DeclaredReferenceKind.ProjectReference, referenceProjectFile));
                }
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "No ProjectReferences to process");
            }

            if (PackageReferences != null)
            {
                Dictionary<string, PackageInfo> packageInfos = GetPackageInfos();
                foreach (ITaskItem packageReference in PackageReferences)
                {
                    // Ignore suppressions
                    if (IsSuppressed(packageReference, "RT0003"))
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping PackageReference '{0}' because it is suppressed via NoWarn=\"RT0003\" or <TreatAsUsed>", packageReference.ItemSpec);
                        continue;
                    }

                    if (!packageInfos.TryGetValue(packageReference.ItemSpec, out PackageInfo packageInfo))
                    {
                        // These are likely Analyzers, tools, etc.
                        Log.LogMessage(MessageImportance.Low, "Skipping PackageReference '{0}' because it has no compile-time assemblies (likely an Analyzer, tool, or content-only package)", packageReference.ItemSpec);
                        continue;
                    }

                    if (packageInfo.BuildFiles.Count > 0)
                    {
                        Log.LogMessage(MessageImportance.Low, "Skipping PackageReference '{0}' because it has build {1} file(s):", packageReference.ItemSpec, packageInfo.BuildFiles.Count);
                        foreach (string buildFile in packageInfo.BuildFiles)
                        {
                            Log.LogMessage(MessageImportance.Low, "  Build file: '{0}'", buildFile);
                        }
                        continue;
                    }

                    foreach (string assemblyPath in packageInfo.CompileTimeAssemblies)
                    {
                        declaredReferences.Add(new DeclaredReference(assemblyPath, DeclaredReferenceKind.PackageReference, packageReference.ItemSpec));
                    }
                }
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "No PackageReferences to process");
            }

            if (OutputFile is not null)
            {
                new DeclaredReferences(declaredReferences).SaveToFile(OutputFile);
                Log.LogMessage(MessageImportance.Low, "Saved {0} declared references to '{1}'", declaredReferences.Count, OutputFile);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }

        return !Log.HasLoggedErrors;
    }

    private Dictionary<string, PackageInfo> GetPackageInfos()
    {
        var packageInfoBuilders = new Dictionary<string, PackageInfoBuilder>(StringComparer.OrdinalIgnoreCase);

        Log.LogMessage(MessageImportance.Low, "Loading lock file from '{0}'", ProjectAssetsFile);
        var lockFile = LockFileUtilities.GetLockFile(ProjectAssetsFile, NullLogger.Instance);
        var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();
        Log.LogMessage(MessageImportance.Low, "Package folders: {0}", string.Join("; ", packageFolders));

        LockFileTarget? nugetTarget = null;
        if (!string.IsNullOrEmpty(TargetFrameworkMoniker))
        {
            var nugetFramework = NuGetFramework.ParseComponents(TargetFrameworkMoniker!, TargetPlatformMoniker);
            nugetTarget = lockFile.GetTarget(nugetFramework, RuntimeIdentifier);
            Log.LogMessage(MessageImportance.Low, "Resolved NuGet target framework: '{0}'", nugetFramework);
        }

        List<LockFileTargetLibrary> nugetLibraries;
        if (nugetTarget?.Libraries is not null)
        {
            nugetLibraries = nugetTarget.Libraries
                .Where(nugetLibrary => string.Equals(nugetLibrary.Type, "Package", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Log.LogMessage(MessageImportance.Low, "Found {0} NuGet package library(ies) in lock file target", nugetLibraries.Count);
        }
        else
        {
            nugetLibraries = new List<LockFileTargetLibrary>();
            Log.LogMessage(MessageImportance.Low, "No NuGet target libraries found in lock file");
        }

        // Compute the hierarchy of packages.
        // Keys are packages and values are packages which depend on that package.
        var nugetDependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
        {
            string? packageId = nugetLibrary.Name;
            if (packageId is null)
            {
                continue;
            }

            foreach (var dependency in nugetLibrary.Dependencies)
            {
                if (!nugetDependents.TryGetValue(dependency.Id, out var parents))
                {
                    parents = new List<string>();
                    nugetDependents.Add(dependency.Id, parents);
                }

                parents.Add(packageId);
            }
        }

        HashSet<string> packageToIgnoreBuildFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in IgnorePackageBuildFiles ?? [])
        {
            packageToIgnoreBuildFiles.Add(item.ItemSpec);
        }

        // Get the transitive closure of assemblies included by each package
        foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
        {
            if (nugetLibrary.Name is null)
            {
                continue;
            }

            string nugetLibraryRelativePath = lockFile.GetLibrary(nugetLibrary.Name, nugetLibrary.Version).Path;
            string? nugetLibraryAbsolutePath = packageFolders
                .Select(packageFolder => Path.Combine(packageFolder, nugetLibraryRelativePath))
                .FirstOrDefault(Directory.Exists);
            if (nugetLibraryAbsolutePath is null)
            {
                // This can happen if the project has a stale lock file.
                // Just ignore it as NuGet itself will likely error.
                Log.LogMessage(MessageImportance.Low, "Package '{0}' could not be found in any package folder (stale lock file?). Skipping.", nugetLibrary.Name);
                continue;
            }

            List<string> nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                .Select(item => item.Path)
                .Where(IsValidFile)
                .Select(path =>
                {
                    var fullPath = Path.Combine(nugetLibraryAbsolutePath, path);
                    return Path.GetFullPath(fullPath);
                })
                .ToList();

            List<string> buildFiles = packageToIgnoreBuildFiles.Contains(nugetLibrary.Name)
                ? []
                : nugetLibrary.Build
                    .Select(item => item.Path)
                    .Where(IsValidFile)
                    .Select(path => Path.Combine(nugetLibraryAbsolutePath, path))
                    .ToList();

            if (packageToIgnoreBuildFiles.Contains(nugetLibrary.Name) && nugetLibrary.Build.Count > 0)
            {
                Log.LogMessage(MessageImportance.Low, "Package '{0}' has {1} build file(s) but they are ignored via IgnorePackageBuildFiles", nugetLibrary.Name, nugetLibrary.Build.Count);
            }

            Log.LogMessage(MessageImportance.Low, "Package '{0}' v{1}: {2} compile-time assembly(ies), {3} build file(s)",
                nugetLibrary.Name, nugetLibrary.Version, nugetLibraryAssemblies.Count, buildFiles.Count);

            // Add this package's assets, if there are any
            if (nugetLibraryAssemblies.Count > 0 || buildFiles.Count > 0)
            {
                // Walk up to add assets to all packages which directly or indirectly depend on this one.
                var seenDependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();
                queue.Enqueue(nugetLibrary.Name);
                while (queue.Count > 0)
                {
                    var packageId = queue.Dequeue();

                    if (!packageInfoBuilders.TryGetValue(packageId, out PackageInfoBuilder packageInfoBuilder))
                    {
                        packageInfoBuilder = new PackageInfoBuilder();
                        packageInfoBuilders.Add(packageId, packageInfoBuilder);
                    }

                    packageInfoBuilder.AddCompileTimeAssemblies(nugetLibraryAssemblies);
                    packageInfoBuilder.AddBuildFiles(buildFiles);

                    // Recurse though dependents
                    if (nugetDependents.TryGetValue(packageId, out var dependents))
                    {
                        foreach (var dependent in dependents)
                        {
                            if (seenDependents.Add(dependent))
                            {
                                queue.Enqueue(dependent);
                            }
                        }
                    }
                }
            }
        }

        // Create the final collection
        var packageInfos = new Dictionary<string, PackageInfo>(packageInfoBuilders.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, PackageInfoBuilder> packageInfoBuilder in packageInfoBuilders)
        {
            packageInfos.Add(packageInfoBuilder.Key, packageInfoBuilder.Value.ToPackageInfo());
        }

        return packageInfos;
    }

    private static bool IsValidFile(string path) => !path.EndsWith("_._", StringComparison.Ordinal);

    private HashSet<string> GetTargetFrameworkAssemblyNames()
    {
        HashSet<string> targetFrameworkAssemblyNames = new();

        // This follows the same logic as FrameworkListReader.
        // See: https://github.com/dotnet/sdk/blob/main/src/Tasks/Common/ConflictResolution/FrameworkListReader.cs
        if (TargetFrameworkDirectories != null)
        {
            Log.LogMessage(MessageImportance.Low, "Scanning {0} TargetFrameworkDirectory(ies) for framework assemblies", TargetFrameworkDirectories.Length);
            foreach (ITaskItem targetFrameworkDirectory in TargetFrameworkDirectories)
            {
                string frameworkListPath = Path.Combine(targetFrameworkDirectory.ItemSpec, "RedistList", "FrameworkList.xml");
                if (!File.Exists(frameworkListPath))
                {
                    Log.LogMessage(MessageImportance.Low, "FrameworkList.xml not found at '{0}'", frameworkListPath);
                    continue;
                }

                XDocument frameworkList = XDocument.Load(frameworkListPath);
                if (frameworkList.Root is not null)
                {
                    foreach (XElement file in frameworkList.Root.Elements("File"))
                    {
                        string? type = file.Attribute("Type")?.Value;
                        if (type?.Equals("Analyzer", StringComparison.OrdinalIgnoreCase) ?? false)
                        {
                            continue;
                        }

                        string? assemblyName = file.Attribute("AssemblyName")?.Value;
                        if (!string.IsNullOrEmpty(assemblyName))
                        {
                            targetFrameworkAssemblyNames.Add(assemblyName!);
                        }
                    }
                }
            }
        }

        return targetFrameworkAssemblyNames;
    }

    /// <summary>
    /// Assembly resolution needed for parsing the lock file, needed if the version the task depends on is a different version than MSBuild's
    /// </summary>
    private Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyName assemblyName = new(args.Name);

        if (NugetAssemblies.Contains(assemblyName.Name))
        {
            string nugetProjectModelFile = Path.Combine(Path.GetDirectoryName(NuGetRestoreTargets)!, assemblyName.Name + ".dll");
            if (File.Exists(nugetProjectModelFile))
            {
                Log.LogMessage(MessageImportance.Low, "Resolved assembly '{0}' from '{1}'", assemblyName.Name, nugetProjectModelFile);
                return Assembly.LoadFrom(nugetProjectModelFile);
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "Could not resolve assembly '{0}' - file not found at '{1}'", assemblyName.Name, nugetProjectModelFile);
            }
        }

        return null;
    }

    private static bool IsSuppressed(ITaskItem item, string warningId)
    {
        ReadOnlySpan<char> warningIdSpan = warningId.AsSpan();
        ReadOnlySpan<char> remainingNoWarn = item.GetMetadata(NoWarn).AsSpan();
        while (!remainingNoWarn.IsEmpty)
        {
            ReadOnlySpan<char> currentNoWarn;
            int idx = remainingNoWarn.IndexOf(';');
            if (idx == -1)
            {
                currentNoWarn = remainingNoWarn;
                remainingNoWarn = ReadOnlySpan<char>.Empty;
            }
            else
            {
                currentNoWarn = remainingNoWarn.Slice(0, idx);
                remainingNoWarn = remainingNoWarn.Slice(idx + 1);
            }

            if (currentNoWarn.Trim().Equals(warningIdSpan, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (item.GetMetadata(TreatAsUsed).Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private sealed class PackageInfoBuilder
    {
        private List<string>? _compileTimeAssemblies;
        private List<string>? _buildFiles;

        public void AddCompileTimeAssemblies(List<string> compileTimeAssemblies)
        {
            if (compileTimeAssemblies.Count == 0)
            {
                return;
            }

            _compileTimeAssemblies ??= new(compileTimeAssemblies.Count);
            _compileTimeAssemblies.AddRange(compileTimeAssemblies);
        }

        public void AddBuildFiles(List<string> buildFiles)
        {
            if (buildFiles.Count == 0)
            {
                return;
            }

            _buildFiles ??= new(buildFiles.Count);
            _buildFiles.AddRange(buildFiles);
        }

        public PackageInfo ToPackageInfo()
            => new(
                (IReadOnlyCollection<string>?)_compileTimeAssemblies ?? Array.Empty<string>(),
                (IReadOnlyCollection<string>?)_buildFiles ?? Array.Empty<string>());
    }

    private readonly record struct PackageInfo(
        IReadOnlyCollection<string> CompileTimeAssemblies,
        IReadOnlyCollection<string> BuildFiles);
}
