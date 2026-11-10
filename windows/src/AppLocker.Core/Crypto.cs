using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AppLocker.Core;

public static class Crypto
{
    private const int SaltSize = 16;
    private const int KeySize = 32;                    // 256 bit
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 600_000;            // PBKDF2-SHA256
    private const int DefaultChunkSize = 1024 * 1024;  // 1 MB chunks for fast throughput on large files
    private const int MaxChunkSize = 4 * 1024 * 1024;  // Accepts legacy 64 KB chunks as well as larger chunks
    private const int StreamBufferSize = 1024 * 1024;

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

    // File encrypt: output = Magic + baseNonce + [len + tag + cipher]...
    public static void EncryptFile(string inputPath, string outputPath, byte[] key, Action<long>? onBytesProcessed = null)
    {
        Span<byte> baseNonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(baseNonce);

        using var input = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            StreamBufferSize, FileOptions.SequentialScan);
        using var aes = new AesGcm(key, TagSize);

        output.Write(Magic);
        output.Write(baseNonce);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);
        byte[] cipher = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);
        Span<byte> tag = stackalloc byte[TagSize];
        Span<byte> lenBytes = stackalloc byte[4];
        Span<byte> nonce = stackalloc byte[NonceSize];

        try
        {
            uint counter = 0;
            int read;

            while ((read = input.ReadAtLeast(buffer.AsSpan(0, DefaultChunkSize), DefaultChunkSize, throwOnEndOfStream: false)) > 0)
            {
                FillNonce(baseNonce, counter, nonce);
                aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher.AsSpan(0, read), tag);

                BinaryPrimitives.WriteInt32LittleEndian(lenBytes, read);
                output.Write(lenBytes);
                output.Write(tag);
                output.Write(cipher.AsSpan(0, read));

                counter++;
                onBytesProcessed?.Invoke(read);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, DefaultChunkSize);
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    public static void DecryptFile(string inputPath, string outputPath, byte[] key, Action<long>? onBytesProcessed = null)
    {
        using var input = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        using var aes = new AesGcm(key, TagSize);

        Span<byte> magic = stackalloc byte[Magic.Length];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not an AppLocker file.");

        Span<byte> baseNonce = stackalloc byte[NonceSize];
        input.ReadExactly(baseNonce);

        using var output = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            StreamBufferSize, FileOptions.SequentialScan);

        Span<byte> lenBytes = stackalloc byte[4];
        Span<byte> tag = stackalloc byte[TagSize];
        Span<byte> nonce = stackalloc byte[NonceSize];
        byte[] cipher = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);
        byte[] plain = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);

        try
        {
            uint counter = 0;

            while (true)
            {
                int got = input.ReadAtLeast(lenBytes, 4, throwOnEndOfStream: false);
                if (got == 0) break;
                if (got != 4) throw new InvalidDataException("Corrupt file.");

                int len = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
                if (len <= 0 || len > MaxChunkSize)
                    throw new InvalidDataException("Corrupt chunk size.");

                if (cipher.Length < len)
                {
                    ArrayPool<byte>.Shared.Return(cipher);
                    cipher = ArrayPool<byte>.Shared.Rent(len);
                }
                if (plain.Length < len)
                {
                    Array.Clear(plain, 0, plain.Length);
                    ArrayPool<byte>.Shared.Return(plain);
                    plain = ArrayPool<byte>.Shared.Rent(len);
                }

                input.ReadExactly(tag);
                input.ReadExactly(cipher.AsSpan(0, len));

                FillNonce(baseNonce, counter, nonce);
                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len));
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException("Wrong password or file tampered.");
                }

                output.Write(plain.AsSpan(0, len));
                counter++;
                onBytesProcessed?.Invoke(len);
            }
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(plain);
        }
    }

    private static void FillNonce(ReadOnlySpan<byte> baseNonce, uint counter, Span<byte> dest)
    {
        baseNonce.CopyTo(dest);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(c, counter);
        for (int i = 0; i < 4; i++)
            dest[NonceSize - 4 + i] ^= c[i];
    }
}