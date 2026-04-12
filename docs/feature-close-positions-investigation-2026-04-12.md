# Feature: Close Positions — Root Cause Investigation and Contingent Fixes (2026-04-12)

## Objective
Root-cause why the bot is systematically losing money on closed positions across the full 31-position history. Produce an evidence-backed analysis document (`docs/analysis-close-positions-2026-04-12.md`), then implement only the fixes that the evidence justifies. This is an **investigation-first feature** — code changes are contingent on analysis outputs, not pre-specified.

## Evidence Source
- User report 2026-04-11: from the Trade Analytics view, the pattern is clear — positions are systematically bleeding money on closes
- Live DB query (2026-04-11): 31 closed positions, 21% win rate, net -$1.92 strategy / -$3.01 exchange PnL
- Academic/practitioner research on cross-exchange perpetual arbitrage close strategies (see Research References below)
- Companion investigation into the strategy-vs-exchange PnL gap in `docs/feature-dashboard-pnl-fixes-2026-04-11.md` Issue 5

## Constraint — investigation comes first
**No code changes until the investigation tasks below produce their outputs.** Fixes A-H are contingent on evidence; the pipeline must NOT pre-implement them in anticipation. If the analysis output file is missing or incomplete, halt the feature and escalate.

---

## Evidence — historical data (live DB, 2026-04-11 snapshot)

**Aggregate:**
- **31 total closed positions in history**
- **Win rate: 21%** (5 wins, 19 losses, net -$1.92 strategy / -$3.01 exchange)
- **Average losing trade: -$0.13 strategy PnL, -$0.16 exchange PnL**
- **Average winning trade: +$0.11 strategy PnL, +$0.07 exchange PnL**
- **Loss-to-win ratio: 1.18×** — even when we win, we win less than when we lose. Math is not in our favour.
- **Total exchange vs strategy PnL gap: -$1.09** (exchange PnL is $1.09 worse than strategy PnL across all 31 closes) — cumulative "phantom PnL" locked in by closing during cross-exchange divergence events.

**Close reason distribution (ranked by frequency):**

| Reason | Count | Avg Strategy PnL | Avg Exchange PnL | Avg Hours Held | Implied issue |
|---|---|---|---|---|---|
| `DivergenceCritical` (13) | 9 | -$0.17 | -$0.09 | **0.5** | **#1 loss driver; closes at 0.5h — way before fees amortize** |
| `ExchangeDrift` (8) | 4 | +$0.02 | -$0.00 | 0.3 | Mixed — not a systematic loser |
| `FundingFlipped` (11) | 4 | -$0.09 | -$0.13 | 0.7 | Legitimate close (strategy invalidated) but hits before fee amortization |
| `SpreadCollapsed` (0) | 3 | -$0.12 | -$0.61 | 3.2 | Exchange PnL is 5× worse than strategy — divergence-driven panic |
| `PnlTargetReached` (3) | 3 | **-$0.02** | +$0.04 | 7.1 | **Close reason called "PnlTargetReached" produces losses — formula bug?** |
| `Manual` (5) | 1 | +$0.26 | +$0.08 | 1.1 | The only convincing win — you closed it yourself |

**Per-exchange-pair performance:**

| Long/Short | N | Win rate | Total PnL | Avg PnL | Avg Hours |
|---|---|---|---|---|---|
| Aster / Hyperliquid | 3 | 33% | -$0.09 | -$0.03 | 0.1 |
| Hyperliquid / Aster | 1 | 0% | -$0.15 | -$0.15 | 0.6 |
| Lighter / Hyperliquid | 9 | 22% | -$0.24 | -$0.03 | 1.7 |
| **Lighter / Aster** | **11** | **18%** | **-$1.44** | **-$0.13** | **2.3** |

**Lighter/Aster is the dominant loss source** — 11 positions, 2/11 win rate, -$1.44 total. It accounts for **75% of all losses** on its own.

---

## Findings

