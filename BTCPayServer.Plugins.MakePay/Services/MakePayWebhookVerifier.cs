#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.MakePay.Services;

public static class MakePayWebhookVerifier
{
    public const string SignatureHeader = "x-makepay-signature";

    public static bool Verify(
        string body,
        string header,
        string secret,
        TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(body) ||
            string.IsNullOrWhiteSpace(header) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var parts = header
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => part[1]);

        if (!parts.TryGetValue("t", out var timestampText) ||
            !parts.TryGetValue("v1", out var signature) ||
            !long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toleranceSeconds = (long)(tolerance ?? TimeSpan.FromMinutes(5)).TotalSeconds;
        if (Math.Abs(now - timestamp) > toleranceSeconds)
        {
            return false;
        }

        var payload = timestamp.ToString(CultureInfo.InvariantCulture) + "." + body;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        return FixedTimeEquals(expected, signature.ToLowerInvariant());
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
