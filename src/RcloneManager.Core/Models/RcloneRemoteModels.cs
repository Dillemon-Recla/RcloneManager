using System.Text.Json.Serialization;

namespace RcloneManager.Core.Models;

public sealed class RcloneProviderOptionExample
{
    [JsonPropertyName("Value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("Help")]
    public string Help { get; set; } = string.Empty;
}

public sealed class RcloneProviderOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Help")]
    public string Help { get; set; } = string.Empty;

    [JsonPropertyName("DefaultStr")]
    public string DefaultStr { get; set; } = string.Empty;

    [JsonPropertyName("IsPassword")]
    public bool IsPassword { get; set; }

    [JsonPropertyName("Required")]
    public bool Required { get; set; }

    [JsonPropertyName("Advanced")]
    public bool Advanced { get; set; }

    [JsonPropertyName("Hide")]
    public int Hide { get; set; }

    [JsonPropertyName("Examples")]
    public List<RcloneProviderOptionExample>? Examples { get; set; }
}

public sealed class RcloneProviderInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("Prefix")]
    public string Prefix { get; set; } = string.Empty;

    [JsonPropertyName("Options")]
    public List<RcloneProviderOption>? Options { get; set; }
}

public sealed class RcloneRemoteDetail
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool HasToken { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RawParameterKeys { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteAuthMode
{
    OAuthBrowserLink,
    ManualTokenJson,
    KeyOrCredentials
}

public sealed class CreateOrUpdateRemoteRequest
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsUpdate { get; set; }
    public RemoteAuthMode AuthMode { get; set; } = RemoteAuthMode.KeyOrCredentials;
    public string? ManualTokenJson { get; set; }
    public bool TriggerOAuthBrowser { get; set; }
    public bool ObscurePasswords { get; set; } = true;
}

public sealed class RemoteTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TopLevelEntriesCount { get; set; }
    public RcloneAboutInfo? About { get; set; }
}
