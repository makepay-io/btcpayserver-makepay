#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Services;

public class MakePayPaymentRecorder
{
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<MakePayPaymentRecorder> _logger;

    public MakePayPaymentRecorder(
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        EventAggregator eventAggregator,
        ILogger<MakePayPaymentRecorder> logger)
    {
        _paymentService = paymentService;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    public async Task<bool> RecordIfComplete(
        InvoiceEntity invoice,
        MakePayPromptDetails promptDetails,
        JObject session,
        string? deliveryId = null)
    {
        var status = Text(session["status"]);
        if (!string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sessionId = Text(session["id"]) ?? Text(session["sessionId"]);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("MakePay complete session for invoice {InvoiceId} did not include a session id.", invoice.Id);
            return false;
        }

        var paymentId = "makepay_" + sessionId;
        if (invoice.GetPayments(false).Any(payment =>
                payment.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId &&
                payment.Id == paymentId))
        {
            return false;
        }

        if (!_handlers.TryGetValue(MakePayPlugin.MakePayPaymentMethodId, out var handler))
        {
            return false;
        }

        var settlementAmount = session["settlementAmount"] as JObject;
        var paymentData = new MakePayPaymentData
        {
            PaymentLinkUid = promptDetails.PaymentLinkUid,
            SessionId = sessionId,
            Status = status ?? "complete",
            SellAsset = Text(session["selectedSellAsset"]) ?? Text(session["sellAsset"]),
            RequiredSellAmount = Text(session["requiredSellAmount"]),
            SettlementAmount =
                Text(settlementAmount?["amount"]) ??
                Text(session.SelectToken("settlement.amount")) ??
                promptDetails.BtcAmount.ToString("0.########"),
            SettlementClassification = Text(settlementAmount?["classification"]),
            DeliveryId = deliveryId
        };

        var payment = new PaymentData
        {
            Id = paymentId,
            InvoiceDataId = invoice.Id,
            Currency = "BTC",
            Amount = promptDetails.BtcAmount,
            Status = PaymentStatus.Settled,
            Created = DateTimeOffset.UtcNow
        };
        payment.Set(invoice, handler, paymentData);

        var searchTerms = new HashSet<string>
        {
            promptDetails.PaymentLinkUid,
            sessionId
        };
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            searchTerms.Add(deliveryId);
        }

        var addedPayment = await _paymentService.AddPayment(payment, searchTerms);
        if (addedPayment is null)
        {
            return false;
        }

        _eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
        {
            Payment = addedPayment
        });

        _logger.LogInformation(
            "Recorded MakePay payment for invoice {InvoiceId}: session {SessionId}, link {PaymentLinkUid}",
            invoice.Id,
            sessionId,
            promptDetails.PaymentLinkUid);

        return true;
    }

    private static string? Text(JToken? token)
    {
        var value = token?.Value<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
