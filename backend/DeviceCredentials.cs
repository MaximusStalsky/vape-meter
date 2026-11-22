using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace VapeMeter.Backend;

internal sealed class DeviceCredentials
{
    public static readonly object ContextKey = new();

    private readonly Dictionary<string, byte[]> hashes = new(StringComparer.Ordinal);

    public DeviceCredentials(string path)
    {
        var configured = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (configured is null || configured.Count == 0)
        {
            throw new InvalidOperationException("At least one device credential is required.");
        }

        foreach (var (device, hexHash) in configured)
        {
            if (device.Length == 0 || !device.All(char.IsAsciiLetterOrDigit) || hexHash?.Length != 64)
            {
                throw new InvalidOperationException("Invalid device credential configuration.");
            }

            hashes.Add(device, Convert.FromHexString(hexHash));
        }
    }

    public string? Authenticate(StringValues authorization)
    {
        if (authorization.Count != 1 || authorization[0] is not { Length: <= 128 } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[7..];
        var separator = token.IndexOf('.');
        if (separator < 1 || !hashes.TryGetValue(token[..separator], out var expected))
        {
            return null;
        }

        var actual = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? token[..separator] : null;
    }
}
