// <copyright file="Net461.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

#if NET461
namespace ReferenceTrimmer
{
    using System.Collections.Generic;

    internal static class Net461
    {
        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source, IEqualityComparer<TSource> comparer) => new HashSet<TSource>(source, comparer);
    }
}
#endif