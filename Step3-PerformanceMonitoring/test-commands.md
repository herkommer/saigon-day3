# Step 3 - Test Commands

## Prerequisites

**IMPORTANT: Before running, complete TASK 1 in `Program.cs`:**

Uncomment these two lines:

```csharp
builder.Services.AddSingleton<PerformanceMonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PerformanceMonitoringService>());
```

Then start the API:

```powershell
dotnet run
```

API will start on `http://localhost:5000`

**WATCH THE CONSOLE:** You should see periodic "Performance Snapshot" logs every 30 seconds.

## Scenario: Watch Autonomous Monitoring

### Step 1: Start API and Observe

After starting, watch console for:

```
[HH:mm:ss INF] Initial model trained (version 1)
[HH:mm:ss INF] Performance monitoring service started (checking every 30s)
[HH:mm:ss INF] Now listening on: http://localhost:5000
```

Wait 30 seconds...

```
[HH:mm:ss INF] Skipping monitoring: not enough labeled data (0 < 5)
```

**Observation:** Background service is running but needs data.

### Step 2: Generate Initial Labeled Data

Create 10 labeled observations quickly:

```powershell
# Generate 10 quick predictions and labels
foreach ($i in 1..10) {
    $value = $i * 0.08  # 0.08, 0.16, 0.24, ..., 0.80
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Created 10 labeled observations. Wait 30s for next monitoring cycle..."
```

### Step 3: Watch Autonomous Monitoring Kick In

Wait 30 seconds. You should see:

```
[HH:mm:ss INF] Performance Snapshot: Accuracy=100%, Labeled=10, AvgConfidence=0.XX, LowConf=0
```

**Congratulations!** The background service is now monitoring automatically.

### Step 4: Introduce Accuracy Changes

Generate poor predictions to trigger anomaly detection:

```powershell
# Phase 1: More good predictions
foreach ($i in 1..10) {
    $value = $i * 0.05
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Phase 1 complete. Wait for snapshot..."
Start-Sleep -Seconds 35

# Phase 2: Introduce errors (spike)
foreach ($i in 1..8) {
    $value = 0.3  # Low value
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    # Label opposite (create errors)
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Out-Null
}

Write-Host "Phase 2 (errors) complete. Wait for snapshot and anomaly detection..."
Start-Sleep -Seconds 35

# Phase 3: Recovery
foreach ($i in 1..10) {
    $value = $i * 0.07
    $r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Phase 3 (recovery) complete. Wait for snapshot..."
Start-Sleep -Seconds 35
```

**Expected Console Output:**

```
[HH:mm:ss INF] Performance Snapshot: Accuracy=95%, Labeled=20, AvgConfidence=0.XX, LowConf=1
[HH:mm:ss INF] Performance Snapshot: Accuracy=72%, Labeled=28, AvgConfidence=0.XX, LowConf=3
```

**Note:** Anomaly detection (spike/change point warnings) requires 12+ performance snapshots. Monitoring runs every 30 seconds, so you'll need to wait ~6 minutes for enough data. After 12+ snapshots, you should see:

```
[HH:mm:ss WAR] SPIKE DETECTED: Sudden accuracy change detected at index 7: 72% (p-value: 0.0234)
[HH:mm:ss WAR] Anomalies detected: 1 spikes, 0 change points
```

Continue to Phase 3:

```
[HH:mm:ss INF] Performance Snapshot: Accuracy=81%, Labeled=38, AvgConfidence=0.XX, LowConf=2
```

### Step 5: View Performance History

Check collected snapshots:

