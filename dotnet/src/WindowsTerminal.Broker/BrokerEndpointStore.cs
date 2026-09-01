using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsTerminal.Broker;

public sealed class BrokerEndpointStore
{
    private readonly string _endpointPath;

    public BrokerEndpointStore(string? rootDirectory = null, string instanceKey = "default")
    {
        var root = rootDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsTerminal");
        Directory.CreateDirectory(root);
        _endpointPath = Path.Combine(root, $"broker-v{BrokerProtocol.Version}-{SafeName(instanceKey)}.json");
    }

    public string EndpointPath => _endpointPath;

    public BrokerEndpoint? Read()
    {
        try
        {
            using var stream = new FileStream(_endpointPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize(stream, BrokerJsonContext.Default.BrokerEndpoint);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(BrokerEndpoint endpoint)
    {
        var temporaryPath = $"{_endpointPath}.{Environment.ProcessId}.{RandomNumberGenerator.GetHexString(8)}.tmp";
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, endpoint, BrokerJsonContext.Default.BrokerEndpoint);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, _endpointPath, overwrite: true);
    }

    public void DeleteIfMatches(BrokerEndpoint endpoint)
    {
        var current = Read();
        if (current == endpoint)
        {
            File.Delete(_endpointPath);
        }
    }

    private static string SafeName(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
