namespace ReferenceTrimmer.Shared;

internal record DeclaredReferences(IReadOnlyList<DeclaredReference> References)
{
    private const char FieldDelimiter = '\t';

    private static readonly char[] FieldDelimiters = new[] { FieldDelimiter };

    private static readonly Dictionary<DeclaredReferenceKind, string> KindEnumToString = new()
    {
        { DeclaredReferenceKind.Reference, nameof(DeclaredReferenceKind.Reference) },
        { DeclaredReferenceKind.ProjectReference, nameof(DeclaredReferenceKind.ProjectReference) },
        { DeclaredReferenceKind.PackageReference, nameof(DeclaredReferenceKind.PackageReference) },
    };

    private static readonly Dictionary<string, DeclaredReferenceKind> KindStringToEnum = new()
    {
        { nameof(DeclaredReferenceKind.Reference), DeclaredReferenceKind.Reference },
        { nameof(DeclaredReferenceKind.ProjectReference), DeclaredReferenceKind.ProjectReference },
        { nameof(DeclaredReferenceKind.PackageReference), DeclaredReferenceKind.PackageReference },
    };

    public void SaveToFile(string filePath)
    {
        using FileStream stream = File.Create(filePath);
        using StreamWriter writer = new(stream);

        foreach (DeclaredReference reference in References)
        {
            writer.Write(reference.AssemblyPath);
            writer.Write(FieldDelimiter);
            writer.Write(KindEnumToString[reference.Kind]);
            writer.Write(FieldDelimiter);
            writer.Write(reference.Spec);
            writer.WriteLine();
        }
    }

    public static DeclaredReferences ReadFromFile(string filePath)
    {
        List<DeclaredReference> references = new();

        using FileStream stream = File.OpenRead(filePath);
        using StreamReader reader = new(stream);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] parts = line.Split(FieldDelimiters, 3);
            if (parts.Length != 3)
            {
                throw new InvalidDataException($"File '{filePath}' is invalid. Line: {references.Count + 1}");
            }

            string assemblyName = parts[0];
            DeclaredReferenceKind kind = KindStringToEnum[parts[1]];
            string spec = parts[2];
            DeclaredReference reference = new(assemblyName, kind, spec);
            references.Add(reference);
        }

        return new DeclaredReferences(references);
    }
}

internal record DeclaredReference(string AssemblyPath, DeclaredReferenceKind Kind, string Spec);

internal enum DeclaredReferenceKind { Reference, ProjectReference, PackageReference }