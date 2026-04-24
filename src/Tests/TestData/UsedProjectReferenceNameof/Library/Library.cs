namespace Library
{
    public static class Bar
    {
        // Dependency.Foo used only in nameof() — lowered to a string literal in the IOperation tree.
        // Only the syntax-level nameof handler catches this.
        public static string GetName() => nameof(Dependency.Foo);
    }
}
