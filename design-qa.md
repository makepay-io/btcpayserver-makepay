# Cash App embedded checkout design QA

## Reference and implementation

- Reference: `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/btcpay-makepay-audit/00-reference-cash-app-card.png`
- Implemented payment options: `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/btcpay-makepay-audit/05-v160-payment-options.png`
- Implemented region enforcement: `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/btcpay-makepay-audit/06-v160-region-enforcement.png`
- Viewport: 874 × 874 CSS pixels

## Review

- The Cash App card preserves BTCPay typography, spacing, border, dark theme,
  amount placement, and the existing green Cash App treatment.
- The hosted-checkout description was replaced with “Pay securely in Cash
  App,” matching the embedded behavior.
- Selection remains inside the BTCPay invoice and presents a native Cash App
  summary, quote action, USD 950 disclosure, and inline errors.
- The transfer state uses the existing BTCPay card/button system for the USD
  amount, expiry, QR, “Open Cash App” action, payment-request copy action, and
  live status.
- The live QA browser was outside the eligible US region, so the authoritative
  MakePay route rejected quote creation before a provider QR was issued. This
  rejection was rendered correctly in the native BTCPay state, and the invoice
  URL remained open. The QR/deep-link state is covered by the checkout source
  contract and JavaScript syntax checks.
- No clipping, horizontal overflow, broken controls, console errors, or
  unintended MakePay page navigation were observed.

final result: passed
