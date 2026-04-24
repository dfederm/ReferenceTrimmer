namespace Library
{
    public static class Foo
    {
        // Library uses Dependency but does NOT use TransitiveDependency directly
        public static string Bar() => Dependency.Foo.Bar();
    }
}
