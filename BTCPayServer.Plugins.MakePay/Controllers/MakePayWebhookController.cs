#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Controllers;

[Route("plugins/makepay/webhook")]
[ApiController]
public class MakePayWebhookController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayPaymentRecorder _paymentRecorder;
    private readonly MakePaySecretProtector _secretProtector;
    private readonly ILogger<MakePayWebhookController> _logger;

    public MakePayWebhookController(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayPaymentRecorder paymentRecorder,
        MakePaySecretProtector secretProtector,
        ILogger<MakePayWebhookController> logger)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _paymentRecorder = paymentRecorder;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    [HttpPost("{storeId}")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> HandleWebhook(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return BadRequest("Store not found.");
        }

        var config = _secretProtector.Unprotect(store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
            MakePayPlugin.MakePayPaymentMethodId,
            _handlers) ?? new MakePayPaymentMethodConfig());
        if (config is not { IsConfigured: true })
        {
            return BadRequest("Store is not configured for MakePay.");
        }

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        var signature = Request.Headers[MakePayWebhookVerifier.SignatureHeader].FirstOrDefault();
        if (!MakePayWebhookVerifier.Verify(body, signature ?? string.Empty, config.WebhookSecret!))
        {
            _logger.LogWarning("Rejected MakePay webhook for store {StoreId}: invalid signature.", storeId);
            return BadRequest("Invalid signature.");
        }

        JObject payload;
        try
        {
            payload = JObject.Parse(body);
        }
        catch
        {
            return BadRequest("Invalid JSON.");
        }

        var paymentLinkUid = Text(payload.SelectToken("paymentLink.uid"));
        if (string.IsNullOrWhiteSpace(paymentLinkUid))
        {
            return Ok();
        }

        var invoiceRef = await _invoiceRepository.GetInvoiceFromAddress(
            MakePayPlugin.MakePayPaymentMethodId,
            paymentLinkUid);
        if (invoiceRef?.Id is not { } invoiceId)
        {
            _logger.LogWarning("MakePay webhook link {PaymentLinkUid} did not match any BTCPay invoice.", paymentLinkUid);
            return Ok();
        }

        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        var prompt = invoice.GetPaymentPrompt(MakePayPlugin.MakePayPaymentMethodId);
        if (prompt is null || !_handlers.TryGetValue(MakePayPlugin.MakePayPaymentMethodId, out var handler))
        {
            return Ok();
        }

        var promptDetails = (MakePayPromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
        if (!string.Equals(promptDetails.PaymentLinkUid, paymentLinkUid, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "MakePay webhook link {PaymentLinkUid} did not match invoice prompt link {PromptLinkUid}.",
                paymentLinkUid,
                promptDetails.PaymentLinkUid);
            return Ok();
        }

        var session = payload["session"] as JObject;
        if (session is null)
        {
            return Ok();
        }

        await _paymentRecorder.RecordIfComplete(
            invoice,
            promptDetails,
            session,
            Text(payload["deliveryId"]) ?? Text(payload["delivery_id"]));

        return Ok();
    }

    private static string? Text(JToken? token)
    {
        var value = token?.Value<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
