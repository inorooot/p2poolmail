using System.Text;

namespace p2poolmail;

internal static class EmailTemplates
{
    public const string RecoverySubject = "Alert Cleared";

    public static string GetSubject(Notification.Type type) => Notification.GetSubject(type);

    public static string GetBody(Notification.Type type) => Notification.GetBody(type);

    public static string GetRecoverySubject(Notification.Type type) => RecoverySubject;

    public static string GetRecoveryBody(Notification.Type type)
        => ToStrikethrough(Notification.GetBody(type));

    public static string ToStrikethrough(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        var sb = new StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            sb.Append(ch);
            sb.Append('\u0336');
        }
        return sb.ToString();
    }
}
