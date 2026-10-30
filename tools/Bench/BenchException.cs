namespace AiCodeAgent.Bench;

/// <summary>A problem with the input or the environment, reported without a stack trace.</summary>
internal sealed class BenchException : Exception
{
    public BenchException(string message) : base(message) { }
    public BenchException(string message, Exception inner) : base(message, inner) { }
}
