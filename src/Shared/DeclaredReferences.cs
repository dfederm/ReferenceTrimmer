namespace ReferenceTrimmer.Shared;

internal record DeclaredReferences(IReadOnlyList<DeclaredReference> References);

internal record DeclaredReference(string AssemblyName, DeclaredReferenceKind Kind, string Spec);

internal enum DeclaredReferenceKind { Reference, ProjectReference, PackageReference }