using Microsoft.ML;
using Microsoft.ML.Data;
using Serilog;
using System.Collections.Concurrent;

public class SignalData
{
    public float Threshold { get; set; }
    public bool Alert { get; set; }
}

public class AlertPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }
    public float Probability { get; set; }
    public float Score { get; set; }
}

public class Observation
{
    public Guid ObservationId { get; set; }
    public DateTime Timestamp { get; set; }
    public double Threshold { get; set; }
    public bool PredictedAlert { get; set; }
    public double Confidence { get; set; }
    public bool? ActualAlert { get; set; }
    public bool Labeled { get; set; }
    public int ModelVersion { get; set; }
}

public class AccuracyDataPoint
{
    public float Accuracy { get; set; }
}

public class SpikeDetectionResult
{
    [VectorType(3)]
    public double[] Prediction { get; set; } = new double[3];
}

public class ChangePointDetectionResult
{
    [VectorType(4)]
    public double[] Prediction { get; set; } = new double[4];
}

public class AnomalyAlert
{
    public DateTime Timestamp { get; set; }
    public string AnomalyType { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class PerformanceSnapshot
{
    public DateTime Timestamp { get; set; }
    public int TotalObservations { get; set; }
    public int LabeledObservations { get; set; }
    public double Accuracy { get; set; }
    public int ModelVersion { get; set; }
    public double AverageConfidence { get; set; }
    public int LowConfidenceCount { get; set; }
}

// ============================================================================
// NEW: Retraining decision models
// ============================================================================

public class RetrainingDecision
{
    public DateTime Timestamp { get; set; }
    public bool ShouldRetrain { get; set; }
    public double ConfidenceScore { get; set; }
    public List<string> Triggers { get; set; } = new();
    public Dictionary<string, double> FactorScores { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public class RetrainingHistory
{
    public DateTime Timestamp { get; set; }
    public int OldVersion { get; set; }
    public int NewVersion { get; set; }
    public double OldAccuracy { get; set; }
    public double NewAccuracy { get; set; }
    public int TrainingDataCount { get; set; }
    public string TriggerReason { get; set; } = string.Empty;
}

public class ObservationStore
{
    private readonly ConcurrentBag<Observation> _observations = new();

    public void Add(Observation observation) => _observations.Add(observation);

    public Observation? Get(Guid observationId) =>
        _observations.FirstOrDefault(o => o.ObservationId == observationId);

    public List<Observation> GetAll() => _observations.ToList();

    public List<Observation> GetLabeled() =>
        _observations.Where(o => o.Labeled).ToList();
}

public class ModelService
{
    private ITransformer _model;
    private int _version = 0;
    private DateTime _trainedAt = DateTime.UtcNow;
    private readonly object _lock = new();

    public ITransformer Model
    {
        get { lock (_lock) { return _model; } }
    }

    public int CurrentVersion
    {
        get { lock (_lock) { return _version; } }
    }

    public DateTime TrainedAt
    {
        get { lock (_lock) { return _trainedAt; } }
    }

    public void TrainInitialModel()
    {
        var mlContext = new MLContext(seed: 42);
        var sampleData = new[]
        {
            new SignalData { Threshold = 0.1f, Alert = false },
            new SignalData { Threshold = 0.2f, Alert = false },
            new SignalData { Threshold = 0.5f, Alert = false },
            new SignalData { Threshold = 0.7f, Alert = true },
            new SignalData { Threshold = 0.9f, Alert = true }
        };

        var dataView = mlContext.Data.LoadFromEnumerable(sampleData);
        var pipeline = mlContext.Transforms.CopyColumns(outputColumnName: "Label", inputColumnName: "Alert")
            .Append(mlContext.Transforms.Concatenate("Features", "Threshold"))
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());

        lock (_lock)
        {
            _model = pipeline.Fit(dataView);
            _version = 1;
            _trainedAt = DateTime.UtcNow;
        }

        Log.Information("Initial model trained (version {Version})", _version);
    }

    public void UpdateModel(ITransformer newModel)
    {
        lock (_lock)
        {
            _model = newModel;
            _version++;
            _trainedAt = DateTime.UtcNow;
        }

        Log.Information("Model updated to version {Version}", _version);
    }
}

public class FallbackService
{
    public object GetFallbackPrediction(double threshold)
    {
        var fallbackAlert = threshold > 0.6;

        Log.Warning("Using fallback: threshold {Threshold} → {Alert}", threshold, fallbackAlert);

        return new
        {
            predictedAlert = fallbackAlert,
            confidence = 0.5,
            observationId = Guid.NewGuid(),
            modelVersion = -1,
            fallbackUsed = true,
            fallbackReason = "Circuit breaker open or model unavailable"
        };
    }
}

public class AnomalyDetectionService
{
    private readonly List<AnomalyAlert> _anomalyHistory = new();
    private readonly object _lock = new();

    public List<AnomalyAlert> DetectAccuracySpikes(List<double> accuracyHistory)
    {
        if (accuracyHistory.Count < 12)
        {
            Log.Information("Not enough data for spike detection (need 12+, have {Count})", accuracyHistory.Count);
            return new List<AnomalyAlert>();
        }

        var mlContext = new MLContext(seed: 42);
        var dataPoints = accuracyHistory.Select(a => new AccuracyDataPoint { Accuracy = (float)a }).ToArray();
        var dataView = mlContext.Data.LoadFromEnumerable(dataPoints);

        var pipeline = mlContext.Transforms.DetectSpikeBySsa(
            outputColumnName: nameof(SpikeDetectionResult.Prediction),
            inputColumnName: nameof(AccuracyDataPoint.Accuracy),
            confidence: 95,
            pvalueHistoryLength: accuracyHistory.Count / 4,
            trainingWindowSize: accuracyHistory.Count / 2,
            seasonalityWindowSize: 3);

        var model = pipeline.Fit(dataView);
        var transformedData = model.Transform(dataView);
        var predictions = mlContext.Data.CreateEnumerable<SpikeDetectionResult>(transformedData, reuseRowObject: false).ToList();

        var anomalies = new List<AnomalyAlert>();

        for (int i = 0; i < predictions.Count; i++)
        {
            var prediction = predictions[i].Prediction;

            if (prediction[0] == 1)
            {
                var anomaly = new AnomalyAlert
                {
                    Timestamp = DateTime.UtcNow,
                    AnomalyType = "AccuracySpike",
                    Value = accuracyHistory[i],
                    Severity = prediction[2] < 0.01 ? "High" : "Medium",
                    Message = $"Sudden accuracy change detected at index {i}: {accuracyHistory[i]:P1}"
                };

                anomalies.Add(anomaly);
                lock (_lock)
                {
                    _anomalyHistory.Add(anomaly);
                }

                Log.Warning("SPIKE DETECTED: {Message}", anomaly.Message);
            }
        }

        return anomalies;
    }

    public List<AnomalyAlert> DetectAccuracyChangePoints(List<double> accuracyHistory)
    {
        if (accuracyHistory.Count < 5)
        {
            return new List<AnomalyAlert>();
        }

        var mlContext = new MLContext(seed: 42);
        var dataPoints = accuracyHistory.Select(a => new AccuracyDataPoint { Accuracy = (float)a }).ToArray();
        var dataView = mlContext.Data.LoadFromEnumerable(dataPoints);

        var pipeline = mlContext.Transforms.DetectIidChangePoint(
            outputColumnName: nameof(ChangePointDetectionResult.Prediction),
            inputColumnName: nameof(AccuracyDataPoint.Accuracy),
            confidence: 95,
            changeHistoryLength: accuracyHistory.Count / 4);

        var model = pipeline.Fit(dataView);
        var transformedData = model.Transform(dataView);
        var predictions = mlContext.Data.CreateEnumerable<ChangePointDetectionResult>(transformedData, reuseRowObject: false).ToList();

        var anomalies = new List<AnomalyAlert>();

        for (int i = 0; i < predictions.Count; i++)
        {
            var prediction = predictions[i].Prediction;

            if (prediction[0] == 1)
            {
                var anomaly = new AnomalyAlert
                {
                    Timestamp = DateTime.UtcNow,
                    AnomalyType = "AccuracyChangePoint",
                    Value = accuracyHistory[i],
                    Severity = prediction[3] > 0.9 ? "High" : "Medium",
                    Message = $"Fundamental accuracy shift detected at index {i}: {accuracyHistory[i]:P1}"
                };

                anomalies.Add(anomaly);
                lock (_lock)
                {
                    _anomalyHistory.Add(anomaly);
                }

                Log.Warning("CHANGE POINT DETECTED: {Message}", anomaly.Message);
            }
        }

        return anomalies;
    }

    public List<AnomalyAlert> GetAnomalyHistory()
    {
        lock (_lock)
        {
            return _anomalyHistory.ToList();
        }
    }

    public List<AnomalyAlert> GetRecentAnomalies(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - window;
            return _anomalyHistory.Where(a => a.Timestamp >= cutoff).ToList();
        }
    }
}

// ============================================================================
// NEW: Intelligent Retraining Decision Engine
// ============================================================================

public class RetrainingDecisionEngine
{
    private readonly List<RetrainingDecision> _decisionHistory = new();
    private readonly List<RetrainingHistory> _retrainingHistory = new();
    private readonly object _lock = new();

    // Configuration thresholds
    private readonly double _accuracyDegradationThreshold = 0.90; // Retrain if < 90% of baseline
    private readonly double _lowConfidenceThreshold = 0.30; // Retrain if > 30% low confidence
    private readonly int _minLabeledForRetraining = 10;
    private readonly double _dataGrowthThreshold = 1.5; // Retrain if 50% more data
    private readonly TimeSpan _maxModelAge = TimeSpan.FromDays(7);
    private readonly TimeSpan _minTimeBetweenRetraining = TimeSpan.FromHours(1);

    public RetrainingDecision ShouldRetrain(
        PerformanceSnapshot current,
        List<PerformanceSnapshot> history,
        List<AnomalyAlert> recentAnomalies,
        ModelService modelService)
    {
        var decision = new RetrainingDecision
        {
            Timestamp = DateTime.UtcNow,
            ShouldRetrain = false,
            ConfidenceScore = 0.0,
            Triggers = new List<string>(),
            FactorScores = new Dictionary<string, double>()
        };

        // Factor 1: Minimum data requirement
        if (current.LabeledObservations < _minLabeledForRetraining)
        {
            decision.Reason = $"Not enough labeled data ({current.LabeledObservations} < {_minLabeledForRetraining})";
            return decision;
        }

        // Factor 2: Rate limiting (don't retrain too frequently)
        var lastRetraining = GetLastRetrainingTime();
        if (lastRetraining.HasValue && DateTime.UtcNow - lastRetraining.Value < _minTimeBetweenRetraining)
        {
            decision.Reason = $"Too soon since last retraining (minimum {_minTimeBetweenRetraining.TotalMinutes}min)";
            return decision;
        }

        double totalScore = 0.0;

        // Factor 3: Accuracy degradation
        var baselineAccuracy = GetBaselineAccuracy(history);
        if (baselineAccuracy > 0)
        {
            var accuracyRatio = current.Accuracy / baselineAccuracy;
            var accuracyScore = accuracyRatio < _accuracyDegradationThreshold ? 0.3 : 0.0;
            decision.FactorScores["AccuracyDegradation"] = accuracyScore;
            totalScore += accuracyScore;

            if (accuracyScore > 0)
            {
                decision.Triggers.Add($"Accuracy degraded: {current.Accuracy:P1} vs baseline {baselineAccuracy:P1}");
            }
        }

        // Factor 4: Low confidence predictions
        var lowConfidenceRate = current.LabeledObservations > 0
            ? (double)current.LowConfidenceCount / current.LabeledObservations
            : 0.0;
        var confidenceScore = lowConfidenceRate > _lowConfidenceThreshold ? 0.25 : 0.0;
        decision.FactorScores["LowConfidence"] = confidenceScore;
        totalScore += confidenceScore;

        if (confidenceScore > 0)
        {
            decision.Triggers.Add($"High low-confidence rate: {lowConfidenceRate:P1}");
        }

        // Factor 5: Data growth
        var baselineDataCount = GetBaselineDataCount(history);
        if (baselineDataCount > 0)
        {
            var dataGrowthRatio = (double)current.LabeledObservations / baselineDataCount;
            var dataGrowthScore = dataGrowthRatio >= _dataGrowthThreshold ? 0.15 : 0.0;
            decision.FactorScores["DataGrowth"] = dataGrowthScore;
            totalScore += dataGrowthScore;

            if (dataGrowthScore > 0)
            {
                decision.Triggers.Add($"Significant data growth: {current.LabeledObservations} vs baseline {baselineDataCount}");
            }
        }

        // Factor 6: Recent anomalies
        var highSeverityAnomalies = recentAnomalies.Count(a => a.Severity == "High");
        var anomalyScore = highSeverityAnomalies > 0 ? 0.2 : (recentAnomalies.Count > 0 ? 0.1 : 0.0);
        decision.FactorScores["Anomalies"] = anomalyScore;
        totalScore += anomalyScore;

        if (anomalyScore > 0)
        {
            decision.Triggers.Add($"Anomalies detected: {recentAnomalies.Count} total, {highSeverityAnomalies} high severity");
        }

        // Factor 7: Model age
        var modelAge = DateTime.UtcNow - modelService.TrainedAt;
        var ageScore = modelAge > _maxModelAge ? 0.1 : 0.0;
        decision.FactorScores["ModelAge"] = ageScore;
        totalScore += ageScore;

        if (ageScore > 0)
        {
            decision.Triggers.Add($"Model age: {modelAge.TotalDays:F1} days");
        }

        // Decision logic: Retrain if total score > 0.5
        decision.ConfidenceScore = totalScore;
        decision.ShouldRetrain = totalScore >= 0.5;

        if (decision.ShouldRetrain)
        {
            decision.Reason = $"Multiple factors triggered retraining (score: {totalScore:F2})";
        }
        else
        {
            decision.Reason = $"No retraining needed (score: {totalScore:F2} < 0.5 threshold)";
        }

        lock (_lock)
        {
            _decisionHistory.Add(decision);
        }

        return decision;
    }

    public void RecordRetraining(int oldVersion, int newVersion, double oldAccuracy, double newAccuracy,
        int trainingDataCount, string triggerReason)
    {
        var record = new RetrainingHistory
        {
            Timestamp = DateTime.UtcNow,
            OldVersion = oldVersion,
            NewVersion = newVersion,
            OldAccuracy = oldAccuracy,
            NewAccuracy = newAccuracy,
            TrainingDataCount = trainingDataCount,
            TriggerReason = triggerReason
        };

        lock (_lock)
        {
            _retrainingHistory.Add(record);
        }

        Log.Information("Retraining recorded: v{Old} → v{New}, accuracy {OldAcc:P1} → {NewAcc:P1}",
            oldVersion, newVersion, oldAccuracy, newAccuracy);
    }

    private double GetBaselineAccuracy(List<PerformanceSnapshot> history)
    {
        if (history.Count == 0) return 0.0;

        // Use average of first 5 snapshots as baseline
        var baseline = history.Take(5).ToList();
        return baseline.Count > 0 ? baseline.Average(s => s.Accuracy) : 0.0;
    }

    private int GetBaselineDataCount(List<PerformanceSnapshot> history)
    {
        if (history.Count == 0) return 0;

        // Use first snapshot's data count as baseline
        return history.First().LabeledObservations;
    }

    private DateTime? GetLastRetrainingTime()
    {
        lock (_lock)
        {
            return _retrainingHistory.LastOrDefault()?.Timestamp;
        }
    }

    public List<RetrainingDecision> GetDecisionHistory()
    {
        lock (_lock)
        {
            return _decisionHistory.ToList();
        }
    }

    public List<RetrainingHistory> GetRetrainingHistory()
    {
        lock (_lock)
        {
            return _retrainingHistory.ToList();
        }
    }
}

// ============================================================================
// UPDATED: Performance Monitoring with Intelligent Retraining
// ============================================================================

public class PerformanceMonitoringService : BackgroundService
{
    private readonly ObservationStore _observationStore;
    private readonly AnomalyDetectionService _anomalyService;
    private readonly ModelService _modelService;
    private readonly RetrainingDecisionEngine? _decisionEngine;
    private readonly List<PerformanceSnapshot> _performanceHistory = new();
    private readonly object _lock = new();
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public PerformanceMonitoringService(
        ObservationStore observationStore,
        AnomalyDetectionService anomalyService,
        ModelService modelService,
        RetrainingDecisionEngine? decisionEngine = null)
    {
        _observationStore = observationStore;
        _anomalyService = anomalyService;
        _modelService = modelService;
        _decisionEngine = decisionEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("Performance monitoring service started with intelligent retraining (checking every {Interval}s)",
            _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await MonitorPerformanceAsync();
            }
            catch (OperationCanceledException)
            {
                Log.Information("Performance monitoring service stopping");
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in performance monitoring");
            }
        }
    }

    private async Task MonitorPerformanceAsync()
    {
        var labeled = _observationStore.GetLabeled();
        var all = _observationStore.GetAll();

        if (labeled.Count < 5)
        {
            Log.Debug("Skipping monitoring: not enough labeled data ({Count} < 5)", labeled.Count);
            return;
        }

        var correct = labeled.Count(o => o.PredictedAlert == o.ActualAlert);
        var accuracy = (double)correct / labeled.Count;
        var avgConfidence = labeled.Average(o => o.Confidence);
        var lowConfidenceCount = labeled.Count(o => o.Confidence < 0.6);

        var snapshot = new PerformanceSnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalObservations = all.Count,
            LabeledObservations = labeled.Count,
            Accuracy = accuracy,
            ModelVersion = _modelService.CurrentVersion,
            AverageConfidence = avgConfidence,
            LowConfidenceCount = lowConfidenceCount
        };

        lock (_lock)
        {
            _performanceHistory.Add(snapshot);
        }

        Log.Information(
            "Performance Snapshot: Accuracy={Accuracy:P1}, Labeled={Labeled}, AvgConfidence={AvgConf:F2}, LowConf={LowConf}",
            accuracy, labeled.Count, avgConfidence, lowConfidenceCount);

        // Run anomaly detection
        if (_performanceHistory.Count >= 5)
        {
            var accuracyHistory = _performanceHistory.Skip(Math.Max(0, _performanceHistory.Count - 20))
                .Select(s => s.Accuracy).ToList();

            var spikes = _anomalyService.DetectAccuracySpikes(accuracyHistory);
            var changePoints = _anomalyService.DetectAccuracyChangePoints(accuracyHistory);

            if (spikes.Any() || changePoints.Any())
            {
                Log.Warning("Anomalies detected: {Spikes} spikes, {ChangePoints} change points",
                    spikes.Count, changePoints.Count);
            }
        }

        // Check if we should retrain (only if decision engine is registered)
        if (_decisionEngine != null)
        {
            var recentAnomalies = _anomalyService.GetRecentAnomalies(TimeSpan.FromMinutes(10));
            var decision = _decisionEngine.ShouldRetrain(snapshot, _performanceHistory, recentAnomalies, _modelService);

            if (decision.ShouldRetrain)
            {
                Log.Warning("RETRAINING RECOMMENDED: {Reason}", decision.Reason);
                Log.Information("Triggers: {Triggers}", string.Join(", ", decision.Triggers));
                Log.Information("Confidence score: {Score:F2}", decision.ConfidenceScore);

                // Note: Actual retraining would happen here in Step 5/6
                // For now, just log the recommendation
            }
            else
            {
                Log.Information("No retraining needed: {Reason}", decision.Reason);
            }
        }

        await Task.CompletedTask;
    }

    public List<PerformanceSnapshot> GetPerformanceHistory()
    {
        lock (_lock)
        {
            return _performanceHistory.ToList();
        }
    }
}
