namespace Library
{
    public static class Bar
    {
        /// <summary>
        /// See <see cref="Dependency.Foo"/> for details.
        /// </summary>
        /// <remarks>
        /// Also references <see cref="Dependency.Foo.Bar"/>.
        /// </remarks>
        public static string GetName() => "bar";
    }
}
