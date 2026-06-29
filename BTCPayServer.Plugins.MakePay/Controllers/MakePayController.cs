#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.Models;
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
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayApiClient _makePayApiClient;
    private readonly MakePaySecretProtector _secretProtector;

    public MakePayController(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient,
        MakePaySecretProtector secretProtector)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
        _secretProtector = secretProtector;
    }

    [HttpGet("")]
    public async Task<IActionResult> Configure(string storeId)
    {
        if (await _storeRepository.FindStore(storeId) is null)
        {
            return NotFound();
        }

        return RedirectToAction(nameof(Payments), new { storeId });
    }

    [HttpGet("general")]
    public Task<IActionResult> General(string storeId)
    {
        return Settings(storeId, "general", "MakePay Settings");
    }

    [HttpGet("currencies")]
    public Task<IActionResult> AllowedCurrencies(string storeId)
    {
        return Settings(storeId, "currencies", "MakePay Currencies");
    }

    [HttpGet("settlement")]
    public Task<IActionResult> Settlement(string storeId)
    {
        return Settings(storeId, "settlement", "MakePay Settlement");
    }

    private async Task<IActionResult> Settings(string storeId, string section, string title)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        ViewData["MakePaySection"] = section;
        ViewData["MakePayActivePage"] = "MakePay" + char.ToUpperInvariant(section[0]) + section[1..];
        ViewData["MakePayTitle"] = title;
        return View("Configure", GetConfig(store));
    }

    [HttpGet("api/currencies")]
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
        catch
        {
            return StatusCode(502, new { error = "Unable to load MakePay currencies." });
        }
    }

    [HttpPost("")]
    public Task<IActionResult> Configure(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command)
    {
        return SaveGeneral(storeId, posted, command);
    }

    [HttpPost("general")]
    public Task<IActionResult> SaveGeneral(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command)
    {
        return SaveSettings(storeId, posted, command, "general", nameof(General));
    }

    [HttpPost("currencies")]
    public Task<IActionResult> SaveAllowedCurrencies(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command)
    {
        return SaveSettings(storeId, posted, command, "currencies", nameof(AllowedCurrencies));
    }

    [HttpPost("settlement")]
    public Task<IActionResult> SaveSettlement(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command)
    {
        return SaveSettings(storeId, posted, command, "settlement", nameof(Settlement));
    }

    private async Task<IActionResult> SaveSettings(
        string storeId,
        MakePayPaymentMethodConfig posted,
        string command,
        string section,
        string actionName)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        var config = MergePostedConfig(GetConfig(store), posted, section);

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
                    config.LastError = SafeError(ex.Message);
                    await SaveConfig(store, config.ClearOAuthState());
                    SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay connection failed. Check the MakePay settings and try again.");
                    return RedirectToAction(nameof(General), new { storeId });
                }
            case "disconnect":
                config.ClearConnection();
                await SaveConfig(store, config);
                SetStatus(StatusMessageModel.StatusSeverity.Success, "MakePay disconnected.");
                return RedirectToAction(nameof(General), new { storeId });
            case "save":
                await SaveConfig(store, config);
                SetStatus(StatusMessageModel.StatusSeverity.Success, "MakePay settings saved.");
                return RedirectToAction(actionName, new { storeId });
            default:
                return await Settings(storeId, section, ViewTitle(section));
        }
    }

    [HttpGet("payments")]
    public async Task<IActionResult> Payments(
        string storeId,
        [FromQuery] string? searchTerm,
        [FromQuery] int skip = 0)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return NotFound();
        }

        const int take = 50;
        const int invoiceScanLimit = 500;
        skip = Math.Max(0, skip);
        var query = new InvoiceQuery
        {
            StoreId = [store.Id],
            IncludeArchived = true,
            TextSearch = NormalizeOptional(searchTerm),
            Take = invoiceScanLimit
        };
        var invoices = await _invoiceRepository.GetInvoices(query);
        var makePayPayments = invoices
            .SelectMany(invoice => invoice.GetPayments(false)
                .Where(payment => payment.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId)
                .Select(payment => ToPaymentListItem(invoice, payment)))
            .OrderByDescending(payment => payment.Created)
            .ToList();
        var items = makePayPayments
            .Skip(skip)
            .Take(take)
            .ToList();

        var model = new MakePayPaymentsViewModel
        {
            StoreId = store.Id,
            SearchTerm = NormalizeOptional(searchTerm),
            Skip = skip,
            Take = take,
            HasMore = makePayPayments.Count > skip + take,
            Payments = items
        };

        return View(model);
    }

    [HttpGet("payments/{paymentId}")]
    public async Task<IActionResult> PaymentDetails(string storeId, string paymentId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null || string.IsNullOrWhiteSpace(paymentId))
        {
            return NotFound();
        }

        var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [store.Id],
            IncludeArchived = true,
            TextSearch = paymentId,
            Take = 100
        });
        var payment = invoices
            .SelectMany(invoice => invoice.GetPayments(false)
                .Where(candidate =>
                    candidate.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId &&
                    string.Equals(candidate.Id, paymentId, StringComparison.Ordinal))
                .Select(candidate => ToPaymentListItem(invoice, candidate)))
            .FirstOrDefault();

        if (payment is null)
        {
            invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
            {
                StoreId = [store.Id],
                IncludeArchived = true,
                Take = 500
            });
            payment = invoices
                .SelectMany(invoice => invoice.GetPayments(false)
                    .Where(candidate =>
                        candidate.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId &&
                        string.Equals(candidate.Id, paymentId, StringComparison.Ordinal))
                    .Select(candidate => ToPaymentListItem(invoice, candidate)))
                .FirstOrDefault();
        }

        if (payment is null)
        {
            return NotFound();
        }

        var model = new MakePayPaymentDetailsViewModel
        {
            StoreId = store.Id,
            Payment = payment,
            ExplorerLinks = BuildExplorerLinks(payment)
        };

        return View(model);
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
            config.LastError = SafeError(error);
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay OAuth failed. Please try connecting again.");
            return RedirectToAction(nameof(General), new { storeId });
        }

        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(state) ||
            !config.HasPendingOAuth ||
            !string.Equals(config.OAuthState, state, StringComparison.Ordinal))
        {
            config.LastError = "Invalid OAuth state.";
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "Invalid MakePay OAuth state. Please try connecting again.");
            return RedirectToAction(nameof(General), new { storeId });
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
            config.LastError = SafeError(ex.Message);
            await SaveConfig(store, config.ClearOAuthState());
            SetStatus(StatusMessageModel.StatusSeverity.Error, "MakePay OAuth exchange failed. Please try connecting again.");
        }

        return RedirectToAction(nameof(General), new { storeId });
    }

    private async Task<IActionResult> Connect(StoreData store, MakePayPaymentMethodConfig config)
    {
        var dpop = MakePayDpopService.GenerateKeyPair();
        var siteUrl = config.NormalizedSiteUrl();
        if (string.IsNullOrWhiteSpace(siteUrl))
        {
            siteUrl = Request.GetAbsoluteRoot().TrimEnd('/');
        }
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

        if (string.IsNullOrWhiteSpace(config.SiteUrl))
        {
            config.SiteUrl = Request.GetAbsoluteRoot().TrimEnd('/');
        }

        return config;
    }

    private async Task SaveConfig(StoreData store, MakePayPaymentMethodConfig config)
    {
        config.NormalizeSettlement();
        var handler = _handlers[MakePayPlugin.MakePayPaymentMethodId];
        var storeBlob = store.GetStoreBlob();
        storeBlob.SetExcluded(handler.PaymentMethodId, !config.Enabled);
        store.SetStoreBlob(storeBlob);
        store.SetPaymentMethodConfig(handler, _secretProtector.Protect(config));
        await _storeRepository.UpdateStore(store);
    }

    private static MakePayPaymentMethodConfig MergePostedConfig(
        MakePayPaymentMethodConfig existing,
        MakePayPaymentMethodConfig posted,
        string section)
    {
        if (string.Equals(section, "general", StringComparison.OrdinalIgnoreCase))
        {
            existing.Enabled = posted.Enabled;
            existing.RequestReceiptEmailFromCustomer = posted.RequestReceiptEmailFromCustomer;
            existing.DefaultReceiptEmail = NormalizeOptional(posted.DefaultReceiptEmail);
            existing.DisplayQuoteApproval = posted.DisplayQuoteApproval;
            existing.RefundAddressMode = posted.NormalizedRefundAddressMode();
            existing.PaymentFeePayer = posted.NormalizedPaymentFeePayer();
            existing.AllowedPaymentVariancePercent = posted.NormalizedAllowedPaymentVariancePercent();
            existing.AllowedPaymentVarianceFixedUsd = posted.NormalizedAllowedPaymentVarianceFixedUsd();
            existing.MerchantSurchargePercent = posted.NormalizedMerchantSurchargePercent();
            existing.SiteUrl = NormalizeAbsoluteUrl(posted.SiteUrl) ?? existing.SiteUrl;
        }
        else if (string.Equals(section, "currencies", StringComparison.OrdinalIgnoreCase))
        {
            existing.AllowedAssetIdentifiers = NormalizeAllowedAssetIdentifiers(posted.AllowedAssetIdentifiers);
        }
        else if (string.Equals(section, "settlement", StringComparison.OrdinalIgnoreCase))
        {
            existing.SettlementCurrency = posted.NormalizedSettlementCurrency();
            existing.SettlementPrioritiesJson = NormalizeSettlementPrioritiesJson(posted.SettlementPrioritiesJson);
            existing.ChainAddressesJson = NormalizeChainAddressesJson(posted.ChainAddressesJson);
        }

        return existing;
    }

    private MakePayPaymentListItem ToPaymentListItem(InvoiceEntity invoice, PaymentEntity payment)
    {
        MakePayPaymentData details;
        if (_handlers.TryGet(MakePayPlugin.MakePayPaymentMethodId) is MakePayPaymentMethodHandler handler)
        {
            details = handler.ParsePaymentDetails(payment.Details) as MakePayPaymentData ?? new MakePayPaymentData();
        }
        else
        {
            details = new MakePayPaymentData();
        }

        return new MakePayPaymentListItem
        {
            PaymentId = payment.Id,
            InvoiceId = invoice.Id,
            OrderId = invoice.Metadata.OrderId,
            Created = payment.ReceivedTime,
            Status = payment.Status,
            Amount = payment.Value,
            Currency = payment.Currency,
            PaymentLinkUid = details.PaymentLinkUid,
            SessionId = details.SessionId,
            MakePayStatus = details.Status,
            SellAsset = details.SellAsset,
            BuyAsset = details.BuyAsset,
            RequiredSellAmount = details.RequiredSellAmount,
            SettlementAmount = details.SettlementAmount,
            SettlementClassification = details.SettlementClassification,
            DepositNetwork = details.DepositNetwork,
            DepositAddress = details.DepositAddress,
            PaymentRequest = details.PaymentRequest,
            TransactionIds = details.TransactionIds,
            DeliveryId = details.DeliveryId
        };
    }

    private static List<MakePayExplorerLink> BuildExplorerLinks(MakePayPaymentListItem payment)
    {
        var chain = NormalizeExplorerChain(payment.DepositNetwork) ??
                    NormalizeExplorerChain(payment.SellAsset) ??
                    NormalizeExplorerChain(payment.BuyAsset);
        if (chain is null || string.IsNullOrWhiteSpace(payment.TransactionIds))
        {
            return [];
        }

        return payment.TransactionIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tx => new { TransactionId = tx, Url = ExplorerUrl(chain, tx) })
            .Where(link => !string.IsNullOrWhiteSpace(link.Url))
            .Select(link => new MakePayExplorerLink
            {
                Label = chain,
                TransactionId = link.TransactionId,
                Url = link.Url!
            })
            .ToList();
    }

    private static string? NormalizeExplorerChain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var chain = value.Trim();
        if (chain.Contains('.', StringComparison.Ordinal))
        {
            chain = chain.Split('.', 2)[0];
        }

        chain = chain.ToUpperInvariant();
        return chain switch
        {
            "BITCOIN" => "BTC",
            "ETHEREUM" or "ERC20" => "ETH",
            "POLYGON" or "MATIC" => "POL",
            "ARBITRUM" => "ARB",
            "OPTIMISM" => "OP",
            "AVALANCHE" => "AVAX",
            "TRX" => "TRON",
            "BINANCE" or "BSC" => "BNB",
            _ => chain
        };
    }

    private static string? ExplorerUrl(string chain, string tx)
    {
        if (string.IsNullOrWhiteSpace(tx) || tx == "0x")
        {
            return null;
        }

        var escaped = Uri.EscapeDataString(tx.Trim());
        return chain switch
        {
            "BTC" => "https://mempool.space/tx/" + escaped,
            "ETH" => "https://etherscan.io/tx/" + escaped,
            "ARB" => "https://arbiscan.io/tx/" + escaped,
            "BASE" => "https://basescan.org/tx/" + escaped,
            "OP" => "https://optimistic.etherscan.io/tx/" + escaped,
            "POL" => "https://polygonscan.com/tx/" + escaped,
            "BNB" => "https://bscscan.com/tx/" + escaped,
            "AVAX" => "https://snowtrace.io/tx/" + escaped,
            "TRON" => "https://tronscan.org/#/transaction/" + escaped,
            "SOL" => "https://solscan.io/tx/" + escaped,
            "LTC" => "https://blockchair.com/litecoin/transaction/" + escaped,
            "BCH" => "https://blockchair.com/bitcoin-cash/transaction/" + escaped,
            "DOGE" => "https://blockchair.com/dogecoin/transaction/" + escaped,
            "DASH" => "https://blockchair.com/dash/transaction/" + escaped,
            "ZEC" => "https://blockchair.com/zcash/transaction/" + escaped,
            "XRP" => "https://xrpscan.com/tx/" + escaped,
            "TON" => "https://tonscan.org/tx/" + escaped,
            _ => null
        };
    }

    private static string ViewTitle(string section)
    {
        return section switch
        {
            "currencies" => "MakePay Currencies",
            "settlement" => "MakePay Settlement",
            _ => "MakePay General"
        };
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

    private static string SafeError(string? value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value)
            ? "MakePay request failed."
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
        return sanitized.Length <= 256 ? sanitized : sanitized[..256];
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

    private static string? NormalizeSettlementPrioritiesJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var parsed = JArray.Parse(value);
            return MakePayPaymentMethodConfig.SerializeSettlementPriorities(
                MakePayPaymentMethodConfig.NormalizeSettlementPriorities(parsed));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeChainAddressesJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return MakePayPaymentMethodConfig.SerializeChainAddresses(
                MakePayPaymentMethodConfig.NormalizeChainAddresses(JToken.Parse(value)));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeAbsoluteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var isLocalHttp =
            uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host == "localhost" || uri.Host == "127.0.0.1");
        if (uri.Scheme != Uri.UriSchemeHttps && !isLocalHttp)
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
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
