using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VapeMeter.Windows;

internal static class WindowsCredentials
{
    public static string? GetSyncToken()
    {
        var path = Path.Combine(LaunchOptions.Default.DataDirectory, "device-tokens.dpapi");
        try
        {
            var protectedBytes = Convert.FromHexString(File.ReadAllText(path).Trim());
            var cleartext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            try
            {
                using var credentials = JsonDocument.Parse(Encoding.Unicode.GetString(cleartext));
                if (credentials.RootElement.ValueKind == JsonValueKind.Object &&
                    credentials.RootElement.TryGetProperty("windows", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var token = value.GetString();
                    if (token?.StartsWith("windows.", StringComparison.Ordinal) == true)
                    {
                        return token;
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cleartext);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or FormatException or CryptographicException or JsonException)
        {
            // The local app remains usable when this profile has no usable server credential.
        }

        return null;
    }
}
