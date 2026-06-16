#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/makepay")]
public class MakePayController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayApiClient _makePayApiClient;
    private readonly MakePaySecretProtector _secretProtector;

    public MakePayController(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient,
        MakePaySecretProtector secretProtector)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
        _secretProtector = secretProtector;
    }

    [HttpGet("")]
    public async Task<IActionResult> Configure(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        return View(GetConfig(store));
    }

    [HttpGet("currencies")]
    public async Task<IActionResult> Currencies(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        try
        {
            var tokens = await _makePayApiClient.SendPublic(
                GetConfig(store),
                HttpMethod.Get,
                "/api/swap/tokens");
            return Content(tokens.ToString(Formatting.None), "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    [HttpPost("")]
    public async Task<IActionResult> Configure(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        var config = MergePostedConfig(GetConfig(store), posted);

        switch ((command ?? string.Empty).ToLowerInvariant())
        {
            case "connect":
                try
                {
                    return await Connect(store, config);
                }
                catch (Exception ex)
                {
                    config.ClientId = null;
                    config.DpopPrivateKeyPem = null;
                    config.DpopJkt = null;
                    config.LastError = ex.Message;
                    await SaveConfig(store, config.ClearOAuthState());
                    SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay connection failed: " + ex.Message);
                    return RedirectToAction(nameof(Configure), new { storeId });
                }
            case "disconnect":
                config.ClearConnection();
                await SaveConfig(store, config);
                SetStatus(StatusMessageModel.StatusSeverity.Success, "MakePay disconnected.");
                return RedirectToAction(nameof(Configure), new { storeId });
            case "save":
                await SaveConfig(store, config);
                SetStatus(StatusMessageModel.StatusSeverity.Success, "MakePay settings saved.");
                return RedirectToAction(nameof(Configure), new { storeId });
            default:
                return View(config);
        }
    }

    [HttpGet("oauth/callback")]
    [HttpPost("oauth/callback")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OAuthCallback(
        string storeId,
        string? code,
        string? state,
        string? error)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        var config = GetConfig(store);
        if (!string.IsNullOrWhiteSpace(error))
        {
            config.LastError = error;
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay OAuth failed: " + error);
            return RedirectToAction(nameof(Configure), new { storeId });
        }

        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(state) ||
            !config.HasPendingOAuth ||
            !string.Equals(config.OAuthState, state, StringComparison.Ordinal))
        {
            config.LastError = "Invalid OAuth state.";
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "Invalid MakePay OAuth state. Please try connecting again.");
            return RedirectToAction(nameof(Configure), new { storeId });
        }

        try
        {
            var token = await _makePayApiClient.ExchangeCode(config, code);
            MakePayApiClient.ApplyTokenResponse(config, token);
            var secretResponse = await _makePayApiClient.RotateWebhookSecret(config);
            var webhookSecret = secretResponse.SelectToken("webhookSecret.secret")?.Value<string>();
            if (string.IsNullOrWhiteSpace(webhookSecret))
            {
                throw new InvalidOperationException("MakePay did not return a webhook signing secret.");
            }

            config.WebhookSecret = webhookSecret;
            config.WebhookSecretLast4 =
                secretResponse.SelectToken("webhookSecret.last4")?.Value<string>() ??
                webhookSecret[^Math.Min(4, webhookSecret.Length)..];
            config.Enabled = true;
            config.LastError = null;
            config.ClearOAuthState();
            await SaveConfig(store, config);

            SetStatus(StatusMessageModel.StatusSeverity.Success, "MakePay connected.");
        }
        catch (Exception ex)
        {
            config.LastError = ex.Message;
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay OAuth exchange failed: " + ex.Message);
        }

        return RedirectToAction(nameof(Configure), new { storeId });
    }

    private async Task<IActionResult> Connect(StoreData store, MakePayPaymentMethodConfig config)
    {
        var dpop = MakePayDpopService.GenerateKeyPair();
        var siteUrl = Request.GetAbsoluteRoot().TrimEnd('/');
        var redirectUri = siteUrl + "/plugins/" + Uri.EscapeDataString(store.Id) + "/makepay/oauth/callback";
        var state = MakePayDpopService.RandomToken(32);
        var verifier = MakePayDpopService.RandomToken(64);

        config.SiteUrl = siteUrl;
        config.DpopPrivateKeyPem = dpop.PrivateKeyPem;
        config.DpopJkt = dpop.Thumbprint;
        config.OAuthState = state;
        config.OAuthCodeVerifier = verifier;
        config.OAuthRedirectUri = redirectUri;
        config.OAuthExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        config.LastError = null;

        var registration = await _makePayApiClient.RegisterNativeInstallation(
            config,
            siteUrl,
            redirectUri,
            dpop.Thumbprint,
            null);
        config.ClientId = registration["client_id"]?.Value<string>() ??
                          throw new InvalidOperationException("MakePay did not return a client id.");

        await SaveConfig(store, config);

        var authorizeUrl = BuildAuthorizeUrl(
            config.NormalizedApiBaseUrl(),
            new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = "company:read makepay:payment-links:read makepay:payment-links:write makepay:settings:read makepay:settings:write",
                ["code_challenge"] = MakePayDpopService.CodeChallenge(verifier),
                ["code_challenge_method"] = "S256",
                ["dpop_jkt"] = dpop.Thumbprint,
                ["state"] = state
            });

        return Redirect(authorizeUrl);
    }

    private MakePayPaymentMethodConfig GetConfig(StoreData store)
    {
        var config = store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
            MakePayPlugin.MakePayPaymentMethodId,
            _handlers) ?? new MakePayPaymentMethodConfig();
        config = _secretProtector.Unprotect(config);

        if (string.IsNullOrWhiteSpace(config.DefaultReceiptEmail))
        {
            config.DefaultReceiptEmail = CurrentUserEmail();
        }

        return config;
    }

    private async Task SaveConfig(StoreData store, MakePayPaymentMethodConfig config)
    {
        var handler = _handlers[MakePayPlugin.MakePayPaymentMethodId];
        store.SetPaymentMethodConfig(handler, _secretProtector.Protect(config));
        await _storeRepository.UpdateStore(store);
    }

    private static MakePayPaymentMethodConfig MergePostedConfig(
        MakePayPaymentMethodConfig existing,
        MakePayPaymentMethodConfig posted)
    {
        existing.Enabled = posted.Enabled;
        existing.RequestReceiptEmailFromCustomer = posted.RequestReceiptEmailFromCustomer;
        existing.DefaultReceiptEmail = NormalizeOptional(posted.DefaultReceiptEmail);
        existing.RefundAddressMode = posted.NormalizedRefundAddressMode();
        existing.AllowedAssetIdentifiers = NormalizeAllowedAssetIdentifiers(posted.AllowedAssetIdentifiers);
        return existing;
    }

    private string? CurrentUserEmail()
    {
        var email =
            User.FindFirst(ClaimTypes.Email)?.Value ??
            User.FindFirst("email")?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email.Trim();
        }

        var name = User.Identity?.Name;
        return !string.IsNullOrWhiteSpace(name) && name.Contains('@')
            ? name.Trim()
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeAllowedAssetIdentifiers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(
            new[] { ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    private void SetStatus(StatusMessageModel.StatusSeverity severity, string message)
    {
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = severity,
            Message = message
        });
    }

    private static string BuildAuthorizeUrl(
        string apiBaseUrl,
        IReadOnlyDictionary<string, string?> query)
    {
        var pairs = new List<string>();
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        }

        return apiBaseUrl.TrimEnd('/') + "/oauth/authorize?" + string.Join("&", pairs);
    }
}
