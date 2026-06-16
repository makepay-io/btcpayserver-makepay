#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Services;

public static class MakePayDpopService
{
    public sealed record DpopKeyPair(string PrivateKeyPem, string PublicJwkJson, string Thumbprint);

    public static DpopKeyPair GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicJwk = PublicJwkFromKey(key);
        return new DpopKeyPair(
            key.ExportECPrivateKeyPem(),
            publicJwk.ToString(Newtonsoft.Json.Formatting.None),
            Thumbprint(publicJwk));
    }

    public static string CreateProof(
        string privateKeyPem,
        string method,
        string url,
        string? accessToken = null)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var publicJwk = PublicJwkFromKey(key);
        var header = new JObject
        {
            ["typ"] = "dpop+jwt",
            ["alg"] = "ES256",
            ["jwk"] = publicJwk
        };
        var payload = new JObject
        {
            ["htu"] = NormalizeUrl(url),
            ["htm"] = method.ToUpperInvariant(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString()
        };

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            payload["ath"] = Base64UrlEncode(
                SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        }

        var signingInput =
            Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None))) +
            "." +
            Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return signingInput + "." + Base64UrlEncode(signature);
    }

    public static string CodeChallenge(string verifier)
    {
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
    }

    public static string RandomToken(int bytes = 32)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
    }

    public static JObject DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return new JObject();
        }

        var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        return JObject.Parse(json);
    }

    private static JObject PublicJwkFromKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return new JObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64UrlEncode(parameters.Q.X ?? []),
            ["y"] = Base64UrlEncode(parameters.Q.Y ?? [])
        };
    }

    private static string Thumbprint(JObject publicJwk)
    {
        var canonical = new JObject
        {
            ["crv"] = publicJwk["crv"],
            ["kty"] = publicJwk["kty"],
            ["x"] = publicJwk["x"],
            ["y"] = publicJwk["y"]
        }.ToString(Newtonsoft.Json.Formatting.None);

        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
