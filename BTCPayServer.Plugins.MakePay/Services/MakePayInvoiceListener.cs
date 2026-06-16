#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Services;

public class MakePayInvoiceListener : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayApiClient _makePayApiClient;
    private readonly MakePayPaymentRecorder _paymentRecorder;
    private readonly MakePaySecretProtector _secretProtector;
    private readonly ILogger<MakePayInvoiceListener> _logger;

    public MakePayInvoiceListener(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient,
        MakePayPaymentRecorder paymentRecorder,
        MakePaySecretProtector secretProtector,
        ILogger<MakePayInvoiceListener> logger)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
        _paymentRecorder = paymentRecorder;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcilePendingInvoices(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MakePay reconciliation failed.");
            }

            await Task.Delay(ReconcileInterval, stoppingToken);
        }
    }

    private async Task ReconcilePendingInvoices(CancellationToken cancellationToken)
    {
        var invoices = await _invoiceRepository.GetMonitoredInvoices(
            MakePayPlugin.MakePayPaymentMethodId,
            cancellationToken);
        if (invoices.Length == 0)
        {
            return;
        }

        foreach (var invoice in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileInvoice(invoice, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to reconcile MakePay invoice {InvoiceId}.", invoice.Id);
            }
        }
    }

    private async Task ReconcileInvoice(
        InvoiceEntity invoice,
        CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(MakePayPlugin.MakePayPaymentMethodId, out var handler))
        {
            return;
        }

        var prompt = invoice.GetPaymentPrompt(MakePayPlugin.MakePayPaymentMethodId);
        if (prompt is null)
        {
            return;
        }

        var promptDetails = (MakePayPromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
        if (string.IsNullOrWhiteSpace(promptDetails.PaymentLinkUid))
        {
            return;
        }

        var store = await _storeRepository.FindStore(invoice.StoreId);
        var config = store is null
            ? null
            : _secretProtector.Unprotect(store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
                  MakePayPlugin.MakePayPaymentMethodId,
                  _handlers) ?? new MakePayPaymentMethodConfig());
        if (config is not { IsConfigured: true })
        {
            return;
        }

        var current = await _makePayApiClient.GetCurrentSession(
            config,
            promptDetails.PaymentLinkUid,
            cancellationToken);
        if (current is null)
        {
            return;
        }

        var session = new JObject
        {
            ["id"] = current["sessionId"],
            ["status"] = current["status"],
            ["selectedSellAsset"] = current["sellAsset"] ?? current.SelectToken("payable.sellAsset"),
            ["requiredSellAmount"] = current.SelectToken("payable.requiredSellAmount") ?? current.SelectToken("whatToSend.amount"),
            ["settlementAmount"] = current["settlementAmount"]
        };

        await _paymentRecorder.RecordIfComplete(invoice, promptDetails, session);
    }
}
