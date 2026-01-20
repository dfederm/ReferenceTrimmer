namespace ReferenceTrimmer.Shared;

internal readonly record struct DeclaredReference(string AssemblyPath, DeclaredReferenceKind Kind, string Spec, string AdditionalSpec);

internal enum DeclaredReferenceKind { Reference, ProjectReference, PackageReference }