using System;
using System.IO;
using AppLocker.Core;

class Program
{
    static void Main()
    {
        Console.WriteLine("=== Testing Biometric Vault & Phone Connection ===");
        var settings = LockerSettings.Load();
        if (settings.PairedPhone == null)
        {
            Console.WriteLine("No paired phone found!");
            return;
        }

        Console.WriteLine($"Phone: {settings.PairedPhone.DeviceName} ({settings.PairedPhone.LastIpAddress})");
        Console.WriteLine($"Secret: {settings.PairedPhone.SharedSecretKey}");

        byte[]? key = MasterConfig.GetBiometricVaultKey(settings.PairedPhone.SharedSecretKey);
        if (key != null)
        {
            Console.WriteLine($"SUCCESS: Vault decrypted! Master key length: {key.Length} bytes");
        }
        else
        {
            Console.WriteLine("FAIL: GetBiometricVaultKey returned NULL!");
        }

        Console.WriteLine("Checking phone reachability on port 58821...");
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(settings.PairedPhone.LastIpAddress, 58821);
            Console.WriteLine("SUCCESS: Connected to phone on port 58821!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: Could not connect to phone: {ex.Message}");
        }
    }
}
