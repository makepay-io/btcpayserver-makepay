#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NicolasDorier.RateLimits;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Controllers;

[Route("api/plugins/makepay/{storeId}/{invoiceId}")]
[ApiController]
public class MakePayCheckoutController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayApiClient _makePayApiClient;
    private readonly MakePaySecretProtector _secretProtector;
    private readonly ILogger<MakePayCheckoutController> _logger;

    public MakePayCheckoutController(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient,
        MakePaySecretProtector secretProtector,
        ILogger<MakePayCheckoutController> logger)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    [HttpGet("tokens")]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> Tokens(string storeId, string invoiceId)
    {
        var resolved = await Resolve(storeId, invoiceId);
        if (resolved is null)
        {
            return NotFound();
        }

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Get,
            "/api/swap/tokens?paymentLinkUid=" + Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid)));
    }

    [HttpPost("quote")]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    [RequestSizeLimit(MakePayCheckoutPolicy.MaxJsonBodyBytes)]
    public async Task<IActionResult> Quote(string storeId, string invoiceId)
    {
        var resolved = await Resolve(storeId, invoiceId);
        if (resolved is null)
        {
            return NotFound();
        }

        var body = await ReadJsonBody();
        if (body is null)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        var validationError = MakePayCheckoutPolicy.ValidatePaymentRequestBody(body);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var paymentBody = MakePayCheckoutPolicy.ApplyCheckoutRequestPolicy(
            resolved.Config,
            resolved.Prompt,
            body);

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Post,
            "/api/public/payment-links/" + Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid) + "/quote-payin",
            paymentBody));
    }

    [HttpPost("start")]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    [RequestSizeLimit(MakePayCheckoutPolicy.MaxJsonBodyBytes)]
    public async Task<IActionResult> Start(string storeId, string invoiceId)
    {
        var resolved = await Resolve(storeId, invoiceId);
        if (resolved is null)
        {
            return NotFound();
        }

        var body = await ReadJsonBody();
        if (body is null)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        var validationError = MakePayCheckoutPolicy.ValidatePaymentRequestBody(body);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var paymentBody = MakePayCheckoutPolicy.ApplyCheckoutRequestPolicy(
            resolved.Config,
            resolved.Prompt,
            body);

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Post,
            "/api/public/payment-links/" + Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid) + "/start-payment",
            paymentBody));
    }

    [HttpGet("status")]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> Status(string storeId, string invoiceId, [FromQuery] string sessionId)
    {
        var resolved = await Resolve(storeId, invoiceId);
        if (resolved is null)
        {
            return NotFound();
        }

        if (!MakePayCheckoutPolicy.IsValidSessionId(sessionId))
        {
            return BadRequest(new { error = "Invalid sessionId." });
        }

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Get,
            "/api/public/payment-links/" +
            Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid) +
            "/payment-status?sessionId=" +
            Uri.EscapeDataString(sessionId)));
    }

    private async Task<ResolvedCheckout?> Resolve(string storeId, string invoiceId)
    {
        var store = await _storeRepository.FindStore(storeId);
        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        if (store is null || invoice is null || invoice.StoreId != store.Id)
        {
            return null;
        }

        var config = _secretProtector.Unprotect(store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
            MakePayPlugin.MakePayPaymentMethodId,
            _handlers) ?? new MakePayPaymentMethodConfig());
        if (!config.Enabled)
        {
            return null;
        }

        if (!MakePayCheckoutPolicy.IsInvoicePayable(invoice))
        {
            return null;
        }

        if (!_handlers.TryGetValue(MakePayPlugin.MakePayPaymentMethodId, out var handler))
        {
            return null;
        }

        var prompt = invoice.GetPaymentPrompt(MakePayPlugin.MakePayPaymentMethodId);
        if (prompt is null)
        {
            return null;
        }

        var details = (MakePayPromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
        if (!string.IsNullOrWhiteSpace(details.CheckoutBaseUrl))
        {
            config.CheckoutBaseUrl = details.CheckoutBaseUrl;
        }

        return new ResolvedCheckout(config, details);
    }

    private async Task<JToken?> ReadJsonBody()
    {
        if (Request.ContentLength > MakePayCheckoutPolicy.MaxJsonBodyBytes)
        {
            return null;
        }

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        if (body.Length > MakePayCheckoutPolicy.MaxJsonBodyBytes)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new JObject();
        }

        try
        {
            return JToken.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IActionResult> Proxy(Func<Task<JToken>> send)
    {
        try
        {
            return JsonContent(await send());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MakePay public checkout proxy failed.");
            return StatusCode(502, new { error = "MakePay checkout is temporarily unavailable." });
        }
    }

    private ContentResult JsonContent(JToken payload)
    {
        return Content(payload.ToString(Formatting.None), "application/json");
    }

    private sealed record ResolvedCheckout(
        MakePayPaymentMethodConfig Config,
        MakePayPromptDetails Prompt);
}
