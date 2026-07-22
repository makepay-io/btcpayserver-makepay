#nullable enable

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayPaymentData
{
    public string PaymentLinkUid { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
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
}
