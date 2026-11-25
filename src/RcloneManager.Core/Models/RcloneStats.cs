using System.Text.Json.Serialization;

namespace RcloneManager.Core.Models;

public sealed class RcloneTransferItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("percentage")]
    public int Percentage { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("speedAvg")]
    public double SpeedAvg { get; set; }

    [JsonPropertyName("eta")]
    public double? Eta { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }
}

public sealed class RcloneStats
{
    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("checks")]
    public long Checks { get; set; }

    [JsonPropertyName("totalChecks")]
    public long TotalChecks { get; set; }

    [JsonPropertyName("transfers")]
    public long Transfers { get; set; }

    [JsonPropertyName("totalTransfers")]
    public long TotalTransfers { get; set; }

    [JsonPropertyName("errors")]
    public long Errors { get; set; }

    [JsonPropertyName("deletes")]
    public long Deletes { get; set; }

    [JsonPropertyName("renames")]
    public long Renames { get; set; }

    [JsonPropertyName("listed")]
    public long Listed { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("eta")]
    public double? Eta { get; set; }

    [JsonPropertyName("elapsedTime")]
    public double ElapsedTime { get; set; }

    [JsonPropertyName("transferTime")]
    public double TransferTime { get; set; }

    [JsonPropertyName("fatalError")]
    public bool FatalError { get; set; }

    [JsonPropertyName("retryError")]
    public bool RetryError { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("transferring")]
    public List<RcloneTransferItem>? Transferring { get; set; }

    [JsonIgnore]
    public double ProgressPercent =>
        TotalBytes > 0
            ? Math.Clamp(Bytes * 100.0 / TotalBytes, 0.0, 100.0)
            : (TotalTransfers > 0 ? Math.Clamp(Transfers * 100.0 / TotalTransfers, 0.0, 100.0) : 0.0);
}

public sealed class RcloneBwLimitInfo
{
    [JsonPropertyName("bytesPerSecond")]
    public long BytesPerSecond { get; set; }

    [JsonPropertyName("bytesPerSecondRx")]
    public long BytesPerSecondRx { get; set; }

    [JsonPropertyName("bytesPerSecondTx")]
    public long BytesPerSecondTx { get; set; }

    [JsonPropertyName("rate")]
    public string Rate { get; set; } = "off";
}

public sealed class RcloneAboutInfo
{
    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("used")]
    public long? Used { get; set; }

    [JsonPropertyName("free")]
    public long? Free { get; set; }

    [JsonPropertyName("trashed")]
    public long? Trashed { get; set; }
}
