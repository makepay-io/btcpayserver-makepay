#nullable enable
using System;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayPaymentMethodConfig
{
    public const string RefundAddressModeMerchantWallet = "merchant_wallet";
    public const string RefundAddressModePayerEntered = "payer_entered";

    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://www.makecrypto.io";
    public string CheckoutBaseUrl { get; set; } = "https://makepay.io";
    public bool RequestReceiptEmailFromCustomer { get; set; }
    public string? DefaultReceiptEmail { get; set; }
    public string RefundAddressMode { get; set; } = RefundAddressModeMerchantWallet;
    public string? AllowedAssetIdentifiers { get; set; }
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

    public string NormalizedApiBaseUrl() => NormalizeBaseUrl(ApiBaseUrl, "https://www.makecrypto.io");
    public string NormalizedCheckoutBaseUrl() => NormalizeBaseUrl(CheckoutBaseUrl, "https://makepay.io");
    public string NormalizedRefundAddressMode() =>
        string.Equals(RefundAddressMode, RefundAddressModePayerEntered, StringComparison.Ordinal)
            ? RefundAddressModePayerEntered
            : RefundAddressModeMerchantWallet;

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
}
