#nullable enable
using System;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.MakePay.Tests;

public class MakePayCheckoutPolicyTests
{
    [Fact]
    public void MerchantWalletModeOverwritesClientSuppliedRefundAddresses()
    {
        var config = new MakePayPaymentMethodConfig
        {
            ChainAddressesJson = MakePayPaymentMethodConfig.SerializeChainAddresses(
            [
                new MakePayChainAddress
                {
                    Chain = "ETH",
                    Address = "0x1111111111111111111111111111111111111111"
                }
            ])
        };
        var prompt = new MakePayPromptDetails
        {
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet
        };
        var payload = JObject.Parse(
            """
            {
              "sellAsset": "USDC.ETH",
              "refundAddress": "0x2222222222222222222222222222222222222222",
              "sourceAddress": "0x3333333333333333333333333333333333333333"
            }
            """);

        var result = (JObject)MakePayCheckoutPolicy.ApplyRefundAddressPolicy(config, prompt, payload);

        Assert.Equal("0x1111111111111111111111111111111111111111", result["refundAddress"]?.Value<string>());
        Assert.Equal("0x1111111111111111111111111111111111111111", result["sourceAddress"]?.Value<string>());
    }

    [Fact]
    public void PayerEnteredModeAllowsClientSuppliedRefundAddresses()
    {
        var config = new MakePayPaymentMethodConfig();
        var prompt = new MakePayPromptDetails
        {
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModePayerEntered
        };
        var payload = JObject.Parse(
            """
            {
              "sellAsset": "USDC.ETH",
              "refundAddress": "0x2222222222222222222222222222222222222222",
              "sourceAddress": "0x3333333333333333333333333333333333333333"
            }
            """);

        var result = (JObject)MakePayCheckoutPolicy.ApplyRefundAddressPolicy(config, prompt, payload);

        Assert.Equal("0x2222222222222222222222222222222222222222", result["refundAddress"]?.Value<string>());
        Assert.Equal("0x3333333333333333333333333333333333333333", result["sourceAddress"]?.Value<string>());
    }

    [Fact]
    public void MerchantWalletModeRemovesClientAddressesWhenNoMerchantAddressMatchesAsset()
    {
        var config = new MakePayPaymentMethodConfig();
        var prompt = new MakePayPromptDetails
        {
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet
        };
        var payload = JObject.Parse(
            """
            {
              "sellAsset": "USDC.ETH",
              "refundAddress": "0x2222222222222222222222222222222222222222",
              "sourceAddress": "0x3333333333333333333333333333333333333333"
            }
            """);

        var result = (JObject)MakePayCheckoutPolicy.ApplyRefundAddressPolicy(config, prompt, payload);

        Assert.Null(result["refundAddress"]);
        Assert.Null(result["sourceAddress"]);
    }

    [Fact]
    public void InvoiceMustBeNewAndUnexpiredToUsePublicCheckoutApi()
    {
        var payable = new InvoiceEntity
        {
            Status = InvoiceStatus.New,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var expired = new InvoiceEntity
        {
            Status = InvoiceStatus.New,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var processing = new InvoiceEntity
        {
            Status = InvoiceStatus.Processing,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        Assert.True(MakePayCheckoutPolicy.IsInvoicePayable(payable));
        Assert.False(MakePayCheckoutPolicy.IsInvoicePayable(expired));
        Assert.False(MakePayCheckoutPolicy.IsInvoicePayable(processing));
    }

    [Fact]
    public void WebhookResolvedInvoiceMustBelongToSigningStore()
    {
        var invoice = new InvoiceEntity
        {
            StoreId = "victim-store"
        };

        Assert.True(MakePayCheckoutPolicy.InvoiceBelongsToStore(invoice, "victim-store"));
        Assert.False(MakePayCheckoutPolicy.InvoiceBelongsToStore(invoice, "attacker-store"));
        Assert.False(MakePayCheckoutPolicy.InvoiceBelongsToStore(null, "attacker-store"));
    }

    [Fact]
    public void PaymentRequestBodyRequiresValidAssetIdentifier()
    {
        Assert.Null(MakePayCheckoutPolicy.ValidatePaymentRequestBody(
            JObject.Parse("""{ "sellAsset": "USDC.ETH", "paymentMethod": "crypto" }""")));
        Assert.Equal("Invalid sellAsset.", MakePayCheckoutPolicy.ValidatePaymentRequestBody(
            JObject.Parse("""{ "sellAsset": "../ETH" }""")));
    }

    [Fact]
    public void StatusSessionIdIsBounded()
    {
        Assert.True(MakePayCheckoutPolicy.IsValidSessionId("session_123"));
        Assert.False(MakePayCheckoutPolicy.IsValidSessionId("session/123"));
        Assert.False(MakePayCheckoutPolicy.IsValidSessionId(new string('a', 161)));
    }
}
