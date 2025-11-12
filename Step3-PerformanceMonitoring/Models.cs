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

// ============================================================================
// NEW: Performance snapshot model
// ============================================================================

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
    private readonly object _lock = new();

    public ITransformer Model
    {
        get { lock (_lock) { return _model; } }
    }

    public int CurrentVersion
    {
        get { lock (_lock) { return _version; } }
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
        }

        Log.Information("Initial model trained (version {Version})", _version);
    }

    public void UpdateModel(ITransformer newModel)
    {
        lock (_lock)
        {
            _model = newModel;
            _version++;
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

                Log.Warning("SPIKE DETECTED: {Message} (p-value: {PValue:F4})",
                    anomaly.Message, prediction[2]);
            }
        }

        return anomalies;
    }

    public List<AnomalyAlert> DetectAccuracyChangePoints(List<double> accuracyHistory)
    {
        if (accuracyHistory.Count < 12)
        {
            Log.Information("Not enough data for change point detection (need 12+, have {Count})", accuracyHistory.Count);
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

                Log.Warning("CHANGE POINT DETECTED: {Message} (martingale: {Martingale:F4})",
                    anomaly.Message, prediction[3]);
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
}

// ============================================================================
// NEW: Performance Monitoring Background Service
// ============================================================================

public class PerformanceMonitoringService : BackgroundService
{
    private readonly ObservationStore _observationStore;
    private readonly AnomalyDetectionService _anomalyService;
    private readonly ModelService _modelService;
    private readonly List<PerformanceSnapshot> _performanceHistory = new();
    private readonly object _lock = new();
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public PerformanceMonitoringService(
        ObservationStore observationStore,
        AnomalyDetectionService anomalyService,
        ModelService modelService)
    {
        _observationStore = observationStore;
        _anomalyService = anomalyService;
        _modelService = modelService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("Performance monitoring service started (checking every {Interval}s)",
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
            Log.Information("Skipping monitoring: not enough labeled data ({Count} < 5)", labeled.Count);
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

        // Run anomaly detection if we have enough history
        if (_performanceHistory.Count >= 5)
        {
            var accuracyHistory = _performanceHistory.Skip(_performanceHistory.Count - 20)
                .Select(s => s.Accuracy).ToList();

            var spikes = _anomalyService.DetectAccuracySpikes(accuracyHistory);
            var changePoints = _anomalyService.DetectAccuracyChangePoints(accuracyHistory);

            if (spikes.Any() || changePoints.Any())
            {
                Log.Warning("Anomalies detected: {Spikes} spikes, {ChangePoints} change points",
                    spikes.Count, changePoints.Count);
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
