namespace Dependency
{
    public static class Foo
    {
        // Dependency actually USES TransitiveDependency
        public static string Bar() => TransitiveDependency.Foo.Baz();
    }
}
