#nullable enable
using System;
using System.IO;
using System.Linq;
using BTCPayServer.Data;
using BTCPayServer.Plugins.MakePay.Models;
using BTCPayServer.Plugins.MakePay.Services;
using Xunit;

namespace BTCPayServer.Plugins.MakePay.Tests;

public class MakePayDashboardTests
{
    [Fact]
    public void StatisticsUsePreferredInvoiceCurrencyAndSeparatePaymentMethods()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var payments = new[]
        {
            Payment(now.AddDays(-1), PaymentStatus.Settled, 100m, "USD", "crypto"),
            Payment(now.AddDays(-2), PaymentStatus.Settled, 50m, "USD", "cash_app_onramp"),
            Payment(now.AddDays(-3), PaymentStatus.Processing, 25m, "USD", "crypto"),
            Payment(now.AddDays(-4), PaymentStatus.Settled, 90m, "EUR", "crypto"),
            Payment(now.AddDays(-40), PaymentStatus.Settled, 100m, "USD", "crypto")
        };

        var result = MakePayDashboardService.BuildStatistics(payments, "USD", 30, now);

        Assert.Equal("USD", result.Currency);
        Assert.Equal(150m, result.SettledVolume);
        Assert.Equal(75m, result.AveragePayment);
        Assert.Equal(50m, result.VolumeChangePercent);
        Assert.Equal(3, result.SettledCount);
        Assert.Equal(1, result.ProcessingCount);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal(75m, result.SuccessRate);
        Assert.Equal(1, result.CashAppCount);
        Assert.Equal(3, result.CryptoCount);
        Assert.Equal(30, result.Series.Count);
        Assert.Equal(150m, result.Series.Sum(point => point.Volume));
        Assert.Equal(4, result.RecentPayments.Count);
    }

    [Fact]
    public void StatisticsNormalizeUnsupportedPeriodToThirtyDays()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        var result = MakePayDashboardService.BuildStatistics([], "USD", 31, now);

        Assert.Equal(30, result.Days);
        Assert.Equal(30, result.Series.Count);
        Assert.Equal("USD", result.Currency);
        Assert.Empty(result.RecentPayments);
    }

    [Fact]
    public void DashboardPagesUseClientCacheAndStatisticsIsDefaultNavigation()
    {
        var payments = Fixture("Payments.cshtml");
        var statistics = Fixture("Statistics.cshtml");
        var navigation = Fixture("StoreNavExtension.cshtml");

        Assert.Contains("localStorage.getItem(cacheKey(params))", payments);
        Assert.Contains("fetch(url", payments);
        Assert.Contains("checking open sessions in the background", payments);
        Assert.DoesNotContain("@foreach (var payment", payments);

        Assert.Contains("makepay:statistics:", statistics);
        Assert.Contains("new Chartist.Line", statistics);
        Assert.Contains("new Chartist.Pie", statistics);
        Assert.Contains("Recent MakePay payments", statistics);

        Assert.Contains("asp-action=\"Statistics\"", navigation);
        Assert.Contains("<span>Statistics</span>", navigation);
    }

    private static MakePayPaymentListItem Payment(
        DateTimeOffset created,
        PaymentStatus status,
        decimal invoiceAmount,
        string invoiceCurrency,
        string paymentMethod)
    {
        return new MakePayPaymentListItem
        {
            PaymentId = Guid.NewGuid().ToString("N"),
            InvoiceId = Guid.NewGuid().ToString("N"),
            Created = created,
            Status = status,
            Amount = invoiceAmount,
            Currency = invoiceCurrency,
            InvoiceAmount = invoiceAmount,
            InvoiceCurrency = invoiceCurrency,
            PaymentMethod = paymentMethod
        };
    }

    private static string Fixture(string filename)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename));
    }
}
