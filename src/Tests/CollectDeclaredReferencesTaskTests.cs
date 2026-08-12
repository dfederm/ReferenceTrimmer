using System.Collections;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using ReferenceTrimmer.Tasks;

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class CollectDeclaredReferencesTaskTests
{
    [TestMethod]
    public void ExecuteWithAllNullCollectionInputsDoesNotThrow()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tsv");
        try
        {
            var engine = new MockBuildEngine();
            var task = new CollectDeclaredReferencesTask
            {
                BuildEngine = engine,
                OutputFile = outputFile,
                References = null,
                ResolvedReferences = null,
                ProjectReferences = null,
                PackageReferences = null,
                IgnorePackageBuildFiles = null,
                TargetFrameworkDirectories = null,
            };

            bool result = task.Execute();

            Assert.IsTrue(result, "Task should succeed when all collection inputs are null. Errors: " + string.Join("; ", engine.Errors));
            Assert.AreEqual(0, engine.Errors.Count, "No errors should be logged. Errors: " + string.Join("; ", engine.Errors));
        }
        finally
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    [TestMethod]
    public void ExecuteWithNullResolvedReferencesAndUnresolvableReferenceDoesNotThrow()
    {
        // Repros the vcxproj NRE: a Reference whose path can't be found locally falls into the
        // ResolvedReferences.SingleOrDefault branch, but ResolvedReferences is null because
        // ReferencePathWithRefAssemblies is empty/uninitialized for vcxproj projects.
        string outputFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tsv");
        try
        {
            var engine = new MockBuildEngine();
            var task = new CollectDeclaredReferencesTask
            {
                BuildEngine = engine,
                OutputFile = outputFile,
                References = new ITaskItem[]
                {
                    new MockTaskItem("SomeUnresolvableReference"),
                },
                ResolvedReferences = null,
            };

            bool result = task.Execute();

            Assert.IsTrue(result, "Task should succeed even when ResolvedReferences is null. Errors: " + string.Join("; ", engine.Errors));
            Assert.AreEqual(0, engine.Errors.Count, "No errors should be logged. Errors: " + string.Join("; ", engine.Errors));
        }
        finally
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    [TestMethod]
    public void ExecuteWithPackageReferencesAndNullProjectAssetsFileDoesNotThrow()
    {
        // Guards against NRE inside GetPackageInfos when PackageReferences is non-null but
        // ProjectAssetsFile is null/missing (no NuGet restore performed for this project).
        string outputFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tsv");
        try
        {
            var engine = new MockBuildEngine();
            var task = new CollectDeclaredReferencesTask
            {
                BuildEngine = engine,
                OutputFile = outputFile,
                PackageReferences = new ITaskItem[]
                {
                    new MockTaskItem("SomePackage"),
                },
                ProjectAssetsFile = null,
            };

            bool result = task.Execute();

            Assert.IsTrue(result, "Task should succeed when ProjectAssetsFile is null. Errors: " + string.Join("; ", engine.Errors));
            Assert.AreEqual(0, engine.Errors.Count, "No errors should be logged. Errors: " + string.Join("; ", engine.Errors));
        }
        finally
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    [TestMethod]
    public void ExecuteSerializesProjectAssemblyIdentity()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tsv");
        try
        {
            const string fusionName = "LogicalAssembly, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null";
            var projectReference = new MockTaskItem(Path.Combine("ref", "PhysicalFileName.dll"));
            projectReference.SetMetadata("OriginalProjectReferenceItemSpec", @"..\Dependency\Dependency.csproj");
            projectReference.SetMetadata("FusionName", fusionName);

            var engine = new MockBuildEngine();
            var task = new CollectDeclaredReferencesTask
            {
                BuildEngine = engine,
                OutputFile = outputFile,
                ProjectReferences = [projectReference],
            };

            bool result = task.Execute();

            Assert.IsTrue(result, "Task should succeed. Errors: " + string.Join("; ", engine.Errors));
            string expected =
                Path.GetFullPath(projectReference.ItemSpec)
                + "\tProjectReference\t"
                + projectReference.GetMetadata("OriginalProjectReferenceItemSpec")
                + "\t"
                + fusionName;
            Assert.AreEqual(expected, File.ReadAllText(outputFile).TrimEnd());
        }
        finally
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    [TestMethod]
    public void ProjectAssemblyIdentityParticipatesInIncrementalHash()
    {
        string targetsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Package",
            "build",
            "ReferenceTrimmer.targets"));
        XDocument targets = XDocument.Load(targetsPath);

        string? projectHashInput = targets
            .Descendants("_CollectDeclaredReferencesHashInputs")
            .Select(element => (string?)element.Attribute("Include"))
            .SingleOrDefault(include => include?.Contains("_ReferenceTrimmerProjectReferences", StringComparison.Ordinal) == true);

        Assert.IsNotNull(projectHashInput);
        StringAssert.Contains(projectHashInput, "%(FusionName)");
    }

    private sealed class MockBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Messages { get; } = new();

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e.Message ?? string.Empty);
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e.Message ?? string.Empty);
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
    }

    private sealed class MockTaskItem : ITaskItem
    {
        private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);

        public MockTaskItem(string itemSpec)
        {
            ItemSpec = itemSpec;
        }

        public string ItemSpec { get; set; }

        public ICollection MetadataNames => _metadata.Keys;

        public int MetadataCount => _metadata.Count;

        public IDictionary CloneCustomMetadata() => new Dictionary<string, string>(_metadata);

        public void CopyMetadataTo(ITaskItem destinationItem)
        {
            foreach (var kvp in _metadata)
            {
                destinationItem.SetMetadata(kvp.Key, kvp.Value);
            }
        }

        public string GetMetadata(string metadataName) => _metadata.TryGetValue(metadataName, out var value) ? value : string.Empty;

        public void RemoveMetadata(string metadataName) => _metadata.Remove(metadataName);

        public void SetMetadata(string metadataName, string metadataValue) => _metadata[metadataName] = metadataValue;
    }
}
