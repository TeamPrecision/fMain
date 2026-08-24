using System.Text.Json;
using fMain.Models;

namespace fMain.Services;

public class ConfigService
{
    private readonly string _configFile;
    private FMainConfig _config;
    private readonly ILogger<ConfigService> _logger;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ConfigService(IConfiguration configuration, ILogger<ConfigService> logger)
    {
        _logger = logger;
        _configFile = Path.Combine(AppContext.BaseDirectory, "fmain_config.json");
        _config = Load(configuration);
    }

    public FMainConfig Config => _config;

    private FMainConfig Load(IConfiguration configuration)
    {
        if (File.Exists(_configFile))
        {
            try
            {
                var text = File.ReadAllText(_configFile);
                var saved = JsonSerializer.Deserialize<FMainConfig>(text);
                if (saved != null)
                {
                    _logger.LogInformation("Config loaded from {Path}", _configFile);
                    return saved;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse saved config, using appsettings");
            }
        }

        var cfg = new FMainConfig();
        configuration.GetSection("fMain").Bind(cfg);
        return cfg;
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_config, _json);
        await File.WriteAllTextAsync(_configFile, json);
        _logger.LogInformation("Config saved");
    }

    public void Update(FMainConfig newConfig)
    {
        _config = newConfig;
        _ = SaveAsync();
    }

    public bool IsSpecialIp(string ip) =>
        _config.SpecialIPs.Any(s => s.Equals(ip, StringComparison.OrdinalIgnoreCase));

    public bool IsAdminIp(string ip) =>
        _config.AdminIPs.Any(a => a.Equals(ip, StringComparison.OrdinalIgnoreCase))
        || ip == "127.0.0.1" || ip == "::1" || ip == "localhost";
}
