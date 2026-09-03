namespace Devolutions.Terminal.Settings;

public enum SettingsDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SettingsDiagnostic(
    SettingsDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Source = null,
    long? Line = null,
    long? Column = null);

public sealed class SettingsLoadException : Exception
{
    public SettingsLoadException(SettingsDiagnostic diagnostic, Exception? innerException = null)
        : base(diagnostic.Message, innerException)
    {
        Diagnostic = diagnostic;
    }

    public SettingsDiagnostic Diagnostic { get; }
}
