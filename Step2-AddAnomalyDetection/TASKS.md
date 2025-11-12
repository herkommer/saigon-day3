# Step 2 - Add Anomaly Detection

## Overview

Add proactive anomaly detection using ML.NET TimeSeries to identify accuracy spikes and change points before they become failures.

**New capability:** Detect when model performance degrades or shifts fundamentally.

**Your task:** Enable anomaly detection endpoints to monitor accuracy patterns over time.

## Concepts

### Spike Detection

**What:** Identifies sudden, temporary changes in a metric (e.g., accuracy drops from 90% to 60% then recovers)

**Use case:** Detect temporary issues like:

- Bad batch of data
- Transient system problem
- One-off edge case

**ML.NET API:** `DetectSpikeBySsa`

### Change Point Detection

**What:** Identifies fundamental, persistent shifts in data distribution (e.g., accuracy permanently drops from 90% to 75%)

**Use case:** Detect fundamental changes like:

- Real-world data distribution shifted (concept drift)
- Model is no longer appropriate
- System behavior permanently changed

**ML.NET API:** `DetectIidChangePoint`

## Tasks

### TASK 1: Register AnomalyDetectionService

Uncomment the service registration in `Program.cs`:

```csharp
builder.Services.AddSingleton<AnomalyDetectionService>();
```

This makes the anomaly detection service available for dependency injection.

### TASK 2: Enable Spike Detection Endpoint

Uncomment the `/detect-spikes` endpoint. This endpoint:

1. Calculates rolling accuracy over labeled observations
2. Runs spike detection algorithm
3. Returns detected anomalies with severity

### TASK 3: Enable Change Point Detection Endpoint

Uncomment the `/detect-changepoints` endpoint. This endpoint:

1. Calculates rolling accuracy over labeled observations
2. Runs change point detection algorithm
3. Returns detected fundamental shifts

### TASK 4: Enable Anomaly History Endpoint

Uncomment the `/anomaly-history` endpoint to view all detected anomalies.

## Test

Run the API:

```powershell
dotnet run
```

Follow the comprehensive test sequence in `test-commands.md` to:

1. Generate labeled observations with intentional accuracy patterns
2. Detect spikes (sudden changes)
3. Detect change points (persistent shifts)

## Discussion Questions

**Before uncommenting:**

1. **Spike vs Change Point:**

   - What's the difference between a temporary spike and a permanent shift?
   - How should you respond differently to each?

2. **Sensitivity:**

   - Spike detection uses `confidence: 95`. What happens at 99%? At 80%?
   - More sensitive = more false positives, less sensitive = miss real issues

3. **Action Triggers:**
   - Should spike detection trigger immediate retraining?
   - Should change point detection trigger automatic retraining?
   - When should humans be alerted vs auto-response?

**After testing:**

1. **Pattern Recognition:**

   - Look at the `accuracyHistory` in test results. Can you see the patterns?
   - Did the algorithms detect what you expected?

2. **Production Implications:**
   - How often should you run anomaly detection? Every minute? Hour? Day?
   - What's the cost of running these algorithms?

## Key Insights

### Proactive vs Reactive

| Approach  | Detection Method      | Response Time  | Example                            |
| --------- | --------------------- | -------------- | ---------------------------------- |
| Reactive  | Circuit breaker opens | After failure  | API returns 503, then use fallback |
| Proactive | Anomaly detected      | Before failure | Accuracy dropping, retrain now     |

**Insight:** Anomaly detection moves you from "respond to failure" to "prevent failure"

### Drift Types

| Type          | Detection Method | Retraining Urgency   | Example                     |
| ------------- | ---------------- | -------------------- | --------------------------- |
| Spike         | Spike detection  | Monitor, don't panic | One bad batch of data       |
| Change Point  | Change point     | Retrain soon         | Market fundamentals changed |
| Gradual Drift | Trend analysis   | Schedule retrain     | Slow seasonal shift         |

**Insight:** Not all drift requires immediate action. Distinguish temporary from permanent.

## Production Considerations

### Performance

- Anomaly detection is **computationally cheap** for small datasets
- For large datasets (1M+ points), consider:
  - Sampling (every 10th point)
  - Windowing (only last 1000 points)
  - Background processing (don't block requests)

### Thresholds

Current configuration:

- `confidence: 95` - 5% chance of false positive
- `pvalueHistoryLength: accuracyHistory.Count / 4` - lookback window
- `trainingWindowSize: accuracyHistory.Count / 2` - baseline period

**Tuning:** More restrictive (confidence: 99) = fewer alerts but might miss real issues

### Integration

In Step 3, you'll run anomaly detection automatically via BackgroundService. For now, it's manual (POST to endpoints).

## Next

Proceed to `Step3-PerformanceMonitoring` to run anomaly detection continuously in the background.
