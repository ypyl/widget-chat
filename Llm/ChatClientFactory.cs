using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace WidgetChat.Llm;

public sealed record LlmSettings(
    string Provider,
    string Model,
    string BaseUrl,
    bool IsStub,
    int MaxOutputTokens,
    float? Temperature)
{
    /// <summary>
    /// Header that carries the conversation id, or null to send none. OpenCode Go asks for
    /// <c>x-opencode-session</c>; Llm:SessionHeader overrides it (set it to "none" to disable).
    /// </summary>
    public string? SessionHeader { get; init; }
}

public static class ChatClientFactory
{
    /// <summary>Identifies this app to the provider instead of looking like a bare SDK.</summary>
    public const string UserAgent = "widgetchat/1.0";

    public static LlmSettings ReadSettings(IConfiguration configuration)
    {
        var provider = FirstNonEmpty(configuration["Llm:Provider"], "stub")!;
        var model = FirstNonEmpty(
            configuration["Llm:Model"],
            Environment.GetEnvironmentVariable("LLM_MODEL"),
            "deepseek-v4.1-flash")!;
        var baseUrl = FirstNonEmpty(
            configuration["Llm:BaseUrl"],
            Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
            "https://opencode.ai/inference/go/openai/v1")!;
        var maxTokens = configuration.GetValue("Llm:MaxOutputTokens", 8000);
        var sessionHeader = ResolveSessionHeader(configuration, baseUrl);

        // Left unset by default: Kimi and some other models accept only 1 and reject anything
        // else, so sending a crafted value turns every request into a 400.
        var temperature = configuration.GetValue<float?>("Llm:Temperature");

        return new LlmSettings(provider, model, baseUrl, IsStub(provider), maxTokens, temperature)
        {
            SessionHeader = sessionHeader,
        };
    }

    private static string? ResolveSessionHeader(IConfiguration configuration, string baseUrl)
    {
        var configured = configuration["Llm:SessionHeader"]?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            return configured.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : configured;
        }

        var isOpenCode = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith("opencode.ai", StringComparison.OrdinalIgnoreCase);

        return isOpenCode ? OpenCodeSessionPolicy.DefaultHeaderName : null;
    }

    public static IChatClient Create(IConfiguration configuration, WidgetSamples samples, LlmSettings settings)
    {
        if (settings.IsStub)
        {
            return new StubChatClient(samples);
        }

        if (!settings.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unknown Llm:Provider '{settings.Provider}'. Use 'stub' or 'openai' " +
                "(where 'openai' means any OpenAI-compatible endpoint: OpenAI, OpenCode, Azure OpenAI, " +
                "OpenRouter, Ollama /v1, llama.cpp, vLLM).");
        }

        var apiKey = FirstNonEmpty(
            configuration["Llm:ApiKey"],
            configuration["OPENCODE_API_KEY"],
            Environment.GetEnvironmentVariable("OPENCODE_API_KEY"),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

        if (apiKey is null)
        {
            throw new InvalidOperationException(
                "Llm:Provider is 'openai' but no API key was found. Set it with " +
                "`dotnet user-secrets set \"Llm:ApiKey\" \"<key>\"`, or the Llm__ApiKey / " +
                "OPENCODE_API_KEY / OPENAI_API_KEY environment variable, or set Llm:Provider to " +
                "'stub' to run offline.");
        }

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            UserAgentApplicationId = UserAgent,
        };

        if (!string.IsNullOrEmpty(settings.SessionHeader))
        {
            options.AddPolicy(new OpenCodeSessionPolicy(settings.SessionHeader), PipelinePosition.PerTry);
        }

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(settings.Model).AsIChatClient();
    }

    public static bool IsStub(string provider) => provider.Equals("stub", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
