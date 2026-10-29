using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YoutubeDownloader.Cli;

// Reads the login saved by the desktop app (same key derivation as its settings converter)
internal static class AppSettingsCookies
{
    private const string EncryptionSalt = "HimalayanPinkSalt";

    private static string? TryGetMachineId()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography"
            );

            return regKey?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<Cookie>? TryLoad(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(settingsFilePath));
            if (
                !document.RootElement.TryGetProperty("LastAuthCookies", out var property)
                || property.ValueKind != JsonValueKind.String
            )
                return null;

            var encryptedData = Convert.FromHexString(property.GetString()!);
            var cookieData = new byte[encryptedData.Length - 28];

            var key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(TryGetMachineId() ?? string.Empty),
                Encoding.UTF8.GetBytes(EncryptionSalt),
                600_000,
                HashAlgorithmName.SHA256,
                16
            );

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                encryptedData.AsSpan(0, 12),
                encryptedData.AsSpan(28),
                encryptedData.AsSpan(12, 16),
                cookieData
            );

            using var cookies = JsonDocument.Parse(cookieData);
            return cookies
                .RootElement.EnumerateArray()
                .Select(c => new Cookie(
                    c.GetProperty("Name").GetString()!,
                    c.GetProperty("Value").GetString()!,
                    c.GetProperty("Path").GetString()!,
                    c.GetProperty("Domain").GetString()!
                ))
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    // Netscape cookies.txt, as exported by browser extensions or yt-dlp
    public static IReadOnlyList<Cookie> LoadCookiesFile(string filePath) =>
        File.ReadAllLines(filePath)
            .Select(line =>
                line.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? line[10..] : line
            )
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 7)
            .Select(parts => new Cookie(parts[5], parts[6], parts[2], parts[0]))
            .ToArray();
}
