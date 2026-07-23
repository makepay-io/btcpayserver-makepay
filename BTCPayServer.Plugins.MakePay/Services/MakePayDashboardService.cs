#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.Models;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.Services;

public sealed class MakePayDashboardService
{
    private static readonly TimeSpan RecordedRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(2);
    private const int InvoiceScanLimit = 1000;
    private const int OpenSessionScanLimit = 200;
    private const int OpenSessionConcurrency = 8;

    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePayApiClient _makePayApiClient;
    private readonly ILogger<MakePayDashboardService> _logger;
    private readonly ConcurrentDictionary<string, MakePayPaymentsSnapshot> _recordedSnapshots = new();
    private readonly ConcurrentDictionary<string, MakePayPaymentsSnapshot> _fullSnapshots = new();
    private readonly ConcurrentDictionary<string, Task<MakePayPaymentsSnapshot?>> _recordedRefreshes = new();
    private readonly ConcurrentDictionary<string, Task<MakePayPaymentsSnapshot?>> _fullRefreshes = new();

    public MakePayDashboardService(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        MakePayApiClient makePayApiClient,
        ILogger<MakePayDashboardService> logger)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _makePayApiClient = makePayApiClient;
        _logger = logger;
    }

    public async Task<(MakePayPaymentsSnapshot? Snapshot, bool IsRefreshing)> GetPayments(
        string storeId,
        bool forceRefresh = false)
    {
        if (_fullSnapshots.TryGetValue(storeId, out var full))
        {
            var shouldRefresh = forceRefresh ||
                                DateTimeOffset.UtcNow - full.FetchedAt >= FullRefreshInterval;
            if (shouldRefresh)
            {
                _ = StartFullRefresh(storeId);
            }

            return (full, IsFullRefreshRunning(storeId));
        }

        var recorded = await GetRecorded(storeId, forceRefresh);
        _ = StartFullRefresh(storeId);
        return (recorded.Snapshot, true);
    }

    public async Task<(MakePayPaymentsSnapshot? Snapshot, bool IsRefreshing)> GetRecorded(
        string storeId,
        bool forceRefresh = false)
    {
        if (_recordedSnapshots.TryGetValue(storeId, out var snapshot))
        {
            var shouldRefresh = forceRefresh ||
                                DateTimeOffset.UtcNow - snapshot.FetchedAt >= RecordedRefreshInterval;
            if (shouldRefresh)
            {
                _ = StartRecordedRefresh(storeId);
            }

            return (snapshot, IsRecordedRefreshRunning(storeId));
        }

        var loaded = await StartRecordedRefresh(storeId);
        return (loaded, false);
    }

    private Task<MakePayPaymentsSnapshot?> StartRecordedRefresh(string storeId)
    {
        return _recordedRefreshes.GetOrAdd(storeId, RefreshRecorded);
    }

    private Task<MakePayPaymentsSnapshot?> StartFullRefresh(string storeId)
    {
        return _fullRefreshes.GetOrAdd(storeId, RefreshFull);
    }

    private bool IsRecordedRefreshRunning(string storeId) =>
        _recordedRefreshes.TryGetValue(storeId, out var task) && !task.IsCompleted;

    private bool IsFullRefreshRunning(string storeId) =>
        _fullRefreshes.TryGetValue(storeId, out var task) && !task.IsCompleted;

    private async Task<MakePayPaymentsSnapshot?> RefreshRecorded(string storeId)
    {
        // Ensure the task is registered in the single-flight dictionary before it can remove itself.
        await Task.Yield();
        try
        {
            var store = await _storeRepository.FindStore(storeId);
            if (store is null)
            {
                return null;
            }

            var invoices = await LoadInvoices(storeId);
            var snapshot = new MakePayPaymentsSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Payments = LoadRecordedPayments(invoices)
            };
            _recordedSnapshots[storeId] = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to refresh MakePay recorded payments for store {StoreId}.", storeId);
            return _recordedSnapshots.TryGetValue(storeId, out var cached) ? cached : null;
        }
        finally
        {
            _recordedRefreshes.TryRemove(storeId, out _);
        }
    }

    private async Task<MakePayPaymentsSnapshot?> RefreshFull(string storeId)
    {
        // Ensure the task is registered in the single-flight dictionary before it can remove itself.
        await Task.Yield();
        try
        {
            var store = await _storeRepository.FindStore(storeId);
            if (store is null)
            {
                return null;
            }

            var invoices = await LoadInvoices(storeId);
            var payments = LoadRecordedPayments(invoices);
            payments.AddRange(await LoadOpenSessionItems(invoices, payments));
            payments = payments
                .OrderByDescending(payment => payment.Created)
                .ToList();

            var snapshot = new MakePayPaymentsSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Payments = payments
            };
            _fullSnapshots[storeId] = snapshot;
            _recordedSnapshots[storeId] = new MakePayPaymentsSnapshot
            {
                FetchedAt = snapshot.FetchedAt,
                Payments = payments.Where(payment => !payment.IsSessionOnly).ToList()
            };
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to refresh MakePay payments for store {StoreId}.", storeId);
            return _fullSnapshots.TryGetValue(storeId, out var cached) ? cached : null;
        }
        finally
        {
            _fullRefreshes.TryRemove(storeId, out _);
        }
    }

    private Task<InvoiceEntity[]> LoadInvoices(string storeId)
    {
        return _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [storeId],
            IncludeArchived = true,
            Take = InvoiceScanLimit
        });
    }

    private List<MakePayPaymentListItem> LoadRecordedPayments(IEnumerable<InvoiceEntity> invoices)
    {
        return invoices
            .SelectMany(invoice => invoice.GetPayments(false)
                .Where(payment => payment.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId)
                .Select(payment => ToPaymentListItem(invoice, payment)))
            .OrderByDescending(payment => payment.Created)
            .ToList();
    }

    private async Task<List<MakePayPaymentListItem>> LoadOpenSessionItems(
        IReadOnlyList<InvoiceEntity> invoices,
        IReadOnlyList<MakePayPaymentListItem> recordedPayments)
    {
        if (_handlers.TryGet(MakePayPlugin.MakePayPaymentMethodId) is not MakePayPaymentMethodHandler handler)
        {
            return [];
        }

        var recordedLinks = recordedPayments
            .Select(payment => payment.PaymentLinkUid)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        var recordedSessions = recordedPayments
            .Select(payment => payment.SessionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = invoices
            .Take(OpenSessionScanLimit)
            .Where(invoice => !invoice.GetPayments(false)
                .Any(payment => payment.PaymentMethodId == MakePayPlugin.MakePayPaymentMethodId))
            .Select(invoice =>
            {
                var prompt = invoice.GetPaymentPrompt(MakePayPlugin.MakePayPaymentMethodId);
                if (prompt is null)
                {
                    return null;
                }

                var details = (MakePayPromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
                return string.IsNullOrWhiteSpace(details.PaymentLinkUid) ||
                       recordedLinks.Contains(details.PaymentLinkUid)
                    ? null
                    : new OpenSessionCandidate(invoice, details);
            })
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToList();

        using var gate = new SemaphoreSlim(OpenSessionConcurrency);
        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync();
            try
            {
                return await LoadOpenSessionItem(candidate, recordedSessions);
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }

    private async Task<MakePayPaymentListItem?> LoadOpenSessionItem(
        OpenSessionCandidate candidate,
        IReadOnlySet<string> recordedSessions)
    {
        var config = new MakePayPaymentMethodConfig
        {
            CheckoutBaseUrl = candidate.Details.CheckoutBaseUrl
        };
        var current = await _makePayApiClient.GetCurrentSession(config, candidate.Details.PaymentLinkUid);
        var session = current?["session"] as JObject;
        var sessionId = Text(session?["sessionId"]);
        if (session is null ||
            string.IsNullOrWhiteSpace(sessionId) ||
            recordedSessions.Contains(sessionId))
        {
            return null;
        }

        var whatToSend = session["whatToSend"] as JObject;
        var deposit = session["deposit"] as JObject;
        var cashApp = session["cashApp"] as JObject;
        var selectedAsset = Text(session["selectedSellAsset"]);
        var requiredAmount = Text(whatToSend?["amount"]);
        var requiredAsset = Text(whatToSend?["asset"]) ?? selectedAsset;
        var settlementAmount = Text(session.SelectToken("settlementAmount.targetAmount"));
        var settlementAsset = Text(session.SelectToken("settlementAmount.targetAsset"));

        return new MakePayPaymentListItem
        {
            PaymentId = "session:" + sessionId,
            InvoiceId = candidate.Invoice.Id,
            OrderId = candidate.Invoice.Metadata.OrderId,
            Created = candidate.Details.CreatedAt,
            Status = PaymentStatus.Processing,
            Amount = candidate.Details.BtcAmount,
            Currency = "BTC",
            InvoiceAmount = candidate.Invoice.Price,
            InvoiceCurrency = candidate.Invoice.Currency,
            PaymentLinkUid = candidate.Details.PaymentLinkUid,
            SessionId = sessionId,
            MakePayStatus = Text(session["status"]) ?? "pending",
            PaymentMethod = Text(session["paymentMethod"]) ?? "crypto",
            SellAsset = selectedAsset,
            BuyAsset = settlementAsset ?? candidate.Details.SettlementAsset,
            RequiredSellAmount = !string.IsNullOrWhiteSpace(requiredAmount)
                ? requiredAmount +
                  (!string.IsNullOrWhiteSpace(requiredAsset) ? " " + ShortAsset(requiredAsset) : string.Empty)
                : null,
            SettlementAmount = settlementAmount,
            SettlementClassification = Text(session.SelectToken("settlementAmount.classification")),
            DepositNetwork = ChainFromAsset(selectedAsset),
            DepositAddress = Text(deposit?["address"]),
            PaymentRequest = Text(cashApp?["paymentRequest"]),
            IsSessionOnly = true
        };
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
            InvoiceAmount = payment.InvoicePaidAmount.Gross,
            InvoiceCurrency = invoice.Currency,
            PaymentLinkUid = details.PaymentLinkUid,
            SessionId = details.SessionId,
            MakePayStatus = details.Status,
            PaymentMethod = details.PaymentMethod,
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

    internal static MakePayStatisticsData BuildStatistics(
        IReadOnlyCollection<MakePayPaymentListItem> payments,
        string preferredCurrency,
        int days,
        DateTimeOffset now)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var today = now.UtcDateTime.Date;
        var from = new DateTimeOffset(today.AddDays(-(days - 1)), TimeSpan.Zero);
        var previousFrom = from.AddDays(-days);
        var primaryCurrency = payments
            .Where(payment =>
                payment.Created >= previousFrom &&
                string.Equals(payment.InvoiceCurrency, preferredCurrency, StringComparison.OrdinalIgnoreCase))
            .Select(payment => payment.InvoiceCurrency)
            .FirstOrDefault() ??
            payments
                .Where(payment => payment.Created >= previousFrom)
                .GroupBy(payment => payment.InvoiceCurrency, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ??
            preferredCurrency;
        var current = payments
            .Where(payment => payment.Created >= from && payment.Created < from.AddDays(days))
            .ToList();
        var settled = current.Where(payment => payment.Status == PaymentStatus.Settled).ToList();
        var currentVolume = settled
            .Where(payment => string.Equals(
                payment.InvoiceCurrency,
                primaryCurrency,
                StringComparison.OrdinalIgnoreCase))
            .Sum(payment => payment.InvoiceAmount);
        var previousVolume = payments
            .Where(payment =>
                payment.Status == PaymentStatus.Settled &&
                payment.Created >= previousFrom &&
                payment.Created < from &&
                string.Equals(payment.InvoiceCurrency, primaryCurrency, StringComparison.OrdinalIgnoreCase))
            .Sum(payment => payment.InvoiceAmount);
        var comparableSettled = settled
            .Where(payment => string.Equals(
                payment.InvoiceCurrency,
                primaryCurrency,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var cashAppCount = current.Count(IsCashApp);
        var cryptoCount = current.Count(payment => !IsCashApp(payment));
        var totalCount = current.Count;

        return new MakePayStatisticsData
        {
            FetchedAt = now,
            Days = days,
            Currency = primaryCurrency,
            SettledVolume = currentVolume,
            AveragePayment = comparableSettled.Count == 0 ? 0m : currentVolume / comparableSettled.Count,
            VolumeChangePercent = previousVolume == 0m
                ? null
                : Math.Round(((currentVolume - previousVolume) / previousVolume) * 100m, 1),
            SettledCount = settled.Count,
            ProcessingCount = current.Count(payment => payment.Status == PaymentStatus.Processing),
            TotalCount = totalCount,
            SuccessRate = totalCount == 0
                ? 0m
                : Math.Round((decimal)settled.Count / totalCount * 100m, 1),
            CashAppCount = cashAppCount,
            CryptoCount = cryptoCount,
            Series = Enumerable.Range(0, days)
                .Select(offset =>
                {
                    var date = from.AddDays(offset);
                    var dayPayments = settled.Where(payment =>
                        payment.Created >= date &&
                        payment.Created < date.AddDays(1) &&
                        string.Equals(
                            payment.InvoiceCurrency,
                            primaryCurrency,
                            StringComparison.OrdinalIgnoreCase));
                    return new MakePayStatisticsPoint
                    {
                        Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Volume = dayPayments.Sum(payment => payment.InvoiceAmount),
                        Count = dayPayments.Count()
                    };
                })
                .ToList(),
            RecentPayments = current
                .OrderByDescending(payment => payment.Created)
                .Take(5)
                .Select(ToApiItem)
                .ToList()
        };
    }

    internal static MakePayPaymentApiItem ToApiItem(MakePayPaymentListItem payment)
    {
        return new MakePayPaymentApiItem
        {
            PaymentId = payment.PaymentId,
            InvoiceId = payment.InvoiceId,
            OrderId = payment.OrderId,
            Created = payment.Created,
            Status = payment.Status.ToString(),
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentLinkUid = payment.PaymentLinkUid,
            MakePayStatus = payment.MakePayStatus,
            PaymentMethod = payment.PaymentMethod,
            SellAsset = payment.SellAsset,
            BuyAsset = payment.BuyAsset,
            RequiredSellAmount = payment.RequiredSellAmount,
            SettlementAmount = payment.SettlementAmount,
            DepositNetwork = payment.DepositNetwork,
            DepositAddress = payment.DepositAddress,
            PaymentRequest = payment.PaymentRequest,
            TransactionIds = payment.TransactionIds,
            DeliveryId = payment.DeliveryId,
            IsSessionOnly = payment.IsSessionOnly
        };
    }

    private static bool IsCashApp(MakePayPaymentListItem payment) =>
        string.Equals(payment.PaymentMethod, "cash_app_onramp", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return null;
        }

        var value = token.Type == JTokenType.String
            ? token.Value<string>()
            : token.ToString(Newtonsoft.Json.Formatting.None);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static string? ChainFromAsset(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return null;
        }

        var value = asset.Trim();
        return value.Contains('.', StringComparison.Ordinal)
            ? value.Split('.', 2)[0].ToUpperInvariant()
            : null;
    }

    private static string ShortAsset(string asset)
    {
        var value = asset.Trim();
        if (value.Contains('.', StringComparison.Ordinal))
        {
            value = value.Split('.', 2)[1];
        }

        return value.Split('-', 2)[0].ToUpperInvariant();
    }

    private sealed record OpenSessionCandidate(InvoiceEntity Invoice, MakePayPromptDetails Details);
}
