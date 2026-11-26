using RcloneManager.Core.Models;

namespace RcloneManager.Core.Onboarding;

public sealed class WizardRemoteAuthInput
{
    public string RemoteName { get; init; } = string.Empty;
    public string Provider { get; init; } = "drive";
    public RemoteAuthMode AuthMode { get; init; } = RemoteAuthMode.OAuthBrowserLink;
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ManualTokenJson { get; init; }
    public string? KeyFilePath { get; init; }
    public string? HostOrEndpoint { get; init; }
    public string? UserOrAccessKey { get; init; }
    public string? PasswordOrSecretKey { get; init; }
}

public static class RemoteAuthRequestBuilder
{
    public static CreateOrUpdateRemoteRequest Build(
        WizardRemoteAuthInput input,
        ExportedRemoteBlueprint? blueprint,
        IReadOnlySet<string> existingRemoteNames)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(existingRemoteNames);

        var normalizedName = input.RemoteName.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("Informe o nome do remote.", nameof(input));

        var provider = string.IsNullOrWhiteSpace(input.Provider) ? "drive" : input.Provider.Trim();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (blueprint?.NonSensitiveParameters != null)
        {
            foreach (var (k, v) in blueprint.NonSensitiveParameters)
                parameters[k] = v;
        }

        if (!string.IsNullOrWhiteSpace(input.ClientId))
            parameters["client_id"] = input.ClientId.Trim();

        if (!string.IsNullOrWhiteSpace(input.ClientSecret))
            parameters["client_secret"] = input.ClientSecret.Trim();

        if (input.AuthMode == RemoteAuthMode.KeyOrCredentials)
            PopulateCredentialParameters(parameters, provider, input);

        return new CreateOrUpdateRemoteRequest
        {
            Name = normalizedName,
            Type = provider,
            Parameters = parameters,
            IsUpdate = existingRemoteNames.Contains(normalizedName),
            AuthMode = input.AuthMode,
            ManualTokenJson = input.ManualTokenJson?.Trim() ?? string.Empty,
            TriggerOAuthBrowser = input.AuthMode == RemoteAuthMode.OAuthBrowserLink,
            ObscurePasswords = true
        };
    }

    private static void PopulateCredentialParameters(
        IDictionary<string, string> parameters,
        string provider,
        WizardRemoteAuthInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.KeyFilePath))
            parameters[ResolveKeyFileOptionName(provider)] = input.KeyFilePath.Trim();

        if (!string.IsNullOrWhiteSpace(input.HostOrEndpoint))
            parameters[ResolveHostOptionName(provider)] = input.HostOrEndpoint.Trim();

        if (!string.IsNullOrWhiteSpace(input.UserOrAccessKey))
            parameters[ResolveUserOptionName(provider)] = input.UserOrAccessKey.Trim();

        if (!string.IsNullOrEmpty(input.PasswordOrSecretKey))
            parameters[ResolveSecretOptionName(provider)] = input.PasswordOrSecretKey;
    }

    private static string ResolveKeyFileOptionName(string provider) =>
        provider.Equals("drive", StringComparison.OrdinalIgnoreCase) ? "service_account_file" : "key_file";

    private static string ResolveHostOptionName(string provider)
    {
        if (provider.Equals("s3", StringComparison.OrdinalIgnoreCase))
            return "endpoint";
        if (provider.Equals("webdav", StringComparison.OrdinalIgnoreCase))
            return "url";
        return "host";
    }

    private static string ResolveUserOptionName(string provider) =>
        provider.Equals("s3", StringComparison.OrdinalIgnoreCase) ? "access_key_id" : "user";

    private static string ResolveSecretOptionName(string provider) =>
        provider.Equals("s3", StringComparison.OrdinalIgnoreCase) ? "secret_access_key" : "pass";
}
