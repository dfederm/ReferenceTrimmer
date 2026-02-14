using Castle.Core.Logging;

namespace Test
{
    public class Foo
    {
        public static ILogger Logger() => NullLogger.Instance;
    }
}
