using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ReferenceTrimmer.Analyzer;

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class AnalyzerTests
{
    [TestMethod]
    public async Task UsedViaMethodCall()
    {
        var dep = EmitDependency("namespace Dep { public static class Foo { public static void Bar() {} } }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M() { Dep.Foo.Bar(); } }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnusedReportsDiagnostic()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { }",
            dep);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
    }

    [TestMethod]
    public async Task UsedViaSwitchPattern()
    {
        var dep = EmitDependency("namespace Dep { public class SpecialType {} }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                bool Check(object o) {
                    switch (o) { case Dep.SpecialType _: return true; default: return false; }
                }
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaDefaultExpression()
    {
        var dep = EmitDependency("namespace Dep { public struct ValueHolder { public int Value; } }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { object M() => default(Dep.ValueHolder); }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaOperator()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                public struct Amount {
                    public int Value;
                    public Amount(int v) => Value = v;
                    public static Amount operator +(Amount a, Amount b) => new Amount(a.Value + b.Value);
                }
            }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                int M() {
                    var a = new Dep.Amount(1);
                    var b = new Dep.Amount(2);
                    return (a + b).Value;
                }
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaIsPattern()
    {
        var dep = EmitDependency("namespace Dep { public class Marker {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { bool Check(object o) => o is Dep.Marker; }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaCatchClause()
    {
        var dep = EmitDependency("namespace Dep { public class MyException : System.Exception {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M() { try {} catch (Dep.MyException) {} } }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaBaseType()
    {
        var dep = EmitDependency("namespace Dep { public class Base {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C : Dep.Base { }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaAttribute()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public class MyAttribute : System.Attribute {}
            }");
        var diagnostics = await RunAnalyzerAsync(
            "[Dep.My] class C { }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaTypeOf()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { System.Type M() => typeof(Dep.Foo); }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaGenericTypeArgument()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { System.Collections.Generic.List<Dep.Foo> M() => null; }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaInterface()
    {
        var dep = EmitDependency("namespace Dep { public interface IMarker {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C : Dep.IMarker { }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaTypeConstraint()
    {
        var dep = EmitDependency("namespace Dep { public class Base {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M<T>() where T : Dep.Base {} }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaFieldType()
    {
        var dep = EmitDependency("namespace Dep { public class Holder {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { Dep.Holder _field; }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaParameterType()
    {
        var dep = EmitDependency("namespace Dep { public class Input {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M(Dep.Input x) {} }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaSwitchExpression()
    {
        var dep = EmitDependency("namespace Dep { public class SpecialType {} }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                bool Check(object o) => o switch { Dep.SpecialType _ => true, _ => false };
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaConversionOperator()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                public struct Wrapper {
                    public int Value;
                    public Wrapper(int v) => Value = v;
                    public static implicit operator int(Wrapper w) => w.Value;
                }
            }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C { int M() { var w = new Dep.Wrapper(42); return w; } }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaAssemblyAttribute()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                [System.AttributeUsage(System.AttributeTargets.Assembly)]
                public class MyAsmAttribute : System.Attribute {}
            }");
        var diagnostics = await RunAnalyzerAsync(
            "[assembly: Dep.MyAsm] class C { }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task MultipleDepsOnlyUnusedReported()
    {
        var used = EmitDependency(
            "namespace Used { public class Foo {} }",
            assemblyName: "UsedDep");
        var unused = EmitDependency(
            "namespace Unused { public class Bar {} }",
            assemblyName: "UnusedDep");
        var diagnostics = await RunAnalyzerAsync(
            "class C : Used.Foo { }",
            [(used.Reference, used.Path, "ProjectReference", "../Used/Used.csproj"),
             (unused.Reference, unused.Path, "ProjectReference", "../Unused/Unused.csproj")]);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
        Assert.IsTrue(diagnostics[0].GetMessage(CultureInfo.InvariantCulture).Contains("Unused"));
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Test infrastructure
    // ──────────────────────────────────────────────────────────────────────

    private static void AssertNoDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.Length > 0)
        {
            Assert.Fail($"Expected no diagnostics but got:\n{string.Join("\n", diagnostics.Select(d => $"  {d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}"))}");
        }
    }

    private static readonly MetadataReference CorlibRef =
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    /// <summary>
    /// Compile dependency source into a DLL on disk and return the metadata reference + path.
    /// </summary>
    private static (MetadataReference Reference, string Path) EmitDependency(string source, string assemblyName = "Dependency")
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            [CorlibRef],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string path = Path.Combine(Path.GetTempPath(), $"RT_Test_{assemblyName}_{Guid.NewGuid():N}.dll");
        var result = compilation.Emit(path);
        Assert.IsTrue(result.Success, $"Dependency compilation failed:\n{string.Join("\n", result.Diagnostics)}");

        return (MetadataReference.CreateFromFile(path), path);
    }

    /// <summary>
    /// Run the ReferenceTrimmerAnalyzer on the given library source with symbol-based analysis enabled.
    /// Dependencies are declared as ProjectReference entries in the TSV file.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(
        string librarySource,
        (MetadataReference Reference, string Path) dependency,
        string dependencySpec = "../Dependency/Dependency.csproj",
        string referenceKind = "ProjectReference")
    {
        return await RunAnalyzerAsync(
            librarySource,
            [(dependency.Reference, dependency.Path, referenceKind, dependencySpec)]);
    }

    /// <summary>
    /// Run the ReferenceTrimmerAnalyzer with multiple declared dependencies.
    /// Each dependency tuple: (Reference, Path, Kind, Spec).
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(
        string librarySource,
        (MetadataReference Reference, string Path, string Kind, string Spec)[] dependencies)
    {
        var tree = CSharpSyntaxTree.ParseText(librarySource);
        var references = new List<MetadataReference> { CorlibRef };
        var tsvLines = new List<string>();
        foreach (var dep in dependencies)
        {
            references.Add(dep.Reference);
            tsvLines.Add($"{dep.Path}\t{dep.Kind}\t{dep.Spec}");
        }

        var compilation = CSharpCompilation.Create(
            "Library",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string tsvContent = string.Join("\n", tsvLines);

        var additionalTexts = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("_ReferenceTrimmer_DeclaredReferences.tsv", tsvContent));

        var globalOptions = new TestGlobalOptions(new Dictionary<string, string>
        {
            ["build_property.ReferenceTrimmerUseSymbolAnalysis"] = "true",
        });

        var options = new AnalyzerOptions(additionalTexts, new TestOptionsProvider(globalOptions));
        var analyzer = new ReferenceTrimmerAnalyzer();
        var compilationWithAnalyzers = new CompilationWithAnalyzers(
            compilation,
            [analyzer],
            new CompilationWithAnalyzersOptions(options, null, concurrentAnalysis: true, logAnalyzerExecutionTime: false));

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    // ── Mock types ───────────────────────────────────────────────────────

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content);
    }

    private sealed class TestGlobalOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
#nullable disable
        public override bool TryGetValue(string key, out string value)
            => values.TryGetValue(key, out value);
#nullable restore
    }

    private sealed class TestOptionsProvider(AnalyzerConfigOptions globalOptions) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
            => new TestGlobalOptions([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => new TestGlobalOptions([]);
    }
}
