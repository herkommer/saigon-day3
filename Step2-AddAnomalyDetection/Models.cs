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

// ============================================================================
// NEW: Anomaly detection data models
// ============================================================================

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

// ============================================================================
// NEW: Anomaly Detection Service
// ============================================================================

public class AnomalyDetectionService
{
    private readonly List<AnomalyAlert> _anomalyHistory = new();
    private readonly object _lock = new();

    public List<AnomalyAlert> DetectAccuracySpikes(List<double> accuracyHistory)
    {
        if (accuracyHistory.Count < 5)
        {
            Log.Information("Not enough data for spike detection (need 5+, have {Count})", accuracyHistory.Count);
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
            // prediction[0] = isSpike (0 or 1)
            // prediction[1] = raw score
            // prediction[2] = p-value

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
        if (accuracyHistory.Count < 5)
        {
            Log.Information("Not enough data for change point detection (need 5+, have {Count})", accuracyHistory.Count);
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
            // prediction[0] = alert (0 or 1)
            // prediction[1] = raw score
            // prediction[2] = p-value
            // prediction[3] = martingale score

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
