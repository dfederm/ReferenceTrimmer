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

    [TestMethod]
    public async Task UsedViaNameof()
    {
        var dep = EmitDependency("namespace Dep { public class Marker {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { string M() => nameof(Dep.Marker); }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaNameofMember()
    {
        var dep = EmitDependency("namespace Dep { public class Foo { public static int Bar; } }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { string M() => nameof(Dep.Foo.Bar); }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaXmlDocCref()
    {
        var dep = EmitDependency("namespace Dep { public class Documented {} }");
        var diagnostics = await RunAnalyzerAsync(
            @"/// <summary>See <see cref=""Dep.Documented""/>.</summary>
            class C { }",
            [(dep.Reference, dep.Path, "ProjectReference", "../Dependency/Dependency.csproj")],
            new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose));
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaXmlDocCrefMember()
    {
        var dep = EmitDependency("namespace Dep { public class Foo { public static void Bar() {} } }");
        var diagnostics = await RunAnalyzerAsync(
            @"/// <summary>See <see cref=""Dep.Foo.Bar""/>.</summary>
            class C { }",
            [(dep.Reference, dep.Path, "ProjectReference", "../Dependency/Dependency.csproj")],
            new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose));
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnusedViaCrefWhenDocModeDisabled()
    {
        var dep = EmitDependency("namespace Dep { public class Documented {} }");
        // With DocumentationMode.None, cref references should NOT prevent removal
        var diagnostics = await RunAnalyzerAsync(
            @"/// <summary>See <see cref=""Dep.Documented""/>.</summary>
            class C { }",
            [(dep.Reference, dep.Path, "ProjectReference", "../Dependency/Dependency.csproj")],
            new CSharpParseOptions(documentationMode: DocumentationMode.None));
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
    }

    [TestMethod]
    public async Task UsedViaTypeForwarding()
    {
        // Emit "Runtime" assembly with the actual type
        var runtime = EmitDependency(
            "namespace Dep { public class Foo {} }",
            assemblyName: "Runtime");

        // Emit "Facade" assembly that forwards Dep.Foo to Runtime
        var facadeTree = CSharpSyntaxTree.ParseText(@"
            using System.Runtime.CompilerServices;
            [assembly: TypeForwardedTo(typeof(Dep.Foo))]
        ");
        var facadeComp = CSharpCompilation.Create(
            "Facade",
            [facadeTree],
            [CorlibRef, runtime.Reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string facadePath = Path.Combine(Path.GetTempPath(), $"RT_Test_Facade_{Guid.NewGuid():N}.dll");
        var facadeResult = facadeComp.Emit(facadePath);
        Assert.IsTrue(facadeResult.Success, $"Facade compilation failed:\n{string.Join("\n", facadeResult.Diagnostics)}");
        var facadeRef = MetadataReference.CreateFromFile(facadePath);

        // Library uses Dep.Foo (resolves to Runtime), but Facade should also be kept
        var diagnostics = await RunAnalyzerAsync(
            "class C : Dep.Foo { }",
            [(facadeRef, facadePath, "ProjectReference", "../Facade/Facade.csproj"),
             (runtime.Reference, runtime.Path, "ProjectReference", "../Runtime/Runtime.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaLocalVariableType()
    {
        var dep = EmitDependency("namespace Dep { public class Holder { public int Value; } }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                void M() {
                    Dep.Holder h = null;
                    _ = h;
                }
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaLambdaParameterType()
    {
        // The lambda parameter type is the sole reference path to the dependency.
        // System.Action<object> avoids referencing Dep.Input through the delegate type.
        var dep = EmitDependency("namespace Dep { public class Input {} }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                void M() {
                    System.Action<object> a = (object x) => {
                        var y = (Dep.Input)x;
                    };
                }
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaLocalFunctionReturnType()
    {
        // The local function's return type is the sole reference path — no call site
        // to avoid tracking via IInvocationOperation.
        var dep = EmitDependency("namespace Dep { public class Result {} }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                void M() {
                    Dep.Result Local() => null;
                    _ = (object)null;
                }
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaArrayElementType()
    {
        var dep = EmitDependency("namespace Dep { public class Item {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { Dep.Item[] M() => null; }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaAttributeTypeofArgument()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public class TypedAttribute : System.Attribute {
                    public TypedAttribute(System.Type t) {}
                }
                public class Target {}
            }");
        var diagnostics = await RunAnalyzerAsync(
            "[Dep.TypedAttribute(typeof(Dep.Target))] class C { }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaRecursivePattern()
    {
        var dep = EmitDependency(@"
            namespace Dep {
                public class Point {
                    public int X { get; set; }
                    public int Y { get; set; }
                }
            }");
        var diagnostics = await RunAnalyzerAsync(
            @"class C {
                bool Check(object o) => o is Dep.Point { X: > 0 };
            }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaEventType()
    {
        var dep = EmitDependency("namespace Dep { public delegate void MyHandler(int x); }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { event Dep.MyHandler MyEvent; }",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnusedPackageReportsRT0003()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { }",
            [(dep.Reference, dep.Path, "PackageReference", "Dep.Package")]);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0003", diagnostics[0].Id);
        Assert.IsTrue(diagnostics[0].GetMessage(CultureInfo.InvariantCulture).Contains("Dep.Package"));
    }

    [TestMethod]
    public async Task UsedPackageDoesNotReportRT0003()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C : Dep.Foo { }",
            [(dep.Reference, dep.Path, "PackageReference", "Dep.Package")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task PackageUsedIfAnyAssemblyUsed()
    {
        // A package has two assemblies; only one is used. The package should be kept.
        var usedAsm = EmitDependency(
            "namespace DepA { public class Foo {} }",
            assemblyName: "Dep.PackageA");
        var unusedAsm = EmitDependency(
            "namespace DepB { public class Bar {} }",
            assemblyName: "Dep.PackageB");
        var diagnostics = await RunAnalyzerAsync(
            "class C : DepA.Foo { }",
            [(usedAsm.Reference, usedAsm.Path, "PackageReference", "Dep.Package"),
             (unusedAsm.Reference, unusedAsm.Path, "PackageReference", "Dep.Package")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnusedBareReferenceReportsRT0001()
    {
        var dep = EmitDependency("namespace Dep { public class Foo {} }");
        var diagnostics = await RunAnalyzerAsync(
            "class C { }",
            [(dep.Reference, dep.Path, "Reference", dep.Path)]);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0001", diagnostics[0].Id);
    }

    [TestMethod]
    public async Task UsedViaDelegateParameterType()
    {
        // The external type is referenced only via a delegate's parameter type.
        // The delegate's Invoke method is implicitly declared, so a SymbolKind.Method
        // action does not fire for it — the analyzer must track parameters/return type
        // through the INamedTypeSymbol with TypeKind.Delegate.
        var dep = EmitDependency("namespace Dep { public class Service {} }");
        var diagnostics = await RunAnalyzerAsync(
            "public delegate void Configure(Dep.Service s);",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaDelegateReturnType()
    {
        // The external type is referenced only via a delegate's return type.
        var dep = EmitDependency("namespace Dep { public class Result {} }");
        var diagnostics = await RunAnalyzerAsync(
            "public delegate Dep.Result Produce();",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaDelegateGenericReturnType()
    {
        // The external type is referenced only via a generic argument in the delegate's return type.
        var dep = EmitDependency("namespace Dep { public class Item {} }");
        var diagnostics = await RunAnalyzerAsync(
            "public delegate System.Collections.Generic.List<Dep.Item> ProduceItems();",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaDelegateTypeParameterConstraint()
    {
        // External type referenced only via a type parameter constraint on a generic delegate.
        var dep = EmitDependency("namespace Dep { public class Base {} }");
        var diagnostics = await RunAnalyzerAsync(
            "public delegate void Apply<T>(T x) where T : Dep.Base;",
            dep);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnusedDelegateNoExternalTypesReportsDiagnostic()
    {
        // Negative test: a delegate using only same-assembly types should NOT mark
        // an unrelated external assembly as used.
        var dep = EmitDependency("namespace Dep { public class Service {} }");
        var diagnostics = await RunAnalyzerAsync(
            "public class Local {} public delegate Local Produce(Local x);",
            dep);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
    }

    [TestMethod]
    public async Task UsedViaInheritedStaticMethod()
    {
        // The static method is *defined* on the base class in another assembly, but called
        // through the derived class. Without tracking the type qualifier in the member access
        // syntax, only the base assembly would be credited and the derived assembly would be
        // wrongly flagged as unused.
        var baseAsm = EmitDependency(
            "namespace Dep { public class Base { public static void Foo() {} } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M() { Dep.Derived.Foo(); } }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaInheritedStaticField()
    {
        // Static field defined on base, accessed via the derived type.
        var baseAsm = EmitDependency(
            "namespace Dep { public class Base { public static int Counter; } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class C { int M() => Dep.Derived.Counter; }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaOverridePropertyAccess()
    {
        // Property is *defined* abstract on the base in another assembly, overridden on the
        // derived in a second assembly, and accessed via the derived type. Without walking
        // OverriddenProperty, only the derived assembly is credited and the base assembly
        // would be wrongly flagged removable — yet removing it produces CS0012 because the
        // C# compiler validates the override chain.
        var baseAsm = EmitDependency(
            "namespace Dep { public abstract class Base { public abstract string SomeProperty { get; } } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { public override string SomeProperty => \"value\"; } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class C { string M(Dep.Derived d) => d.SomeProperty; }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaOverrideMethodCall()
    {
        // Method is *defined* abstract on the base in another assembly, overridden on the
        // derived in a second assembly, and invoked via the derived type.
        var baseAsm = EmitDependency(
            "namespace Dep { public abstract class Base { public abstract void Run(); } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { public override void Run() {} } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M(Dep.Derived d) => d.Run(); }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaOverrideEventAccess()
    {
        // Event is *defined* abstract on the base in another assembly, overridden on the
        // derived in a second assembly, and subscribed to via the derived type.
        var baseAsm = EmitDependency(
            "namespace Dep { public abstract class Base { public abstract event System.EventHandler Changed; } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { public override event System.EventHandler Changed; } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class C { void M(Dep.Derived d, System.EventHandler h) => d.Changed += h; }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaOverrideMultiLevelChain()
    {
        // Three-level override chain: A defines abstract, B overrides, C overrides again.
        // Consumer accesses via C — every assembly in the chain must be credited because
        // the C# compiler validates the full chain.
        var aAsm = EmitDependency(
            "namespace Dep { public abstract class A { public abstract string SomeProperty { get; } } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public abstract class B : A { public override string SomeProperty => \"b\"; } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var cAsm = EmitDependency(
            "namespace Dep { public class C : B { public override string SomeProperty => \"c\"; } }",
            assemblyName: "CAsm",
            additionalReferences: [aAsm.Reference, bAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer { string M(Dep.C c) => c.SomeProperty; }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj"),
             (cAsm.Reference, cAsm.Path, "ProjectReference", "../C/C.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnrelatedReferenceNotMarkedByOverride()
    {
        // Negative test: accessing an overridden member on a derived type from one assembly
        // should not credit an entirely unrelated external assembly. Only the assemblies on
        // the override chain (and the qualifier's assembly) are required.
        var baseAsm = EmitDependency(
            "namespace Dep { public abstract class Base { public abstract string SomeProperty { get; } } }",
            assemblyName: "BaseAsm");
        var derivedAsm = EmitDependency(
            "namespace Dep { public class Derived : Base { public override string SomeProperty => \"value\"; } }",
            assemblyName: "DerivedAsm",
            additionalReferences: [baseAsm.Reference]);
        var unrelated = EmitDependency(
            "namespace Other { public class Unused {} }",
            assemblyName: "UnrelatedAsm");
        var diagnostics = await RunAnalyzerAsync(
            "class C { string M(Dep.Derived d) => d.SomeProperty; }",
            [(baseAsm.Reference, baseAsm.Path, "ProjectReference", "../Base/Base.csproj"),
             (derivedAsm.Reference, derivedAsm.Path, "ProjectReference", "../Derived/Derived.csproj"),
             (unrelated.Reference, unrelated.Path, "ProjectReference", "../Unrelated/Unrelated.csproj")]);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
        StringAssert.Contains(diagnostics[0].GetMessage(CultureInfo.InvariantCulture), "Unrelated");
    }

    [TestMethod]
    public async Task UsedViaInheritedBaseType()
    {
        // The canonical issue #144 scenario: Consumer derives from a class in B, which itself
        // derives from a class in A. With <DisableTransitiveProjectReferences>true</...>, A
        // does not flow transitively from B → Consumer, so Consumer must reference A directly.
        // Without walking the BaseType chain we'd flag A as removable, but removing it produces
        // CS0012 because the C# compiler validates the entire base-type chain.
        var aAsm = EmitDependency(
            "namespace Dep { public class ProviderDependency { public int Counter; } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Provider : ProviderDependency { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer : Dep.Provider { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaImplementedInterface()
    {
        // Interface chain: A defines IFoo, B defines a class Foo : IFoo, Consumer derives from Foo.
        // Consumer's reference to A is required because the compiler validates that Foo's
        // implemented interface is reachable. Without walking the interface chain we'd miss A.
        var aAsm = EmitDependency(
            "namespace Dep { public interface IFoo { } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Foo : IFoo { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer : Dep.Foo { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaMultiLevelInheritanceChain()
    {
        // Three-level base-type chain: A ← B ← C. Consumer references C and uses it as a base
        // class. Every assembly along the chain must be credited because the C# compiler
        // validates the full inheritance chain (CS0012 fires on any missing link).
        var aAsm = EmitDependency(
            "namespace Dep { public class A { } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class B : A { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var cAsm = EmitDependency(
            "namespace Dep { public class C : B { } }",
            assemblyName: "CAsm",
            additionalReferences: [aAsm.Reference, bAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer : Dep.C { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj"),
             (cAsm.Reference, cAsm.Path, "ProjectReference", "../C/C.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaInheritanceChainOnVariableType()
    {
        // The chain must be walked even when the named type is encountered as a variable type
        // (not as an explicit base in Consumer's own code). Declaring a parameter of type
        // Dep.Provider still requires every assembly in Provider's inheritance chain.
        var aAsm = EmitDependency(
            "namespace Dep { public class ProviderDependency { } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Provider : ProviderDependency { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer { void M(Dep.Provider p) { } }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaMixedBaseAndInterfaceChain()
    {
        // Mixed scenario: B's class inherits from A's class and implements D's interface.
        // Consumer derives from B's class. All four assemblies (A, B, D, and B itself via the
        // direct base) must be credited.
        var aAsm = EmitDependency(
            "namespace Dep { public class BaseA { } }",
            assemblyName: "AAsm");
        var dAsm = EmitDependency(
            "namespace Dep { public interface IFromD { } }",
            assemblyName: "DAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Provider : BaseA, IFromD { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference, dAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer : Dep.Provider { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj"),
             (dAsm.Reference, dAsm.Path, "ProjectReference", "../D/D.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UsedViaGenericConstraintBaseChain()
    {
        // Generic constraint variant: T : Provider where Provider : ProviderDependency.
        // Consumer's constraint forces the compiler to validate Provider's base chain, so A
        // must remain a reference.
        var aAsm = EmitDependency(
            "namespace Dep { public class ProviderDependency { } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Provider : ProviderDependency { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer<T> where T : Dep.Provider { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj")]);
        AssertNoDiagnostics(diagnostics);
    }

    [TestMethod]
    public async Task UnrelatedReferenceNotMarkedByInheritance()
    {
        // Negative test: deriving from a type whose inheritance chain spans two assemblies
        // should not credit an entirely unrelated assembly. Only the assemblies along the
        // chain are required.
        var aAsm = EmitDependency(
            "namespace Dep { public class ProviderDependency { } }",
            assemblyName: "AAsm");
        var bAsm = EmitDependency(
            "namespace Dep { public class Provider : ProviderDependency { } }",
            assemblyName: "BAsm",
            additionalReferences: [aAsm.Reference]);
        var unrelated = EmitDependency(
            "namespace Other { public class Unused { } }",
            assemblyName: "UnrelatedAsm");
        var diagnostics = await RunAnalyzerAsync(
            "class Consumer : Dep.Provider { }",
            [(aAsm.Reference, aAsm.Path, "ProjectReference", "../A/A.csproj"),
             (bAsm.Reference, bAsm.Path, "ProjectReference", "../B/B.csproj"),
             (unrelated.Reference, unrelated.Path, "ProjectReference", "../Unrelated/Unrelated.csproj")]);
        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual("RT0002", diagnostics[0].Id);
        StringAssert.Contains(diagnostics[0].GetMessage(CultureInfo.InvariantCulture), "Unrelated");
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
        => EmitDependency(source, assemblyName, additionalReferences: null);

    private static (MetadataReference Reference, string Path) EmitDependency(
        string source,
        string assemblyName,
        MetadataReference[]? additionalReferences)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference> { CorlibRef };
        if (additionalReferences != null)
        {
            references.AddRange(additionalReferences);
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            references,
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
        (MetadataReference Reference, string Path, string Kind, string Spec)[] dependencies,
        CSharpParseOptions? parseOptions = null)
    {
        var tree = CSharpSyntaxTree.ParseText(librarySource, parseOptions);
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
