namespace AiCodeAgent.Screenshots;

/// <summary>A scene could not reach the state it should show (timeout, missing control, failed run).</summary>
internal sealed class SceneException : Exception
{
    public SceneException(string message) : base(message) { }
}
