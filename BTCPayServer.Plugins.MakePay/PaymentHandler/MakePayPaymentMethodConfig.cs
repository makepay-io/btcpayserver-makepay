#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayPaymentMethodConfig
{
    public const string RefundAddressModeMerchantWallet = "merchant_wallet";
    public const string RefundAddressModePayerEntered = "payer_entered";
    public const string PaymentFeePayerMerchant = "merchant";
    public const string PaymentFeePayerCustomer = "customer";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://www.makecrypto.io";
    public string CheckoutBaseUrl { get; set; } = "https://makepay.io";
    public bool RequestReceiptEmailFromCustomer { get; set; }
    public string? DefaultReceiptEmail { get; set; }
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public bool DisplayQuoteApproval { get; set; } = true;
    public string RefundAddressMode { get; set; } = RefundAddressModeMerchantWallet;
    public string PaymentFeePayer { get; set; } = PaymentFeePayerCustomer;
    public decimal AllowedPaymentVariancePercent { get; set; } = 1m;
    public decimal AllowedPaymentVarianceFixedUsd { get; set; }
    public decimal MerchantSurchargePercent { get; set; }
    public string? AllowedAssetIdentifiers { get; set; }
    public string SettlementCurrency { get; set; } = "BTC";
    public string? SettlementPrioritiesJson { get; set; }
    public string? ChainAddressesJson { get; set; }
    public string? SiteUrl { get; set; }
    public string? ClientId { get; set; }
    public string? CompanyId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public long AccessTokenExpiresAt { get; set; }
    public string? DpopPrivateKeyPem { get; set; }
    public string? DpopJkt { get; set; }
    public string? WebhookSecret { get; set; }
    public string? WebhookSecretLast4 { get; set; }
    public string? OAuthState { get; set; }
    public string? OAuthCodeVerifier { get; set; }
    public string? OAuthRedirectUri { get; set; }
    public long OAuthExpiresAt { get; set; }
    public string? LastError { get; set; }
    public bool SecretsProtected { get; set; }

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(RefreshToken) &&
        !string.IsNullOrWhiteSpace(DpopPrivateKeyPem) &&
        !string.IsNullOrWhiteSpace(WebhookSecret);

    [JsonIgnore]
    public bool HasPendingOAuth =>
        !string.IsNullOrWhiteSpace(OAuthState) &&
        !string.IsNullOrWhiteSpace(OAuthCodeVerifier) &&
        !string.IsNullOrWhiteSpace(OAuthRedirectUri) &&
        OAuthExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonIgnore]
    public bool HasAnonymousSettlement =>
        !IsConfigured &&
        ResolveSettlementPriorities().Count > 0;

    public string NormalizedApiBaseUrl() => NormalizeBaseUrl(ApiBaseUrl, "https://www.makecrypto.io");
    public string NormalizedCheckoutBaseUrl() => NormalizeBaseUrl(CheckoutBaseUrl, "https://makepay.io");
    public string NormalizedSiteUrl() => NormalizeBaseUrl(SiteUrl, string.Empty);
    public string NormalizedRefundAddressMode() =>
        string.Equals(RefundAddressMode, RefundAddressModePayerEntered, StringComparison.Ordinal)
            ? RefundAddressModePayerEntered
            : RefundAddressModeMerchantWallet;
    public string NormalizedPaymentFeePayer() =>
        string.Equals(PaymentFeePayer, PaymentFeePayerMerchant, StringComparison.Ordinal)
            ? PaymentFeePayerMerchant
            : PaymentFeePayerCustomer;
    public decimal NormalizedAllowedPaymentVariancePercent() => Clamp(AllowedPaymentVariancePercent, 0m, 100m);
    public decimal NormalizedAllowedPaymentVarianceFixedUsd() => Clamp(AllowedPaymentVarianceFixedUsd, 0m, 1000000m);
    public decimal NormalizedMerchantSurchargePercent() => Clamp(MerchantSurchargePercent, -1m, 1m);
    public string NormalizedSettlementCurrency() => NormalizeSymbol(SettlementCurrency) ?? "BTC";

    public IReadOnlyList<MakePaySettlementPriority> GetSettlementPriorities()
    {
        if (string.IsNullOrWhiteSpace(SettlementPrioritiesJson))
        {
            return [];
        }

        try
        {
            return NormalizeSettlementPriorities(JArray.Parse(SettlementPrioritiesJson));
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<MakePaySettlementPriority> ResolveSettlementPriorities()
    {
        var configured = GetSettlementPriorities();
        if (configured.Count > 0)
        {
            return configured;
        }

        var currency = NormalizedSettlementCurrency();
        var addresses = GetChainAddresses();
        var primary = currency switch
        {
            "BTC" => addresses.FirstOrDefault(address =>
                string.Equals(address.Chain, "BTC", StringComparison.OrdinalIgnoreCase)),
            _ => addresses.FirstOrDefault(address =>
                string.Equals(address.Chain, currency, StringComparison.OrdinalIgnoreCase))
        };

        if (primary is null)
        {
            return [];
        }

        return
        [
            new MakePaySettlementPriority
            {
                Chain = primary.Chain,
                Address = primary.Address,
                Asset = currency == "BTC" && primary.Chain == "BTC" ? "BTC.BTC" : null
            }
        ];
    }

    public IReadOnlyList<MakePayChainAddress> GetChainAddresses()
    {
        if (string.IsNullOrWhiteSpace(ChainAddressesJson))
        {
            return [];
        }

        try
        {
            var parsed = JToken.Parse(ChainAddressesJson);
            return NormalizeChainAddresses(parsed);
        }
        catch
        {
            return [];
        }
    }

    public void NormalizeSettlement()
    {
        PaymentFeePayer = NormalizedPaymentFeePayer();
        AllowedPaymentVariancePercent = NormalizedAllowedPaymentVariancePercent();
        AllowedPaymentVarianceFixedUsd = NormalizedAllowedPaymentVarianceFixedUsd();
        MerchantSurchargePercent = NormalizedMerchantSurchargePercent();
        SettlementCurrency = NormalizedSettlementCurrency();
        ChainAddressesJson = SerializeChainAddresses(GetChainAddresses());
        SettlementPrioritiesJson = SerializeSettlementPriorities(ResolveSettlementPriorities());
    }

    public MakePayPaymentMethodConfig ClearOAuthState()
    {
        OAuthState = null;
        OAuthCodeVerifier = null;
        OAuthRedirectUri = null;
        OAuthExpiresAt = 0;
        return this;
    }

    public MakePayPaymentMethodConfig ClearConnection()
    {
        Enabled = false;
        ClientId = null;
        CompanyId = null;
        AccessToken = null;
        RefreshToken = null;
        AccessTokenExpiresAt = 0;
        DpopPrivateKeyPem = null;
        DpopJkt = null;
        WebhookSecret = null;
        WebhookSecretLast4 = null;
        SecretsProtected = false;
        LastError = null;
        return ClearOAuthState();
    }

    public static string? SerializeSettlementPriorities(
        IReadOnlyCollection<MakePaySettlementPriority> priorities)
    {
        var normalized = NormalizeSettlementPriorities(JArray.FromObject(priorities));
        return normalized.Count == 0
            ? null
            : JArray.FromObject(normalized).ToString(Formatting.None);
    }

    public static string? SerializeChainAddresses(
        IReadOnlyCollection<MakePayChainAddress> addresses)
    {
        var normalized = NormalizeChainAddresses(JArray.FromObject(addresses));
        return normalized.Count == 0
            ? null
            : JArray.FromObject(normalized).ToString(Formatting.None);
    }

    public static IReadOnlyList<MakePaySettlementPriority> NormalizeSettlementPriorities(JArray array)
    {
        var output = new List<MakePaySettlementPriority>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in array.OfType<JObject>())
        {
            if (output.Count >= 2)
            {
                break;
            }

            var chain = NormalizeSymbol(
                token["chain"]?.Value<string>() ??
                token["Chain"]?.Value<string>() ??
                token["chainCode"]?.Value<string>() ??
                token["ChainCode"]?.Value<string>());
            var address = (token["address"]?.Value<string>() ??
                           token["Address"]?.Value<string>())?.Trim();
            var asset = NormalizeAssetIdentifier(
                token["asset"]?.Value<string>() ??
                token["Asset"]?.Value<string>() ??
                token["assetIdentifier"]?.Value<string>() ??
                token["AssetIdentifier"]?.Value<string>());

            if (chain is null ||
                string.IsNullOrWhiteSpace(address) ||
                address.Length > 256)
            {
                continue;
            }

            var key = asset ?? chain;
            if (!seen.Add(key))
            {
                continue;
            }

            output.Add(new MakePaySettlementPriority
            {
                Chain = chain,
                Address = address,
                Asset = asset
            });
        }

        return output;
    }

    public static IReadOnlyList<MakePayChainAddress> NormalizeChainAddresses(JToken token)
    {
        var items = new List<JObject>();

        if (token is JArray array)
        {
            items.AddRange(array.OfType<JObject>());
        }
        else if (token is JObject map)
        {
            foreach (var property in map.Properties())
            {
                if (property.Value.Type == JTokenType.String)
                {
                    items.Add(new JObject
                    {
                        ["chain"] = property.Name,
                        ["address"] = property.Value.Value<string>()
                    });
                }
                else if (property.Value is JObject value)
                {
                    items.Add(new JObject
                    {
                        ["chain"] = property.Name,
                        ["address"] =
                            value["address"]?.Value<string>() ??
                            value["Address"]?.Value<string>() ??
                            value["sourceAddress"]?.Value<string>() ??
                            value["SourceAddress"]?.Value<string>()
                    });
                }
            }
        }

        var output = new List<MakePayChainAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var chain = NormalizeSymbol(
                item["chain"]?.Value<string>() ??
                item["Chain"]?.Value<string>() ??
                item["chainCode"]?.Value<string>() ??
                item["ChainCode"]?.Value<string>());
            var address = (item["address"]?.Value<string>() ??
                           item["Address"]?.Value<string>() ??
                           item["sourceAddress"]?.Value<string>() ??
                           item["SourceAddress"]?.Value<string>())?.Trim();

            if (chain is null ||
                string.IsNullOrWhiteSpace(address) ||
                address.Length > 256 ||
                !seen.Add(chain))
            {
                continue;
            }

            output.Add(new MakePayChainAddress
            {
                Chain = chain,
                Address = address
            });
        }

        return output;
    }

    private static string? NormalizeSymbol(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(normalized) &&
               Regex.IsMatch(normalized, "^[A-Z0-9_]{2,24}$")
            ? normalized
            : null;
    }

    private static string? NormalizeAssetIdentifier(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 160 &&
               Regex.IsMatch(normalized, "^[A-Za-z0-9_.:-]+$")
            ? normalized
            : null;
    }

    private static string NormalizeBaseUrl(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return fallback;
        }

        var isLocalHttp =
            uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host == "localhost" || uri.Host == "127.0.0.1");
        if (uri.Scheme != Uri.UriSchemeHttps && !isLocalHttp)
        {
            return fallback;
        }

        return uri.ToString().TrimEnd('/');
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}

public class MakePaySettlementPriority
{
    public string Chain { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Asset { get; set; }
}

public class MakePayChainAddress
{
    public string Chain { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}
