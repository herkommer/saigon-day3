# Step 4 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API will start on `http://localhost:5000`

**Background monitoring is running** - watch console for periodic snapshots and decision checks.

## Scenario: Trigger Intelligent Retraining Decision

### Step 1: Establish Baseline

Create initial high-quality data:

```powershell
# Generate 15 correct predictions (establish baseline)
foreach ($i in 1..15) {
    $value = $i * 0.06  # 0.06, 0.12, ..., 0.90
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Baseline created. Wait 60 seconds for monitoring to collect snapshots..."
Start-Sleep -Seconds 60
```

**Expected Console Output:**

```
[HH:mm:ss INF] Performance Snapshot: Accuracy=100%, Labeled=15, AvgConfidence=0.XX, LowConf=0
```

**Note:**

- Performance monitoring runs every 30 seconds
- Anomaly detection (spike detection) requires 12+ historical snapshots (~6 minutes of monitoring)
- Decision engine messages will only show after completing TASK 1

### Step 2: Check Initial Decision

Manually check retraining decision:

```powershell
Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "shouldRetrain": false,
  "confidenceScore": 0.1,
  "reason": "No retraining needed (score: 0.10 < 0.5 threshold)",
  "triggers": ["Model age: 0.0 days"],
  "factorScores": {
    "AccuracyDegradation": 0.0,
    "LowConfidence": 0.0,
    "DataGrowth": 0.0,
    "Anomalies": 0.0,
    "ModelAge": 0.1
  },
  "currentAccuracy": 1.0,
  "modelVersion": 1,
  "modelAge": 0.02
}
```

**Analysis:** Model is healthy, no triggers activated.

### Step 3: Introduce Accuracy Degradation

Create intentionally poor predictions:

```powershell
# Phase 1: Add some low-confidence errors
foreach ($i in 1..10) {
    $value = 0.35  # Low value that model might be uncertain about
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    # Label opposite of prediction (create errors)
    $wrongLabel = -not $r.predictedAlert
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$wrongLabel" -Method POST | Out-Null
}

Write-Host "Added 10 incorrect labels. Wait 35 seconds..."
Start-Sleep -Seconds 35
```

**Expected Console Output:**

```
[HH:mm:ss INF] Performance Snapshot: Accuracy=60%, Labeled=25, AvgConfidence=0.XX, LowConf=8
[HH:mm:ss WAR] SPIKE DETECTED: Sudden accuracy change detected...
[HH:mm:ss WAR] RETRAINING RECOMMENDED: Multiple factors triggered retraining (score: 0.65)
[HH:mm:ss INF] Triggers: Accuracy degraded: 60% vs baseline 100%, High low-confidence rate: 32%
```

### Step 4: Check Decision After Degradation

```powershell
Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "shouldRetrain": true,
  "confidenceScore": 0.65,
  "reason": "Multiple factors triggered retraining (score: 0.65)",
  "triggers": [
    "Accuracy degraded: 60% vs baseline 100%",
    "High low-confidence rate: 32%",
    "Anomalies detected: 2 total, 1 high severity"
  ],
  "factorScores": {
    "AccuracyDegradation": 0.3,
    "LowConfidence": 0.25,
    "DataGrowth": 0.0,
    "Anomalies": 0.1,
    "ModelAge": 0.0
  },
  "currentAccuracy": 0.6,
  "modelVersion": 1,
  "modelAge": 1.5
}
```

**Analysis:** Three factors triggered (accuracy, confidence, anomalies) → Total score 0.65 ≥ 0.5 → **Retraining recommended!**

### Step 5: View Decision History

```powershell
Invoke-RestMethod "http://localhost:5000/decision-history" | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "decisionCount": 5,
  "retrainingRecommendations": 3,
  "decisions": [
    {
      "timestamp": "2025-11-11T10:15:00Z",
      "shouldRetrain": false,
      "confidenceScore": 0.1,
      "reason": "No retraining needed...",
      "triggerCount": 0
    },
    {
      "timestamp": "2025-11-11T10:15:30Z",
      "shouldRetrain": false,
      "confidenceScore": 0.0,
      "reason": "No retraining needed...",
      "triggerCount": 0
    },
    {
      "timestamp": "2025-11-11T10:16:05Z",
      "shouldRetrain": true,
      "confidenceScore": 0.65,
      "reason": "Multiple factors triggered retraining",
      "triggerCount": 3,
      "triggers": [
        "Accuracy degraded: 60% vs baseline 100%",
        "High low-confidence rate: 32%",
        "Anomalies detected: 2 total, 1 high severity"
      ]
    }
  ]
}
```

**Analysis:** Clear progression from "no need" to "retrain now" as conditions deteriorated.

### Step 6: Test Data Growth Trigger

Add significant new labeled data:

