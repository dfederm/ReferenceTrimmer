using System.Linq;
using Provider;

namespace Library
{
    public static class Foo
    {
        public static ProviderMarker GetProviderMarker() => new ProviderMarker();

        public static int[] CreateValues() => Enumerable.Range(0, 21).ToArray();
    }
}
