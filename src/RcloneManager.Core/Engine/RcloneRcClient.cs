using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Engine;

public sealed class RcloneRcClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public RcloneRcClient(int port, TimeSpan? timeout = null)
    {
        _baseUrl = $"http://127.0.0.1:{port}";
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        };
    }

    public string BaseUrl => _baseUrl;

    private async Task<JsonDocument?> PostAsync(string endpoint, object? payload = null, CancellationToken ct = default)
    {
        var json = payload == null ? "{}" : JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(endpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Rclone RC {endpoint} retornou {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            return null;

        return JsonDocument.Parse(body);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostAsync("/core/pid", null, ct);
            return doc != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("/core/version", null, ct);
        if (doc != null && doc.RootElement.TryGetProperty("version", out var versionProp))
            return versionProp.GetString();
        return null;
    }

    public async Task<List<string>> ListRemotesAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("/config/listremotes", null, ct);
        var remotes = new List<string>();
        if (doc != null && doc.RootElement.TryGetProperty("remotes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    remotes.Add(name.EndsWith(':') ? name : name + ":");
            }
        }
        return remotes;
    }

    public async Task<RcloneStats?> GetStatsAsync(string? group = null, CancellationToken ct = default)
    {
        object? payload = string.IsNullOrWhiteSpace(group) ? null : new { group };
        using var doc = await PostAsync("/core/stats", payload, ct);
        if (doc == null)
            return null;

        return doc.RootElement.Deserialize<RcloneStats>(JsonOptions);
    }

    public async Task<RcloneBwLimitInfo?> GetBandwidthLimitAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync("/core/bwlimit", null, ct);
        return doc?.RootElement.Deserialize<RcloneBwLimitInfo>(JsonOptions);
    }

    public async Task<RcloneBwLimitInfo?> SetBandwidthLimitAsync(string rate, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(rate) ? "off" : rate.Trim();
        using var doc = await PostAsync("/core/bwlimit", new { rate = normalized }, ct);
        return doc?.RootElement.Deserialize<RcloneBwLimitInfo>(JsonOptions);
    }

    public async Task<RcloneAboutInfo?> GetAboutAsync(string remoteFs, CancellationToken ct = default)
    {
        using var doc = await PostAsync("/operations/about", new { fs = remoteFs }, ct);
        return doc?.RootElement.Deserialize<RcloneAboutInfo>(JsonOptions);
    }

    public async Task<int> StartAsyncOperationAsync(string endpoint, string srcFs, string dstFs, bool dryRun, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["srcFs"] = srcFs,
            ["dstFs"] = dstFs,
            ["_async"] = true
        };

        if (dryRun)
        {
            payload["_config"] = new Dictionary<string, object> { ["DryRun"] = true };
        }

        using var doc = await PostAsync(endpoint, payload, ct);
        if (doc != null && doc.RootElement.TryGetProperty("jobid", out var jobProp))
            return jobProp.GetInt32();

        throw new InvalidOperationException($"Resposta RC não continha 'jobid' para {endpoint}.");
    }

    public async Task<(bool Finished, bool Success, string? Error)> GetJobStatusAsync(int jobId, CancellationToken ct = default)
    {
        using var doc = await PostAsync("/job/status", new { jobid = jobId }, ct);
        if (doc == null)
            return (false, false, null);

        var root = doc.RootElement;
        var finished = root.TryGetProperty("finished", out var finProp) && finProp.GetBoolean();
        var success = root.TryGetProperty("success", out var sucProp) && sucProp.GetBoolean();
        var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
        return (finished, success, error);
    }

    public async Task StopJobAsync(int jobId, CancellationToken ct = default)
    {
        await PostAsync("/job/stop", new { jobid = jobId }, ct);
    }

    public async Task QuitAsync(CancellationToken ct = default)
    {
        try
        {
            await PostAsync("/core/quit", null, ct);
        }
        catch
        {
            // O processo rclone pode encerrar a conexão imediatamente ao receber /core/quit.
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
