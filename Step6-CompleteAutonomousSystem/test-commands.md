# Step 6 - Test Commands (Reference Implementation)

## Overview

Step 6 is the **complete working implementation**. All code is uncommented and fully functional.

## Quick Start

```powershell
dotnet run
```

## Complete Test Scenarios

### Scenario 1: Verify Complete System

```powershell
# 1. Check initial status
Invoke-RestMethod "http://localhost:5000/stats"
Invoke-RestMethod "http://localhost:5000/governance"

# 2. Make predictions
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1

# 3. Label observations
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST

# 4. View audit log
Invoke-RestMethod "http://localhost:5000/audit-log"
```

**Expected:** All endpoints functional, audit log recording actions.

### Scenario 2: Trigger Autonomous Retraining

```powershell
# Generate baseline
foreach ($i in 1..15) {
    $value = $i * 0.06
    $r = Invoke-RestMethod "http://localhost:5000/predict/$value"
    $label = $value -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=$label" -Method POST | Out-Null
}

Write-Host "Baseline established. Wait 60s for monitoring..."
Start-Sleep -Seconds 60

# Introduce errors to trigger retraining
foreach ($i in 1..10) {
    $value = 0.35
    $r = Invoke-RestMethod "http://localhost:5000/predict/$value"
    # Wrong labels to degrade accuracy
    Invoke-WebRequest "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Out-Null
}

Write-Host "Errors introduced. Wait 90s for autonomous retraining decision..."
Start-Sleep -Seconds 90

# Check if retraining was triggered
Invoke-RestMethod "http://localhost:5000/stats"
Invoke-RestMethod "http://localhost:5000/audit-log" | ConvertTo-Json -Depth 5
```

**Expected Console Output:**

```
[HH:mm:ss WAR] RETRAINING RECOMMENDED: Multiple factors triggered retraining (score: 0.65)
[HH:mm:ss INF] Autonomous retraining enabled, proceeding...
[HH:mm:ss INF] Model updated to version 2
[HH:mm:ss INF] AUDIT: AutonomousRetraining - Autonomous retraining completed: v1 → v2
```

### Scenario 3: Test Kill Switch

```powershell
# Disable autonomous retraining
$body = @{ enabled = $false } | ConvertTo-Json
Invoke-RestMethod "http://localhost:5000/toggle-autonomy" -Method POST -Body $body -ContentType "application/json"

# Verify governance state
Invoke-RestMethod "http://localhost:5000/governance"

# Try to trigger retraining (should be blocked)
# ... introduce errors like Scenario 2 ...
# Background service will log: "Autonomous retraining disabled by governance"
```

**Expected:**

```json
{
  "autonomousRetrainingEnabled": false,
  "requireHumanApproval": false,
  "auditLogCount": 5
}
```

### Scenario 4: View Complete Audit Trail

```powershell
Invoke-RestMethod "http://localhost:5000/audit-log" | ConvertTo-Json -Depth 5
```

**Expected Output:**

```json
{
  "auditLogCount": 8,
  "events": [
    {
      "timestamp": "2025-11-11T10:15:00Z",
      "eventType": "PredictionMade",
      "details": "Prediction for threshold 0.7",
      "userId": "system",
      "metadata": { "modelVersion": 1 }
    },
    {
      "timestamp": "2025-11-11T10:16:30Z",
      "eventType": "AutonomousRetraining",
      "details": "Autonomous retraining completed: v1 → v2",
      "userId": "system",
      "metadata": {
        "trigger": "Accuracy degraded: 65% vs baseline 100%",
        "confidenceScore": 0.65
      }
    },
    {
      "timestamp": "2025-11-11T10:18:00Z",
      "eventType": "GovernanceChange",
      "details": "Autonomous retraining disabled",
      "userId": "admin",
      "metadata": {}
    }
  ]
}
```

### Scenario 5: Performance History Analysis

```powershell
Invoke-RestMethod "http://localhost:5000/performance-history" | ConvertTo-Json -Depth 5
```

Analyze:

- Accuracy trend over time
- When model versions changed
- Correlation between anomalies and retraining

## Production-Like Testing

### Load Testing

```powershell
# Simulate high traffic
$jobs = 1..100 | ForEach-Object {
    Start-Job -ScriptBlock {
        param($i)
        $value = ($i % 100) / 100.0
        Invoke-RestMethod "http://localhost:5000/predict/$value"
    } -ArgumentList $_
}

$jobs | Wait-Job | Receive-Job
```

### Chaos Testing

```powershell
# Kill and restart service mid-operation
# Watch how background service recovers
# Verify no data loss in audit log
```

## Monitoring Console Output

### Normal Operation

```
[10:15:00 INF] Initial model trained (version 1)
[10:15:00 INF] Performance monitoring service started with intelligent retraining
[10:15:30 INF] Performance Snapshot: Accuracy=100%, Labeled=5, AvgConfidence=0.85
[10:15:30 DBG] No retraining needed: score: 0.10 < 0.5 threshold
```

### Autonomous Retraining

```
[10:16:00 INF] Performance Snapshot: Accuracy=62%, Labeled=25, AvgConfidence=0.71
[10:16:00 WAR] SPIKE DETECTED: Sudden accuracy change detected...
[10:16:00 WAR] RETRAINING RECOMMENDED: Multiple factors triggered retraining (score: 0.70)
[10:16:00 INF] Triggers: Accuracy degraded: 62% vs baseline 98%, High low-confidence rate: 35%
[10:16:00 INF] Autonomous retraining enabled, proceeding...
[10:16:01 INF] Retrained: v1 → v2, accuracy: 0.85
[10:16:01 INF] Model updated to version 2
[10:16:01 INF] AUDIT: AutonomousRetraining - completed: v1 → v2
```

### Kill Switch Activated

```
[10:18:00 INF] Performance Snapshot: Accuracy=55%, Labeled=30
[10:18:00 WAR] RETRAINING RECOMMENDED: score: 0.75
[10:18:00 WAR] Autonomous retraining disabled by governance - manual intervention required
[10:18:00 INF] AUDIT: GovernanceOverride - Retraining blocked by governance
```

## Troubleshooting

### Issue: Autonomous retraining not happening

**Checks:**

1. Governance enabled? `GET /governance`
2. Score above threshold? Watch console logs
3. Rate limiting? Check last retraining time
4. Enough data? Need 10+ labeled observations

### Issue: Audit log not recording

**Cause:** GovernanceService not registered

**Fix:** Verify in Program.cs (should be registered in Step 6)

### Issue: Background service not running

**Symptoms:** No periodic "Performance Snapshot" logs

**Fix:** Ensure `AddHostedService<PerformanceMonitoringService>()`

## Key Observations

1. **Autonomy in Action:** Watch the system decide and act without human input
2. **Transparency:** Every action logged and explainable
3. **Safety:** Kill switch immediately stops autonomous behavior
4. **Auditability:** Complete history for compliance

## Discussion Points

1. **Trust:** Would you trust this system in production? Why/why not?
2. **Governance:** Are the current controls sufficient for your domain?
3. **Explainability:** Can you explain every autonomous action?
4. **Improvements:** What additional safety mechanisms would you add?
