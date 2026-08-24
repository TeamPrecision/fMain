using fMain.Hubs;
using fMain.Services;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Kestrel: listen on all interfaces ─────────────────────────────────────────
builder.WebHost.UseUrls("http://0.0.0.0:49600");

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSignalR(opt =>
{
    opt.EnableDetailedErrors = true;
    opt.MaximumReceiveMessageSize = 2 * 1024 * 1024;
    opt.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    opt.KeepAliveInterval = TimeSpan.FromSeconds(15);
})
.AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Application services (singletons so state is shared across all requests/hubs)
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<AccessControlService>();
builder.Services.AddSingleton<ModuleLoaderService>();
builder.Services.AddSingleton<HeadStateService>();
builder.Services.AddSingleton<TestPlanService>();
builder.Services.AddSingleton<TestRunnerService>();
builder.Services.AddSingleton<fMain.Services.BarcodeService>();
builder.Services.AddSingleton<DatalogService>();
builder.Services.AddSingleton<PrismService>();
builder.Services.AddSingleton<WorkOrderService>();

// Forward headers from proxy (192.168.10.6:8080 → this server)
builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders = ForwardedHeaders.All;
    // Accept forwarded headers from any network (LAN use)
    opt.KnownNetworks.Clear();
    opt.KnownProxies.Clear();
});

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Startup tasks ─────────────────────────────────────────────────────────────
// Eagerly start BarcodeService (installs keyboard hook) and configure module statics
var barcodeSvc  = app.Services.GetRequiredService<fMain.Services.BarcodeService>();
var datalogSvc  = app.Services.GetRequiredService<DatalogService>();
await datalogSvc.EnsureSchemaAsync();
// PrismService is instantiated by DI above (singleton ctor calls TryLoad)
_ = app.Services.GetRequiredService<PrismService>();

var hwCfg = app.Services.GetRequiredService<ConfigService>().Config.Hardware;

// DevManViewModule.DevManViewPath is a static property in a Roslyn-compiled assembly,
// so we set the default here; the compiled module reads it via reflection at invoke time.
// Since DevManViewModule is compiled at scan time (below), we patch the static after scan.

var moduleLoader = app.Services.GetRequiredService<ModuleLoaderService>();
await moduleLoader.ScanAndLoadAsync();

// Patch DevManViewModule static path after modules are compiled
var devManPath = !string.IsNullOrEmpty(hwCfg.DevManViewPath)
    ? hwCfg.DevManViewPath
    : Path.Combine(AppContext.BaseDirectory, "devmanview.exe");
moduleLoader.SetModuleStatic("DevManViewModule", "DevManViewPath", devManPath);

// ── Middleware pipeline ───────────────────────────────────────────────────────
app.UseForwardedHeaders();      // Must be first so real IPs propagate
app.UseStaticFiles();
app.UseRouting();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapHub<TestHub>("/hub/test");
app.MapControllers();
app.MapFallbackToFile("index.html");   // SPA-style: unknown routes → index.html

app.Logger.LogInformation("fMain started on http://0.0.0.0:49600");
app.Run();
