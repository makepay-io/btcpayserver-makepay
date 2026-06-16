#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
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

    public MakePayCheckoutController(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
    }

    [HttpGet("tokens")]
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

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Post,
            "/api/public/payment-links/" + Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid) + "/quote-payin",
            body));
    }

    [HttpPost("start")]
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

        return await Proxy(() => _makePayApiClient.SendPublic(
            resolved.Config,
            HttpMethod.Post,
            "/api/public/payment-links/" + Uri.EscapeDataString(resolved.Prompt.PaymentLinkUid) + "/start-payment",
            body));
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(string storeId, string invoiceId, [FromQuery] string sessionId)
    {
        var resolved = await Resolve(storeId, invoiceId);
        if (resolved is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "Missing required query: sessionId" });
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

        var config = store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
            MakePayPlugin.MakePayPaymentMethodId,
            _handlers);
        if (config is not { IsConfigured: true })
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
        return new ResolvedCheckout(config, details);
    }

    private async Task<JToken?> ReadJsonBody()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
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
            return StatusCode(502, new { error = ex.Message });
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
