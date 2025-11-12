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
