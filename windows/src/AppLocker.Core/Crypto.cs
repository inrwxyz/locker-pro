using System.Security.Cryptography;
using System.Text;

namespace AppLocker.Core;

public static class Crypto
{
    private const int SaltSize = 16;
    private const int KeySize = 32;          // 256 bit
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 600_000;  // PBKDF2-SHA256
    private const int ChunkSize = 64 * 1024; // 64 KB

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APLK1");

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    // File encrypt: output = Magic + baseNonce + [len + nonce-counter chunk + tag + data]...
    public static void EncryptFile(string inputPath, string outputPath, byte[] key)
    {
        byte[] baseNonce = RandomNumberGenerator.GetBytes(NonceSize);

        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        using var aes = new AesGcm(key, TagSize);

        output.Write(Magic);
        output.Write(baseNonce);

        byte[] buffer = new byte[ChunkSize];
        byte[] cipher = new byte[ChunkSize];
        byte[] tag = new byte[TagSize];
        uint counter = 0;
        int read;

        while ((read = input.Read(buffer, 0, ChunkSize)) > 0)
        {
            byte[] nonce = MakeNonce(baseNonce, counter);
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher.AsSpan(0, read), tag);

            output.Write(BitConverter.GetBytes(read));
            output.Write(tag);
            output.Write(cipher, 0, read);
            counter++;
        }
    }

    public static void DecryptFile(string inputPath, string outputPath, byte[] key)
    {
        using var input = File.OpenRead(inputPath);
        using var aes = new AesGcm(key, TagSize);

        byte[] magic = new byte[Magic.Length];
        input.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not an AppLocker file.");

        byte[] baseNonce = new byte[NonceSize];
        input.ReadExactly(baseNonce);

        using var output = File.Create(outputPath);
        byte[] lenBytes = new byte[4];
        byte[] tag = new byte[TagSize];
        byte[] cipher = new byte[ChunkSize];
        byte[] plain = new byte[ChunkSize];
        uint counter = 0;

        while (true)
        {
            int got = input.Read(lenBytes, 0, 4);
            if (got == 0) break;
            if (got != 4) throw new InvalidDataException("Corrupt file.");

            int len = BitConverter.ToInt32(lenBytes);
            if (len <= 0 || len > ChunkSize)
                throw new InvalidDataException("Corrupt chunk size.");

            input.ReadExactly(tag);
            input.ReadExactly(cipher, 0, len);

            byte[] nonce = MakeNonce(baseNonce, counter);
            try
            {
                aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len));
            }
            catch (CryptographicException)
            {
                throw new CryptographicException("Wrong password or file tampered.");
            }

            output.Write(plain, 0, len);
            counter++;
        }
    }

    private static byte[] MakeNonce(byte[] baseNonce, uint counter)
    {
        byte[] nonce = (byte[])baseNonce.Clone();
        byte[] c = BitConverter.GetBytes(counter);
        for (int i = 0; i < 4; i++)
            nonce[NonceSize - 4 + i] ^= c[i];
        return nonce;
    }
}