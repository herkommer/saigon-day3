using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;
using Serilog;
using System.Collections.Concurrent;

// === CONCEPTUAL MODELS FOR STEP 5 ===
// These are design patterns, not full implementations.
// See Step6 for production-ready code.

// === DOMAIN MODELS (from previous steps) ===

public class Observation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public float Threshold { get; set; }
    public bool? ActualAlert { get; set; }
    public AlertPrediction? Prediction { get; set; }
    public bool? WasCorrect => ActualAlert.HasValue && Prediction != null
        ? ActualAlert.Value == Prediction.Alert
        : null;
}

// ML.NET training data class (only properties ML.NET can process)
public class TrainingData
{
    public float Threshold { get; set; }
    public bool ActualAlert { get; set; }
}

public class AlertPrediction
{
    [ColumnName("PredictedLabel")]
    public bool Alert { get; set; }

    [ColumnName("Score")]
    public float Confidence { get; set; }
}

public class AccuracyDataPoint
{
    public float Accuracy { get; set; }
}

public class PerformanceSnapshot
{
    public DateTime Timestamp { get; set; }
    public double Accuracy { get; set; }
    public int LabeledCount { get; set; }
    public double AverageConfidence { get; set; }
    public int ModelVersion { get; set; }
    public bool AnomalyDetected { get; set; }
}

// === STORAGE (from previous steps) ===

public class ObservationStore
{
    private readonly ConcurrentBag<Observation> _observations = new();
    public ConcurrentBag<PerformanceSnapshot> PerformanceSnapshots { get; } = new();

    public void Add(Observation obs, AlertPrediction prediction)
    {
        obs.Prediction = prediction;
        _observations.Add(obs);
    }

    public bool Label(Guid id, bool actualAlert)
    {
        var obs = _observations.FirstOrDefault(o => o.Id == id);
        if (obs != null)
        {
            obs.ActualAlert = actualAlert;
            return true;
        }
        return false;
    }

    public IEnumerable<Observation> GetAllObservations() => _observations;
    public IEnumerable<Observation> GetLabeledObservations() =>
        _observations.Where(o => o.ActualAlert.HasValue);

    public List<AccuracyDataPoint> GetAccuracyTimeSeries()
    {
        var labeled = GetLabeledObservations().OrderBy(o => o.Timestamp).ToList();
        if (labeled.Count < 5) return new();

        var windowSize = 5;
        var timeSeries = new List<AccuracyDataPoint>();

        for (int i = windowSize; i <= labeled.Count; i++)
        {
            var window = labeled.Skip(i - windowSize).Take(windowSize);
            var correct = window.Count(o => o.WasCorrect == true);
            var accuracy = (float)correct / windowSize;
            timeSeries.Add(new AccuracyDataPoint { Accuracy = accuracy });
        }

        return timeSeries;
    }

    public void RecordSnapshot(PerformanceSnapshot snapshot)
    {
        PerformanceSnapshots.Add(snapshot);
    }
}

// === ML SERVICE (from previous steps) ===

public class ModelService
{
    private readonly MLContext _mlContext = new();
    private ITransformer? _model;
    private PredictionEngine<TrainingData, AlertPrediction>? _predictionEngine;
    private readonly ObservationStore _store;
    public int CurrentVersion { get; private set; } = 0;

    public ModelService(ObservationStore store)
    {
        _store = store;
    }

    public void InitializeModel()
    {
        Log.Information("Initializing model with sample data...");

        var trainingData = new[]
        {
            new TrainingData { Threshold = 0.1f, ActualAlert = false },
            new TrainingData { Threshold = 0.3f, ActualAlert = false },
            new TrainingData { Threshold = 0.5f, ActualAlert = false },
            new TrainingData { Threshold = 0.7f, ActualAlert = true },
            new TrainingData { Threshold = 0.9f, ActualAlert = true }
        };

        TrainModel(trainingData);
    }

    public void TrainModel(IEnumerable<TrainingData> labeledData)
    {
        var data = _mlContext.Data.LoadFromEnumerable(labeledData);

        var pipeline = _mlContext.Transforms.Concatenate("Features", nameof(TrainingData.Threshold))
            .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName: nameof(TrainingData.ActualAlert),
                featureColumnName: "Features"));

        _model = pipeline.Fit(data);
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<TrainingData, AlertPrediction>(_model);

        CurrentVersion++;
        Log.Information("Model trained (version {Version})", CurrentVersion);
    }

    public void Retrain()
    {
        var labeledObs = _store.GetLabeledObservations().ToList();
        if (labeledObs.Count >= 5)
        {
            var oldVersion = CurrentVersion;

            // Convert observations to training data
            var trainingData = labeledObs
                .Where(o => o.ActualAlert.HasValue)
                .Select(o => new TrainingData
                {
                    Threshold = o.Threshold,
                    ActualAlert = o.ActualAlert!.Value
                })
                .ToList();

            TrainModel(trainingData);

            Log.Information("Retrained: v{Old} → v{New}", oldVersion, CurrentVersion);
        }
    }

    public AlertPrediction Predict(Observation obs)
    {
        if (_predictionEngine == null)
        {
            return new AlertPrediction { Alert = false, Confidence = 0 };
        }

        // Convert to training data for prediction
        var input = new TrainingData { Threshold = obs.Threshold, ActualAlert = false };
        return _predictionEngine.Predict(input);
    }
}

