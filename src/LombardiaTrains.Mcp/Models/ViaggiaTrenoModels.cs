using System.Text.Json.Serialization;

namespace LombardiaTrains.Mcp.Models;

public sealed class VtStation
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("nomeLungo")] public string? LongName { get; init; }
    [JsonPropertyName("nomeBreve")] public string? ShortName { get; init; }
}

/// <summary>One row of a departures or arrivals board.</summary>
public sealed class VtBoardRow
{
    [JsonPropertyName("numeroTreno")] public int? TrainNumber { get; init; }
    [JsonPropertyName("categoria")] public string? Category { get; init; }
    [JsonPropertyName("destinazione")] public string? Destination { get; init; }
    [JsonPropertyName("origine")] public string? Origin { get; init; }

    /// <summary>Already formatted as "HH:MM" by the upstream API.</summary>
    [JsonPropertyName("compOrarioPartenza")] public string? DepartureTime { get; init; }
    [JsonPropertyName("compOrarioArrivo")] public string? ArrivalTime { get; init; }

    /// <summary>Minutes late. Negative means early.</summary>
    [JsonPropertyName("ritardo")] public int? Delay { get; init; }

    /// <summary>1 means the train has been cancelled.</summary>
    [JsonPropertyName("provvedimento")] public int? Measure { get; init; }

    [JsonPropertyName("binarioEffettivoPartenzaDescrizione")] public string? ActualDepPlatform { get; init; }
    [JsonPropertyName("binarioProgrammatoPartenzaDescrizione")] public string? PlannedDepPlatform { get; init; }
    [JsonPropertyName("binarioEffettivoArrivoDescrizione")] public string? ActualArrPlatform { get; init; }
    [JsonPropertyName("binarioProgrammatoArrivoDescrizione")] public string? PlannedArrPlatform { get; init; }

    /// <summary>Actual platform wins over the planned one; "-" when neither is published.</summary>
    [JsonIgnore]
    public string Platform =>
        Coalesce(ActualDepPlatform, PlannedDepPlatform, ActualArrPlatform, PlannedArrPlatform) ?? "-";

    [JsonIgnore] public bool IsCancelled => Measure == 1;

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Live progress of a train, stop by stop.</summary>
public sealed class VtTrainProgress
{
    [JsonPropertyName("numeroTreno")] public int? TrainNumber { get; init; }
    [JsonPropertyName("categoria")] public string? Category { get; init; }
    [JsonPropertyName("origine")] public string? Origin { get; init; }
    [JsonPropertyName("destinazione")] public string? Destination { get; init; }
    [JsonPropertyName("ritardo")] public int? Delay { get; init; }
    [JsonPropertyName("stazioneUltimoRilevamento")] public string? LastSeenAt { get; init; }
    [JsonPropertyName("compOraUltimoRilevamento")] public string? LastSeenTime { get; init; }
    [JsonPropertyName("provvedimento")] public int? Measure { get; init; }
    [JsonPropertyName("fermate")] public List<VtStop> Stops { get; init; } = [];

    [JsonIgnore] public bool IsCancelled => Measure == 1;
}

public sealed class VtStop
{
    [JsonPropertyName("stazione")] public string? Station { get; init; }

    // ---- Epoch milliseconds ---------------------------------------------
    // These are Unix timestamps in milliseconds. They must always be converted
    // declaring Europe/Rome explicitly: on a machine running in UTC they come
    // out two hours off, and the bug only shows up in production.

    [JsonPropertyName("programmata")] public long? Scheduled { get; init; }
    [JsonPropertyName("partenza_teorica")] public long? ScheduledDeparture { get; init; }
    [JsonPropertyName("arrivo_teorico")] public long? ScheduledArrival { get; init; }
    [JsonPropertyName("effettiva")] public long? Actual { get; init; }
    [JsonPropertyName("partenzaReale")] public long? ActualDeparture { get; init; }
    [JsonPropertyName("arrivoReale")] public long? ActualArrival { get; init; }

    [JsonPropertyName("ritardo")] public int? Delay { get; init; }

    [JsonPropertyName("binarioEffettivoArrivoDescrizione")] public string? ActualArrPlatform { get; init; }
    [JsonPropertyName("binarioProgrammatoArrivoDescrizione")] public string? PlannedArrPlatform { get; init; }
    [JsonPropertyName("binarioProgrammatoPartenzaDescrizione")] public string? PlannedDepPlatform { get; init; }

    [JsonIgnore]
    public string Platform =>
        new[] { ActualArrPlatform, PlannedArrPlatform, PlannedDepPlatform }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "-";
}
