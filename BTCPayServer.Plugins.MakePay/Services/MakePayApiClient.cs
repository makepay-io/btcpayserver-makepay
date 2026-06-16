#nullable enable
using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Services;

public class MakePayApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MakePayApiClient> _logger;

    public MakePayApiClient(HttpClient httpClient, ILogger<MakePayApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JObject> RegisterNativeInstallation(
        MakePayPaymentMethodConfig config,
        string siteUrl,
        string redirectUri,
        string dpopJkt,
        string? btcpayVersion,
        CancellationToken cancellationToken = default)
    {
        var url = config.NormalizedApiBaseUrl() + "/oauth/native/installations";
        var body = new JObject
        {
            ["platform"] = "btcpay-server",
            ["siteUrl"] = siteUrl,
            ["redirectUri"] = redirectUri,
            ["dpopJkt"] = dpopJkt,
            ["pluginVersion"] = MakePayPlugin.PluginVersion,
            ["btcpayServerVersion"] = btcpayVersion
        };

        return await SendJson(url, body, cancellationToken);
    }

    public async Task<JObject> ExchangeCode(
        MakePayPaymentMethodConfig config,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) ||
            string.IsNullOrWhiteSpace(config.OAuthRedirectUri) ||
            string.IsNullOrWhiteSpace(config.OAuthCodeVerifier) ||
            string.IsNullOrWhiteSpace(config.DpopPrivateKeyPem))
        {
            throw new InvalidOperationException("MakePay OAuth registration is incomplete.");
        }

        var url = config.NormalizedApiBaseUrl() + "/oauth/token";
        var form = new FormUrlEncodedContent(
        [
            new("grant_type", "authorization_code"),
            new("client_id", config.ClientId),
            new("code", code),
            new("redirect_uri", config.OAuthRedirectUri),
            new("code_verifier", config.OAuthCodeVerifier)
        ]);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.TryAddWithoutValidation(
            "DPoP",
            MakePayDpopService.CreateProof(config.DpopPrivateKeyPem, "POST", url));

        return await Send(request, cancellationToken);
    }

    public async Task RefreshAccessToken(
        MakePayPaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) ||
            string.IsNullOrWhiteSpace(config.RefreshToken) ||
            string.IsNullOrWhiteSpace(config.DpopPrivateKeyPem))
        {
            throw new InvalidOperationException("MakePay OAuth refresh credentials are incomplete.");
        }

        var url = config.NormalizedApiBaseUrl() + "/oauth/token";
        var form = new FormUrlEncodedContent(
        [
            new("grant_type", "refresh_token"),
            new("client_id", config.ClientId),
            new("refresh_token", config.RefreshToken)
        ]);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.TryAddWithoutValidation(
            "DPoP",
            MakePayDpopService.CreateProof(config.DpopPrivateKeyPem, "POST", url));

        var token = await Send(request, cancellationToken);
        ApplyTokenResponse(config, token);
    }

    public async Task<JObject> RotateWebhookSecret(
        MakePayPaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        var url = config.NormalizedApiBaseUrl() + "/api/partner/v1/makepay/webhook-secret";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        return await SendAuthenticated(config, request, cancellationToken);
    }

    public async Task<MakePayPaymentLinkResponse> CreatePaymentLink(
        MakePayPaymentMethodConfig config,
        string storeId,
        string invoiceId,
        decimal btcAmount,
        string settlementAddress,
        string webhookUrl,
        string invoiceCheckoutUrl,
        CancellationToken cancellationToken = default)
    {
        var url = config.NormalizedApiBaseUrl() + "/api/partner/v1/makepay/payment-links";
        var amount = btcAmount.ToString("0.########", CultureInfo.InvariantCulture);
        var body = new JObject
        {
            ["source"] = "btcpay-server",
            ["status"] = "active",
            ["payload"] = new JObject
            {
                ["amount"] = amount,
                ["currency"] = "BTC",
                ["asset"] = "BTC.BTC",
                ["settlementCurrency"] = "BTC",
                ["label"] = "BTCPay invoice " + invoiceId,
                ["description"] = "BTCPay Server invoice " + invoiceId,
                ["expirationTime"] = "never",
                ["webhookUrl"] = webhookUrl,
                ["returnRedirectUrl"] = invoiceCheckoutUrl,
                ["successRedirectUrl"] = invoiceCheckoutUrl,
                ["failureRedirectUrl"] = invoiceCheckoutUrl,
                ["metadata"] = new JObject
                {
                    ["source"] = "btcpay-server",
                    ["btcpayStoreId"] = storeId,
                    ["btcpayInvoiceId"] = invoiceId
                },
                ["receiptEmail"] = config.RequestReceiptEmailFromCustomer
                    ? null
                    : NullIfWhiteSpace(config.DefaultReceiptEmail)
            },
            ["settlement"] = new JObject
            {
                ["currency"] = "BTC",
                ["priorities"] = new JArray
                {
                    new JObject
                    {
                        ["chain"] = "BTC",
                        ["asset"] = "BTC.BTC",
                        ["address"] = settlementAddress
                    }
                }
            }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        var response = await SendAuthenticated(config, request, cancellationToken);
        var paymentLink = response["paymentLink"] as JObject;
        var uid = paymentLink?["uid"]?.Value<string>();
        var publicUrl = paymentLink?["publicUrl"]?.Value<string>();

        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(publicUrl))
        {
            throw new InvalidOperationException("MakePay did not return a payment link uid and public URL.");
        }

        return new MakePayPaymentLinkResponse(uid, publicUrl);
    }

    public async Task<JObject?> GetCurrentSession(
        MakePayPaymentMethodConfig config,
        string paymentLinkUid,
        CancellationToken cancellationToken = default)
    {
        var url = config.NormalizedCheckoutBaseUrl() +
                  "/api/public/payment-links/" +
                  Uri.EscapeDataString(paymentLinkUid) +
                  "/current-session";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            return await Send(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load MakePay current session for {PaymentLinkUid}", paymentLinkUid);
            return null;
        }
    }

    public async Task<JToken> SendPublic(
        MakePayPaymentMethodConfig config,
        HttpMethod method,
        string pathAndQuery,
        JToken? body = null,
        CancellationToken cancellationToken = default)
    {
        var url = config.NormalizedCheckoutBaseUrl().TrimEnd('/') +
                  (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToString(Formatting.None),
                Encoding.UTF8,
                "application/json");
        }

        return await SendToken(request, cancellationToken);
    }

    public static void ApplyTokenResponse(MakePayPaymentMethodConfig config, JObject token)
    {
        var accessToken = token["access_token"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("MakePay token response did not include an access token.");
        }

        config.AccessToken = accessToken;
        config.RefreshToken = token["refresh_token"]?.Value<string>() ?? config.RefreshToken;
        config.AccessTokenExpiresAt =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
            Math.Max(60, token["expires_in"]?.Value<int?>() ?? 3600);

        var claims = MakePayDpopService.DecodeJwtPayload(accessToken);
        config.CompanyId = claims["company_id"]?.Value<string>() ?? config.CompanyId;
    }

    private async Task<JObject> SendAuthenticated(
        MakePayPaymentMethodConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await EnsureAccessToken(config, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", config.AccessToken);
        request.Headers.TryAddWithoutValidation(
            "DPoP",
            MakePayDpopService.CreateProof(
                config.DpopPrivateKeyPem!,
                request.Method.Method,
                request.RequestUri!.ToString(),
                config.AccessToken));

        return await Send(request, cancellationToken);
    }

    private async Task EnsureAccessToken(
        MakePayPaymentMethodConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.AccessToken) &&
            config.AccessTokenExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
        {
            return;
        }

        await RefreshAccessToken(config, cancellationToken);
    }

    private async Task<JObject> SendJson(
        string url,
        JObject body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        return await Send(request, cancellationToken);
    }

    private async Task<JObject> Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await SendToken(request, cancellationToken);
        if (token is JObject obj)
        {
            return obj;
        }

        throw new InvalidOperationException("MakePay returned an unexpected JSON payload.");
    }

    private async Task<JToken> SendToken(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.ParseAdd("MakePayBTCPayServer/" + MakePayPlugin.PluginVersion);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = string.IsNullOrWhiteSpace(body) ? new JObject() : JToken.Parse(body);

        if (!response.IsSuccessStatusCode)
        {
            var message = json is JObject errorObj
                ? errorObj["error"]?.Value<string>()
                : null;
            message ??= $"MakePay request failed with HTTP {(int)response.StatusCode}.";
            throw new InvalidOperationException(message);
        }

        return json;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed record MakePayPaymentLinkResponse(string Uid, string PublicUrl);
