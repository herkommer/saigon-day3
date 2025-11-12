using Microsoft.ML;
using Microsoft.ML.Data;
using Serilog;
using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;

// ============================================================================
// Step 2 - Add Anomaly Detection
// ============================================================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Step2-AnomalyDetection"))
    .WithTracing(tracerProviderBuilder =>
        tracerProviderBuilder
            .AddSource("SelfLearningAPI")
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter());

builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<FallbackService>();

// ============================================================================
// TASK 1: Uncomment AnomalyDetectionService registration
// ============================================================================

// builder.Services.AddSingleton<AnomalyDetectionService>();

var app = builder.Build();

var modelService = app.Services.GetRequiredService<ModelService>();
modelService.TrainInitialModel();

var activitySource = new ActivitySource("SelfLearningAPI");

var combinedPolicy = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        OnRetry = args =>
        {
            Log.Warning("Retry attempt {AttemptNumber} after {Delay}ms",
                args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
            return ValueTask.CompletedTask;
        }
    })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(10),
        MinimumThroughput = 3,
        BreakDuration = TimeSpan.FromSeconds(30),
        OnOpened = args =>
        {
            Log.Error("Circuit breaker OPENED - failing fast for {Duration}s", args.BreakDuration.TotalSeconds);
            return ValueTask.CompletedTask;
        },
        OnClosed = args =>
        {
            Log.Information("Circuit breaker CLOSED - system recovered");
            return ValueTask.CompletedTask;
        },
        OnHalfOpened = args =>
        {
            Log.Warning("Circuit breaker HALF-OPEN - testing recovery");
            return ValueTask.CompletedTask;
        }
    })
    .Build();

app.MapGet("/predict/{threshold:double}", (double threshold, ObservationStore observationStore,
    ModelService modelService, FallbackService fallbackService) =>
{
    using var activity = activitySource.StartActivity("PredictWithCompletePolicy");
    activity?.SetTag("threshold", threshold);

    Log.Information("Prediction requested for threshold {Threshold}", threshold);

    try
    {
        var result = combinedPolicy.Execute(() =>
        {
            var observationId = Guid.NewGuid();
            var mlContext = new MLContext(seed: 42);
            var inputData = new[] { new SignalData { Threshold = (float)threshold } };
            var dataView = mlContext.Data.LoadFromEnumerable(inputData);
            var predictions = modelService.Model.Transform(dataView);
            var predictionResults = mlContext.Data.CreateEnumerable<AlertPrediction>(predictions, reuseRowObject: false);
            var prediction = predictionResults.First();

            var observation = new Observation
            {
                ObservationId = observationId,
                Timestamp = DateTime.UtcNow,
                Threshold = threshold,
                PredictedAlert = prediction.PredictedLabel,
                Confidence = prediction.Probability,
                ModelVersion = modelService.CurrentVersion
            };

            observationStore.Add(observation);

            Log.Information("Prediction: {Alert} (confidence: {Confidence})",
                observation.PredictedAlert, observation.Confidence);

            activity?.SetTag("predicted_alert", observation.PredictedAlert);
            activity?.SetTag("fallback_used", false);

            return new
            {
                predictedAlert = observation.PredictedAlert,
                confidence = observation.Confidence,
                observationId = observation.ObservationId,
                modelVersion = observation.ModelVersion,
                fallbackUsed = false
            };
        });

        return Results.Ok(result);
    }
    catch (BrokenCircuitException)
    {
        Log.Warning("Circuit open - using fallback");
        activity?.SetTag("circuit_open", true);
        activity?.SetTag("fallback_used", true);
        return Results.Ok(fallbackService.GetFallbackPrediction(threshold));
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Prediction failed - using fallback");
        activity?.SetTag("error", true);
        activity?.SetTag("fallback_used", true);
        return Results.Ok(fallbackService.GetFallbackPrediction(threshold));
    }
});

app.MapPost("/label/{observationId:guid}", (Guid observationId, bool actualAlert,
    ObservationStore observationStore) =>
{
    using var activity = activitySource.StartActivity("LabelObservation");
    activity?.SetTag("observation_id", observationId);
    activity?.SetTag("actual_alert", actualAlert);

    var observation = observationStore.Get(observationId);
    if (observation == null)
    {
        return Results.NotFound(new { message = "Observation not found" });
    }

    observation.ActualAlert = actualAlert;
    observation.Labeled = true;

    Log.Information("Labeled {ObservationId} as {ActualAlert}", observationId, actualAlert);

    return Results.Ok(new { message = "Observation labeled successfully", observationId });
});

app.MapGet("/observations", (ObservationStore observationStore) =>
{
    using var activity = activitySource.StartActivity("GetObservations");

    var observations = observationStore.GetAll()
        .Select(o => new
        {
            observationId = o.ObservationId,
            timestamp = o.Timestamp,
            threshold = o.Threshold,
            predictedAlert = o.PredictedAlert,
            confidence = o.Confidence,
            actualAlert = o.ActualAlert,
            labeled = o.Labeled,
            modelVersion = o.ModelVersion
        });

    return Results.Ok(observations);
});

