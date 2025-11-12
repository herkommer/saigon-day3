# Step 2 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API will start on `http://localhost:5000`

## Scenario: Detect Accuracy Drift

This test creates intentional accuracy patterns to demonstrate spike and change point detection.

### Step 1: Generate Baseline Performance (High Accuracy)

Create observations with high accuracy (threshold < 0.6 = no alert):

```powershell
# Generate 15 correct predictions (high accuracy phase)
foreach ($i in 1..15) {
    $value = 0.1 + ($i * 0.02)  # 0.12, 0.14, 0.16, ... 0.40
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=false" -Method POST | Out-Null
}

# Check stats
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** ~100% accuracy (model correctly predicts "no alert" for low values)

### Step 2: Introduce Spike (Temporary Accuracy Drop)

Create a few incorrect predictions (spike):

```powershell
# Generate 5 WRONG predictions (temporary spike)
foreach ($i in 1..5) {
    $value = 0.2 + ($i * 0.02)  # Low values
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    # Label as TRUE even though model predicts FALSE (intentional mislabel)
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Out-Null
}

# Check stats
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** Accuracy drops temporarily (spike)

### Step 3: Return to Baseline

Generate more correct predictions:

```powershell
# Generate 10 correct predictions (recovery)
foreach ($i in 1..10) {
    $value = 0.1 + ($i * 0.03)
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=false" -Method POST | Out-Null
}

# Check stats
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** Accuracy recovers (end of spike)

### Step 4: Introduce Change Point (Permanent Shift)

Create persistent incorrect predictions (fundamental shift):

```powershell
# Generate 15 WRONG predictions (permanent shift)
foreach ($i in 1..15) {
    $value = 0.3 + ($i * 0.02)
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    # Label opposite of what model predicts
    $oppositeLabel = -not $r.predictedAlert
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$oppositeLabel" -Method POST | Out-Null
}

# Check stats
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** Accuracy remains low (change point)

### Step 5: Detect Spikes

Run spike detection:

```powershell
Invoke-RestMethod "http://localhost:5000/detect-spikes" -Method POST | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "message": "Spike detection complete",
  "dataPoints": 45,
  "anomaliesDetected": 1-3,
  "anomalies": [
    {
      "timestamp": "2025-11-11T...",
      "anomalyType": "AccuracySpike",
      "value": 0.67,
      "severity": "Medium",
      "message": "Sudden accuracy change detected at index 18: 67%"
    }
  ]
}
```

**What to look for:**

- Spike around index 15-20 (where we introduced temporary errors)
- Severity based on p-value (< 0.01 = High, otherwise Medium)

### Step 6: Detect Change Points

Run change point detection:

```powershell
Invoke-RestMethod "http://localhost:5000/detect-changepoints" -Method POST | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "message": "Change point detection complete",
  "dataPoints": 45,
  "anomaliesDetected": 1-2,
  "anomalies": [
    {
      "timestamp": "2025-11-11T...",
      "anomalyType": "AccuracyChangePoint",
      "value": 0.50,
      "severity": "High",
      "message": "Fundamental accuracy shift detected at index 30: 50%"
    }
  ]
}
```

**What to look for:**

- Change point around index 30-35 (where we introduced persistent errors)
- Martingale score > 0.9 indicates high confidence

### Step 7: View Anomaly History

Get all detected anomalies:

```powershell
Invoke-RestMethod "http://localhost:5000/anomaly-history" | ConvertTo-Json -Depth 5
```

**Expected:** Combined list of all spikes and change points detected

## Quick Test (Abbreviated)

```powershell
# Generate varied data
foreach ($i in 1..20) {
    $value = $i * 0.04
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

# Introduce anomalies
foreach ($i in 1..10) {
    $value = 0.3
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Out-Null
}

# Detect
Invoke-RestMethod "http://localhost:5000/detect-spikes" -Method POST
Invoke-RestMethod "http://localhost:5000/detect-changepoints" -Method POST
```

## Understanding the Results

### What is P-Value?

**P-value** (probability value) answers: _"If everything was normal, what's the probability of seeing a result this extreme just by random chance?"_

**Key concept:**

- **Low p-value** (< 0.05): Very unlikely to happen by chance → **Real anomaly detected!**
- **High p-value** (> 0.05): Could easily happen by chance → **Probably normal variation**

**Example:**

- Your model has 95% accuracy for 10 days straight
- Day 11: Accuracy suddenly drops to 67%
- P-value = 0.023 (2.3%)
- **Interpretation:** "If nothing changed, there's only a 2.3% chance I'd see this drop"
- **Conclusion:** This is NOT random! Something real happened (spike detected)

**Why not just use a fixed threshold?**

❌ **Bad:** `if (accuracy < 80%) alert();` → Treats 79% and 50% the same, ignores historical context

