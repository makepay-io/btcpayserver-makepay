#nullable enable
using System;
using System.Collections.Generic;
using BTCPayServer.Data;

namespace BTCPayServer.Plugins.MakePay.Models;

public class MakePayPaymentsViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public string? SearchTerm { get; set; }
    public string? StatusFilter { get; set; }
    public string? AssetFilter { get; set; }
    public string? NetworkFilter { get; set; }
    public string? TimeRange { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
    public bool HasMore { get; set; }
    public List<MakePayPaymentListItem> Payments { get; set; } = [];
    public List<string> StatusOptions { get; set; } = [];
    public List<string> AssetOptions { get; set; } = [];
    public List<string> NetworkOptions { get; set; } = [];
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
