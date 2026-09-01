using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Terminal.Settings;

public static class ProfileGuid
{
    private static readonly Guid RuntimeGeneratedNamespace =
        new("f65ddb7e-706b-4499-8a50-40313caf510a");

    public static Guid Create(string name, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var profileNamespace = string.IsNullOrEmpty(source)
            ? RuntimeGeneratedNamespace
            : CreateV5(RuntimeGeneratedNamespace, Encoding.Unicode.GetBytes(source));
        return CreateV5(profileNamespace, Encoding.Unicode.GetBytes(name));
    }

    public static Guid CreateV5(Guid namespaceId, ReadOnlySpan<byte> name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        if (!namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out var bytesWritten) ||
            bytesWritten != namespaceBytes.Length)
        {
            throw new InvalidOperationException("Could not encode UUID namespace.");
        }

        var input = new byte[namespaceBytes.Length + name.Length];
        namespaceBytes.CopyTo(input);
        name.CopyTo(input.AsSpan(namespaceBytes.Length));
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
