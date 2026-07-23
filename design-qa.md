# MakePay Statistics and Client-side Payments — Design QA

## Evidence

- Source visual truth:
  - `/var/folders/70/d8rtf1ds5f14stgc4yv_4c600000gn/T/TemporaryItems/NSIRD_screencaptureui_Qoz7SP/Screenshot 2026-07-23 at 1.51.24 PM.png`
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/source-current-payments.png`
- Browser-rendered implementation:
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/implementation-statistics-empty.png`
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/implementation-payments-client-cached.png`
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/implementation-statistics-mobile-viewport.png`
- Combined comparison inputs:
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/comparison-payments-normalized.png`
  - `/Users/jozefvojtas/.codex/visualizations/2026/07/22/019f8876-fcf8-7fb3-ada9-70130dc944df/makepay-statistics-dashboard/comparison-statistics-normalized.png`

## Viewports and normalization

- Desktop browser viewport: 1280 × 720 CSS px, device pixel ratio 2.
- Source full-page capture: 1280 × 2193 px.
- Statistics full-page capture: 1280 × 1412 px.
- Payments full-page capture: 1280 × 1223 px.
- Mobile browser viewport and capture: 390 × 844 CSS/px.
- Desktop comparison inputs use matching 1280-pixel-wide captures, cropped to the same top 900 pixels, horizontally combined, then scaled to 1280 × 450 for a readable side-by-side review. The Browser screenshot backend normalizes its DPR 2 capture to CSS-pixel width; DOM measurements confirmed the desktop page had `innerWidth = scrollWidth = 1280`.

## State

- Statistics: connected Demo store, 30-day period, no recorded settled MakePay payments.
- Payments: cached/full session snapshot loaded, 30-day filter active, six matching open sessions.
- Responsive state: Statistics at 390 × 844, single-column KPI and chart layout.

## Findings

- No actionable P0, P1, or P2 visual issues remain.
- Fonts and typography: existing BTCPay font stack, weight hierarchy, input typography, headings, badges, and muted text are preserved.
- Spacing and layout rhythm: cards use BTCPay tile spacing and radii; the four-column desktop grid becomes two columns at tablet size and one column at mobile size. Measured page `scrollWidth` equals viewport width at 1280, 768, and 390 pixels.
- Colors and visual tokens: the implementation uses BTCPay background, tile, primary, secondary, success, warning, and danger tokens. Cash App green is limited to the payment-method legend.
- Image quality and asset fidelity: the existing BTCPay logo and icon sprite remain authoritative. No replacement logos, custom SVGs, placeholder imagery, or CSS illustrations were added.
- Copy and content: “Statistics” is the default MakePay destination; KPI labels, chart labels, empty states, refresh state, cached-data state, and recent-payment labels are concise and consistent with BTCPay terminology.
- Focused region review: the sidebar MakePay hierarchy, filter controls, KPI cards, chart empty states, recent-payments table, and mobile sticky header were reviewed separately because those regions carry the navigation and data-density changes.

## Interaction and browser checks

- `/plugins/{storeId}/makepay` redirects to `/statistics`.
- The Statistics shell and Payments shell both open in under one second on the test instance.
- Payments first renders local recorded/cached data, then replaces it with the open-session snapshot in the background.
- The background session refresh completed with 13 rows without blocking the initial page.
- The 30-day Payments filter updated the URL and table client-side without a full navigation.
- Statistics 7D/30D/90D switching and manual refresh work.
- Manual Statistics refresh transitions from the quiet refresh message to a new “Updated” timestamp.
- Desktop and mobile layouts have no horizontal page overflow.
- Browser console error/warning check: none.

## Comparison history

1. Initial interaction pass found a P2 refresh-state issue: Statistics correctly started a server-side stale-while-revalidate refresh, but the client did not request the newly refreshed snapshot afterward.
2. Fixed by adding a bounded one-second background poll while `isRefreshing` is true.
3. Post-fix browser evidence: manual refresh completed and the visible status changed to `Updated 14:57:21`; no console errors were present.

## Follow-up polish

- P3: the empty chart tiles intentionally retain their normal chart height to prevent layout shift when data arrives. This is acceptable, but a future illustration-free onboarding message could make a brand-new store feel less sparse.

final result: passed
