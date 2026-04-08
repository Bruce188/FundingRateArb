# Cross-Exchange USDC Rebalance — Evaluation & Decision

**Date:** 2026-04-09
**Status:** Evaluation complete — **automated cross-exchange USDC transfer NOT recommended at current maturity level**. Monitoring-only approach adopted.
**Reference:** `docs/FundingRateArb-Analysis.md` Section 7.4 (Collateral Rebalancing).

---

## Scope of the decision

The analysis document describes three rebalancing methods (Section 7.4.1):

| Method | Description | Cost | Risk |
|---|---|---|---|
| **A** | Cross-exchange USDC transfer (withdraw → bridge → deposit) | 1–2 USDC bridge fee per leg | Transit exposure: 5–30 min where neither exchange has the funds |
| **B** | Partial close + re-enter at new size | 4× trading fees + slippage on 4 orders | Price may move during close-reopen gap |
| **C** | Monitor only (accept asymmetry) | Zero | Losing leg liquidation risk at high leverage |

Feature #25 in the implementation priorities asks us to "Evaluate and (if feasible) implement automated cross-exchange USDC transfer for rebalancing." The spec explicitly accepts "a documented decision not to implement" as a valid deliverable.

---

## Why Method A is not recommended right now

### 1. Irreversibility and custody risk
Automated Method A requires the bot to sign withdrawals without human approval and manage funds in-transit across bridges. Bridge failures, address poisoning, and smart-contract exploits cause **irrecoverable losses**. Unlike trading losses (which are bounded by position size), a failed bridge transaction can drain the full withdrawal amount. No established crypto trading bot in the FundingRateArb's reference set (50shadesofgwei, Presto, Boros, Gate.io) implements fully automated cross-exchange transfers for this reason.

### 2. Operational complexity vs. benefit at current leverage
The bot's default `MaxLeverageCap` is **3×**, per Feature #24 and the industry consensus from Gate.io / ScienceDirect (Aug 2025). At 3× leverage:
- A 10% price move against the losing leg consumes ~30% of its margin.
- The monitoring + alert + Method C threshold (70% utilization from Feature #13) gives roughly 23% additional price move before liquidation at 3× — that's ~45 minutes at typical ETH realized volatility.
- Method B (partial close + re-enter) completes in under a minute and is already available.

At this leverage tier, the Section 7.4.3 decision tree explicitly recommends "Method C (monitor only) is usually sufficient." Automated Method A is justified only above 5× leverage — which the bot does not permit.

### 3. Bridge heterogeneity makes generic automation unsafe
Per Section 7.4.2, transfer paths vary wildly per exchange pair:
- Hyperliquid ↔ Lighter (both Arbitrum USDC): fastest, shared chain, 5 min round-trip
- Binance ↔ Any DEX: 5–30 min depending on chain choice, multi-step (CEX withdraw → bridge → on-chain deposit)
- dYdX v4 ↔ Everything: requires Cosmos IBC or CCTP, 1–5 min but with Cosmos gas costs
- AsterDEX ↔ Arbitrum DEXs: BSC-native Aster requires a bridge (PancakeSwap / Stargate / Across)

Building a reliable per-pair transfer orchestrator requires integrating four distinct bridge protocols and handling their failure modes (stuck transactions, partial fills, chain reorgs). This is the scope of a standalone project, not a sub-feature of a trading bot.

### 4. Monitoring already provides the safety net
Features #6, #11, #13 are now implemented and together provide:
- Live per-leg margin utilization pulled from exchange APIs (all 5 exchanges now supported)
- Per-leg liquidation price pulled from exchange APIs (fixes the formula-based approximation)
- Leverage-aware alert thresholds (60% for 3–5× leverage, 70% for 1–3×)
- Price-divergence close reasons to exit before margin stress becomes critical
- Automated Method B (close position) when margin utilization crosses alert thresholds

This stack handles the collateral imbalance problem without irreversible on-chain transfers. The user retains full control over when to physically rebalance — typically as a batch at end-of-day or end-of-week.

---

## What IS implemented as part of Feature #25

This evaluation is the deliverable. The following capabilities, added in the same commit series, make Method C (monitor-only) the recommended approach effective:

1. **Per-leg margin utilization from live APIs** — `HyperliquidConnector.GetPositionMarginStateAsync`, `LighterConnector.GetPositionMarginStateAsync`, `BinanceConnector` (existing), `AsterConnector` (existing), `DydxConnector` (not yet exposing margin state — deferred).
2. **Leverage-aware alert thresholds** — `PositionHealthMonitor.CheckMarginUtilization` applies 60% threshold at 3–5× leverage, 70% at 1–3× leverage.
3. **Liquidation distance pulled from exchange APIs** — `PositionHealthMonitor.ComputeLiquidationDistance` prefers API-supplied liquidation prices over the leverage formula.
4. **Divergence-driven close reasons** — `CloseReason.DivergenceCritical` triggers when price divergence exceeds 2× the alert multiplier, exiting before exit slippage dominates.
5. **Collateral imbalance alerts** — already existing via `PositionHealthMonitor` cross-exchange PnL tracking.

---

## Conditions under which to reconsider Method A

Revisit this decision if:

1. `MaxLeverageCap` is raised above 5× — at that point, Method B's round-trip cost becomes material and Method A's amortized cost profile looks better.
2. A single bridge protocol (e.g., CCTP v2 or Circle's native USDC cross-chain) becomes the standard across all supported exchanges, collapsing the per-pair complexity.
3. An established treasury management tool (e.g., Safe{Wallet} + automated execution) offers a reusable abstraction over bridging.
4. The bot is deployed at a size where manual rebalancing operationally dominates — currently not the case with default capital under $100k.

Until then, the recommendation stands: **monitor only, accept asymmetry, rebalance manually at user discretion**.
