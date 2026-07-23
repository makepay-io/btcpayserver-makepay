#nullable enable
using System;
using System.IO;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.MakePay.Controllers;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.MakePay.Tests;

public class MakePayCheckoutPolicyTests
{
    [Fact]
    public void MissingAnonymousFiatOnRampSettingDefaultsToEnabled()
    {
        var config = JsonConvert.DeserializeObject<MakePayPaymentMethodConfig>("{}");

        Assert.NotNull(config);
        Assert.True(config!.AnonymousFiatOnRampEnabled);
        Assert.True(JObject.FromObject(config)["AnonymousFiatOnRampEnabled"]?.Value<bool>());
    }

    [Fact]
    public void AnonymousCheckoutPolicySnapshotsEnabledPaymentMethods()
    {
        var enabled = MakePayApiClient.BuildAnonymousCheckoutPolicy(new MakePayPaymentMethodConfig());
        var disabled = MakePayApiClient.BuildAnonymousCheckoutPolicy(new MakePayPaymentMethodConfig
        {
            AnonymousFiatOnRampEnabled = false
        });

        Assert.Equal(
            ["crypto", "cash_app_onramp"],
            enabled["allowedPaymentMethods"]?.Values<string>());
        Assert.Equal(["crypto"], disabled["allowedPaymentMethods"]?.Values<string>());
    }

    [Fact]
    public void ConnectedSettingsIgnoreForgedAnonymousPolicyFields()
    {
        var existing = new MakePayPaymentMethodConfig
        {
            ClientId = "client",
            AccessToken = "access",
            RefreshToken = "refresh",
            DpopPrivateKeyPem = "private-key",
            WebhookSecret = "webhook",
            AnonymousFiatOnRampEnabled = true,
            PaymentFeePayer = MakePayPaymentMethodConfig.PaymentFeePayerMerchant,
            AllowedPaymentVariancePercent = 2m,
            AllowedPaymentVarianceFixedUsd = 3m,
            MerchantSurchargePercent = 0.5m
        };
        var forged = new MakePayPaymentMethodConfig
        {
            AnonymousFiatOnRampEnabled = false,
            PaymentFeePayer = MakePayPaymentMethodConfig.PaymentFeePayerCustomer,
            AllowedPaymentVariancePercent = 99m,
            AllowedPaymentVarianceFixedUsd = 999m,
            MerchantSurchargePercent = -1m
        };

        var result = MakePayController.MergePostedConfig(existing, forged, "general");

        Assert.True(result.AnonymousFiatOnRampEnabled);
        Assert.Equal(MakePayPaymentMethodConfig.PaymentFeePayerMerchant, result.PaymentFeePayer);
        Assert.Equal(2m, result.AllowedPaymentVariancePercent);
        Assert.Equal(3m, result.AllowedPaymentVarianceFixedUsd);
        Assert.Equal(0.5m, result.MerchantSurchargePercent);
    }

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
    public void MerchantWalletModeAcceptsPayerFallbackWhenNoMerchantAddressMatchesAsset()
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

