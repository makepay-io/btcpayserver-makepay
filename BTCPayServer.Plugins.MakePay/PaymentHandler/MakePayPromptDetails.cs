#nullable enable
using System;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayPromptDetails
{
    public string PaymentLinkUid { get; set; } = string.Empty;
    public string PaymentLinkUrl { get; set; } = string.Empty;
    public bool IsAnonymous { get; set; }
    public decimal BtcAmount { get; set; }
    public string SettlementCurrency { get; set; } = "BTC";
    public string SettlementAsset { get; set; } = "BTC.BTC";
    public string SettlementAddress { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string WebhookSecretLast4 { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
    public string CheckoutBaseUrl { get; set; } = "https://makepay.io";
    public bool RequestReceiptEmailFromCustomer { get; set; }
    public string DefaultReceiptEmail { get; set; } = string.Empty;
    public string RefundAddressMode { get; set; } = MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet;
    public string AllowedAssetIdentifiers { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
