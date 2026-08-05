namespace PoolSense.Api.Configuration;

public class AiSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string ImageApiVersion { get; set; } = string.Empty;
    public AiHttpSettings Http { get; set; } = new();
    public AiModelSettings Models { get; set; } = new();
}

public class AiHttpSettings
{
    public bool AllowCertificateRevocationUnknown { get; set; }
}

public class AiModelSettings
{
    public string Chat { get; set; } = string.Empty;
    public string Embeddings { get; set; } = string.Empty;
}

public class NyraSettings
{
    public string ActiveProfile { get; set; } = string.Empty;
    public string AuthMode { get; set; } = NyraAuthModes.ClientCredentials;
    public string ApiKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid";
    public string GatewayEndpoint { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string ResponsesUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string EmbeddingEndpoint { get; set; } = string.Empty;
    public string EmbeddingGenerateUrl { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string EmbeddingSubscriptionType { get; set; } = string.Empty;
    public string EmbeddingApiVersion { get; set; } = string.Empty;
    public Dictionary<string, NyraProfileSettings> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class NyraProfileSettings
{
    public string AuthMode { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string GatewayEndpoint { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string ResponsesUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string EmbeddingEndpoint { get; set; } = string.Empty;
    public string EmbeddingGenerateUrl { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string EmbeddingSubscriptionType { get; set; } = string.Empty;
    public string EmbeddingApiVersion { get; set; } = string.Empty;
}

public static class NyraAuthModes
{
    public const string ClientCredentials = "ClientCredentials";
    public const string ApiKeyAndClientCredentials = "ApiKeyAndClientCredentials";
}

public static class NyraSettingsResolver
{
    public static void ApplyActiveProfile(NyraSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (TryGetActiveProfile(settings, out var profile))
        {
            settings.AuthMode = FirstNonEmpty(profile.AuthMode, settings.AuthMode, NyraAuthModes.ClientCredentials);
            settings.ApiKey = FirstNonEmpty(profile.ApiKey, settings.ApiKey);
            settings.ClientId = FirstNonEmpty(profile.ClientId, settings.ClientId);
            settings.ClientSecret = FirstNonEmpty(profile.ClientSecret, settings.ClientSecret);
            settings.Issuer = FirstNonEmpty(profile.Issuer, settings.Issuer);
            settings.Audience = FirstNonEmpty(profile.Audience, settings.Audience);
            settings.Scope = FirstNonEmpty(profile.Scope, settings.Scope, "openid");
            settings.GatewayEndpoint = FirstNonEmpty(profile.GatewayEndpoint, settings.GatewayEndpoint);
            settings.TokenUrl = FirstNonEmpty(profile.TokenUrl, settings.TokenUrl);
            settings.ResponsesUrl = FirstNonEmpty(profile.ResponsesUrl, settings.ResponsesUrl);
            settings.Model = FirstNonEmpty(profile.Model, settings.Model);
            settings.EmbeddingEndpoint = FirstNonEmpty(profile.EmbeddingEndpoint, settings.EmbeddingEndpoint);
            settings.EmbeddingGenerateUrl = FirstNonEmpty(profile.EmbeddingGenerateUrl, settings.EmbeddingGenerateUrl);
            settings.EmbeddingModel = FirstNonEmpty(profile.EmbeddingModel, settings.EmbeddingModel);
            settings.EmbeddingSubscriptionType = FirstNonEmpty(profile.EmbeddingSubscriptionType, settings.EmbeddingSubscriptionType);
            settings.EmbeddingApiVersion = FirstNonEmpty(profile.EmbeddingApiVersion, settings.EmbeddingApiVersion);
        }

        settings.AuthMode = FirstNonEmpty(settings.AuthMode, NyraAuthModes.ClientCredentials);
        settings.Scope = FirstNonEmpty(settings.Scope, "openid");
        settings.EmbeddingSubscriptionType = FirstNonEmpty(settings.EmbeddingSubscriptionType, "azure");
    }

    public static bool HasRequiredSettings(NyraSettings settings)
    {
        ApplyActiveProfile(settings);

        return HasRequiredAuth(settings)
            && !string.IsNullOrWhiteSpace(settings.GatewayEndpoint)
            && !string.IsNullOrWhiteSpace(settings.Model)
            && !string.IsNullOrWhiteSpace(settings.EmbeddingModel)
            && (!string.IsNullOrWhiteSpace(settings.EmbeddingGenerateUrl)
                || !string.IsNullOrWhiteSpace(settings.EmbeddingEndpoint))
            && !string.IsNullOrWhiteSpace(settings.EmbeddingApiVersion);
    }

    public static void Validate(NyraSettings settings)
    {
        if (!HasRequiredSettings(settings))
        {
            throw new InvalidOperationException("NYRA configuration is incomplete. Configure Nyra:ActiveProfile with client credentials, issuer or token URL, audience, gateway endpoint, model, embedding model, embedding API version, and embedding generate URL or endpoint.");
        }
    }

    public static string? GetApiKeyHeaderValue(NyraSettings settings)
    {
        return UsesApiKeyHeader(settings) ? settings.ApiKey : null;
    }

    private static bool HasRequiredAuth(NyraSettings settings)
    {
        var hasClientCredentials = !string.IsNullOrWhiteSpace(settings.ClientId)
            && !string.IsNullOrWhiteSpace(settings.ClientSecret)
            && !string.IsNullOrWhiteSpace(settings.Audience)
            && (!string.IsNullOrWhiteSpace(settings.TokenUrl)
                || !string.IsNullOrWhiteSpace(settings.Issuer));

        if (!hasClientCredentials)
        {
            return false;
        }

        return !UsesApiKeyHeader(settings) || !string.IsNullOrWhiteSpace(settings.ApiKey);
    }

    private static bool UsesApiKeyHeader(NyraSettings settings)
    {
        return settings.AuthMode.Equals(NyraAuthModes.ApiKeyAndClientCredentials, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetActiveProfile(NyraSettings settings, out NyraProfileSettings profile)
    {
        profile = new NyraProfileSettings();
        if (string.IsNullOrWhiteSpace(settings.ActiveProfile) || settings.Profiles.Count == 0)
        {
            return false;
        }

        foreach (var candidate in settings.Profiles)
        {
            if (candidate.Key.Equals(settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.ClientId.Equals(settings.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}