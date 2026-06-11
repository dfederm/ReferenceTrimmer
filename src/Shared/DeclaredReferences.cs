namespace ReferenceTrimmer.Shared;

internal readonly record struct DeclaredReference(string AssemblyPath, DeclaredReferenceKind Kind, string Spec, ReferenceTrimmerSeverity Severity);

internal enum DeclaredReferenceKind { Reference, ProjectReference, PackageReference }

// Internal enum to avoid taking a dependency on Microsoft.CodeAnalysis in the Tasks project.
// The ReferenceTrimmerAnalyzer will convert this to DiagnosticSeverity when reading the declared references file.
internal enum ReferenceTrimmerSeverity { Hidden, Info, Warning }