using System;
using System.Collections.Frozen;

namespace Test
{
    public class Foo
    {
        public static FrozenSet<int> SomeSet() => FrozenSet.ToFrozenSet(Array.Empty<int>());
    }
}
