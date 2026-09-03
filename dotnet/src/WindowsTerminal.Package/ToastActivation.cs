using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsTerminal.Package;

public sealed record ToastActivationPayload(
    int ProtocolVersion,
    string NotificationId,
    string TargetWindow,
    string Action);

public static class ToastActivationCodec
{
    public const int ProtocolVersion = 1;
    public const int MaximumEncodedLength = 4096;

    public static string Create(string targetWindow, string? notificationId = null)
    {
        if (!IsValidTarget(targetWindow))
        {
            throw new ArgumentException("Toast activation can only target use-any or a positive window id.", nameof(targetWindow));
        }

        var id = string.IsNullOrWhiteSpace(notificationId)
            ? Guid.NewGuid().ToString("N")
            : notificationId;
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("The toast notification id must be a 32-character GUID.", nameof(notificationId));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ToastActivationPayload(ProtocolVersion, id, targetWindow, "focus"),
            ToastActivationJsonContext.Default.ToastActivationPayload);
        return Base64Url.Encode(bytes);
    }

    public static bool TryParse(
        string encoded,
        out ToastActivationPayload? payload,
        out string? diagnostic)
    {
        payload = null;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumEncodedLength)
        {
            diagnostic = "Toast activation payload is empty or exceeds 4096 characters.";
            return false;
        }

        try
        {
            var bytes = Base64Url.Decode(encoded);
            payload = JsonSerializer.Deserialize(
                bytes,
                ToastActivationJsonContext.Default.ToastActivationPayload);
            if (payload is null ||
                payload.ProtocolVersion != ProtocolVersion ||
                !Guid.TryParseExact(payload.NotificationId, "N", out _) ||
                !string.Equals(payload.Action, "focus", StringComparison.Ordinal) ||
                !IsValidTarget(payload.TargetWindow))
            {
                payload = null;
                diagnostic = "Toast activation payload failed version, id, action, or target validation.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            diagnostic = $"Toast activation payload is invalid: {ex.Message}";
            return false;
        }
    }

    private static bool IsValidTarget(string target) =>
        string.Equals(target, "use-any", StringComparison.Ordinal) ||
        (int.TryParse(target, out var id) && id > 0);
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        if (value.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new FormatException("Only unpadded base64url characters are allowed.");
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("The base64url length is invalid."),
        };
        return Convert.FromBase64String(padded);
    }
}

[JsonSerializable(typeof(ToastActivationPayload))]
internal sealed partial class ToastActivationJsonContext : JsonSerializerContext;
