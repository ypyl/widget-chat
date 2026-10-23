using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using WidgetChat.Chat;
using WidgetChat.Llm;

namespace WidgetChat.Agents;

public static class WidgetAgent
{
    public const string Name = "WidgetAssistant";

    /// <summary>
    /// Builds the Agent Framework agent on top of whatever IChatClient the provider layer supplied.
    /// The agent owns instructions; the sandbox rail owns rendering.
    ///
    /// There is deliberately no reference-widget tool. It was one, and with a reasoning model it
    /// cost a second round trip: the model called it, then reasoned again from scratch with several
    /// kilobytes of widget source in context, and sometimes returned no text at all before the
    /// token cap. Widget quality did not depend on it, so the prompt carries the contract and
    /// Samples/ carries worked examples for humans and for the offline stub.
    /// </summary>
    public static AIAgent Create(IChatClient chatClient, LlmSettings settings)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = WidgetPrompts.System,
        };

        if (settings.Temperature is { } temperature)
        {
            chatOptions.Temperature = temperature;
        }

        // Zero means "let the provider decide". Reasoning models spend a large share of the budget
        // thinking before the first character of prose, so a cap tuned for prose can starve a reply.
        if (settings.MaxOutputTokens > 0)
        {
            chatOptions.MaxOutputTokens = settings.MaxOutputTokens;
        }

        var options = new ChatClientAgentOptions
        {
            Name = Name,
            Description = "Answers questions and streams self-contained visual widgets.",
            // Instructions live on ChatOptions in Agent Framework 1.26; the agent merges them
            // with anything supplied per run.
            ChatOptions = chatOptions,
        };

        return new ChatClientAgent(chatClient, options);
    }
}