✅ **Good:** `if (pValue < 0.05) alert();` → Only alerts when drop is **statistically unusual** given past performance

### Spike Detection Output

```
prediction[0] = isSpike (0 or 1)
prediction[1] = raw score
prediction[2] = p-value (lower = more confident)
```

**Interpretation:**

- p-value < 0.01 → High confidence spike (99% sure it's real)
- p-value < 0.05 → Medium confidence spike (95% sure it's real)
- p-value > 0.05 → Not a spike (likely random variation)

### Change Point Detection Output

```
prediction[0] = alert (0 or 1)
prediction[1] = raw score
prediction[2] = p-value
prediction[3] = martingale score (higher = more confident)
```

**Interpretation:**

- Martingale > 0.9 → High confidence change point
- Martingale > 0.5 → Medium confidence change point
- Martingale < 0.5 → Weak signal

**What is Martingale Score?**

The **Martingale score** is a cumulative confidence measure that builds up over time as evidence accumulates for a fundamental shift in the data.

**Key concept:**

- **Betting analogy:** Imagine betting on "has the pattern changed?" after each data point
- Each correct "change detected" prediction increases your stake (like a winning streak)
- Each incorrect prediction resets your stake (like losing)
- **High score** = many consecutive signals pointing to same change → very confident
- **Low score** = inconsistent signals or no change → not confident

**Example:**

- Points 1-20: Model accuracy = 95% (stable baseline)
- Points 21-30: Model accuracy = 50% (sudden persistent drop)
- Martingale watches each point:
  - Point 21: "Maybe a change?" (score = 0.2)
  - Point 22: "Still low, confidence growing" (score = 0.4)
  - Point 23-25: "Definitely changed!" (score = 0.6 → 0.8 → 0.95)
- Score > 0.9: **"I'm 90%+ confident this is a permanent shift, not random noise"**

**Why use Martingale instead of just comparing averages?**

❌ **Bad:** `if (recent_avg < old_avg - 10%) alert();` → Could trigger on temporary dips

✅ **Good:** `if (martingale > 0.9) alert();` → Only triggers when multiple consecutive points confirm persistent change

**Difference from P-Value:**

- **P-value:** "Is this ONE point unusual?" (snapshot)
- **Martingale:** "Has the ENTIRE pattern fundamentally shifted?" (cumulative evidence)

**Real-world interpretation:**

- **Martingale 0.95**: Almost certain the model's underlying accuracy has permanently changed (retrain recommended)
- **Martingale 0.6**: Some evidence of shift but not conclusive (monitor closely)
- **Martingale 0.3**: Just normal variation (no action needed)

## Expected Console Output

```
[HH:mm:ss INF] Initial model trained (version 1)
[HH:mm:ss INF] Prediction requested for threshold 0.12
[HH:mm:ss INF] Labeled <GUID> as False
...
[HH:mm:ss INF] Spike detection requested
[HH:mm:ss WAR] SPIKE DETECTED: Sudden accuracy change detected at index 18: 67% (p-value: 0.0234)
[HH:mm:ss INF] Spike detection complete: 2 anomalies found
[HH:mm:ss INF] Change point detection requested
[HH:mm:ss WAR] CHANGE POINT DETECTED: Fundamental accuracy shift detected at index 32: 48% (martingale: 0.9456)
[HH:mm:ss INF] Change point detection complete: 1 anomalies found
```

## Troubleshooting

### Issue: "Not enough labeled observations"

**Fix:** Need at least 5 labeled observations

```powershell
Invoke-RestMethod "http://localhost:5000/stats"
# Check labeledCount >= 5
```

### Issue: No anomalies detected

**Causes:**

1. Data is too uniform (all same accuracy)
2. Not enough variation to trigger algorithms
3. Confidence threshold too high (95%)

**Fix:** Create more dramatic accuracy changes or adjust confidence in Models.cs

### Issue: Too many false positives

**Fix:** Increase confidence from 95 to 98 or 99 in `AnomalyDetectionService`

## Discussion After Testing

1. **Visual Pattern:**

   - Can you mentally visualize the accuracy pattern: high → spike → high → shift low?
   - Did the algorithms detect what you expected?

2. **Sensitivity:**

   - Were spikes detected too aggressively or not enough?
   - Would you adjust confidence threshold?

3. **Action Decision:**

   - For the spike: Would you retrain immediately or wait?
   - For the change point: Would you retrain automatically or alert a human?

4. **Production:**
   - How would you run this continuously (not manual POST)?
   - Answer: Step 3 introduces BackgroundService!

## Next Step

In Step 3, you'll:

- Run anomaly detection automatically in background
- Track metrics continuously
- Prepare for intelligent retraining decisions (Step 4)
