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
        var v2 = MasterConfig.LoadBiometricVaultV2();
        if (v2 != null)
        {
            Console.WriteLine($"SUCCESS: Biometric Vault V2 present (deviceId={v2.DeviceId})");
        }
        else
        {
            Console.WriteLine("FAIL: Biometric Vault V2 is not linked yet.");
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
