namespace Sirius.Core.Graph;

/// <summary>
/// An exception as a person can act on it: its message and the messages of
/// what caused it.
/// </summary>
/// <remarks>
/// Some exceptions only say that something else went wrong. "The type
/// initializer for 'Emgu.CV.CvInvoke' threw an exception" is true and no
/// use; the reason - "Unable to load DLL 'cvextern' or one of its
/// dependencies" - is one level down, and is what tells somebody to install
/// a missing Windows feature rather than reinstall the application.
/// </remarks>
public static class ExceptionText
{
    /// <summary>
    /// The message of <paramref name="exception"/> and of each exception
    /// inside it, outermost first, joined by " → "; repeats left out.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();

            if (message.Length > 0 && !messages.Contains(message))
                messages.Add(message);
        }

        return string.Join(" → ", messages);
    }
}
