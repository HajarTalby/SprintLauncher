namespace SprintLauncher.Notify;

public sealed class SlackNotifyException : Exception
{
    public SlackNotifyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
