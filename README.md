# MakePay BTCPay Server Plugin

This repository contains a standalone BTCPay Server plugin project that adds a native `MAKEPAY` payment method.

The plugin connects a BTCPay store to MakePay through native OAuth, creates MakePay payment links for BTCPay invoices, lets payers use MakePay-supported assets in the BTCPay checkout, and settles BTC on-chain to a fresh receive address from the BTCPay store wallet.

Stores can also run without connecting a MakePay account. In that mode the plugin creates anonymous one-time MakePay payment links and sends the configured chain payment addresses with each invoice link.

## Build

BTCPay plugin builds expect the BTCPay Server source tree as a submodule at:

```text
submodules/btcpayserver
```

Clone and build with:

```bash
git clone --recurse-submodules https://github.com/makepay-io/btcpayserver-makepay.git
cd btcpayserver-makepay
git submodule update --init --recursive
dotnet build BTCPayServer.Plugins.MakePay/BTCPayServer.Plugins.MakePay.csproj
```

The project currently targets BTCPay Server `>= 2.3.5`, matching the live compatibility floor used for installation testing.

Run the focused test suite with:

```bash
dotnet test BTCPayServer.Plugins.MakePay.Tests/BTCPayServer.Plugins.MakePay.Tests.csproj
```

## Release Package

Prebuilt `.btcpay` packages are attached to GitHub releases. Download the latest release asset and upload it through BTCPay Server's plugin management page, or install it through any BTCPay plugin source that references this repository.

## MakeCrypto Requirements

The MakeCrypto app must have the `btcpay-server` native OAuth template migration applied. The plugin uses:

- `POST /oauth/native/installations`
- `POST /oauth/token`
- `POST /api/partner/v1/makepay/webhook-secret`
- `POST /api/partner/v1/makepay/payment-links`

The OAuth scopes are:

```text
company:read makepay:payment-links:read makepay:payment-links:write makepay:settings:read makepay:settings:write
```

Anonymous mode uses the same `POST /api/partner/v1/makepay/payment-links` route without MakePay credential headers. Configure the BTCPay Server site URL and the chain payment addresses in the plugin's Settlement tab before enabling anonymous payments. The BTC chain address is used as the default anonymous settlement route, and the full chain address book is sent as `settlement.sourceAddresses`.

## Checkout Settings

- Allowed currencies are shown to payers as a currency-first picker, then a network picker for the selected currency. Public checkout API routes also reject `quote` and `start` requests whose `sellAsset` is not in the invoice's configured MakePay allowed-currency list.
- `Quote approval` is enabled by default. Disable it to skip the intermediate quote confirmation card and start the MakePay payment immediately after a valid quote.
- `Refund address collection` is enforced server-side. In merchant-wallet mode, public checkout API requests cannot provide their own `refundAddress` or `sourceAddress`; the plugin overwrites those fields with the configured merchant wallet address for the selected pay-in chain, or removes them when no merchant address matches.
- Public checkout API routes are rate-limited with BTCPay Server's public invoice limiter, validate bounded request fields before proxying to MakePay, and are only available while the BTCPay invoice is `New` and unexpired.

## Settlement Trust Model

MakePay session webhooks and reconciliation responses are treated as evidence that the MakePay payment session completed. The plugin records that evidence as a `Processing` MakePay payment on the invoice so BTCPay does not mark the invoice as settled solely because MakePay reported a completed session.

BTCPay final settlement should be based on independent BTC wallet/on-chain confirmation for the merchant-controlled settlement address. This repository intentionally avoids converting MakePay session status directly into a settled BTCPay payment.