public class FallbackService
{
    public AlertPrediction GetFallbackPrediction(float threshold)
    {
        Log.Warning("Using fallback prediction for threshold {Threshold}", threshold);
        return new AlertPrediction
        {
            Alert = threshold > 0.5f,
            Confidence = 0.5f
        };
    }
}

// === ANOMALY DETECTION (from Step 2) ===

public class AnomalyDetectionService
{
    private readonly MLContext _mlContext = new();

    public bool DetectAccuracySpike(List<AccuracyDataPoint> accuracyData)
    {
        if (accuracyData.Count < 5) return false;

        var dataView = _mlContext.Data.LoadFromEnumerable(accuracyData);

        var pipeline = _mlContext.Transforms.DetectSpikeBySsa(
            outputColumnName: nameof(SpikePrediction.Prediction),
            inputColumnName: nameof(AccuracyDataPoint.Accuracy),
            confidence: 95,
            pvalueHistoryLength: accuracyData.Count / 4,
            trainingWindowSize: accuracyData.Count / 2,
            seasonalityWindowSize: 5);

        var model = pipeline.Fit(dataView);
        var transformedData = model.Transform(dataView);
        var predictions = _mlContext.Data
            .CreateEnumerable<SpikePrediction>(transformedData, reuseRowObject: false)
            .ToList();

        var lastPrediction = predictions.LastOrDefault();
        if (lastPrediction != null && lastPrediction.Prediction[0] == 1)
        {
            Log.Warning("SPIKE DETECTED: p-value: {PValue:F4}", lastPrediction.Prediction[2]);
            return true;
        }

        return false;
    }

    public bool DetectAccuracyChangePoint(List<AccuracyDataPoint> accuracyData)
    {
        if (accuracyData.Count < 5) return false;

        var dataView = _mlContext.Data.LoadFromEnumerable(accuracyData);

        var pipeline = _mlContext.Transforms.DetectIidChangePoint(
            outputColumnName: nameof(ChangePointPrediction.Prediction),
            inputColumnName: nameof(AccuracyDataPoint.Accuracy),
            confidence: 95,
            changeHistoryLength: accuracyData.Count / 4);

        var model = pipeline.Fit(dataView);
        var transformedData = model.Transform(dataView);
        var predictions = _mlContext.Data
            .CreateEnumerable<ChangePointPrediction>(transformedData, reuseRowObject: false)
            .ToList();

        var lastPrediction = predictions.LastOrDefault();
        if (lastPrediction != null && lastPrediction.Prediction[0] == 1)
        {
            Log.Warning("CHANGE POINT DETECTED: p-value: {PValue:F4}", lastPrediction.Prediction[3]);
            return true;
        }

        return false;
    }
}

public class SpikePrediction
{
    [VectorType(3)]
    public double[] Prediction { get; set; } = new double[3];
}

public class ChangePointPrediction
{
    [VectorType(4)]
    public double[] Prediction { get; set; } = new double[4];
}

// === RETRAINING DECISION ENGINE (from Step 4) ===

public class RetrainingDecisionEngine
{
    private readonly ObservationStore _store;
    private DateTime? _lastRetrainingTime = null; // Nullable to allow first retraining
    private const int MinHoursBetweenRetraining = 1;

    public RetrainingDecisionEngine(ObservationStore store)
    {
        _store = store;
    }

    public (bool shouldRetrain, double score, Dictionary<string, double> factorScores) ShouldRetrain(
        double currentAccuracy,
        double baselineAccuracy,
        bool anomalyDetected)
    {
        var factors = new Dictionary<string, double>();

        var accuracyDrop = baselineAccuracy - currentAccuracy;
        factors["accuracyDegradation"] = Math.Max(0, accuracyDrop) * 3.0;

        var recentObs = _store.GetAllObservations()
            .OrderByDescending(o => o.Timestamp)
            .Take(20)
            .ToList();

        var lowConfidenceRate = recentObs.Any()
            ? recentObs.Count(o => o.Prediction?.Confidence < 0.7) / (double)recentObs.Count
            : 0;
        factors["lowConfidence"] = lowConfidenceRate * 2.5;

        var labeledCount = _store.GetLabeledObservations().Count();
        var dataGrowthFactor = Math.Min(1.0, labeledCount / 50.0);
        factors["dataGrowth"] = dataGrowthFactor * 1.5;

        factors["anomaly"] = anomalyDetected ? 2.0 : 0.0;

        var hoursSinceRetrain = _lastRetrainingTime.HasValue
            ? (DateTime.UtcNow - _lastRetrainingTime.Value).TotalHours
            : 24.0;
        var ageFactor = Math.Min(1.0, hoursSinceRetrain / 24.0);
        factors["modelAge"] = ageFactor * 1.0;

        if (labeledCount < 10)
        {
            factors["insufficientData"] = -10.0;
            var totalScore = factors.Values.Sum();
            var normalizedScore = Math.Max(0, Math.Min(1.0, totalScore / 10.0));
            return (false, normalizedScore, factors);
        }

        if (_lastRetrainingTime.HasValue && hoursSinceRetrain < MinHoursBetweenRetraining)
        {
            factors["rateLimited"] = -10.0;
            var totalScore = factors.Values.Sum();
            var normalizedScore = Math.Max(0, Math.Min(1.0, totalScore / 10.0));
            return (false, normalizedScore, factors);
        }

        var finalScore = factors.Values.Sum();
        var finalNormalizedScore = Math.Max(0, Math.Min(1.0, finalScore / 10.0));

        var shouldRetrain = finalNormalizedScore >= 0.5;

        return (shouldRetrain, finalNormalizedScore, factors);
    }
    public void RecordRetraining()
    {
        _lastRetrainingTime = DateTime.UtcNow;
    }
}

