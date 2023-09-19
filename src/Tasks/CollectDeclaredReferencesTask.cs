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

    [Required]
    public string? OutputFile { get; set; }

    [Required]
    public string? MSBuildProjectFile { get; set; }

    public ITaskItem[]? References { get; set; }

    public ITaskItem[]? ProjectReferences { get; set; }

    public ITaskItem[]? PackageReferences { get; set; }

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
                        continue;
                    }

                    // During the _HandlePackageFileConflicts target (ResolvePackageFileConflicts task), assembly conflicts may be
                    // resolved with an assembly from the target framework instead of a package. The package may be an indirect dependency,
                    // so the resulting reference would be unavoidable.
                    if (targetFrameworkAssemblies.Contains(reference.ItemSpec))
                    {
                        continue;
                    }

                    // Ignore references from packages. Those as handled later.
                    if (reference.GetMetadata("NuGetPackageId").Length != 0)
                    {
                        continue;
                    }

                    // Ignore suppressions
                    if (reference.GetMetadata(NoWarn).Contains("RT0001"))
                    {
                        continue;
                    }

                    var referenceSpec = reference.ItemSpec;
                    var referenceHintPath = reference.GetMetadata("HintPath");

                    string? referencePath;
                    string referenceAssemblyName;

                    if (!string.IsNullOrEmpty(referenceHintPath) && File.Exists(referenceHintPath))
                    {
                        referencePath = referenceHintPath;

                        // If a hint path is given and exists, use that assembly's name.
                        referenceAssemblyName = AssemblyName.GetAssemblyName(referenceHintPath).Name;
                    }
                    else if (File.Exists(referenceSpec))
                    {
                        referencePath = referenceSpec;

                        // If the spec is an existing file, use that assembly's name.
                        referenceAssemblyName = AssemblyName.GetAssemblyName(referenceSpec).Name;
                    }
                    else
                    {
                        referencePath = null;

                        // The assembly name is probably just the item spec.
                        referenceAssemblyName = referenceSpec;
                    }

                    // If the reference is under the nuget package root, it's likely a Reference added in a package's props or targets.
                    if (NuGetPackageRoot != null && referencePath != null)
                    {
                        referencePath = Path.GetFullPath(referencePath);
                        if (referencePath.StartsWith(NuGetPackageRoot))
                        {
                            continue;
                        }
                    }

                    declaredReferences.Add(new DeclaredReference(referenceAssemblyName, DeclaredReferenceKind.Reference, referenceSpec));
                }
            }

            if (ProjectReferences != null)
            {
                foreach (ITaskItem projectReference in ProjectReferences)
                {
                    // Ignore suppressions
                    if (projectReference.GetMetadata(NoWarn).Contains("RT0002"))
                    {
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

                    string projectReferenceAssemblyName = new AssemblyName(projectReference.GetMetadata("FusionName")).Name;
                    string referenceProjectFile = projectReference.GetMetadata("OriginalProjectReferenceItemSpec");

                    declaredReferences.Add(new DeclaredReference(projectReferenceAssemblyName, DeclaredReferenceKind.ProjectReference, referenceProjectFile));
                }
            }

            if (PackageReferences != null)
            {
                Dictionary<string, PackageInfo> packageInfos = GetPackageInfos();
                foreach (ITaskItem packageReference in PackageReferences)
                {
                    // Ignore suppressions
                    if (packageReference.GetMetadata(NoWarn).Contains("RT0003"))
                    {
                        continue;
                    }

                    if (!packageInfos.TryGetValue(packageReference.ItemSpec, out PackageInfo packageInfo))
                    {
                        // These are likely Analyzers, tools, etc.
                        continue;
                    }

                    // Ignore packages with build logic as we cannot easily evaluate whether the build logic is necessary or not.
                    if (packageInfo.BuildFiles.Count > 0)
                    {
                        continue;
                    }

                    foreach (string assemblyName in packageInfo.CompileTimeAssemblies)
                    {
                        declaredReferences.Add(new DeclaredReference(assemblyName, DeclaredReferenceKind.PackageReference, packageReference.ItemSpec));
                    }
                }
            }

            if (OutputFile is not null)
            {
                new DeclaredReferences(declaredReferences).SaveToFile(OutputFile);
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

        var lockFile = LockFileUtilities.GetLockFile(ProjectAssetsFile, NullLogger.Instance);
        var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

        var nugetFramework = NuGetFramework.ParseComponents(TargetFrameworkMoniker, TargetPlatformMoniker);
        LockFileTarget? nugetTarget = lockFile.GetTarget(nugetFramework, RuntimeIdentifier);

        List<LockFileTargetLibrary> nugetLibraries;
        if (nugetTarget?.Libraries is not null)
        {
            nugetLibraries = nugetTarget.Libraries
                .Where(nugetLibrary => string.Equals(nugetLibrary.Type, "Package", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            nugetLibraries = new List<LockFileTargetLibrary>();
        }

        // Compute the hierarchy of packages.
        // Keys are packages and values are packages which depend on that package.
        var nugetDependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
        {
            var packageId = nugetLibrary.Name;
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

        // Get the transitive closure of assemblies included by each package
        foreach (LockFileTargetLibrary nugetLibrary in nugetLibraries)
        {
            string nugetLibraryRelativePath = lockFile.GetLibrary(nugetLibrary.Name, nugetLibrary.Version).Path;
            string nugetLibraryAbsolutePath = packageFolders
                .Select(packageFolder => Path.Combine(packageFolder, nugetLibraryRelativePath))
                .First(Directory.Exists);

            List<string> nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                .Select(item => item.Path)
                .Where(path => !path.EndsWith("_._", StringComparison.Ordinal)) // Ignore special packages
                .Select(path =>
                {
                    var fullPath = Path.Combine(nugetLibraryAbsolutePath, path);
                    return AssemblyName.GetAssemblyName(fullPath).Name;
                })
                .ToList();

            List<string> buildFiles = nugetLibrary.Build
                .Select(item => Path.Combine(nugetLibraryAbsolutePath, item.Path))
                .ToList();

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

    private HashSet<string> GetTargetFrameworkAssemblyNames()
    {
        HashSet<string> targetFrameworkAssemblyNames = new();

        // This follows the same logic as FrameworkListReader.
        // See: https://github.com/dotnet/sdk/blob/main/src/Tasks/Common/ConflictResolution/FrameworkListReader.cs
        if (TargetFrameworkDirectories != null)
        {
            foreach (ITaskItem targetFrameworkDirectory in TargetFrameworkDirectories)
            {
                string frameworkListPath = Path.Combine(targetFrameworkDirectory.ItemSpec, "RedistList", "FrameworkList.xml");
                if (!File.Exists(frameworkListPath))
                {
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
                return Assembly.LoadFrom(nugetProjectModelFile);
            }
        }

        return null;
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
