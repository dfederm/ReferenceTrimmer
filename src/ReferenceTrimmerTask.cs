using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
        public string OutputAssembly { get; set; }

        public bool NeedsTransitiveAssemblyReferences { get; set; }

        public ITaskItem[] References { get; set; }

        public ITaskItem[] ProjectReferences { get; set; }

        public ITaskItem[] PackageReferences { get; set; }

        public string ProjectAssetsFile { get; set; }

        public string NuGetTargetMoniker { get; set; }

        public string RuntimeIdentifier { get; set; }

        public string NuGetRestoreTargets { get; set; }

        public override bool Execute()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            try
            {
                HashSet<string> assemblyReferences = GetAssemblyReferences();
                Dictionary<string, List<string>> packageAssembliesMap = GetPackageAssemblies();

                if (References != null)
                {
                    foreach (ITaskItem reference in References)
                    {
                        // Ignore implicity defined references (references which are SDK-provided)
                        if (reference.GetMetadata("IsImplicitlyDefined").Equals("true", StringComparison.OrdinalIgnoreCase))
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
                            Log.LogWarning($"Reference {referenceSpec} can be removed");
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
                            Log.LogWarning($"ProjectReference {referenceProjectFile} can be removed");
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
                            Log.LogWarning($"PackageReference {packageReference} can be removed");
                        }
                    }
                }

                return !Log.HasLoggedErrors;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
            }
        }

        private HashSet<string> GetAssemblyReferences()
        {
            var assemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = File.OpenRead(OutputAssembly))
            using (var peReader = new PEReader(stream))
            {
                var metadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
                if (!metadata.IsAssembly)
                {
                    Log.LogError($"{OutputAssembly} is not an assembly");
                    return null;
                }

                foreach (var assemblyReferenceHandle in metadata.AssemblyReferences)
                {
                    AssemblyReference reference = metadata.GetAssemblyReference(assemblyReferenceHandle);
                    string name = metadata.GetString(reference.Name);
                    if (!string.IsNullOrEmpty(name))
                    {
                        assemblyReferences.Add(name);
                    }
                }
            }

            return assemblyReferences;
        }

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

        private Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName assemblyName = new(args.Name);

            if (NugetAssemblies.Contains(assemblyName.Name))
            {
                string nugetProjectModelFile = Path.Combine(Path.GetDirectoryName(NuGetRestoreTargets), assemblyName.Name + ".dll");
                if (File.Exists(nugetProjectModelFile))
                {
                    return Assembly.LoadFrom(nugetProjectModelFile);
                }
            }

            return null;
        }
    }
}
