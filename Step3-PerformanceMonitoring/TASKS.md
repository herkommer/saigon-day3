# Step 3 - Add Performance Monitoring

## Overview

Add continuous background monitoring using ASP.NET Core's `BackgroundService` to automatically track model performance and detect anomalies without manual intervention.

**New capability:** Autonomous, continuous monitoring that runs independently of API requests.

**Your task:** Enable the background service to monitor performance every 30 seconds.

## Concepts

### BackgroundService

`BackgroundService` is an ASP.NET Core hosted service that runs independently in the background. Perfect for:

- Scheduled tasks
- Continuous monitoring
- Periodic cleanup
- Health checks

**Key characteristics:**

- Runs automatically when app starts
- Operates on separate thread (doesn't block requests)
- Can be cancelled gracefully on shutdown
- Registered with dependency injection

### Continuous Monitoring Strategy

Instead of manual POST to `/detect-spikes`, the system automatically:

1. Wakes up every 30 seconds
2. Calculates current performance snapshot
3. Stores snapshot in history
4. Runs anomaly detection if enough history exists
5. Logs warnings if anomalies detected

## Tasks

### TASK 1: Register BackgroundService

Uncomment in `Program.cs`:

```csharp
builder.Services.AddHostedService<PerformanceMonitoringService>();
```

This starts the background service automatically.

### TASK 2: Enable Performance History Endpoint

Uncomment the `/performance-history` endpoint to view collected snapshots.

## Test

Run the API:

```powershell
dotnet run
```

**What happens:**

1. API starts
2. Background service starts automatically
3. Every 30 seconds, you'll see: `Performance Snapshot: Accuracy=X%, Labeled=Y`
4. If anomalies detected: `Anomalies detected: X spikes, Y change points`

Follow test sequence in `test-commands.md`.

## Discussion Questions

**Before uncommenting:**

1. **Resource Usage:**

   - Background service checks every 30s. Too frequent? Too slow?
   - What's the cost of running anomaly detection every 30s?

2. **Autonomy Trade-offs:**

   - Manual (Step 2): Full control, but requires human to remember
   - Automatic (Step 3): Hands-off, but harder to debug
   - Which is better for production?

3. **Alert Fatigue:**
   - If background service logs warnings constantly, will ops teams ignore them?
   - How would you prevent false alarms?

**After testing:**

1. **Observation:**

   - Watch console logs. Do you see periodic "Performance Snapshot" messages?
   - How long until anomaly detection kicks in (need 5+ labeled observations)?

2. **Next Steps:**
   - Right now, monitoring just logs warnings. What should happen next?
   - Answer: Step 4 - automatic retraining decisions!

## Key Insights

### Separation of Concerns

| Component               | Responsibility       | Triggered By      |
| ----------------------- | -------------------- | ----------------- |
| API Endpoints           | Handle requests      | User HTTP calls   |
| BackgroundService       | Monitor continuously | Timer (every 30s) |
| AnomalyDetectionService | Detect patterns      | BackgroundService |
| (Future) Retraining     | Improve model        | Anomaly detected  |

**Insight:** Background monitoring enables proactive autonomy.

### Metrics Tracked

Each `PerformanceSnapshot` contains:

- `Accuracy` - Overall correctness
- `AverageConfidence` - Model certainty
- `LowConfidenceCount` - Uncertain predictions
- `ModelVersion` - Which model is active

**Insight:** Multiple signals better than accuracy alone.

## Production Considerations

### Check Interval

```csharp
private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
```

**Tuning:**

- Development: 30 seconds (fast feedback)
- Production: 5-10 minutes (reduce CPU usage)
- High-traffic: 1 minute (responsive to changes)

### History Management

Currently stores all snapshots in memory. For production:

```csharp
// Limit history to last 1000 snapshots
if (_performanceHistory.Count > 1000)
{
    _performanceHistory.RemoveAt(0);
}
```

### Graceful Shutdown

The service already handles:

```csharp
catch (OperationCanceledException)
{
    Log.Information("Performance monitoring service stopping");
    break;
}
```

This ensures clean shutdown when app stops.

## Architecture Evolution

| Step   | Monitoring Approach      | Autonomy Level   |
| ------ | ------------------------ | ---------------- |
| Step 1 | None                     | Manual only      |
| Step 2 | Manual POST to endpoints | Semi-manual      |
| Step 3 | Automatic background     | Autonomous       |
| Step 4 | + Decision engine        | Intelligent      |
| Step 5 | + Safe deployment        | Production-ready |

**We are here:** Autonomous monitoring, but still manual retraining.

## Next

Proceed to `Step4-IntelligentRetrainingTriggers` to automatically decide when to retrain based on monitoring signals.
