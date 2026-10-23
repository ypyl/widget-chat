using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using WidgetChat.Agents;
using WidgetChat.Chat;
using WidgetChat.Llm;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<WidgetSamples>();
builder.Services.AddSingleton(ChatClientFactory.ReadSettings(builder.Configuration));
builder.Services.AddSingleton<IChatClient>(services => ChatClientFactory.Create(
    builder.Configuration,
    services.GetRequiredService<WidgetSamples>(),
    services.GetRequiredService<LlmSettings>()));
builder.Services.AddSingleton(services => WidgetAgent.Create(
    services.GetRequiredService<IChatClient>(),
    services.GetRequiredService<LlmSettings>()));
builder.Services.AddSingleton<ConversationSessions>();

var app = builder.Build();

var settings = app.Services.GetRequiredService<LlmSettings>();
app.Logger.LogInformation(
    "WidgetChat starting. Provider={Provider} Model={Model} Mode={Mode}",
    settings.Provider,
    settings.Model,
    settings.IsStub ? "offline stub (no model calls)" : "live");

if (settings.IsStub)
{
    app.Logger.LogInformation("Set Llm:Provider to 'openai' and provide an API key to use a real model.");
}

// Build the agent once, here. A missing key or an unknown provider then stops the app with its
// message on the console instead of surfacing as an empty HTTP 500 on the first chat message.
app.Services.GetRequiredService<AIAgent>();

app.UseDefaultFiles();

// The chat page must not be framed. A hostile page that embeds it can reach the widget frames
// through the frame tree and post messages into them. X-Frame-Options is not inherited by srcdoc
// frames, so it does not touch the widgets themselves.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next(context);
});

// Revalidate static files on every load. Without this a browser can keep an old app.js from its
// memory cache, which makes editing the widget host look like it has no effect.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache",
});

ChatEndpoints.Map(app);

app.Run();
