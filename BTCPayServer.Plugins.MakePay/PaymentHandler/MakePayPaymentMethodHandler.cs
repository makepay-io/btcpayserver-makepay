#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly MakePayApiClient _makePayApiClient;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly StoreRepository _storeRepository;
    private readonly MakePaySecretProtector _secretProtector;
    private readonly ILogger<MakePayPaymentMethodHandler> _logger;

    public MakePayPaymentMethodHandler(
        MakePayApiClient makePayApiClient,
        BTCPayWalletProvider walletProvider,
        IServiceProvider serviceProvider,
        StoreRepository storeRepository,
        MakePaySecretProtector secretProtector,
        ILogger<MakePayPaymentMethodHandler> logger)
    {
        _makePayApiClient = makePayApiClient;
        _walletProvider = walletProvider;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _secretProtector = secretProtector;
        _logger = logger;
        (_, Serializer) = BlobSerializer.CreateSerializer(null as NBitcoin.Network);
    }

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId => MakePayPlugin.MakePayPaymentMethodId;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var config = ParsePaymentMethodConfigInternal(context.PaymentMethodConfig);
        if (!config.Enabled)
        {
            context.State = null;
            return Task.CompletedTask;
        }

        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;

        if (!config.IsConfigured)
        {
            var fallback = config.HasAnonymousSettlement
                ? default
                : ReserveBtcAddress(context.Store);
            context.State = new PrepareState
            {
                Config = config,
                ReserveAddress = fallback.ReserveAddress,
                WalletUnavailableReason = fallback.WalletUnavailableReason
            };
            return Task.CompletedTask;
        }

        var reserve = ReserveBtcAddress(context.Store);

        context.State = new PrepareState
        {
            Config = config,
            ReserveAddress = reserve.ReserveAddress,
            WalletUnavailableReason = reserve.WalletUnavailableReason
        };
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is not PrepareState state)
        {
            throw new PaymentMethodUnavailableException("MakePay payment method is not prepared.");
        }

        if (!string.IsNullOrWhiteSpace(state.WalletUnavailableReason))
        {
            throw new PaymentMethodUnavailableException(state.WalletUnavailableReason);
        }

        if (state.Config.IsConfigured && state.ReserveAddress is null)
        {
            throw new PaymentMethodUnavailableException("Unable to reserve a BTC settlement address.");
        }

        var config = state.Config;
        var invoice = context.InvoiceEntity;
        var settlementAddress = state.ReserveAddress is null
            ? string.Empty
            : (await state.ReserveAddress).Address.ToString();
        var btcAmount = context.Prompt.Calculate().TotalDue;
        if (btcAmount <= 0m)
        {
            throw new PaymentMethodUnavailableException("The MakePay BTC settlement amount is too small.");
        }

        var baseUrl = config.NormalizedSiteUrl();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new PaymentMethodUnavailableException("MakePay is missing the BTCPay Server site URL. Set it in the MakePay settings.");
        }

        var webhookUrl = baseUrl + "/plugins/makepay/webhook/" + Uri.EscapeDataString(context.Store.Id);
        var checkoutUrl = baseUrl + "/i/" + Uri.EscapeDataString(invoice.Id);
        MakePayPaymentLinkResponse paymentLink;
        if (config.IsConfigured)
        {
            paymentLink = await _makePayApiClient.CreateConnectedPaymentLink(
                config,
                context.Store.Id,
                invoice.Id,
                btcAmount,
                settlementAddress,
                webhookUrl,
                checkoutUrl);
        }
        else
        {
            var priorities = config.ResolveSettlementPriorities();
            var sourceAddresses = config.GetChainAddresses();
            if (priorities.Count == 0)
            {
                if (state.ReserveAddress is null)
                {
                    throw new PaymentMethodUnavailableException("Set a BTC MakePay settlement address or enable a BTC on-chain wallet before using anonymous payment links.");
                }

                settlementAddress = (await state.ReserveAddress).Address.ToString();
                priorities =
                [
                    new MakePaySettlementPriority
                    {
                        Chain = "BTC",
                        Address = settlementAddress,
                        Asset = "BTC.BTC"
                    }
                ];
            }

            if (!sourceAddresses.Any(address =>
                    string.Equals(address.Chain, "BTC", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(settlementAddress))
            {
                sourceAddresses = sourceAddresses.Concat(
                [
                    new MakePayChainAddress
                    {
                        Chain = "BTC",
                        Address = settlementAddress
                    }
                ]).ToArray();
            }

            paymentLink = await _makePayApiClient.CreateAnonymousPaymentLink(
                config,
                context.Store.Id,
                invoice.Id,
                invoice.Price,
                invoice.Currency,
                priorities,
                sourceAddresses,
                webhookUrl,
                checkoutUrl);
            settlementAddress = priorities[0].Address;
        }

        context.Store.SetPaymentMethodConfig(this, _secretProtector.Protect(config));
        await _storeRepository.UpdateStore(context.Store);

        var promptDetails = new MakePayPromptDetails
        {
            PaymentLinkUid = paymentLink.Uid,
            PaymentLinkUrl = paymentLink.PublicUrl,
            IsAnonymous = paymentLink.IsAnonymous,
            BtcAmount = btcAmount,
            SettlementCurrency = paymentLink.SettlementCurrency ?? config.NormalizedSettlementCurrency(),
            SettlementAsset = paymentLink.SettlementAsset ?? "BTC.BTC",
            SettlementAddress = settlementAddress,
            WebhookSecret = paymentLink.WebhookSecret ?? string.Empty,
            WebhookSecretLast4 = paymentLink.WebhookSecretLast4 ?? string.Empty,
            WebhookUrl = webhookUrl,
            CheckoutBaseUrl = config.NormalizedCheckoutBaseUrl(),
            RequestReceiptEmailFromCustomer = config.RequestReceiptEmailFromCustomer,
            DefaultReceiptEmail = config.DefaultReceiptEmail?.Trim() ?? string.Empty,
            RefundAddressMode = config.NormalizedRefundAddressMode(),
            AllowedAssetIdentifiers = config.AllowedAssetIdentifiers?.Trim() ?? string.Empty
        };

        context.Prompt.Divisibility = 8;
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Destination = paymentLink.Uid;
        context.Prompt.Details = JObject.FromObject(promptDetails, Serializer);
        context.TrackedDestinations.Add(paymentLink.Uid);
        context.AdditionalSearchTerms.Add(paymentLink.Uid);
        if (!string.IsNullOrWhiteSpace(settlementAddress))
        {
            context.AdditionalSearchTerms.Add(settlementAddress);
        }
        foreach (var priority in config.ResolveSettlementPriorities())
        {
            context.AdditionalSearchTerms.Add(priority.Address);
        }
        foreach (var sourceAddress in config.GetChainAddresses())
        {
            context.AdditionalSearchTerms.Add(sourceAddress.Address);
        }

        _logger.LogInformation(
            "Created MakePay prompt for invoice {InvoiceId}: link {PaymentLinkUid}, {Amount} BTC ({Mode})",
            invoice.Id,
            paymentLink.Uid,
            btcAmount.ToString("0.########", CultureInfo.InvariantCulture),
            paymentLink.IsAnonymous ? "anonymous" : "connected");
    }

    public Task AfterSavingInvoice(PaymentMethodContext context)
    {
        return Task.CompletedTask;
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<MakePayPromptDetails>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(MakePayPromptDetails)}.");
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfigInternal(config);
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<MakePayPaymentData>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(MakePayPaymentData)}.");
    }

    private MakePayPaymentMethodConfig ParsePaymentMethodConfigInternal(JToken? config)
    {
        var parsed = config?.ToObject<MakePayPaymentMethodConfig>(Serializer) ??
                     new MakePayPaymentMethodConfig { Enabled = false };
        return _secretProtector.Unprotect(parsed);
    }

    private (Task<NBXplorer.Models.KeyPathInformation>? ReserveAddress, string? WalletUnavailableReason)
        ReserveBtcAddress(StoreData store)
    {
        var handlers = _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var btcWallet = store.GetDerivationSchemeSettings(handlers, "BTC", true);
        if (btcWallet?.AccountDerivation is null)
        {
            return (null, "Enable a BTC on-chain wallet before enabling MakePay.");
        }

        var wallet = _walletProvider.GetWallet("BTC");
        if (wallet is null)
        {
            return (null, "The BTC wallet provider is not available.");
        }

        return (
            wallet.ReserveAddressAsync(
                store.Id,
                btcWallet.AccountDerivation,
                "makepay"),
            null);
    }

    private sealed class PrepareState
    {
        public required MakePayPaymentMethodConfig Config { get; init; }
        public Task<NBXplorer.Models.KeyPathInformation>? ReserveAddress { get; init; }
        public string? WalletUnavailableReason { get; init; }
    }
}
