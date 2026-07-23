#nullable enable
using System;
using System.Collections.Generic;
using BTCPayServer.Data;

namespace BTCPayServer.Plugins.MakePay.Models;

public class MakePayPaymentsViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public string DataUrl { get; set; } = string.Empty;
    public string InvoiceUrlTemplate { get; set; } = string.Empty;
    public string PaymentDetailsUrlTemplate { get; set; } = string.Empty;
}

public class MakePayStatisticsViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public string DataUrl { get; set; } = string.Empty;
    public string PaymentsUrl { get; set; } = string.Empty;
    public string InvoiceUrlTemplate { get; set; } = string.Empty;
}

public class MakePayPaymentListItem
{
    public string PaymentId { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string? OrderId { get; set; }
    public DateTimeOffset Created { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "BTC";
    public decimal InvoiceAmount { get; set; }
    public string InvoiceCurrency { get; set; } = "USD";
    public string PaymentLinkUid { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string MakePayStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "crypto";
    public string? SellAsset { get; set; }
    public string? BuyAsset { get; set; }
    public string? RequiredSellAmount { get; set; }
    public string? SettlementAmount { get; set; }
    public string? SettlementClassification { get; set; }
    public string? DepositNetwork { get; set; }
    public string? DepositAddress { get; set; }
    public string? PaymentRequest { get; set; }
    public string? TransactionIds { get; set; }
    public string? DeliveryId { get; set; }
    public bool IsSessionOnly { get; set; }
}

public class MakePayPaymentsSnapshot
{
    public DateTimeOffset FetchedAt { get; set; }
    public List<MakePayPaymentListItem> Payments { get; set; } = [];
}

public class MakePayPaymentsDataResponse
{
    public DateTimeOffset FetchedAt { get; set; }
    public bool IsRefreshing { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
    public List<string> StatusOptions { get; set; } = [];
    public List<string> AssetOptions { get; set; } = [];
    public List<string> NetworkOptions { get; set; } = [];
    public List<MakePayPaymentApiItem> Payments { get; set; } = [];
}

public class MakePayPaymentApiItem
{
    public string PaymentId { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string? OrderId { get; set; }
    public DateTimeOffset Created { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string PaymentLinkUid { get; set; } = string.Empty;
    public string MakePayStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "crypto";
    public string? SellAsset { get; set; }
    public string? BuyAsset { get; set; }
    public string? RequiredSellAmount { get; set; }
    public string? SettlementAmount { get; set; }
    public string? DepositNetwork { get; set; }
    public string? DepositAddress { get; set; }
    public string? PaymentRequest { get; set; }
    public string? TransactionIds { get; set; }
    public string? DeliveryId { get; set; }
    public bool IsSessionOnly { get; set; }
}

public class MakePayStatisticsData
{
    public DateTimeOffset FetchedAt { get; set; }
    public bool IsRefreshing { get; set; }
    public int Days { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal SettledVolume { get; set; }
    public decimal AveragePayment { get; set; }
    public decimal? VolumeChangePercent { get; set; }
    public int SettledCount { get; set; }
    public int ProcessingCount { get; set; }
    public int TotalCount { get; set; }
    public decimal SuccessRate { get; set; }
    public int CashAppCount { get; set; }
    public int CryptoCount { get; set; }
    public List<MakePayStatisticsPoint> Series { get; set; } = [];
    public List<MakePayPaymentApiItem> RecentPayments { get; set; } = [];
}

public class MakePayStatisticsPoint
{
    public string Date { get; set; } = string.Empty;
    public decimal Volume { get; set; }
    public int Count { get; set; }
}

public class MakePayPaymentDetailsViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public MakePayPaymentListItem Payment { get; set; } = new();
    public List<MakePayExplorerLink> ExplorerLinks { get; set; } = [];
}

public class MakePayExplorerLink
{
    public string Label { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