// === BACKGROUND MONITORING (from Step 3/4) ===

public class PerformanceMonitoringService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PerformanceMonitoringService> _logger;
    private RetrainingDecisionEngine? _decisionEngine;

    public PerformanceMonitoringService(
        IServiceProvider serviceProvider,
        ILogger<PerformanceMonitoringService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ObservationStore>();
        _decisionEngine = new RetrainingDecisionEngine(store);

        _logger.LogInformation("Performance monitoring service started (Step 5 - conceptual)");

        var baselineAccuracy = 1.0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                var labeled = store.GetLabeledObservations().ToList();
                if (!labeled.Any()) continue;

                var correct = labeled.Count(o => o.WasCorrect == true);
                var currentAccuracy = (double)correct / labeled.Count;

                var allObs = store.GetAllObservations().ToList();
                var avgConfidence = allObs.Any()
                    ? allObs.Average(o => o.Prediction?.Confidence ?? 0)
                    : 0;

                var modelService = scope.ServiceProvider.GetRequiredService<ModelService>();
                var anomalyService = scope.ServiceProvider.GetRequiredService<AnomalyDetectionService>();

                var accuracyData = store.GetAccuracyTimeSeries();
                var spikeDetected = anomalyService.DetectAccuracySpike(accuracyData);
                var changeDetected = anomalyService.DetectAccuracyChangePoint(accuracyData);
                var anomalyDetected = spikeDetected || changeDetected;

                var snapshot = new PerformanceSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    Accuracy = currentAccuracy,
                    LabeledCount = labeled.Count,
                    AverageConfidence = avgConfidence,
                    ModelVersion = modelService.CurrentVersion,
                    AnomalyDetected = anomalyDetected
                };
                store.RecordSnapshot(snapshot);

                _logger.LogInformation(
                    "Performance Snapshot: Accuracy={Accuracy:P0}, Labeled={Labeled}",
                    currentAccuracy, labeled.Count);

                var (shouldRetrain, score, factors) = _decisionEngine.ShouldRetrain(
                    currentAccuracy, baselineAccuracy, anomalyDetected);

                if (shouldRetrain)
                {
                    _logger.LogWarning("RETRAINING RECOMMENDED (score: {Score:F2})", score);
                    _logger.LogInformation("NOTE: Step 5 is conceptual - see Step 6 for deployment logic");
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when service is stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in performance monitoring loop");
                // Continue monitoring despite errors
            }
        }

        _logger.LogInformation("Performance monitoring service stopped");
    }
}

// === CONCEPTUAL DEPLOYMENT SERVICES (commented out) ===
// These patterns are explained in TASKS.md
// Full implementation in Step6-CompleteAutonomousSystem

// public class ShadowModeService
// {
//     // Concept: Run new model alongside production without switching
//     // Track predictions from both, compare quality
//     // When shadow model proves superior, promote to production
//     
//     private ModelService _productionModel;
//     private ModelService _shadowModel;
//     
//     public ShadowComparisonResult GetShadowComparison()
//     {
//         // Compare accuracy, latency, agreement rate
//         // Return recommendation for promotion
//     }
// }

// public class DeploymentOrchestrator
// {
//     // Concept: Canary deployment - gradually shift traffic
//     // Start: 1% → 5% → 20% → 50% → 100%
//     // Monitor each stage, rollback if quality degrades
//     
//     private int _canaryPercentage = 0;
//     
//     public void SetCanaryPercentage(int percentage)
//     {
//         // Validate percentage
//         // Update routing logic
//         // Monitor for degradation
//     }
//     
//     public void AutomaticRollback(string reason)
//     {
//         // Triggers: accuracy drop > 5%, errors > 2x, latency > 50%
//         // Revert to previous model version
//         // Alert operations team
//     }
// }

// public class ShadowComparisonResult
// {
//     public double ProductionAccuracy { get; set; }
//     public double ShadowAccuracy { get; set; }
//     public double AgreementRate { get; set; }
//     public bool ReadyForPromotion { get; set; }
// }
