# Step 4 - Add Intelligent Retraining Triggers

## Overview

Add a multi-factor decision engine that intelligently decides when to trigger model retraining based on multiple signals, not just a single threshold.

**New capability:** Autonomous decision-making about when retraining is beneficial.

**Your task:** Enable the decision engine and observe how it weighs multiple factors to recommend retraining.

## Concepts

### Multi-Factor Decision Making

Instead of simple rules like "retrain if accuracy < 80%", use weighted scoring across multiple factors:

| Factor               | Weight | Trigger Condition                       |
| -------------------- | ------ | --------------------------------------- |
| Accuracy Degradation | 0.30   | < 90% of baseline accuracy              |
| Low Confidence Rate  | 0.25   | > 30% predictions with confidence < 0.6 |
| Data Growth          | 0.15   | 50% more labeled data than baseline     |
| Recent Anomalies     | 0.20   | High severity anomalies detected        |
| Model Age            | 0.10   | > 7 days since last training            |

**Decision:** Retrain if total score ≥ 0.5

### Why Multi-Factor?

**Single-factor problems:**

- Accuracy drops temporarily (spike) → premature retraining
- Model is fine but old → unnecessary retraining
- Plenty of new data but model still good → waste resources

**Multi-factor benefits:**

- Multiple weak signals → strong recommendation
- Single spike → doesn't trigger (score too low)
- Comprehensive view of model health

### Safety Guards

1. **Minimum data:** Need 10+ labeled observations
2. **Rate limiting:** Minimum 1 hour between retraining
3. **Logging:** All decisions recorded for audit

## Tasks

### TASK 1: Register RetrainingDecisionEngine

Uncomment in `Program.cs`:

```csharp
builder.Services.AddSingleton<RetrainingDecisionEngine>();
```

### TASK 2: Enable Retraining Check Endpoint

Uncomment `/check-retraining` endpoint. This lets you manually trigger a decision check.

### TASK 3: Enable Decision History Endpoint

Uncomment `/decision-history` to view all past decisions.

### TASK 4: Enable Retraining History Endpoint

Uncomment `/retraining-history` to view actual retraining events.

## Test

Run the API:

```powershell
dotnet run
```

Follow test sequence in `test-commands.md` to:

1. Generate baseline performance
2. Introduce conditions that trigger retraining
3. Observe decision engine scoring
4. View decision history

## Discussion Questions

**Before uncommenting:**

1. **Scoring Weights:**

   - Accuracy degradation = 0.30 (highest). Is this right for your domain?
   - Model age = 0.10 (lowest). Should freshness matter more?

2. **Threshold:**

   - Total score ≥ 0.5 triggers retraining. Too sensitive? Too conservative?
   - What happens if threshold is 0.3? 0.7?

3. **Rate Limiting:**
   - Minimum 1 hour between retraining. Appropriate?
   - What if drift happens faster? Slower?

**After testing:**

1. **Factor Analysis:**

   - Which factors triggered in your tests?
   - Were the weights appropriate?

2. **Decision Quality:**
   - Did the engine recommend retraining when you expected?
   - Any false positives (recommended when shouldn't)?

## Key Insights

### Scoring Example

```
Scenario: Model degrading slowly
- Accuracy: 82% (baseline: 95%) → ratio = 0.86 < 0.90 → Score: 0.30
- Low confidence: 35% → > 0.30 → Score: 0.25
- Data growth: 50 labeled (baseline: 40) → ratio = 1.25 < 1.50 → Score: 0.00
- Anomalies: 1 medium → Score: 0.10
- Model age: 3 days → < 7 days → Score: 0.00

Total: 0.30 + 0.25 + 0.00 + 0.10 + 0.00 = 0.65 ≥ 0.5 → RETRAIN
```

**Insight:** Multiple moderate issues trigger action, even though no single factor is critical.

### vs Simple Threshold

| Approach       | Accuracy | Decision | Reasoning                            |
| -------------- | -------- | -------- | ------------------------------------ |
| Simple (< 80%) | 82%      | ✗ No     | Above threshold                      |
| Multi-factor   | 82%      | ✓ Yes    | Combined with confidence & anomalies |

**Insight:** Context-aware decisions are more intelligent than hard thresholds.

## Production Considerations

### Tuning for Your Domain

**High-stakes (healthcare, finance):**

```csharp
_accuracyDegradationThreshold = 0.95; // Very sensitive to accuracy drops
_minTimeBetweenRetraining = TimeSpan.FromHours(24); // Don't change too fast
```

**Fast-changing (recommendation systems):**

```csharp
_maxModelAge = TimeSpan.FromDays(1); // Fresh models critical
_minTimeBetweenRetraining = TimeSpan.FromMinutes(30); // Rapid updates OK
```

### Explainability

Every decision includes:

- `ShouldRetrain`: Boolean decision
- `ConfidenceScore`: Numeric justification
- `Triggers`: Human-readable reasons
- `FactorScores`: Individual component scores

This supports:

- Debugging ("Why did it retrain?")
- Compliance ("Show me the decision log")
- Tuning ("Accuracy factor too sensitive")

### Integration with Step 3

Background monitoring service now:

1. Collects performance snapshot (every 30s)
2. Runs anomaly detection
3. **Calls decision engine**
4. Logs recommendation

In Step 5/6, the system will **execute** the retraining, not just recommend it.

## Architecture Evolution

| Step     | Capability                  | Autonomy Level       |
| -------- | --------------------------- | -------------------- |
| Step 1-2 | Manual retraining           | Human-driven         |
| Step 3   | Automatic monitoring        | Semi-autonomous      |
| Step 4   | Intelligent decision engine | Autonomous advice    |
| Step 5   | + Safe execution            | Autonomous action    |
| Step 6   | + Governance                | Trustworthy autonomy |

**We are here:** System recommends actions but doesn't execute (yet).

## Common Pitfalls

### 1. Over-Retraining

**Symptom:** Retraining every hour

**Cause:** Threshold too low or weights too high

**Fix:** Increase threshold (0.5 → 0.7) or reduce weights

### 2. Under-Retraining

**Symptom:** Never recommends retraining even when needed

**Cause:** Threshold too high or missing factors

**Fix:** Lower threshold or add more factors (e.g., error rate spikes)

### 3. Noise Sensitivity

**Symptom:** Random fluctuations trigger retraining

**Cause:** Short baseline, no smoothing

**Fix:** Use longer baseline (10 snapshots vs 5) or moving average

## Next

Proceed to `Step5-ModelValidationDeployment` to **execute** the retraining decision safely with shadow mode and canary deployment.
