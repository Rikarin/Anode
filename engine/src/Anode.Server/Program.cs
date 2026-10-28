using Anode.Contracts;
using Anode.EngineHost;
using Anode.Server;
using Anode.Server.Api;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Anode.Server.Realtime;
using System.Text.Json.Serialization;

// Anode.Server: the REST and WebSocket API. It owns the project and drives anode-host over a Unix domain socket
// (docs/adr/0001-process-model.md, docs/adr/0004-state-ownership.md). Listens on localhost unless given other URLs,
// which then need an access token (Api/Access.cs).

var startup = System.Diagnostics.Stopwatch.StartNew();
// Rooted at the program's own folder, with no configuration reload: Anode has no appsettings.json; its settings come
// from the environment and the command line, and watching the working directory under systemd would watch the disk.
var builder = WebApplication.CreateSlimBuilder(
    new WebApplicationOptions {
        Args = [..args, "--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory
    }
);
var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5190";
var token = builder.Configuration["Anode:Token"] is { Length: > 0 } configured ? configured : null;
Access.RequireTokenBeyondLoopback(urls, token);
builder.WebHost.UseUrls(urls);
// The hosting log's request lines carry the query string, and the WebSocket presents the access token there.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Anode"));
builder.Services.ConfigureHttpJsonOptions(static o => {
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
        // Numbers are numbers: the web default also accepts "1", which turns every number in the OpenAPI document
        // (and so in generated clients) into number | string.
        o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    }
);
builder.Services.AddProblemDetails();
builder.Services.AddAnodeCors();
builder.Services.AddOpenApi(static o => o.MarkNonNullablePropertiesRequired());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<RecentProjects>();
builder.Services.AddSingleton<PeakJobs>();
builder.Services.AddSingleton<EngineDevice>();
builder.Services.AddSingleton<EngineCatalog>();
builder.Services.AddSingleton<TransportClock>();
builder.Services.AddSingleton<PatchLibrary>();
builder.Services.AddSingleton<ProjectSession>();
builder.Services.AddSingleton<Jobs>();
builder.Services.AddSingleton<AudioImport>();
builder.Services.AddSingleton<UserSettings>();
builder.Services.AddSingleton<EngineConnection>();
builder.Services.AddHostedService(static s => s.GetRequiredService<EngineConnection>());

var app = builder.Build();
if (bool.TryParse(builder.Configuration["Anode:WatchStdin"], out var watchStdin) && watchStdin) {
    // Started by the desktop app: stop when it is gone, however it ended (Anode.EngineHost.StdinWatch).
    StdinWatch.Start(Console.OpenStandardInput(), app.Lifetime.StopApplication);
}

app.UseExceptionHandler(
    new ExceptionHandlerOptions {
        StatusCodeSelector = static e => e is BadHttpRequestException bad
            ? bad.StatusCode
            : StatusCodes.Status500InternalServerError
    }
);
if (builder.Configuration["Anode:EditorDirectory"] is { Length: > 0 } editor) {
    app.UseAnodeEditor(editor);
}

app.UseAnodeAccess(token);
app.UseWebSockets();
app.MapOpenApi();
app.MapAnodeApi();

var hub = app.Services.GetRequiredService<EventHub>();
app.Services.GetRequiredService<EngineConnection>().Changed += status => hub.Publish(new EngineEvent(status));
StartupLog.Phase(app.Logger, "services built", startup.ElapsedMilliseconds);

app.Run();

/// <summary>Entry point, public for integration tests.</summary>
public partial class Program;
