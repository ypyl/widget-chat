using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using WidgetChat.Agents;
using WidgetChat.Llm;

namespace WidgetChat.Chat;

public sealed record ChatRequest(string? SessionId, string? Message);

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/chat", ChatAsync);
        app.MapGet("/api/info", Info);
    }

    private const int MaxAttempts = 2;

    private static IResult Info(LlmSettings settings) => Results.Json(new
    {
        provider = settings.Provider,
        model = settings.IsStub ? "stub-widget" : settings.Model,
        mode = settings.IsStub ? "offline stub" : "live",
    });

    /// <summary>
    /// Streams the reply as newline-delimited JSON: one small object per channel chunk. NDJSON
    /// rather than SSE because the client needs no event-name plumbing and the parser is trivial.
    /// </summary>
    internal static async Task ChatAsync(
        HttpContext http,
        AIAgent agent,
        ConversationSessions sessions,
        LlmSettings settings,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("WidgetChat.Chat");
        var cancellationToken = http.RequestAborted;

        ChatRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ChatRequest>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var message = request?.Message?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("A non-empty message is required.", cancellationToken);
            return;
        }

        if (message.Length > 8000)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await http.Response.WriteAsync("Message is longer than 8000 characters.", cancellationToken);
            return;
        }

        var conversationId = NormalizeConversationId(request!.SessionId);

        using var lease = await sessions.TryAcquireAsync(conversationId, cancellationToken);
        if (lease is null)
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsync("Another message is still streaming on this conversation.", cancellationToken);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/x-ndjson; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Without this, Kestrel or a proxy may hold the response until the run finishes, which
        // would defeat the entire point of streaming the widget.
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var writer = new NdjsonWriter(http.Response);

        // OpenCode Go asks for a stable per-conversation id so it can route and cache prompts.
        // Harmless for other providers: the policy is only registered on OpenCode endpoints.
        using var sessionScope = OpenCodeSessionPolicy.Use(conversationId);

        var answer = new StringBuilder();
        var parser = new WidgetStreamParser();
        var contentTypes = new HashSet<string>(StringComparer.Ordinal);
        var updateCount = 0;
        long outputTokens = 0;
        long inputTokens = 0;

        try
        {
            // A reasoning model occasionally spends its whole budget thinking, or calls the
            // reference tool and then answers with nothing. Both are transient and cost one extra
            // round trip, so retry once before surfacing an error. Only an empty reply is retried:
            // once any text has reached the browser, a retry would duplicate it.
            for (var attempt = 1; ; attempt++)
            {
                answer.Clear();
                parser = new WidgetStreamParser();
                contentTypes.Clear();
                updateCount = 0;
                outputTokens = 0;
                inputTokens = 0;

                // Retry on the same session. A clean session would start with no history, so one
                // empty reply would erase the earlier turns. The retry may therefore send this
                // message twice, which is the cheaper mistake.
                await foreach (var update in agent.RunStreamingAsync(message, lease.Session, cancellationToken: cancellationToken))
                {
                    updateCount++;
                    foreach (var content in update.Contents ?? [])
                    {
                        contentTypes.Add(content.GetType().Name);
                        if (content is UsageContent usage)
                        {
                            outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                            inputTokens = usage.Details.InputTokenCount ?? inputTokens;
                        }
                    }

                    var delta = update.Text;
                    if (string.IsNullOrEmpty(delta))
                    {
                        continue;
                    }

                    answer.Append(delta);

                    foreach (var chunk in parser.Push(delta))
                    {
                        await writer.WriteChunkAsync(chunk, cancellationToken);
                    }
                }

                if (answer.Length > 0 || attempt >= MaxAttempts)
                {
                    break;
                }

                log.LogWarning(
                    "Empty reply for {ConversationId} (attempt {Attempt} of {MaxAttempts}): {UpdateCount} updates, {OutputTokens} output tokens, content types [{ContentTypes}]. Retrying.",
                    conversationId,
                    attempt,
                    MaxAttempts,
                    updateCount,
                    outputTokens,
                    string.Join(", ", contentTypes));
            }

            foreach (var chunk in parser.Complete())
            {
                await writer.WriteChunkAsync(chunk, cancellationToken);
            }

            if (!parser.SawEndMarker && answer.ToString().Contains(WidgetStreamParser.CssMarker, StringComparison.Ordinal))
            {
                log.LogWarning(
                    "Reply for {ConversationId} ended without {EndMarker} after {Length} chars; the widget is incomplete",
                    conversationId,
                    WidgetStreamParser.EndMarker,
                    answer.Length);
                await writer.WriteAsync(new { type = "truncated" }, cancellationToken);
            }

            if (answer.Length == 0)
            {
                // Distinguish "the budget went to reasoning" from "the provider sent nothing",
                // because the fixes are different.
                var budgetSpent = settings.MaxOutputTokens > 0 && outputTokens >= settings.MaxOutputTokens * 0.95;

                log.LogWarning(
                    "Empty reply for {ConversationId} after {Attempt} attempts: {UpdateCount} updates, {OutputTokens} of {MaxOutputTokens} output tokens, content types [{ContentTypes}] (budget spent: {BudgetSpent})",
                    conversationId,
                    MaxAttempts,
                    updateCount,
                    outputTokens,
                    settings.MaxOutputTokens,
                    string.Join(", ", contentTypes),
                    budgetSpent);

                var errorMessage = budgetSpent
                    ? $"The model used all {settings.MaxOutputTokens} output tokens before writing anything, which reasoning models do. Set Llm:MaxOutputTokens to 0 to remove the cap, or use a non-reasoning model."
                    : "The model returned no text twice in a row. Retry, or check the provider status.";

                await writer.WriteAsync(new { type = "error", message = errorMessage }, cancellationToken);
            }

            await writer.WriteAsync(new { type = "done" }, cancellationToken);
            log.LogInformation(
                "Completed conversation {ConversationId}: {Length} chars, widget={HasWidget}, tokens in/out={InputTokens}/{OutputTokens}",
                conversationId,
                answer.Length,
                answer.ToString().Contains(WidgetStreamParser.CssMarker, StringComparison.Ordinal),
                inputTokens,
                outputTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.LogInformation("Client disconnected from conversation {ConversationId}", conversationId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Agent run failed for conversation {ConversationId}", conversationId);
            await writer.TryWriteAsync(new { type = "error", message = Describe(ex) }, cancellationToken);
        }
    }

    /// <summary>Never leaks a stack trace or a connection string into the browser.</summary>
    private static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ClientResultException providerFailure)
            {
                return DescribeProviderStatus(providerFailure.Status, providerFailure.Message);
            }

            if (current is UriFormatException)
            {
                return "Llm:BaseUrl is not a valid absolute URL.";
            }

            if (current is HttpRequestException)
            {
                return "The model endpoint could not be reached. Check Llm:BaseUrl and your connection.";
            }
        }

        var message = exception.Message;
        return message.Length > 400 ? message[..400] : message;
    }

    /// <summary>
    /// Provider failures arrive as an HTTP status with a JSON body. The statuses below are the ones
    /// a first run actually hits, and each has a different fix.
    /// </summary>
    private static string DescribeProviderStatus(int status, string raw) => status switch
    {
        401 => "The model endpoint rejected the API key (401). Check Llm:ApiKey.",
        402 => "The provider account has no funds (402). Add credits, or subscribe to OpenCode Go, for the account behind this API key.",
        403 => "This API key is not allowed to use the configured model (403). Check Llm:Model, and the workspace privacy and region settings.",
        404 => "The endpoint or model was not found (404). Check Llm:BaseUrl and Llm:Model.",
        429 => "The provider is rate limiting this key (429). Retry in a moment.",
        _ => raw.Length > 400 ? raw[..400] : raw,
    };

    private static string NormalizeConversationId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "default";
        }

        // The id is client input and travels as an HTTP header on OpenCode. Drop anything a header
        // is not safe with, so a crafted id cannot make every later request fail.
        var safe = raw.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        if (safe.Length == 0)
        {
            return "default";
        }

        return safe.Length > 64 ? new string(safe[..64]) : new string(safe);
    }
}

/// <summary>Writes one JSON object per line and flushes, so each channel chunk leaves immediately.</summary>
internal sealed class NdjsonWriter
{
    private readonly HttpResponse _response;

    public NdjsonWriter(HttpResponse response) => _response = response;

    public Task WriteChunkAsync(WidgetChunk chunk, CancellationToken cancellationToken) =>
        chunk.Channel == WidgetChannel.Height
            ? WriteAsync(new { type = "height", value = ParseHeight(chunk.Text) }, cancellationToken)
            : WriteAsync(new { type = chunk.Channel.ToString().ToLowerInvariant(), delta = chunk.Text }, cancellationToken);

    public async Task WriteAsync(object payload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web) + "\n";
        await _response.WriteAsync(line, cancellationToken);
        await _response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Best effort: the client is usually gone when this is used.</summary>
    public async Task TryWriteAsync(object payload, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(payload, cancellationToken);
        }
        catch
        {
            // ignored: the response stream is no longer writable
        }
    }

    private static int ParseHeight(string raw) =>
        int.TryParse(raw, out var value) ? value : 320;
}
