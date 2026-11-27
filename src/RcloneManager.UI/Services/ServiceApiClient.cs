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

public sealed class RemotesDetailsResponse
{
    public List<RcloneRemoteDetail> Remotes { get; set; } = new();
    public string SourceUsed { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
}

public sealed class ProvidersResponse
{
    public List<RcloneProviderInfo> Providers { get; set; } = new();
    public string SourceUsed { get; set; } = string.Empty;
}

public sealed class RemoteActionResponse
{
    public bool Saved { get; set; }
    public bool Reconnected { get; set; }
    public bool Deleted { get; set; }
    public string Message { get; set; } = string.Empty;
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
    private readonly HttpClient _longHttp;

    public ServiceApiClient(int port = 5580)
    {
        BaseUrl = $"http://127.0.0.1:{port}";
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(8)
        };
        _longHttp = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromMinutes(4)
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

    public async Task<ExcludedRetentionStatusDto?> GetExcludedRetentionAsync(string jobId, bool scan = false, CancellationToken ct = default)
    {
        var client = scan ? _longHttp : _http;
        using var resp = await client.GetAsync($"/api/jobs/{Uri.EscapeDataString(jobId)}/excluded-retention?scan={scan.ToString().ToLowerInvariant()}", ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ExcludedRetentionStatusDto>(JsonOptions, ct);
    }

    public async Task<PurgeExcludedResultDto?> PurgeExcludedNowAsync(string jobId, CancellationToken ct = default)
    {
        using var resp = await _longHttp.PostAsync($"/api/jobs/{Uri.EscapeDataString(jobId)}/purge-excluded", null, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<PurgeExcludedResultDto>(JsonOptions, ct);
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

    public async Task SetServiceModeAsync(BackgroundServiceMode mode, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/api/service/mode?mode={mode}", null, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
    }

    public async Task CompleteFirstRunAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/service/complete-first-run", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ReloadServiceConfigAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/service/reload", null, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
    }

    public async Task ResetConfigAsync(ResetConfigRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/service/reset", req, JsonOptions, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
    }

    public async Task StopRcdAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/rclone/stop-rcd", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateReleaseConfigAsync(UpdateReleaseConfigRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/rclone/release-config", req, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ShutdownServiceAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/service/shutdown", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<RemotesDetailsResponse?> GetRemotesDetailsAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<RemotesDetailsResponse>("/api/remotes/details", JsonOptions, ct);
    }

    public async Task<ProvidersResponse?> GetProvidersAsync(CancellationToken ct = default)
    {
        return await _longHttp.GetFromJsonAsync<ProvidersResponse>("/api/remotes/providers", JsonOptions, ct);
    }

    public async Task<RemoteActionResponse?> SaveRemoteAsync(CreateOrUpdateRemoteRequest req, CancellationToken ct = default)
    {
        using var resp = await _longHttp.PostAsJsonAsync("/api/remotes/save", req, JsonOptions, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RemoteActionResponse>(JsonOptions, ct);
    }

    public async Task<RemoteActionResponse?> ReconnectRemoteAsync(string name, CancellationToken ct = default)
    {
        using var resp = await _longHttp.PostAsync($"/api/remotes/{Uri.EscapeDataString(name)}/reconnect", null, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RemoteActionResponse>(JsonOptions, ct);
    }

    public async Task<RemoteTestResult?> TestRemoteAsync(string name, CancellationToken ct = default)
    {
        using var resp = await _longHttp.PostAsync($"/api/remotes/{Uri.EscapeDataString(name)}/test", null, ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RemoteTestResult>(JsonOptions, ct);
    }

    public async Task<RemoteActionResponse?> DeleteRemoteAsync(string name, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/remotes/{Uri.EscapeDataString(name)}", ct);
        await EnsureSuccessWithDetailAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RemoteActionResponse>(JsonOptions, ct);
    }

    private static async Task EnsureSuccessWithDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                    throw new InvalidOperationException(errProp.GetString());
                if (doc.RootElement.TryGetProperty("detail", out var detProp) && !string.IsNullOrWhiteSpace(detProp.GetString()))
                    throw new InvalidOperationException(detProp.GetString());
            }
            catch (JsonException)
            {
            }
            throw new HttpRequestException($"Erro {(int)resp.StatusCode}: {body}");
        }

        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _http.Dispose();
        _longHttp.Dispose();
    }
}
