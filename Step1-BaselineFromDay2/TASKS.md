# Step 1 - Baseline from Day 2

## Overview

Restore the complete resilient API from Day 2, Step 6 to establish a baseline before adding autonomous learning capabilities.

**Infrastructure:** Serilog, OpenTelemetry, Polly resilience policies, and model initialization are configured in `Program.cs`.

**Your task:** Enable the commented endpoints in `Program.cs` to restore full resilient functionality.

## Tasks

### Review the Architecture

- `Program.cs` - Infrastructure setup (Serilog, OpenTelemetry, services already active)
- `Models.cs` - Data models and services from Day 2

### Uncomment Resilience Policies (TASK 5)

Uncomment the combined Polly policy that includes:

- Retry with exponential backoff (3 attempts)
- Circuit breaker (50% failure ratio, 30s break duration)

### Uncomment Endpoints (TASKS 6-10)

Enable the following endpoints in `Program.cs`:

- `/predict` - Resilient prediction with fallback
- `/label` - Ground truth feedback
- `/observations` - Observation history
- `/retrain` - Model retraining
- `/stats` - Performance metrics

### Test

Run the API and verify resilience patterns are working:

```powershell
dotnet run
```

Smoke test (see `test-commands.md` for comprehensive tests):

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** All endpoints functional, resilience patterns active.

## Day 2 Resilience Recap

| Pattern             | Purpose                    | Configuration           |
| ------------------- | -------------------------- | ----------------------- |
| **Retry**           | Handle transient failures  | 3 attempts, exponential |
| **Circuit Breaker** | Prevent cascading failures | 50% ratio, 30s break    |
| **Fallback**        | Graceful degradation       | Simple threshold logic  |

**Policy order:** Retry → Circuit Breaker → Fallback

## Autonomy Readiness Gap Analysis

Now that you have a working resilient baseline, consider the autonomy gaps:

| Autonomy Need                      | Current State                  | Day 3 Solution                     |
| ---------------------------------- | ------------------------------ | ---------------------------------- |
| Detect drift before failures occur | Reactive (failures trigger CB) | Anomaly detection (Step 2)         |
| Know when to retrain automatically | Manual `/retrain` call         | Intelligent triggers (Step 4)      |
| Validate new models safely         | Immediate replacement          | Shadow mode + canary (Step 5)      |
| Track performance continuously     | Manual `/stats` check          | BackgroundService monitor (Step 3) |
| Govern autonomous decisions        | No audit trail                 | Governance layer (Step 6)          |

**Key Insight:** A resilient system survives failures. An autonomous system prevents them.

## Discussion Questions

**Before starting Day 3, discuss:**

1. **Proactive vs Reactive:**
   - Circuit breaker is reactive (responds after failures). How could we detect problems earlier?
2. **Retraining Decisions:**
   - Currently retraining is manual. What signals would indicate "time to retrain"?
3. **Deployment Risk:**
   - When `/retrain` runs, the new model immediately replaces the old one. What could go wrong?
4. **Trust & Governance:**
   - If we automate retraining, how do we ensure it doesn't make things worse?

## Next

Proceed to `Step2-AddAnomalyDetection` to enable proactive drift detection.
