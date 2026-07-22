namespace SprintLauncher.Notify;

public sealed class SlackNotifyException : Exception
{
    public SlackNotifyException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