**Finding 1: `DivergenceCritical` closes are firing too often and too early.**
9 of 31 closes (29%) are `DivergenceCritical`, all with ~0.5h average hold. That's before any reasonable fee amortization window and essentially locks in the divergence loss. Per academic research on cross-exchange perpetual arb, **divergence is transient noise that the strategy bets on reverting** — closing on divergence defeats the strategy's core thesis. Sources: [BSIC Perpetual Complexity](https://bsic.it/perpetual-complexity-an-introduction-to-perpetual-future-arbitrage-mechanics-part-1/), [Quant Arb — Small Trader Alpha #6](https://www.algos.org/p/small-trader-alpha-6-perpetual-arbitrage).

The `BotConfiguration.UseRiskBasedDivergenceClose` flag (line 153, default `true`) is intended as a safety gate — it only allows DivergenceCritical closes when the position is past `MinHoldTimeHours` AND liquidation distance has fallen below `LiquidationEarlyWarningPct`. But:
- The flag is currently not exposed in the admin form (companion feature, Issue 6), so operators cannot verify or adjust it.
- Historical data predates the flag's default-on state — many of the 9 closes fired under the old "any divergence breach = immediate close" logic.
- Even with the flag on, the 0.5h average hold time suggests something else is driving early closes that get *recorded* as DivergenceCritical. Could be a race between multiple close reasons in `DetermineCloseReason` — investigate priority order.

**Finding 2: `PnlTargetReached` closes produce negative average PnL (-$0.02).**
`PnlTargetReached` is supposed to mean "funding target hit, take profit". Instead it's closing losers. The close formula is:
```
close when AccumulatedFunding >= TargetPnlMultiplier × entryFees
```
This only checks accumulated *funding*, not *unified PnL*. If funding has accumulated to $0.10 but the price has moved against us by $0.15, the position hits the "target" but has a net unified PnL of -$0.05. It closes as a loser.

**Fix direction:** change the close condition to require BOTH `AccumulatedFunding >= target × entryFees` AND `UnifiedPnl > 0`. Don't take profit when you don't actually have profit.

**Finding 3: Exchange PnL (not strategy PnL) is being used for some close decisions.**
The `SpreadCollapsed` reason's 3 closes show strategy avg -$0.12 but exchange avg -$0.61. A 5× divergence between the two values strongly suggests the close decision is being gated on exchange PnL, not strategy PnL. Exchange PnL bakes in cross-exchange price noise that reverts — closing on it locks in phantom losses.

**Fix direction:** audit `PositionHealthMonitor.DetermineCloseReason` to ensure it uses `unifiedUnrealizedPnl` (strategy PnL) for profitability checks. Exchange PnL should ONLY gate decisions that are about exchange-side margin risk (liquidation, margin utilization alert), not about strategy-level profit.

**Finding 4: `StopLoss` is likely firing on exchange PnL too.**
`BotConfiguration.StopLossPct = 0.10` (10% of margin). The stop loss is firing on exchange PnL, which can look like -10% during a divergence event and then recover. Same fix as Finding 3: use `unifiedUnrealizedPnl` for stop-loss comparisons.

**Finding 5: Lighter/Aster is the dominant loss source.**
11 positions, 18% win rate, -$1.44 total. It's the pair the bot enters most often (largest count) AND the one that loses most often. Possible causes:
- **Aster's fee rate** may be mis-recorded. Check `Exchanges.TakerFeeRate` for Aster — the seed has it at `NULL` (falls back to `ExchangeFeeConstants`), but diagnostic queries showed 4 bps. Verify it's actually 4 bps on Aster (their docs may have it higher).
- **Lighter's rebate handling** may be broken. The 15% rebate is supposed to improve Lighter's net yield on the paying side of funding — check `SignalEngine.cs:212-227` that the sign direction is correct and the boost is actually applied on entry scoring.
- **Lighter/Aster price divergence pattern** — these are both non-Binance DEXes; they may have systematically different price feeds (e.g., one uses their own AMM oracle, the other uses a CEX reference), which would produce a baseline divergence that doesn't revert. If that's the case, the strategy should EXIT this pair from the scanner's eligible set.

**Finding 6: Hold time is too short for fee amortization.**
`FeeAmortizationHours = 24` means the bot projects fees being recovered over 24 hours. But actual average hold times across close reasons are:
- DivergenceCritical: 0.5h
- ExchangeDrift: 0.3h
- FundingFlipped: 0.7h
- SpreadCollapsed: 3.2h
- PnlTargetReached: 7.1h

**Not one of them reaches 24 hours.** The amortization assumption is never validated in practice. Options: (a) lower `FeeAmortizationHours` to something closer to median hold time (say 2h) so entry scoring penalises marginal opportunities correctly, or (b) extend holds via stricter close conditions so the 24h projection is actually achievable.

**Finding 7: Position sizing is too small for fee amortization.**
Current typical position is `size=$27, leverage=3, notional=$81`. Entry fees on Lighter/Aster (~4 bps each × 2 legs × 2 round-trip) = 16 bps × $81 notional = $0.13. That's more than a typical winning trade ($0.11). **Fees alone can eat an entire winning position.** Larger position sizes amortize fixed fees better per dollar of edge. Alternative: go for pairs with known-zero fees (Lighter has 0 bps taker + 15% rebate — but only on the receiving side).

---

## Required Investigation Tasks
(These produce evidence that drives the fixes; each one has a concrete output)

1. **Audit `DetermineCloseReason` priority order** at `PositionHealthMonitor.cs` — document which close reasons fire first when multiple would apply. Propose a new priority that demotes `DivergenceCritical` below `StopLoss` and `MaxHoldTime`.
2. **Counterfactual replay on the 9 DivergenceCritical closes** — for each, compute the strategy PnL if the position had been held for +1h, +4h, +24h using `FundingRateSnapshots` history. If the median counterfactual at +4h is materially better than actual, that confirms the close was premature.
3. **Verify `UseRiskBasedDivergenceClose` flag state** in production — was it applied to any of the 9 historical closes, or all predating it? Cross-reference position close timestamps with when the flag defaulted to `true`.
4. **Audit `PnlTargetReached` formula** — find the check in `PositionHealthMonitor` or `BotOrchestrator` and confirm whether it requires `UnifiedPnl > 0`. If not, that's the fix.
5. **Grep for all uses of `exchangePnl` / `unrealizedPnl` in close-decision code** (`PositionHealthMonitor.DetermineCloseReason`, `BotOrchestrator`, `ExecutionEngine`) and compare with `unifiedUnrealizedPnl`. Every use in a profitability-check context should use unified.
6. **Lighter/Aster drill-down** — pull all 11 closed positions on this pair, compute entry-to-exit spread trajectory, identify whether the pair has a systematic positive-to-negative spread flip pattern, and decide whether to (a) blacklist the pair, (b) fix the fee config, or (c) fix the rebate wiring.
7. **Fee amortization reality check** — compute the median hold time across all closed positions, compare with `FeeAmortizationHours`, and propose a new value. If median is 1.5h, `FeeAmortizationHours` should be 2-4h, not 24.
8. **Minimum viable position size** — given the cheapest exchange pair's fees, compute the minimum position notional at which a 0.02%/hr spread over 2h hold yields net positive (after entry + exit fees). This is the floor below which any position is a statistical loser. Update `MinPositionSizeUsdc` to at least this value.

### Required Analysis Output
All 8 investigation tasks must produce concrete numbers, stored in a new file `docs/analysis-close-positions-2026-04-12.md` with one section per task. Each section must include:
- The raw query / grep / code location
- The resulting data or finding
- A YES/NO recommendation on whether the corresponding Fix (A-H) is justified
- If YES, the specific change proposed (file:line + diff sketch)
- If NO, the reason (e.g., "counterfactual shows closes were actually saving us from worse losses — keep current logic")

Only after `docs/analysis-close-positions-2026-04-12.md` is complete and committed may the implementation phase begin.

---

## Contingent Fixes (implement ONLY those the analysis justifies)

- **Fix A**: change `PnlTargetReached` to require `UnifiedPnl > 0` in addition to the funding threshold.
- **Fix B**: change `StopLoss`, `SpreadCollapsed`, `ExchangeDrift` close conditions to use `unifiedUnrealizedPnl` instead of `unrealizedPnl` where appropriate (keep `unrealizedPnl` only for margin/liquidation gating).
- **Fix C**: demote `DivergenceCritical` in `DetermineCloseReason` priority; require both (a) past `MinHoldTimeHours`, (b) liquidation risk condition, (c) divergence > `DivergenceAlertMultiplier × 2 × entrySpreadCostPct` (i.e., a stricter critical threshold than the alert threshold).
- **Fix D**: audit Aster taker fee; if incorrect, update `Exchanges.TakerFeeRate` for Aster.
- **Fix E**: audit Lighter rebate application in `SignalEngine.cs:212-227`; if wrong direction or wrong magnitude, fix and re-test.
- **Fix F**: adjust `FeeAmortizationHours` downward based on observed median hold time.
- **Fix G**: optionally blacklist Lighter/Aster pair if the drill-down finds a structural issue (or add it to `ExchangePairBlacklist` config, which doesn't exist yet — would need a new field).
- **Fix H**: add a `MinPositionSizeUsdc` floor derived from fee math.

### Required Tests
- Unit test per fix — each fix (A through H) gets a test verifying the new behaviour.
- Counterfactual test: a synthetic position with known PnL trajectory is closed by each close reason, and the counterfactual replay produces expected "would have been" numbers.
- Integration test: run the full close-reason decision tree against a seeded position in each state (post-stop-loss, post-divergence, funding-flipped, PnL-target-hit-but-price-adverse) and verify the right reason fires.

---

## Constraints
- **No code changes until the investigation tasks (1-8 above) produce their outputs in `docs/analysis-close-positions-2026-04-12.md`.** Fixes A-H are contingent on evidence — don't pre-implement them.
- Do NOT change `BotConfiguration.StopLossPct`, `BotConfiguration.MinHoldTimeHours`, or any threshold value as part of this feature — those are risk decisions that need a separate approval. This feature changes the CODE to use the right inputs; tuning is a follow-up.
- Follow the conventions in `.claude/CLAUDE.md` (.NET architecture, DI, data access, testing).
- Preserve all existing unit tests — run `dotnet test tests/FundingRateArb.Tests.Unit` as a regression guard.
- The analysis output file is committed; plan/prompts/progress/review are NOT per `.git/info/exclude` convention.

## Research References
- [BSIC — Perpetual Complexity: An Introduction to Perpetual Future Arbitrage Mechanics](https://bsic.it/perpetual-complexity-an-introduction-to-perpetual-future-arbitrage-mechanics-part-1/)
- [Quant Arb — Small Trader Alpha #6: Perpetual Arbitrage](https://www.algos.org/p/small-trader-alpha-6-perpetual-arbitrage)
- [Boros_Fi — Cross-Exchange Funding Rate Arbitrage: A Fixed-Yield Strategy](https://medium.com/boros-fi/cross-exchange-funding-rate-arbitrage-a-fixed-yield-strategy-through-boros-c9e828b61215)
- [ScienceDirect — Risk and Return Profiles of Funding Rate Arbitrage on CEX and DEX](https://www.sciencedirect.com/science/article/pii/S2096720925000818)
- [Polynomial Trade — Funding Rate Arbitrage 101](https://docs.trade.polynomial.fi/strategies-and-tools/funding-rate-arbitrage-101)
- [Mastering Funding Rate Arbitrage in Crypto — Medium/Xulian](https://medium.com/@Xulian0x/mastering-funding-rate-arbitrage-in-crypto-a-comprehensive-guide-27b4c3bb0f90)
- [Amberdata Blog — Funding Rates: How They Impact Perpetual Swap Positions](https://blog.amberdata.io/funding-rates-how-they-impact-perpetual-swap-positions)

## Run Log
<!-- pipeline-managed -->