app.MapPost("/retrain", (ObservationStore observationStore, ModelService modelService) =>
{
    using var activity = activitySource.StartActivity("RetrainModel");

    Log.Information("Retrain requested");

    var labeledObservations = observationStore.GetLabeled();
    if (labeledObservations.Count < 10)
    {
        return Results.BadRequest(new
        {
            message = $"Not enough labeled observations. Need 10, have {labeledObservations.Count}"
        });
    }

    var previousVersion = modelService.CurrentVersion;
    var mlContext = new MLContext(seed: 42);
    var trainingData = labeledObservations.Select(o => new SignalData
    {
        Threshold = (float)o.Threshold,
        Alert = o.ActualAlert ?? false
    }).ToArray();

    var dataView = mlContext.Data.LoadFromEnumerable(trainingData);
    var pipeline = mlContext.Transforms.CopyColumns(outputColumnName: "Label", inputColumnName: "Alert")
        .Append(mlContext.Transforms.Concatenate("Features", "Threshold"))
        .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());

    var newModel = pipeline.Fit(dataView);
    var predictions = newModel.Transform(dataView);
    var metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");

    modelService.UpdateModel(newModel);

    Log.Information("Retrained: v{Old} → v{New}, accuracy: {Accuracy}",
        previousVersion, modelService.CurrentVersion, metrics.Accuracy);

    activity?.SetTag("new_version", modelService.CurrentVersion);
    activity?.SetTag("accuracy", metrics.Accuracy);

    return Results.Ok(new
    {
        message = "Model retrained successfully",
        previousVersion,
        newVersion = modelService.CurrentVersion,
        trainingDataCount = trainingData.Length,
        accuracy = metrics.Accuracy
    });
});

app.MapGet("/stats", (ObservationStore observationStore, ModelService modelService) =>
{
    using var activity = activitySource.StartActivity("GetStats");

    var labeled = observationStore.GetLabeled();
    var correct = labeled.Count(o => o.PredictedAlert == o.ActualAlert);
    var accuracy = labeled.Count > 0 ? (double)correct / labeled.Count : 0.0;

    var stats = new
    {
        totalObservations = observationStore.GetAll().Count,
        labeledCount = labeled.Count,
        accuracy,
        currentModelVersion = modelService.CurrentVersion
    };

    Log.Information("Stats: {Total} observations, {Labeled} labeled, {Accuracy:P1} accuracy",
        stats.totalObservations, stats.labeledCount, stats.accuracy);

    return Results.Ok(stats);
});

// ============================================================================
// TASK 2: Uncomment /detect-spikes endpoint
// ============================================================================

// app.MapPost("/detect-spikes", (ObservationStore observationStore, AnomalyDetectionService anomalyService) =>
// {
//     using var activity = activitySource.StartActivity("DetectSpikes");
//
//     Log.Information("Spike detection requested");
//
//     var labeled = observationStore.GetLabeled();
//     if (labeled.Count < 5)
//     {
//         return Results.BadRequest(new
//         {
//             message = $"Not enough labeled observations. Need 5+, have {labeled.Count}"
//         });
//     }
//
//     // Calculate rolling accuracy over time
//     var accuracyHistory = new List<double>();
//     var windowSize = 5;
//
//     for (int i = 0; i < labeled.Count; i++)
//     {
//         var window = labeled.Skip(Math.Max(0, i - windowSize + 1)).Take(windowSize).ToList();
//         var correct = window.Count(o => o.PredictedAlert == o.ActualAlert);
//         var accuracy = (double)correct / window.Count;
//         accuracyHistory.Add(accuracy);
//     }
//
//     var anomalies = anomalyService.DetectAccuracySpikes(accuracyHistory);
//
//     Log.Information("Spike detection complete: {Count} anomalies found", anomalies.Count);
//
//     return Results.Ok(new
//     {
//         message = "Spike detection complete",
//         dataPoints = accuracyHistory.Count,
//         anomaliesDetected = anomalies.Count,
//         anomalies = anomalies.Select(a => new
//         {
//             a.Timestamp,
//             a.AnomalyType,
//             a.Value,
//             a.Severity,
//             a.Message
//         })
//     });
// });

// ============================================================================
// TASK 3: Uncomment /detect-changepoints endpoint
// ============================================================================

// app.MapPost("/detect-changepoints", (ObservationStore observationStore, AnomalyDetectionService anomalyService) =>
// {
//     using var activity = activitySource.StartActivity("DetectChangePoints");
//
//     Log.Information("Change point detection requested");
//
//     var labeled = observationStore.GetLabeled();
//     if (labeled.Count < 5)
//     {
//         return Results.BadRequest(new
//         {
//             message = $"Not enough labeled observations. Need 5+, have {labeled.Count}"
//         });
//     }
//
//     // Calculate rolling accuracy over time
//     var accuracyHistory = new List<double>();
//     var windowSize = 5;
//
//     for (int i = 0; i < labeled.Count; i++)
//     {
//         var window = labeled.Skip(Math.Max(0, i - windowSize + 1)).Take(windowSize).ToList();
//         var correct = window.Count(o => o.PredictedAlert == o.ActualAlert);
//         var accuracy = (double)correct / window.Count;
//         accuracyHistory.Add(accuracy);
//     }
//
//     var anomalies = anomalyService.DetectAccuracyChangePoints(accuracyHistory);
//
//     Log.Information("Change point detection complete: {Count} anomalies found", anomalies.Count);
//
//     return Results.Ok(new
//     {
//         message = "Change point detection complete",
//         dataPoints = accuracyHistory.Count,
//         anomaliesDetected = anomalies.Count,
//         anomalies = anomalies.Select(a => new
//         {
//             a.Timestamp,
//             a.AnomalyType,
//             a.Value,
//             a.Severity,
//             a.Message
//         })
//     });
// });

// ============================================================================
// TASK 4: Uncomment /anomaly-history endpoint
// ============================================================================

// app.MapGet("/anomaly-history", (AnomalyDetectionService anomalyService) =>
// {
//     using var activity = activitySource.StartActivity("GetAnomalyHistory");
//
//     var history = anomalyService.GetAnomalyHistory();
//
//     return Results.Ok(new
//     {
//         totalAnomalies = history.Count,
//         anomalies = history.Select(a => new
//         {
//             a.Timestamp,
//             a.AnomalyType,
//             a.Value,
//             a.Severity,
//             a.Message
//         })
//     });
// });

app.Run();
