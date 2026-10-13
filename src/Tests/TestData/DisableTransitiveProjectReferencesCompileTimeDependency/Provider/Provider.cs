namespace Provider
{
    public sealed class ProviderMarker
    {
    }

    public static class CandidateExtensions
    {
        public static object ToArray(this Dependency.CandidateReceiver receiver) => new object();
    }
}
