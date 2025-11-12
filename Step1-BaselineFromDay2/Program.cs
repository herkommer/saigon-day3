using Microsoft.ML;
using Microsoft.ML.Data;
using Serilog;
using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;

// ============================================================================
// TASK 1: Configure Serilog (already active)
// ============================================================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

// ============================================================================
// TASK 2: Configure OpenTelemetry (already active)
// ============================================================================

builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
        tracerProviderBuilder
            .AddSource("SelfLearningAPI")
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter());

// ============================================================================
// TASK 3: Register services (already active)
// ============================================================================

builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<FallbackService>();

var app = builder.Build();

// ============================================================================
// TASK 4: Initialize the ML model (already active)
// ============================================================================

var modelService = app.Services.GetRequiredService<ModelService>();
modelService.TrainInitialModel();

var activitySource = new ActivitySource("SelfLearningAPI");

// ============================================================================
// TASK 5: Uncomment resilience policies
// ============================================================================

// var combinedPolicy = new ResiliencePipelineBuilder()
//     .AddRetry(new RetryStrategyOptions
//     {
//         MaxRetryAttempts = 3,
//         Delay = TimeSpan.FromSeconds(1),
//         BackoffType = DelayBackoffType.Exponential,
//         OnRetry = args =>
//         {
//             Log.Warning("Retry attempt {AttemptNumber} after {Delay}ms",
//                 args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
//             return ValueTask.CompletedTask;
//         }
//     })
//     .AddCircuitBreaker(new CircuitBreakerStrategyOptions
//     {
//         FailureRatio = 0.5,
//         SamplingDuration = TimeSpan.FromSeconds(10),
//         MinimumThroughput = 3,
//         BreakDuration = TimeSpan.FromSeconds(30),
//         OnOpened = args =>
//         {
//             Log.Error("Circuit breaker OPENED - failing fast for {Duration}s", args.BreakDuration.TotalSeconds);
//             return ValueTask.CompletedTask;
//         },
//         OnClosed = args =>
//         {
//             Log.Information("Circuit breaker CLOSED - system recovered");
//             return ValueTask.CompletedTask;
//         },
//         OnHalfOpened = args =>
//         {
//             Log.Warning("Circuit breaker HALF-OPEN - testing recovery");
//             return ValueTask.CompletedTask;
//         }
//     })
//     .Build();

// ============================================================================
// TASK 6: Uncomment /predict endpoint
// ============================================================================

// app.MapGet("/predict/{threshold:double}", (double threshold, ObservationStore observationStore,
//     ModelService modelService, FallbackService fallbackService) =>
// {
//     using var activity = activitySource.StartActivity("PredictWithCompletePolicy");
//     activity?.SetTag("threshold", threshold);
//
//     Log.Information("Prediction requested for threshold {Threshold}", threshold);
//
//     try
//     {
//         var result = combinedPolicy.Execute(() =>
//         {
//             var observationId = Guid.NewGuid();
//             var mlContext = new MLContext(seed: 42);
//             var inputData = new[] { new SignalData { Threshold = (float)threshold } };
//             var dataView = mlContext.Data.LoadFromEnumerable(inputData);
//             var predictions = modelService.Model.Transform(dataView);
//             var predictionResults = mlContext.Data.CreateEnumerable<AlertPrediction>(predictions, reuseRowObject: false);
//             var prediction = predictionResults.First();
//
//             var observation = new Observation
//             {
//                 ObservationId = observationId,
//                 Timestamp = DateTime.UtcNow,
//                 Threshold = threshold,
//                 PredictedAlert = prediction.PredictedLabel,
//                 Confidence = prediction.Probability,
//                 ModelVersion = modelService.CurrentVersion
//             };
//
//             observationStore.Add(observation);
//
//             Log.Information("Prediction: {Alert} (confidence: {Confidence})",
//                 observation.PredictedAlert, observation.Confidence);
//
//             activity?.SetTag("predicted_alert", observation.PredictedAlert);
//             activity?.SetTag("fallback_used", false);
//
//             return new
//             {
//                 predictedAlert = observation.PredictedAlert,
//                 confidence = observation.Confidence,
//                 observationId = observation.ObservationId,
//                 modelVersion = observation.ModelVersion,
//                 fallbackUsed = false
//             };
//         });
//
//         return Results.Ok(result);
//     }
//     catch (BrokenCircuitException)
//     {
//         Log.Warning("Circuit open - using fallback");
//         activity?.SetTag("circuit_open", true);
//         activity?.SetTag("fallback_used", true);
//         return Results.Ok(fallbackService.GetFallbackPrediction(threshold));
//     }
//     catch (Exception ex)
//     {
//         Log.Error(ex, "Prediction failed - using fallback");
//         activity?.SetTag("error", true);
//         activity?.SetTag("fallback_used", true);
//         return Results.Ok(fallbackService.GetFallbackPrediction(threshold));
//     }
// });

