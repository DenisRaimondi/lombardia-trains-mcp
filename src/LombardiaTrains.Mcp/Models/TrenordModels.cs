using System.Text.Json.Serialization;

namespace LombardiaTrains.Mcp.Models;

/// <summary>
/// Trenord returns an array: the same train number can run more than once a day.
/// Three levels of nesting: run -> journey -> stops.
/// </summary>
public sealed class TrenordRun
{
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("dep_time")] public string? DepTime { get; init; }
    [JsonPropertyName("arr_time")] public string? ArrTime { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("dep_station")] public TrenordStation? DepStation { get; init; }
    [JsonPropertyName("arr_station")] public TrenordStation? ArrStation { get; init; }
    [JsonPropertyName("journey_list")] public List<TrenordJourney> Journeys { get; init; } = [];
}

public sealed class TrenordStation
{
    [JsonPropertyName("station_id")] public string? Id { get; init; }
    [JsonPropertyName("station_ori_name")] public string? Name { get; init; }
}

public sealed class TrenordJourney
{
    [JsonPropertyName("train")] public TrenordTrain? Train { get; init; }
    [JsonPropertyName("pass_list")] public List<TrenordStop> Stops { get; init; } = [];
}

public sealed class TrenordTrain
{
    [JsonPropertyName("train_id")] public string? TrainId { get; init; }
    [JsonPropertyName("line")] public string? Line { get; init; }
    [JsonPropertyName("train_category")] public string? Category { get; init; }

    /// <summary>Raw value looks like "TRENORD$:$FNM3". The separator is literally "$:$".</summary>
    [JsonPropertyName("train_operator")] public string? OperatorRaw { get; init; }

    [JsonPropertyName("direction")] public string? Direction { get; init; }

    /// <summary>Minutes. Negative means the train is running early.</summary>
    [JsonPropertyName("delay")] public int? Delay { get; init; }

    /// <summary>N not departed, V travelling, P departed, A arrived, C cancelled.</summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("bicycle")] public bool? Bicycle { get; init; }
    [JsonPropertyName("handicap")] public bool? Handicap { get; init; }
    [JsonPropertyName("mxp")] public bool? MalpensaExpress { get; init; }
    [JsonPropertyName("has_live_info")] public bool? HasLiveInfo { get; init; }
    [JsonPropertyName("actual_station")] public string? ActualStation { get; init; }
    [JsonPropertyName("actual_time")] public string? ActualTime { get; init; }

    // ---- Conditional fields ----------------------------------------------
    // These four are NOT null when there is nothing to report: they are absent
    // from the JSON entirely. Verified on five regular trains (S5, S11, RE_5,
    // R27): none of them carried these properties at all.
    //
    // That is why they are nullable and must never be dereferenced blindly.
    // Code that assumes they exist works on the broken trains and crashes on
    // the normal ones, which is the worst way to find a bug.

    /// <summary>Crowding percentage. Absent when not measured.</summary>
    [JsonPropertyName("average_crowding")] public int? AverageCrowding { get; init; }

    [JsonPropertyName("average_crowding_label")] public string? CrowdingLabel { get; init; }

    /// <summary>2 means suppressed. Absent when the train runs normally.</summary>
    [JsonPropertyName("suppression_type")] public int? SuppressionType { get; init; }

    [JsonPropertyName("alerts")] public List<TrenordAlert>? Alerts { get; init; }

    /// <summary>Operator name without the "$:$" suffix.</summary>
    [JsonIgnore]
    public string? Operator => OperatorRaw?.Split("$:$")[0];

    [JsonIgnore]
    public string StatusText => Status switch
    {
        "N" => "not departed",
        "V" => "travelling",
        "P" => "departed",
        "A" => "arrived",
        "C" => "cancelled",
        _ => Status ?? "unknown"
    };
}

public sealed class TrenordAlert
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed class TrenordStop
{
    [JsonPropertyName("station")] public TrenordStation? Station { get; init; }

    /// <summary>Local time, "HH:MM:SS". Use this for display.</summary>
    [JsonPropertyName("arr_time")] public string? ArrTime { get; init; }
    [JsonPropertyName("dep_time")] public string? DepTime { get; init; }

    /// <summary>ISO 8601 in UTC (with the trailing Z). Convert before showing it.</summary>
    [JsonPropertyName("arr_date_time")] public string? ArrDateTimeUtc { get; init; }
    [JsonPropertyName("dep_date_time")] public string? DepDateTimeUtc { get; init; }

    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("is_actual_platform")] public bool? IsActualPlatform { get; init; }
    [JsonPropertyName("cancelled")] public bool? Cancelled { get; init; }

    /// <summary>O origin, F intermediate stop, D destination.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>Only present for stops the train has already passed.</summary>
    [JsonPropertyName("actual_data")] public TrenordActualData? ActualData { get; init; }
}

public sealed class TrenordActualData
{
    [JsonPropertyName("arr_actual_time")] public string? ArrActualTime { get; init; }
    [JsonPropertyName("dep_actual_time")] public string? DepActualTime { get; init; }
    [JsonPropertyName("arr_delay_actual")] public int? ArrDelay { get; init; }
    [JsonPropertyName("dep_delay_actual")] public int? DepDelay { get; init; }
}
