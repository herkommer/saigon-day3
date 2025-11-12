# Step 1 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API will start on `http://localhost:5000`

## Resilient MAPE Loop Test Sequence

### 1. Monitor - Make predictions with resilience

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
```

**Expected:**

```json
{
  "predictedAlert": true,
  "confidence": 0.XX,
  "observationId": "<GUID>",
  "modelVersion": 1,
  "fallbackUsed": false
}
```

### 2. Analyze - Provide ground truth

```powershell
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST | ConvertFrom-Json
```

**Expected:**

```json
{
  "message": "Observation labeled successfully",
  "observationId": "<GUID>"
}
```

### 3. Plan - Check model statistics

```powershell
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:**

```json
{
  "totalObservations": 1,
  "labeledCount": 1,
  "accuracy": 1.0,
  "currentModelVersion": 1
}
```

### 4. Execute - Trigger model retraining

Make additional predictions and label them (minimum 10 total):

```powershell
# Generate varied predictions
$predictions = @()
foreach ($i in 1..12) {
    $value = $i * 0.08  # 0.08, 0.16, 0.24, ... 0.96
    $r = Invoke-RestMethod "http://localhost:5000/predict/$value"
    $predictions += $r
}

# Label them based on threshold > 0.6
foreach ($p in $predictions) {
    $actualAlert = $p.confidence -gt 0.6
    Invoke-WebRequest "http://localhost:5000/label/$($p.observationId)?actualAlert=$actualAlert" -Method POST | Out-Null
}
```

Initiate retraining:

```powershell
Invoke-RestMethod "http://localhost:5000/retrain" -Method POST
```

**Expected:**

```json
{
  "message": "Model retrained successfully",
  "previousVersion": 1,
  "newVersion": 2,
  "trainingDataCount": 13,
  "accuracy": 0.XX
}
```

Verify new model version:

```powershell
$r2 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r2.modelVersion  # Should be 2
```

### 5. View observation history

```powershell
Invoke-RestMethod "http://localhost:5000/observations" | ConvertTo-Json
```

## Quick Smoke Test

```powershell
# One-liner to verify everything works
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST | Out-Null
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** Stats showing 1 observation, 1 labeled, 100% accuracy

## Resilience Pattern Verification

### Test Fallback (requires forcing failures)

The circuit breaker and fallback are integrated. To test:

```powershell
# Normal prediction
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.5"
$r1.fallbackUsed  # Should be false
```

**Note:** To fully test circuit breaker opening and fallback, you would need to inject failures. This is covered in Day 2 chaos testing exercises.

## Expected Console Output

When running the API, you should see:

```
[HH:mm:ss INF] Initial model trained (version 1)
[HH:mm:ss INF] Now listening on: http://localhost:5000
[HH:mm:ss INF] Prediction requested for threshold 0.7
[HH:mm:ss INF] Prediction: True (confidence: 0.XX)
[HH:mm:ss INF] Labeled <GUID> as True
```

## Troubleshooting

### Issue: "Cannot bind to address already in use"

**Fix:** Kill existing process or change port

```powershell
# Find process on port 5000
netstat -ano | findstr :5000
# Kill it
taskkill /PID <PID> /F
```

### Issue: "Not enough labeled observations"

**Fix:** Need at least 10 labeled observations to retrain

```powershell
Invoke-RestMethod "http://localhost:5000/stats"
# Check labeledCount, must be >= 10
```

### Issue: Endpoints return 404

**Fix:** Ensure you uncommented all endpoint blocks in Program.cs (TASK 6-10)

## Next Step Preparation

After verifying Step 1 works:

1. Note that all decisions are **manual** (you call `/retrain`)
2. There's **no drift detection** (you don't know when model degrades)
3. **Immediate deployment** (new model replaces old instantly)

Step 2 will introduce **anomaly detection** to proactively identify when the model needs attention.