// ============================================================================
// TASK 7: Uncomment /label endpoint
// ============================================================================

// app.MapPost("/label/{observationId:guid}", (Guid observationId, bool actualAlert,
//     ObservationStore observationStore) =>
// {
//     using var activity = activitySource.StartActivity("LabelObservation");
//     activity?.SetTag("observation_id", observationId);
//     activity?.SetTag("actual_alert", actualAlert);
//
//     var observation = observationStore.Get(observationId);
//     if (observation == null)
//     {
//         return Results.NotFound(new { message = "Observation not found" });
//     }
//
//     observation.ActualAlert = actualAlert;
//     observation.Labeled = true;
//
//     Log.Information("Labeled {ObservationId} as {ActualAlert}", observationId, actualAlert);
//
//     return Results.Ok(new { message = "Observation labeled successfully", observationId });
// });

// ============================================================================
// TASK 8: Uncomment /observations endpoint
// ============================================================================

// app.MapGet("/observations", (ObservationStore observationStore) =>
// {
//     using var activity = activitySource.StartActivity("GetObservations");
//
//     var observations = observationStore.GetAll()
//         .Select(o => new
//         {
//             observationId = o.ObservationId,
//             timestamp = o.Timestamp,
//             threshold = o.Threshold,
//             predictedAlert = o.PredictedAlert,
//             confidence = o.Confidence,
//             actualAlert = o.ActualAlert,
//             labeled = o.Labeled,
//             modelVersion = o.ModelVersion
//         });
//
//     return Results.Ok(observations);
// });

// ============================================================================
// TASK 9: Uncomment /retrain endpoint
// ============================================================================

// app.MapPost("/retrain", (ObservationStore observationStore, ModelService modelService) =>
// {
//     using var activity = activitySource.StartActivity("RetrainModel");
//
//     Log.Information("Retrain requested");
//
//     var labeledObservations = observationStore.GetLabeled();
//     if (labeledObservations.Count < 10)
//     {
//         return Results.BadRequest(new
//         {
//             message = $"Not enough labeled observations. Need 10, have {labeledObservations.Count}"
//         });
//     }
//
//     var previousVersion = modelService.CurrentVersion;
//     var mlContext = new MLContext(seed: 42);
//     var trainingData = labeledObservations.Select(o => new SignalData
//     {
//         Threshold = (float)o.Threshold,
//         Alert = o.ActualAlert ?? false
//     }).ToArray();
//
//     var dataView = mlContext.Data.LoadFromEnumerable(trainingData);
//     var pipeline = mlContext.Transforms.CopyColumns(outputColumnName: "Label", inputColumnName: "Alert")
//         .Append(mlContext.Transforms.Concatenate("Features", "Threshold"))
//         .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());
//
//     var newModel = pipeline.Fit(dataView);
//     var predictions = newModel.Transform(dataView);
//     var metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");
//
//     modelService.UpdateModel(newModel);
//
//     Log.Information("Retrained: v{Old} → v{New}, accuracy: {Accuracy}",
//         previousVersion, modelService.CurrentVersion, metrics.Accuracy);
//
//     activity?.SetTag("new_version", modelService.CurrentVersion);
//     activity?.SetTag("accuracy", metrics.Accuracy);
//
//     return Results.Ok(new
//     {
//         message = "Model retrained successfully",
//         previousVersion,
//         newVersion = modelService.CurrentVersion,
//         trainingDataCount = trainingData.Length,
//         accuracy = metrics.Accuracy
//     });
// });

// ============================================================================
// TASK 10: Uncomment /stats endpoint
// ============================================================================

// app.MapGet("/stats", (ObservationStore observationStore, ModelService modelService) =>
// {
//     using var activity = activitySource.StartActivity("GetStats");
//
//     var labeled = observationStore.GetLabeled();
//     var correct = labeled.Count(o => o.PredictedAlert == o.ActualAlert);
//     var accuracy = labeled.Count > 0 ? (double)correct / labeled.Count : 0.0;
//
//     var stats = new
//     {
//         totalObservations = observationStore.GetAll().Count,
//         labeledCount = labeled.Count,
//         accuracy,
//         currentModelVersion = modelService.CurrentVersion
//     };
//
//     Log.Information("Stats: {Total} observations, {Labeled} labeled, {Accuracy:P1} accuracy",
//         stats.totalObservations, stats.labeledCount, stats.accuracy);
//
//     return Results.Ok(stats);
// });

app.Run();
