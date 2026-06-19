#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using BTCPayServer.Plugins.MakePay.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.MakePay;

public class MakePayPlugin : BaseBTCPayServerPlugin
{
    public const string PluginVersion = "0.2.8";
    public static readonly PaymentMethodId MakePayPaymentMethodId = new("MAKEPAY");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.5" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddHttpClient<MakePayApiClient>();
        services.AddSingleton<MakePaySecretProtector>();
        services.AddSingleton<MakePayPaymentRecorder>();
        services.AddSingleton<IPaymentMethodHandler, MakePayPaymentMethodHandler>();
        services.AddSingleton<ICheckoutModelExtension, MakePayCheckoutModelExtension>();
        services.AddHostedService<MakePayInvoiceListener>();

        services.AddUIExtension("store-wallets-nav", "MakePay/StoreNavExtension");
        services.AddUIExtension("checkout-end", "MakePay/CheckoutPaymentMethodExtension");
        services.AddUIExtension("store-invoices-payments", "MakePay/ViewMakePayPaymentData");
        services.AddDefaultPrettyName(MakePayPaymentMethodId, "Other currencies");

        base.Execute(services);
    }
}
