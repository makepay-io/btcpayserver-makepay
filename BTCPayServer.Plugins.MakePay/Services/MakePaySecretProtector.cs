#nullable enable
using System;
using BTCPayServer.Plugins.MakePay.PaymentHandler;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.MakePay.Services;

public class MakePaySecretProtector
{
    private readonly IDataProtector _protector;

    public MakePaySecretProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.MakePay.Config");
    }

    public MakePayPaymentMethodConfig Protect(MakePayPaymentMethodConfig config)
    {
        if (config.SecretsProtected)
        {
            return config;
        }

        config.AccessToken = ProtectValue(config.AccessToken);
        config.RefreshToken = ProtectValue(config.RefreshToken);
        config.DpopPrivateKeyPem = ProtectValue(config.DpopPrivateKeyPem);
        config.WebhookSecret = ProtectValue(config.WebhookSecret);
        config.OAuthState = ProtectValue(config.OAuthState);
        config.OAuthCodeVerifier = ProtectValue(config.OAuthCodeVerifier);
        config.SecretsProtected = true;
        return config;
    }

    public MakePayPaymentMethodConfig Unprotect(MakePayPaymentMethodConfig config)
    {
        if (!config.SecretsProtected)
        {
            return config;
        }

        try
        {
            config.AccessToken = UnprotectValue(config.AccessToken);
            config.RefreshToken = UnprotectValue(config.RefreshToken);
            config.DpopPrivateKeyPem = UnprotectValue(config.DpopPrivateKeyPem);
            config.WebhookSecret = UnprotectValue(config.WebhookSecret);
            config.OAuthState = UnprotectValue(config.OAuthState);
            config.OAuthCodeVerifier = UnprotectValue(config.OAuthCodeVerifier);
            config.SecretsProtected = false;
        }
        catch (Exception ex)
        {
            var message = "MakePay stored secrets could not be unprotected: " + ex.Message;
            config.ClearConnection();
            config.LastError = message;
        }

        return config;
    }

    private string? ProtectValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : _protector.Protect(value);
    }

    private string? UnprotectValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : _protector.Unprotect(value);
    }
}