```powershell
Invoke-RestMethod "http://localhost:5000/performance-history" | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "snapshotCount": 5,
  "snapshots": [
    {
      "timestamp": "2025-11-11T10:15:30Z",
      "totalObservations": 10,
      "labeledObservations": 10,
      "accuracy": 1.0,
      "modelVersion": 1,
      "averageConfidence": 0.85,
      "lowConfidenceCount": 0
    },
    {
      "timestamp": "2025-11-11T10:16:00Z",
      "totalObservations": 20,
      "labeledObservations": 20,
      "accuracy": 0.95,
      "modelVersion": 1,
      "averageConfidence": 0.83,
      "lowConfidenceCount": 1
    },
    ...
  ]
}
```

## Continuous Monitoring Test (Long-Running)

Leave the API running and periodically add observations:

```powershell
# Run this every few minutes
$value = Get-Random -Minimum 0.0 -Maximum 1.0
$r = Invoke-RestMethod "http://localhost:5000/predict/$($value)"
$label = $value -gt 0.6
Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
Write-Host "Added observation: value=$value, label=$label"
```

Watch console for ongoing monitoring logs every 30 seconds.

## Understanding the Output

### Performance Snapshot Fields

```
Accuracy=85%          # Percentage correct predictions
Labeled=42            # Total labeled observations
AvgConfidence=0.72    # Average model confidence
LowConf=5             # Count with confidence < 0.6
```

### Monitoring Lifecycle

1. **< 5 labeled:** "Skipping monitoring: not enough labeled data"
2. **5+ labeled:** Snapshots collected every 30 seconds
3. **12+ snapshots (~6 minutes):** Anomaly detection activates with spike/change point warnings

### Anomaly Detection Timing

```
[HH:mm:ss WAR] Anomalies detected: X spikes, Y change points
```

Only appears when:

- At least 12 performance snapshots exist (~6 minutes of monitoring)
- Anomaly detection algorithms find issues

## Troubleshooting

### Issue: No "Performance Snapshot" logs after adding data

**Cause:** Not enough labeled observations yet

**Fix:** Create at least 5 labeled predictions. The first cycle will show "Skipping monitoring" until you have 5 labeled observations.

### Issue: "Skipping monitoring" every time

**Cause:** Not enough labeled observations

**Fix:** Create at least 5 labeled predictions (Step 2)

### Issue: No anomaly warnings

**Possible causes:**

1. Data is too uniform (all same accuracy)
2. Not enough performance history (< 5 snapshots)
3. Changes aren't dramatic enough

**Fix:** Create more varied accuracy patterns (Step 4)

### Issue: Can't access /performance-history

**Cause:** Endpoint not uncommented

**Fix:** Ensure TASK 2 is uncommented in Program.cs

## Monitoring Interval Adjustment

To test faster, change in `Models.cs`:

```csharp
private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(10);  // Instead of 30
```

Rebuild and rerun:

```powershell
dotnet run
```

Now monitoring runs every 10 seconds (faster feedback for testing).

## Production Monitoring Patterns

What you've built is a foundation for:

1. **Alerting:** Send notifications when anomalies detected
2. **Dashboarding:** Visualize performance history over time
3. **Automated actions:** Trigger retraining (Step 4!)
4. **Compliance:** Audit trail of model performance

## Discussion After Testing

1. **Autonomous vs Manual:**

   - How does automatic monitoring feel compared to manual POST in Step 2?
   - What are the trade-offs?

2. **Signal Quality:**

   - Is accuracy enough, or do you need other metrics?
   - What about `LowConfidenceCount` - is that useful?

3. **Next Step:**
   - Background service detects issues. Now what?
   - Should it automatically retrain? (Hint: Step 4!)

## Expected Timeline

- **0:00** - Start API
- **0:30** - First monitoring check (not enough data)
- **1:00** - Generate 10 labeled observations
- **1:30** - First real performance snapshot
- **2:00** - Add more data
- **2:30** - Snapshot with more observations
- **3:00** - Introduce errors
- **3:30** - Anomaly detected!

## Next Step

Proceed to `Step4-IntelligentRetrainingTriggers` where the background monitor will not just detect issues but **automatically decide** whether to trigger retraining.
