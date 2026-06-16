#nullable enable
using System;
using System.Globalization;
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
        if (!config.Enabled || !config.IsConfigured)
        {
            context.State = null;
            return Task.CompletedTask;
        }

        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;

        var handlers = _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var btcWallet = context.Store.GetDerivationSchemeSettings(handlers, "BTC", true);
        if (btcWallet?.AccountDerivation is null)
        {
            context.State = new PrepareState
            {
                Config = config,
                WalletUnavailableReason = "Enable a BTC on-chain wallet before enabling MakePay."
            };
            return Task.CompletedTask;
        }

        var wallet = _walletProvider.GetWallet("BTC");
        if (wallet is null)
        {
            context.State = new PrepareState
            {
                Config = config,
                WalletUnavailableReason = "The BTC wallet provider is not available."
            };
            return Task.CompletedTask;
        }

        context.State = new PrepareState
        {
            Config = config,
            ReserveAddress = wallet.ReserveAddressAsync(
                context.Store.Id,
                btcWallet.AccountDerivation,
                "makepay")
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

        if (state.ReserveAddress is null)
        {
            throw new PaymentMethodUnavailableException("Unable to reserve a BTC settlement address.");
        }

        var config = state.Config;
        var invoice = context.InvoiceEntity;
        var settlementAddress = (await state.ReserveAddress).Address.ToString();
        var btcAmount = context.Prompt.Calculate().TotalDue;
        if (btcAmount <= 0m)
        {
            throw new PaymentMethodUnavailableException("The MakePay BTC settlement amount is too small.");
        }

        var baseUrl = (config.SiteUrl ?? string.Empty).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new PaymentMethodUnavailableException("MakePay is missing the BTCPay Server site URL. Reconnect MakePay.");
        }

        var webhookUrl = baseUrl + "/plugins/makepay/webhook/" + Uri.EscapeDataString(context.Store.Id);
        var checkoutUrl = baseUrl + "/i/" + Uri.EscapeDataString(invoice.Id);
        var paymentLink = await _makePayApiClient.CreatePaymentLink(
            config,
            context.Store.Id,
            invoice.Id,
            btcAmount,
            settlementAddress,
            webhookUrl,
            checkoutUrl);

        context.Store.SetPaymentMethodConfig(this, _secretProtector.Protect(config));
        await _storeRepository.UpdateStore(context.Store);

        var promptDetails = new MakePayPromptDetails
        {
            PaymentLinkUid = paymentLink.Uid,
            PaymentLinkUrl = paymentLink.PublicUrl,
            BtcAmount = btcAmount,
            SettlementAddress = settlementAddress,
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
        context.AdditionalSearchTerms.Add(settlementAddress);

        _logger.LogInformation(
            "Created MakePay prompt for invoice {InvoiceId}: link {PaymentLinkUid}, {Amount} BTC to {Address}",
            invoice.Id,
            paymentLink.Uid,
            btcAmount.ToString("0.########", CultureInfo.InvariantCulture),
            settlementAddress);
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

    private sealed class PrepareState
    {
        public required MakePayPaymentMethodConfig Config { get; init; }
        public Task<NBXplorer.Models.KeyPathInformation>? ReserveAddress { get; init; }
        public string? WalletUnavailableReason { get; init; }
    }
}
