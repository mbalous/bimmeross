using System;
using System.Threading.Tasks;
using BmwCoder.Transport;

namespace BmwCoder.Tools.HsfzPing;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "169.254.16.17";
        int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 6801;
        byte target = args.Length > 2 ? Convert.ToByte(args[2], 16) : (byte)0x40; // Default CAS (0x40)

        Console.WriteLine($"=== HsfzPing Utility ===");
        Console.WriteLine($"Target Host:   {host}:{port}");
        Console.WriteLine($"Target ECU:    0x{target:X2}");
        Console.WriteLine($"Connecting...");

        using var connection = new HsfzConnection { Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            await connection.ConnectAsync(host, port);
            Console.WriteLine("Connected! Sending TesterPresent (3E 00)...");

            var request = new HsfzFrame(target, new byte[] { 0x3E, 0x00 });
            var response = await connection.SendAsync(request);

            Console.WriteLine($"Received response from 0x{response.SourceAddress:X2}:");
            Console.WriteLine($"Payload (hex): {Convert.ToHexString(response.Payload)}");

            if (response.Payload.Length >= 2 && response.Payload[0] == 0x7E && response.Payload[1] == 0x00)
            {
                Console.WriteLine("SUCCESS: Positive TesterPresent response (7E 00) received!");
                return 0;
            }

            Console.WriteLine("Response received, but not positive 7E 00.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }
}
