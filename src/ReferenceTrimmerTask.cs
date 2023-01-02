using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace ReferenceTrimmer
{
    public sealed class ReferenceTrimmerTask : MSBuildTask
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

        [Required]
        public string MSBuildProjectFile { get; set; }

        public bool NeedsTransitiveAssemblyReferences { get; set; }

        public ITaskItem[] UsedReferences { get; set; }
        
        public ITaskItem[] References { get; set; }

        public ITaskItem[] ProjectReferences { get; set; }

        public ITaskItem[] PackageReferences { get; set; }

        public string ProjectAssetsFile { get; set; }

        public string NuGetTargetMoniker { get; set; }

        public string RuntimeIdentifier { get; set; }

        public string NuGetRestoreTargets { get; set; }

        public ITaskItem[] TargetFrameworkDirectories { get; set; }

        public override bool Execute()
        {
            // System.Diagnostics.Debugger.Launch();
            HashSet<string> assemblyReferences = GetAssemblyReferences();
            Dictionary<string, List<string>> packageAssembliesMap = GetPackageAssemblies();
            HashSet<string> targetFrameworkAssemblies = GetTargetFrameworkAssemblyNames();

            if (References != null)
            {
                foreach (ITaskItem reference in References)
                {
                    // Ignore implicity defined references (references which are SDK-provided)
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

                    var referenceSpec = reference.ItemSpec;
                    var referenceHintPath = reference.GetMetadata("HintPath");
                    var referenceName = reference.GetMetadata("Name");

                    string referenceAssemblyName;

                    if (!string.IsNullOrEmpty(referenceHintPath) && File.Exists(referenceHintPath))
                    {
                        // If a hint path is given and exists, use that assembly's name.
                        referenceAssemblyName = AssemblyName.GetAssemblyName(referenceHintPath).Name;
                    }
                    else if (!string.IsNullOrEmpty(referenceName) && File.Exists(referenceSpec))
                    {
                        // If a name is given and the spec is an existing file, use that assembly's name.
                        referenceAssemblyName = AssemblyName.GetAssemblyName(referenceSpec).Name;
                    }
                    else
                    {
                        // The assembly name is probably just the item spec.
                        referenceAssemblyName = referenceSpec;
                    }

                    if (!assemblyReferences.Contains(referenceAssemblyName))
                    {
                        LogWarning("Reference {0} can be removed", referenceSpec);
                    }
                }
            }

            if (ProjectReferences != null)
            {
                foreach (ITaskItem projectReference in ProjectReferences)
                {
                    AssemblyName projectReferenceAssemblyName = new(projectReference.GetMetadata("FusionName"));
                    if (!assemblyReferences.Contains(projectReferenceAssemblyName.Name))
                    {
                        string referenceProjectFile = projectReference.GetMetadata("OriginalProjectReferenceItemSpec");
                        LogWarning("ProjectReference {0} can be removed", referenceProjectFile);
                    }
                }
            }

            if (PackageReferences != null)
            {
                foreach (ITaskItem packageReference in PackageReferences)
                {
                    if (!packageAssembliesMap.TryGetValue(packageReference.ItemSpec, out var packageAssemblies))
                    {
                        // These are likely Analyzers, tools, etc.
                        continue;
                    }

                    if (!packageAssemblies.Any(packageAssembly => assemblyReferences.Contains(packageAssembly)))
                    {
                        LogWarning("PackageReference {0} can be removed", packageReference);
                    }
                }
            }

            return !Log.HasLoggedErrors;
        }

        private void LogWarning(string message, params object[] messageArgs) => Log.LogWarning(null, null, null, MSBuildProjectFile, 0, 0, 0, 0, message, messageArgs);

        private HashSet<string> GetAssemblyReferences() => new(UsedReferences.Select(usedReference => AssemblyName.GetAssemblyName(usedReference.ItemSpec).Name), StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, List<string>> GetPackageAssemblies()
        {
            var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var lockFile = LockFileUtilities.GetLockFile(ProjectAssetsFile, NullLogger.Instance);
            var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

            var nugetTarget = lockFile.GetTarget(NuGetFramework.Parse(NuGetTargetMoniker), RuntimeIdentifier);
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
                        return AssemblyName.GetAssemblyName(fullPath).Name!;
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

            return packageAssemblies;
        }

        internal HashSet<string> GetTargetFrameworkAssemblyNames()
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
                    foreach (XElement file in frameworkList.Root.Elements("File"))
                    {
                        string type = file.Attribute("Type")?.Value;
                        if (type?.Equals("Analyzer", StringComparison.OrdinalIgnoreCase) ?? false)
                        {
                            continue;
                        }

                        string assemblyName = file.Attribute("AssemblyName")?.Value;
                        if (!string.IsNullOrEmpty(assemblyName))
                        {
                            targetFrameworkAssemblyNames.Add(assemblyName);
                        }
                    }
                }
            }

            return targetFrameworkAssemblyNames;
        }
    }
}
