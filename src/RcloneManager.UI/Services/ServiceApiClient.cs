using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RcloneManager.Core.Models;

namespace RcloneManager.UI.Services;

public sealed class RemotesResponse
{
    public List<string> Remotes { get; set; } = new();
    public string SourceUsed { get; set; } = string.Empty;
}

public sealed class LogsResponse
{
    public List<string> ServiceLogs { get; set; } = new();
    public List<string> RcloneLogLines { get; set; } = new();
    public string RcloneLogPath { get; set; } = string.Empty;
}

public sealed class FilterAnalyzeResponse
{
    public FilterAnalysisResult Analysis { get; set; } = new();
    public string Content { get; set; } = string.Empty;
}

public sealed class ToggleRcdResponse
{
    public bool Running { get; set; }
    public int Port { get; set; }
}

public sealed class StartupToggleResponse
{
    public bool StartupWithWindowsEnabled { get; set; }
}

public sealed class ServiceApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public ServiceApiClient(int port = 5580)
    {
        BaseUrl = $"http://127.0.0.1:{port}";
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public string BaseUrl { get; }

    public async Task<ServiceStatusDto?> GetStatusAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<ServiceStatusDto>("/api/status", JsonOptions, ct);
    }

    public async Task<SyncJob?> SaveJobAsync(SyncJob job, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs", job, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SyncJob>(JsonOptions, ct);
    }

    public async Task DeleteJobAsync(string jobId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/jobs/{Uri.EscapeDataString(jobId)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RunJobAsync(string jobId, bool dryRun = false, ExecutionStrategy? overrideStrategy = null, CancellationToken ct = default)
    {
        var req = new RunJobRequest { DryRun = dryRun, OverrideStrategy = overrideStrategy };
        using var resp = await _http.PostAsJsonAsync($"/api/jobs/{Uri.EscapeDataString(jobId)}/run?dryRun={dryRun}", req, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task StopJobAsync(string jobId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/jobs/{Uri.EscapeDataString(jobId)}/stop", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task SetBandwidthLimitAsync(string rate, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/bwlimit", new BandwidthUpdateRequest { Rate = rate }, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ToggleRcdResponse?> ToggleRcDaemonAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/rcd/toggle", null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ToggleRcdResponse>(JsonOptions, ct);
    }

    public async Task<RemotesResponse?> GetRemotesAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<RemotesResponse>("/api/remotes", JsonOptions, ct);
    }

    public async Task<RcloneAboutInfo?> GetRemoteAboutAsync(string remote, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/about?remote={Uri.EscapeDataString(remote)}", ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await resp.Content.ReadFromJsonAsync<RcloneAboutInfo>(JsonOptions, ct);
    }

    public async Task<LogsResponse?> GetLogsAsync(int maxLines = 250, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<LogsResponse>($"/api/logs?maxLines={maxLines}", JsonOptions, ct);
    }

    public async Task<FilterAnalyzeResponse?> AnalyzeFilterAsync(string? path, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(path)
            ? "/api/filters/analyze"
            : $"/api/filters/analyze?path={Uri.EscapeDataString(path)}";
        return await _http.GetFromJsonAsync<FilterAnalyzeResponse>(url, JsonOptions, ct);
    }

    public async Task<FilterAnalyzeResponse?> SaveFilterAsync(string filePath, string content, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/filters/save", new FilterFilePayload { FilePath = filePath, Content = content }, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<FilterAnalyzeResponse>(JsonOptions, ct);
    }

    public async Task<FilterAnalyzeResponse?> OptimizeFilterAsync(string filePath, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/filters/optimize", new FilterFilePayload { FilePath = filePath }, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<FilterAnalyzeResponse>(JsonOptions, ct);
    }

    public async Task<StartupToggleResponse?> SetStartupAsync(bool enabled, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/service/startup?enabled={enabled}", null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<StartupToggleResponse>(JsonOptions, ct);
    }

    public async Task ShutdownServiceAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/service/shutdown", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
