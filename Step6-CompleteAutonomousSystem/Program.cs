using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;
using Serilog;
using System.Collections.Concurrent;

// === STEP 6: Complete Autonomous System (Reference Implementation) ===
// All code is UNCOMMENTED and FULLY FUNCTIONAL.
// This is the complete reference implementation with:
// - Anomaly detection (Step 2)
// - Performance monitoring (Step 3)
// - Intelligent retraining (Step 4)
// - Governance and audit (Step 6)

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// Register services
builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<FallbackService>();
builder.Services.AddSingleton<AnomalyDetectionService>();
builder.Services.AddSingleton<GovernanceService>();
builder.Services.AddHostedService<PerformanceMonitoringService>();

var app = builder.Build();

// Initialize model on startup
var modelService = app.Services.GetRequiredService<ModelService>();
modelService.InitializeModel();

// === ENDPOINTS ===

app.MapGet("/predict/{threshold:double}", (double threshold, ModelService model, ObservationStore store, GovernanceService gov) =>
{
    var obs = new Observation { Threshold = (float)threshold };
    var prediction = model.Predict(obs);

    store.Add(obs, prediction);

    gov.LogAudit("PredictionMade", $"Prediction for threshold {threshold}", "system",
        new Dictionary<string, object> { ["modelVersion"] = model.CurrentVersion });

    return new
    {
        observationId = obs.Id,
        threshold = obs.Threshold,
        prediction = prediction.Alert,
        confidence = prediction.Confidence,
        modelVersion = model.CurrentVersion,
        timestamp = obs.Timestamp
    };
});

app.MapPost("/label/{id}", (Guid id, bool actualAlert, ObservationStore store, GovernanceService gov) =>
{
    if (store.Label(id, actualAlert))
    {
        gov.LogAudit("ObservationLabeled", $"Observation {id} labeled as {actualAlert}", "user");
        return Results.Ok(new { success = true, observationId = id });
    }
    return Results.NotFound();
});

app.MapGet("/stats", (ObservationStore store, ModelService model, AnomalyDetectionService anomaly) =>
{
    var labeled = store.GetLabeledObservations().ToList();
    var allObs = store.GetAllObservations().ToList();

    var accuracy = 0.0;
    if (labeled.Any())
    {
        var correct = labeled.Count(o => o.WasCorrect == true);
        accuracy = (double)correct / labeled.Count;
    }

    var avgConfidence = allObs.Any() ? allObs.Average(o => o.Prediction?.Confidence ?? 0) : 0;

    // Check for anomalies
    var accuracyData = store.GetAccuracyTimeSeries();
    var spikeDetected = anomaly.DetectAccuracySpike(accuracyData);
    var changeDetected = anomaly.DetectAccuracyChangePoint(accuracyData);

    return new
    {
        totalObservations = allObs.Count,
        labeledObservations = labeled.Count,
        currentAccuracy = accuracy,
        averageConfidence = avgConfidence,
        modelVersion = model.CurrentVersion,
        anomalies = new
        {
            spikeDetected,
            changePointDetected = changeDetected
        }
    };
});

app.MapGet("/performance-history", (ObservationStore store) =>
{
    var snapshots = store.PerformanceSnapshots.ToList();
    return new
    {
        snapshotCount = snapshots.Count,
        snapshots = snapshots.OrderByDescending(s => s.Timestamp).Take(50)
    };
});

app.MapGet("/governance", (GovernanceService gov) =>
{
    return new
    {
        autonomousRetrainingEnabled = gov.AutonomousRetrainingEnabled,
        requireHumanApproval = gov.RequireHumanApproval,
        auditLogCount = gov.GetAuditLog().Count()
    };
});

app.MapPost("/toggle-autonomy", (GovernanceToggleRequest request, GovernanceService gov) =>
{
    gov.AutonomousRetrainingEnabled = request.Enabled;
    gov.LogAudit("GovernanceChange",
        $"Autonomous retraining {(request.Enabled ? "enabled" : "disabled")}",
        "admin");
    return Results.Ok(new { autonomousRetrainingEnabled = gov.AutonomousRetrainingEnabled });
});

app.MapGet("/audit-log", (GovernanceService gov) =>
{
    var log = gov.GetAuditLog().OrderByDescending(e => e.Timestamp).Take(100);
    return new
    {
        auditLogCount = gov.GetAuditLog().Count(),
        events = log
    };
});

app.Run();

Log.CloseAndFlush();

// === REQUEST/RESPONSE MODELS ===
public record GovernanceToggleRequest(bool Enabled);
