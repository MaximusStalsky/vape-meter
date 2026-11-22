using System.Security.Cryptography;
using System.Text;
using global::Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace VapeMeter.Android;

internal sealed class AndroidCredentials(string path)
{
    private const string KeyAlias = "org.stalsky.vapemeter.sync.v1";
    private const string KeyStoreProvider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const byte OfflineMarker = 0;
    private const byte ProtectedTokenMarker = 1;
    private const int IvLength = 12;
    private const int TagLengthBits = 128;

    public (bool Configured, string? Token) Load()
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }

        var stored = File.ReadAllBytes(path);
        if (stored is [OfflineMarker])
        {
            return (true, null);
        }

        if (stored.Length <= 1 + IvLength + TagLengthBits / 8 || stored[0] != ProtectedTokenMarker)
        {
            throw new InvalidDataException("Неподдерживаемый формат ключа Android.");
        }

        using var key = GetKey(allowCreate: false);
        using var cipher = Cipher.GetInstance(Transformation)!;
        using var gcm = new GCMParameterSpec(TagLengthBits, stored[1..(1 + IvLength)]);
        cipher.Init(CipherMode.DecryptMode, key, gcm);
        var plaintext = cipher.DoFinal(stored[(1 + IvLength)..])!;
        try
        {
            var token = Encoding.UTF8.GetString(plaintext);
            if (!TimewebConnection.IsAndroidKey(token))
            {
                throw new InvalidDataException("Сохранённый ключ имеет неверный формат.");
            }
            return (true, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(string token)
    {
        using var key = GetKey(allowCreate: true);
        using var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, key);
        var plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            var iv = cipher.GetIV();
            if (iv is not { Length: IvLength })
            {
                throw new CryptographicException("Android Keystore вернул неподдерживаемый IV.");
            }

            var ciphertext = cipher.DoFinal(plaintext)!;
            SaveFile([ProtectedTokenMarker, .. iv, .. ciphertext]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void UseOffline() => SaveFile([OfflineMarker]);

    private static ISecretKey GetKey(bool allowCreate)
    {
        using var store = KeyStore.GetInstance(KeyStoreProvider)!;
        store.Load(null, null);
        if (store.GetKey(KeyAlias, null) is ISecretKey existing)
        {
            return existing;
        }
        if (!allowCreate)
        {
            throw new CryptographicException("Ключ Android Keystore больше недоступен.");
        }

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider)!;
        using var specification = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey()!;
    }

    private void SaveFile(byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
