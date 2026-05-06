namespace ReferenceTrimmer.Shared;

internal readonly record struct DeclaredReference(string AssemblyPath, DeclaredReferenceKind Kind, string Spec, ReferenceTrimmerSeverity Severity);

internal enum DeclaredReferenceKind { Reference, ProjectReference, PackageReference }

internal enum ReferenceTrimmerSeverity { Hidden, Info, Warning }