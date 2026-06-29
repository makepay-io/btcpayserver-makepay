#nullable enable
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MakePay.PaymentHandler;

public class MakePayCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "MakePayCheckout";

    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly MakePaySecretProtector _secretProtector;

    public MakePayCheckoutModelExtension(
        PaymentMethodHandlerDictionary handlers,
        MakePaySecretProtector secretProtector)
    {
        _handlers = handlers;
        _secretProtector = secretProtector;
    }

    public PaymentMethodId PaymentMethodId => MakePayPlugin.MakePayPaymentMethodId;
    public string Image => string.Empty;
    public string Badge => "MP";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not MakePayPaymentMethodHandler handler)
        {
            return;
        }

        var promptDetails =
            (MakePayPromptDetails)handler.ParsePaymentPromptDetails(context.Prompt.Details);
        var config = _secretProtector.Unprotect(
            context.Store.GetPaymentMethodConfig<MakePayPaymentMethodConfig>(
                MakePayPlugin.MakePayPaymentMethodId,
                _handlers) ?? new MakePayPaymentMethodConfig());
        var refundAddressMode = string.Equals(
            promptDetails.RefundAddressMode,
            MakePayPaymentMethodConfig.RefundAddressModePayerEntered,
            System.StringComparison.Ordinal)
            ? MakePayPaymentMethodConfig.RefundAddressModePayerEntered
            : MakePayPaymentMethodConfig.RefundAddressModeMerchantWallet;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        context.Model.AdditionalData["makePayStoreId"] = JToken.FromObject(context.Store.Id);
        context.Model.AdditionalData["makePayIsAnonymous"] = JToken.FromObject(promptDetails.IsAnonymous);
        context.Model.AdditionalData["makePaySettlementAsset"] = JToken.FromObject(promptDetails.SettlementAsset);
        context.Model.AdditionalData["makePayRequestReceiptEmailFromCustomer"] =
            JToken.FromObject(promptDetails.RequestReceiptEmailFromCustomer);
        context.Model.AdditionalData["makePayDisplayQuoteApproval"] =
            JToken.FromObject(config.DisplayQuoteApproval);
        context.Model.AdditionalData["makePayRefundAddressMode"] =
            JToken.FromObject(refundAddressMode);
        context.Model.AdditionalData["makePayAllowedAssetIdentifiers"] =
            JToken.FromObject(config.AllowedAssetIdentifiers?.Trim() ?? string.Empty);
        context.Model.Address = $"{promptDetails.BtcAmount:0.########} BTC";
        context.Model.ShowRecommendedFee = false;
    }
}
