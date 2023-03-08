namespace Library
{
    public static class Foo
    {
        public static string Bar() => Dependency.Foo.Bar();
    }
}
