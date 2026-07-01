#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public static class MakePayCheckoutPolicy
{
    public const int MaxJsonBodyBytes = 64 * 1024;
    private static readonly Regex AssetIdentifier = new("^[A-Za-z0-9_.:-]{2,160}$", RegexOptions.Compiled);
    private static readonly Regex SessionId = new("^[A-Za-z0-9_.:-]{1,160}$", RegexOptions.Compiled);

    public static bool IsInvoicePayable(InvoiceEntity invoice) =>
        invoice.GetInvoiceState().Status == InvoiceStatus.New &&
        !invoice.IsExpired();

    public static bool InvoiceBelongsToStore(InvoiceEntity? invoice, string storeId) =>
        invoice is not null &&
        string.Equals(invoice.StoreId, storeId, StringComparison.Ordinal);

    public static string? ValidatePaymentRequestBody(JToken body)
    {
        if (body is not JObject payload)
        {
            return "Expected a JSON object.";
        }

        var sellAsset = ReadString(payload, "sellAsset")?.Trim();
        if (string.IsNullOrWhiteSpace(sellAsset) || !AssetIdentifier.IsMatch(sellAsset))
        {
            return "Invalid sellAsset.";
        }

        return ValidateOptionalString(payload, "paymentMethod", 32) ??
               ValidateOptionalString(payload, "receiptEmail", 320) ??
               ValidateOptionalString(payload, "refundAddress", 256) ??
               ValidateOptionalString(payload, "sourceAddress", 256);
    }

    public static bool IsValidSessionId(string? sessionId)
    {
        var value = sessionId?.Trim();
        return !string.IsNullOrWhiteSpace(value) && SessionId.IsMatch(value);
    }

    public static bool IsValidPaymentLinkUid(string? paymentLinkUid) =>
        IsValidSessionId(paymentLinkUid);

    public static string? ValidateAllowedAsset(MakePayPromptDetails prompt, JToken body)
    {
        if (body is not JObject payload)
        {
            return "Expected a JSON object.";
        }

        var allowedAssets = ParseAllowedAssets(prompt.AllowedAssetIdentifiers);
        if (allowedAssets.Count == 0)
        {
            return null;
        }

        var sellAsset = NormalizeAssetKey(ReadString(payload, "sellAsset"));
        return sellAsset is not null && allowedAssets.Contains(sellAsset)
            ? null
            : "Selected asset is not allowed for this invoice.";
    }

    public static JToken ApplyCheckoutRequestPolicy(
        MakePayPaymentMethodConfig config,
        MakePayPromptDetails prompt,
        JToken body)
    {
        if (body is not JObject payload)
        {
            return body;
        }

        ApplyReceiptEmailPolicy(prompt, payload);
        ApplyRefundAddressPolicy(config, prompt, payload);
        return payload;
    }

    public static JToken ApplyRefundAddressPolicy(
        MakePayPaymentMethodConfig config,
        MakePayPromptDetails prompt,
        JToken body)
    {
        if (body is not JObject payload)
        {
            return body;
        }

        if (!string.Equals(
                prompt.RefundAddressMode,
                MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet,
                StringComparison.Ordinal))
        {
            return body;
        }

        var address = FindMerchantRefundAddress(
            config,
            prompt,
            ReadString(payload, "sellAsset"),
            out var chain);
        if (string.IsNullOrWhiteSpace(address))
        {
            var payerAddress = ReadString(payload, "refundAddress") ?? ReadString(payload, "sourceAddress");
            if (!string.IsNullOrWhiteSpace(payerAddress))
            {
                payload["refundAddress"] = payerAddress;
                payload["sourceAddress"] = payerAddress;
            }
            else
            {
                payload.Remove("refundAddress");
                payload.Remove("sourceAddress");
            }

            return payload;
        }

        var normalizedAddress = MakePayApiClient.NormalizeAddressForMakePay(chain, address);
        payload["refundAddress"] = normalizedAddress;
        payload["sourceAddress"] = normalizedAddress;
        return payload;
    }

    private static void ApplyReceiptEmailPolicy(MakePayPromptDetails prompt, JObject payload)
    {
        if (prompt.RequestReceiptEmailFromCustomer)
        {
            return;
        }

        var defaultReceiptEmail = prompt.DefaultReceiptEmail?.Trim();
        if (string.IsNullOrWhiteSpace(defaultReceiptEmail))
        {
            payload.Remove("receiptEmail");
            return;
        }

        payload["receiptEmail"] = defaultReceiptEmail;
    }

    public static string? FindMerchantRefundAddress(
        MakePayPaymentMethodConfig config,
        MakePayPromptDetails prompt,
        string? sellAsset) =>
        FindMerchantRefundAddress(config, prompt, sellAsset, out _);

    public static string? FindMerchantRefundAddress(
        MakePayPaymentMethodConfig config,
        MakePayPromptDetails prompt,
        string? sellAsset,
        out string? chain)
    {
        var addresses = config.GetChainAddresses().ToList();
        if (!addresses.Any(address => string.Equals(address.Chain, "BTC", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(prompt.SettlementAddress) &&
            string.Equals(prompt.SettlementCurrency, "BTC", StringComparison.OrdinalIgnoreCase))
        {
            addresses.Add(new MakePayChainAddress
            {
                Chain = "BTC",
                Address = prompt.SettlementAddress
            });
        }

        chain = ResolveChainFromSellAsset(sellAsset, addresses);
        var resolvedChain = chain;
        return resolvedChain is null
            ? null
            : addresses.FirstOrDefault(address =>
                string.Equals(address.Chain, resolvedChain, StringComparison.OrdinalIgnoreCase))?.Address;
    }

    private static string? ResolveChainFromSellAsset(
        string? sellAsset,
        IReadOnlyCollection<MakePayChainAddress> addresses)
    {
        var value = sellAsset?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var knownChains = addresses
            .Select(address => address.Chain)
            .Where(chain => !string.IsNullOrWhiteSpace(chain))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (knownChains.Count == 0)
        {
            return null;
        }

        var parts = value
            .Split(['.', '-', ':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant());
        foreach (var part in parts)
        {
            if (knownChains.Contains(part))
            {
                return part;
            }
        }

        var normalized = value.ToUpperInvariant();
        return knownChains.Contains(normalized)
            ? normalized
            : null;
    }

    private static string? ValidateOptionalString(JObject payload, string propertyName, int maxLength)
    {
        if (payload[propertyName] is null)
        {
            return null;
        }

        var value = ReadString(payload, propertyName);
        return value is null || value.Length > maxLength
            ? $"Invalid {propertyName}."
            : null;
    }

    private static string? ReadString(JObject payload, string propertyName) =>
        payload[propertyName]?.Type == JTokenType.String
            ? payload[propertyName]?.Value<string>()
            : null;

    private static HashSet<string> ParseAllowedAssets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeAssetKey)
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? NormalizeAssetKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Regex.Replace(
                value.Trim().ToUpperInvariant(),
                "\\s*([-.:])\\s*",
                "$1")
            .Trim();
    }
}