```powershell
# Add 20 more correct predictions (data growth)
foreach ($i in 1..20) {
    $value = $i * 0.045
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Added 20 new observations. Checking decision..."
Start-Sleep -Seconds 35

Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST | ConvertTo-Json -Depth 5
```

**Expected:** `DataGrowth` factor score > 0 if labeled observations increased by 50%+

### Step 7: Test Model Age Trigger

In a real scenario, you'd wait 7+ days. For testing, modify `Models.cs`:

```csharp
// Temporarily for testing
private readonly TimeSpan _maxModelAge = TimeSpan.FromMinutes(2);
```

Then:

```powershell
# Wait 2 minutes
Write-Host "Waiting 2 minutes to trigger model age..."
Start-Sleep -Seconds 120

Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST
```

**Expected:** `ModelAge` factor score = 0.10

## Understanding Factor Scores

### Individual Factor Breakdown

```json
"factorScores": {
  "AccuracyDegradation": 0.30,  // Accuracy < 90% of baseline
  "LowConfidence": 0.25,         // > 30% predictions uncertain
  "DataGrowth": 0.0,             // Not enough new data yet
  "Anomalies": 0.10,             // Medium severity anomaly
  "ModelAge": 0.0                // Model too fresh
}
```

**Total: 0.30 + 0.25 + 0.0 + 0.10 + 0.0 = 0.65 ≥ 0.5 → RETRAIN**

### Score Interpretation

| Total Score | Decision | Meaning                           |
| ----------- | -------- | --------------------------------- |
| 0.0 - 0.2   | No       | Model healthy                     |
| 0.2 - 0.4   | No       | Minor issues, monitor             |
| 0.4 - 0.5   | No       | Borderline, approaching threshold |
| 0.5 - 0.7   | **Yes**  | Multiple moderate issues          |
| 0.7 - 1.0   | **Yes**  | Critical issues, retrain urgently |

## Testing Edge Cases

### Edge Case 1: Not Enough Data

```powershell
# Restart API (clears data), create only 5 observations
dotnet run
# ... wait for startup, then:
foreach ($i in 1..5) {
    $value = $i * 0.1
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Out-Null
}

Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST
```

**Expected:**

```json
{
  "message": "No performance history available"
}
```

**Reason:** Need at least 10 labeled observations (minimum data requirement).

### Edge Case 2: Rate Limiting

```powershell
# Manually trigger twice in quick succession
Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST
Start-Sleep -Seconds 5
Invoke-RestMethod "http://localhost:5000/check-retraining" -Method POST
```

**Expected:** Second call succeeds (checking is allowed), but if actual retraining happened, rate limit would prevent.

## Performance History Analysis

View how performance evolved:

```powershell
Invoke-RestMethod "http://localhost:5000/performance-history" | ConvertTo-Json -Depth 5
```

Look for:

- Accuracy trend (100% → 95% → 85% → 60%)
- Low confidence count increasing
- Snapshots taken every 30 seconds

## Troubleshooting

### Issue: Decision engine not logging recommendations

**Cause:** `RetrainingDecisionEngine` not registered

**Fix:** Ensure TASK 1 uncommented

### Issue: Score always 0.0

**Causes:**

1. Not enough performance history (need 5+ snapshots)
2. Data is too uniform (no variance to trigger factors)
3. Model too new (age = 0)

**Fix:** Wait longer, add more varied data

### Issue: Always recommends retraining

**Causes:**

1. Thresholds too sensitive
2. Baseline calculated from poor initial data

**Fix:** Adjust thresholds in `Models.cs` or restart with better baseline

## Discussion After Testing

1. **Factor Weights:**

   - Did the default weights (0.30, 0.25, 0.15, 0.20, 0.10) feel right?
   - Which factors mattered most in your tests?

2. **Threshold Sensitivity:**

   - Score threshold = 0.5. Appropriate?
   - Try mentally adjusting to 0.3 or 0.7 - what changes?

3. **Transparency:**

   - Can you explain why each decision was made?
   - Would this satisfy an auditor or regulator?

4. **Real-World Application:**
   - What factors would matter in your domain?
   - Prediction latency? Error severity? Business impact?

## Next Steps

**In Step 5**, the decision engine's recommendations will **trigger actual retraining**, but safely:

- Shadow mode testing first
- Gradual canary deployment
- Automatic rollback on degradation

**Key difference:** Step 4 = "should we retrain?" Step 5 = "retrain and deploy safely"

## Expected Timeline

- **0:00** - Start API, create baseline (15 obs)
- **1:00** - First decision check (no retraining)
- **2:00** - Add errors
- **2:30** - Accuracy drops, anomaly detected
- **3:00** - Decision check (should retrain!)
- **4:00** - View decision history

## Next

Proceed to `Step5-ModelValidationDeployment` where retraining decisions are **executed autonomously** with comprehensive safety mechanisms.
