namespace Library
{
    public static class Foo
    {
        public static string Bar() => Dependency.Foo.Bar();

        public static string Baz() => Newtonsoft.Json.JsonConvert.SerializeObject(null);
    }
}
