# Day 3 Starter - Autonomous Learning System

## Overview

This folder contains the **complete Day 3** using progressive disclosure methodology.

All code is **pre-written and commented out**. You only need to uncomment code block by block, following TASKS.md guides in each step.

## Folder Structure

```
Day3-Starter/
├── Step1-BaselineFromDay2/              # Recap of Day 2 (resilient API)
├── Step2-AddAnomalyDetection/           # Spike and change point detection
├── Step3-PerformanceMonitoring/         # BackgroundService metrics tracking
├── Step4-IntelligentRetrainingTriggers/ # Multi-factor decision engine
├── Step5-ModelValidationDeployment/     # Shadow mode, canary, rollback
└── Step6-CompleteAutonomousSystem/      # Complete reference implementation
```

## Learning Path

| Step       | Focus                      | Duration | Key Concepts                           |
| ---------- | -------------------------- | -------- | -------------------------------------- |
| **Step 1** | Baseline from Day 2        | 20 min   | Resilient API, why autonomy matters    |
| **Step 2** | Anomaly Detection          | 40 min   | Spike detection, change points, drift  |
| **Step 3** | Performance Monitoring     | 30 min   | BackgroundService, metrics collection  |
| **Step 4** | Intelligent Retraining     | 30 min   | Multi-factor triggers, decision engine |
| **Step 5** | Validation & Deployment    | 45 min   | Shadow mode, canary, gradual rollout   |
| **Step 6** | Complete Autonomous System | 30 min   | Governance, audit, kill switches       |

## Each Step Contains

- **{StepName}.csproj** - Project file with necessary packages
- **Program.cs** - All code commented out with TASK markers
- **Models.cs** - Data models and services
- **TASKS.md** - Step-by-step uncomment guide with discussion prompts
- **test-commands.md** - PowerShell commands to test functionality

**Step 6 is different:**

- **Program.cs** - Complete uncommented reference implementation
- **ARCHITECTURE.md** - System design and patterns
- **README.md** - Production checklist and governance considerations

### Discussion Points

Each TASKS.md includes discussion questions:

**Step 2 (Anomaly Detection):**

- "How do you detect drift before it causes failures?"
- "What's the difference between spike and change point detection?"

**Step 3 (Performance Monitoring):**

- "How often should you check metrics?"
- "What metrics indicate model degradation?"

**Step 4 (Intelligent Retraining):**

- "Should you retrain immediately when drift is detected?"
- "How do you balance stability vs continuous improvement?"

**Step 5 (Validation & Deployment):**

- "Why not just deploy the new model immediately?"
- "What's the difference between shadow mode and canary?"

## Autonomy Patterns Summary

| Pattern                 | Purpose                    | Key Mechanism             | Safety Control          |
| ----------------------- | -------------------------- | ------------------------- | ----------------------- |
| **Anomaly Detection**   | Detect drift proactively   | ML.NET spike/change point | Alert thresholds        |
| **Performance Monitor** | Track metrics continuously | BackgroundService         | Rate limiting           |
| **Intelligent Trigger** | Decide when to retrain     | Multi-factor scoring      | Manual override         |
| **Shadow Mode**         | Test without risk          | Parallel execution        | No user impact          |
| **Canary Deployment**   | Gradual rollout            | Traffic percentage shift  | Automatic rollback      |
| **Governance**          | Audit and control          | Logging + kill switches   | Human approval required |

## Testing Strategy

### Normal Operation

All steps have smoke tests in test-commands.md:

```powershell
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$o1 = $r1.observationId
Invoke-WebRequest -Uri "http://localhost:5000/label/${o1}?actualAlert=true" -Method POST
Invoke-RestMethod -Uri "http://localhost:5000/stats"
```

### Autonomy Testing

Steps 3-6 include autonomous behavior testing:

- Trigger drift detection with changing data patterns
- Observe automatic retraining decisions
- Verify shadow mode execution
- Test canary deployment rollout
- Validate rollback on degradation

## Common Issues

### Issue: "Package Microsoft.ML.TimeSeries not found"

**Fix:** Restore packages

```powershell
dotnet restore
```

### Issue: "Anomaly detection not triggering"

**Fix:** Need sufficient data points (minimum 10-20) and actual pattern changes

### Issue: "Shadow mode running both models"

**Expected:** This is correct - shadow mode runs new model alongside production without switching

## Comparison to Day 1 and Day 2

| Aspect       | Day 1                    | Day 2               | Day 3                      |
| ------------ | ------------------------ | ------------------- | -------------------------- |
| Focus        | Observability + Learning | Resilience          | Autonomy + Governance      |
| Steps        | 6                        | 6                   | 6                          |
| Key Library  | ML.NET                   | Polly               | ML.NET.TimeSeries          |
| Architecture | MAPE-K loop              | Resilience patterns | Autonomous decision engine |
| Complexity   | Medium                   | High                | Very High                  |

## Autonomy Philosophy

**Key Point:**

"Autonomy without governance is dangerous. The most sophisticated autonomous systems have the most sophisticated safeguards."

**Principles:**

1. **Transparency** - Every decision is logged and explainable
2. **Safety** - Kill switches and manual overrides always available
3. **Validation** - Never deploy without testing
4. **Gradual** - Incremental changes, not big bang
5. **Reversible** - Easy rollback is non-negotiable
6. **Audit** - Complete history for compliance and debugging

## Take-aways:

- ✅ How to detect drift and anomalies proactively
- ✅ How to make intelligent retraining decisions
- ✅ How to safely deploy ML models to production
- ✅ How to implement governance and safety controls
- ✅ When autonomy is appropriate vs when humans should intervene
- ✅ How to build trust in autonomous systems