        Assert.Equal("0x2222222222222222222222222222222222222222", result["refundAddress"]?.Value<string>());
        Assert.Equal("0x2222222222222222222222222222222222222222", result["sourceAddress"]?.Value<string>());
    }

    [Fact]
    public void CheckoutRequestPolicyInjectsMerchantControlledDefaults()
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
            DefaultReceiptEmail = "merchant@example.com",
            RequestReceiptEmailFromCustomer = false,
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet
        };
        var payload = JObject.Parse(
            """
            {
              "sellAsset": "USDC.ETH",
              "receiptEmail": "payer@example.com",
              "refundAddress": "0x2222222222222222222222222222222222222222",
              "sourceAddress": "0x3333333333333333333333333333333333333333"
            }
            """);

        var result = (JObject)MakePayCheckoutPolicy.ApplyCheckoutRequestPolicy(config, prompt, payload);

        Assert.Equal("merchant@example.com", result["receiptEmail"]?.Value<string>());
        Assert.Equal("0x1111111111111111111111111111111111111111", result["refundAddress"]?.Value<string>());
        Assert.Equal("0x1111111111111111111111111111111111111111", result["sourceAddress"]?.Value<string>());
    }

    [Fact]
    public void MerchantWalletModeNormalizesEvmRefundAddressForMakePay()
    {
        var config = new MakePayPaymentMethodConfig
        {
            ChainAddressesJson = MakePayPaymentMethodConfig.SerializeChainAddresses(
            [
                new MakePayChainAddress
                {
                    Chain = "ETH",
                    Address = "0xa895520E4B739c3163A667863670E3a23Fc3b120"
                }
            ])
        };
        var prompt = new MakePayPromptDetails
        {
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet
        };
        var payload = JObject.Parse("""{ "sellAsset": "ETH.USDT-0xdac17f958d2ee523a2206206994597c13d831ec7" }""");

        var result = (JObject)MakePayCheckoutPolicy.ApplyRefundAddressPolicy(config, prompt, payload);

        Assert.Equal("0xa895520e4b739c3163a667863670e3a23fc3b120", result["refundAddress"]?.Value<string>());
        Assert.Equal("0xa895520e4b739c3163a667863670e3a23fc3b120", result["sourceAddress"]?.Value<string>());
    }

    [Fact]
    public void CheckoutRequestPolicyKeepsPayerReceiptEmailOnlyWhenConfigured()
    {
        var config = new MakePayPaymentMethodConfig();
        var prompt = new MakePayPromptDetails
        {
            RequestReceiptEmailFromCustomer = true,
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModePayerEntered
        };
        var payload = JObject.Parse(
            """
            {
              "sellAsset": "USDC.ETH",
              "receiptEmail": "payer@example.com",
              "refundAddress": "0x2222222222222222222222222222222222222222"
            }
            """);

        var result = (JObject)MakePayCheckoutPolicy.ApplyCheckoutRequestPolicy(config, prompt, payload);

        Assert.Equal("payer@example.com", result["receiptEmail"]?.Value<string>());
        Assert.Equal("0x2222222222222222222222222222222222222222", result["refundAddress"]?.Value<string>());
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
        Assert.Equal("Invalid paymentMethod.", MakePayCheckoutPolicy.ValidatePaymentRequestBody(
            JObject.Parse("""{ "sellAsset": "USDC.ETH", "paymentMethod": "cash_app_onramp" }""")));
    }

    [Fact]
    public void CheckoutProxyForcesNativeFlowToCrypto()
    {
        var config = new MakePayPaymentMethodConfig();
        var prompt = new MakePayPromptDetails
        {
            RequestReceiptEmailFromCustomer = true,
            RefundAddressMode = MakePayPaymentMethodConfig.RefundAddressModePayerEntered
        };
        var payload = JObject.Parse("""{ "sellAsset": "USDC.ETH" }""");

        var result = (JObject)MakePayCheckoutPolicy.ApplyCheckoutRequestPolicy(config, prompt, payload);

        Assert.Equal("crypto", result["paymentMethod"]?.Value<string>());
    }

    [Fact]
    public void EmptyAllowedAssetsAllowsAnyValidPaymentAsset()
    {
        var prompt = new MakePayPromptDetails();
        var payload = JObject.Parse("""{ "sellAsset": "ETH.USDT-0xdac17f958d2ee523a2206206994597c13d831ec7" }""");

        Assert.Null(MakePayCheckoutPolicy.ValidateAllowedAsset(prompt, payload));
    }

    [Fact]
    public void AllowedAssetsPermitConfiguredAssetIdentifier()
    {
        var prompt = new MakePayPromptDetails
        {
            AllowedAssetIdentifiers = "BTC.BTC\nETH.USDT-0xdac17f958d2ee523a2206206994597c13d831ec7"
        };
        var payload = JObject.Parse("""{ "sellAsset": " eth.usdt - 0xdac17f958d2ee523a2206206994597c13d831ec7 " }""");

        Assert.Null(MakePayCheckoutPolicy.ValidateAllowedAsset(prompt, payload));
    }

    [Fact]
    public void AllowedAssetsRejectUnconfiguredAssetIdentifier()
    {
        var prompt = new MakePayPromptDetails
        {
            AllowedAssetIdentifiers = "BTC.BTC\nETH.USDT-0xdac17f958d2ee523a2206206994597c13d831ec7"
        };
        var payload = JObject.Parse("""{ "sellAsset": "BSC.USDT-0x55d398326f99059ff775485246999027b3197955" }""");

        Assert.Equal(
            "Selected asset is not allowed for this invoice.",
            MakePayCheckoutPolicy.ValidateAllowedAsset(prompt, payload));
    }

    [Fact]
    public void StatusSessionIdIsBounded()
    {
        Assert.True(MakePayCheckoutPolicy.IsValidSessionId("session_123"));
        Assert.False(MakePayCheckoutPolicy.IsValidSessionId("session/123"));
        Assert.False(MakePayCheckoutPolicy.IsValidSessionId(new string('a', 161)));
    }

    [Fact]
    public void CashAppCheckoutStaysInsideBtcpayAndUsesOriginScopedApi()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "CheckoutPaymentMethodExtension.cshtml"));

        Assert.Contains("/btcpay-checkout", source);
        Assert.Contains("Open Cash App", source);
        Assert.Contains("Keep this invoice open", source);
        Assert.Contains("credentials: this.isCashAppPayment ? 'omit' : 'same-origin'", source);
        Assert.Contains("target=\"_blank\"", source);
        Assert.DoesNotContain("window.location.assign", source);
        Assert.DoesNotContain("paymentMethod=cash_app_onramp", source);
    }
}
