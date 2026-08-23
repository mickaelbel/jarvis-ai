namespace JarvisAI.Application.Security;

public sealed class ConfirmationResult
{
    public bool Confirmed { get; }
    public string? ResponseText { get; }
    public ConfirmationMethod Method { get; }
    public TimeSpan ResponseTime { get; }

    public ConfirmationResult(bool confirmed, ConfirmationMethod method, TimeSpan responseTime, string? responseText = null)
    {
        Confirmed = confirmed;
        ResponseText = responseText;
        Method = method;
        ResponseTime = responseTime;
    }

    public static ConfirmationResult Accepted(ConfirmationMethod method, TimeSpan responseTime, string? text = null)
        => new(true, method, responseTime, text);

    public static ConfirmationResult Denied(ConfirmationMethod method, TimeSpan responseTime, string? text = null)
        => new(false, method, responseTime, text);

    public static ConfirmationResult AutoConfirmed()
        => new(true, ConfirmationMethod.Automatic, TimeSpan.Zero);
}

public enum ConfirmationMethod
{
    Automatic,
    Text,
    Voice,
    BypassedByDeveloperMode
}
