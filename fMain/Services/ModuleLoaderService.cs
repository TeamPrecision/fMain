using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using fMain.Models;

namespace fMain.Services;

// ── Attributes that module authors use ────────────────────────────────────────

[AttributeUsage(AttributeTargets.Class)]
public class FMainModuleAttribute(string name, string description = "", string category = "General", string version = "1.0.0") : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Category { get; } = category;
    public string Version { get; } = version;
}

[AttributeUsage(AttributeTargets.Method)]
public class FMainFunctionAttribute(string description = "", string category = "") : Attribute
{
    public string Description { get; } = description;
    public string Category { get; } = category;
}

// ── Loader ────────────────────────────────────────────────────────────────────

public class ModuleLoaderService
{
    private readonly List<ModuleInfo> _modules = new();
    private readonly Dictionary<string, Assembly> _assemblies = new();  // sourcePath → compiled assembly
    private readonly object _lock = new();
    private readonly ConfigService _cfg;
    private readonly ILogger<ModuleLoaderService> _logger;

    // Pre-built references shared across all compilations
    private readonly IReadOnlyList<MetadataReference> _sharedRefs;

    public ModuleLoaderService(ConfigService cfg, ILogger<ModuleLoaderService> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _sharedRefs = BuildSharedReferences();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task ScanAndLoadAsync()
    {
        var basePath = _cfg.Config.ModulesBasePath;

        // Scan D:/svn/*/Module/ directories
        if (Directory.Exists(basePath))
        {
            var moduleDirs = Directory.GetDirectories(basePath, "Module", SearchOption.AllDirectories);
            foreach (var dir in moduleDirs)
                foreach (var cs in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                    await LoadFileAsync(cs);
        }

        // Also load built-in modules from the app's own Modules/ folder
        var builtIn = Path.Combine(AppContext.BaseDirectory, "Modules");
        if (Directory.Exists(builtIn))
            foreach (var cs in Directory.GetFiles(builtIn, "*.cs"))
                await LoadFileAsync(cs);

        _logger.LogInformation("Module scan complete: {Ok} loaded, {Err} errors",
            _modules.Count(m => m.IsLoaded), _modules.Count(m => !m.IsLoaded));
    }

    public async Task<ModuleInfo> LoadFileAsync(string filePath)
    {
        var info = new ModuleInfo
        {
            SourcePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
            LoadedAt = DateTime.Now
        };

        Assembly? asm = null;
        try
        {
            var src = await File.ReadAllTextAsync(filePath);
            asm = Compile(src, Path.GetFileNameWithoutExtension(filePath));
            ExtractMeta(asm, info);
            info.IsLoaded = true;
        }
        catch (Exception ex)
        {
            info.LoadError = ex.Message;
            _logger.LogWarning("Module load failed [{File}]: {Err}", filePath, ex.Message);
        }

        lock (_lock)
        {
            _modules.RemoveAll(m => m.SourcePath == filePath);
            _modules.Add(info);
            if (asm != null) _assemblies[filePath] = asm;
        }
        return info;
    }

    public List<ModuleInfo> GetAll()
    {
        lock (_lock) return _modules.ToList();
    }

    /// <summary>
    /// Sets a static property on a Roslyn-compiled type by name.
    /// Used at startup to pass configuration values into compiled modules.
    /// </summary>
    public void SetModuleStatic(string typeName, string propertyName, object value)
    {
        List<Assembly> asms;
        lock (_lock) asms = _assemblies.Values.ToList();
        foreach (var asm in asms)
        {
            var type = asm.GetType(typeName);
            if (type == null) continue;
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, value);
            return;
        }
        _logger.LogWarning("SetModuleStatic: type '{Type}' not found in any loaded assembly", typeName);
    }

    // ── Function invocation ───────────────────────────────────────────────────

    /// <summary>
    /// Finds and invokes a public method by name across all loaded assemblies.
    /// Uses TestContext.Current for result communication.
    /// Throws InvalidOperationException if the function is not found.
    /// </summary>
    public void InvokeFunction(string functionName, string p1, string p2, string p3, string p4)
    {
        List<Assembly> asms;
        lock (_lock) asms = _assemblies.Values.ToList();

        foreach (var asm in asms)
        {
            foreach (var type in asm.GetTypes())
            {
                var method = type.GetMethod(functionName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (method == null) continue;

                var rawArgs = new[] { p1, p2, p3, p4 };
                var paramInfos = method.GetParameters();
                var args = paramInfos.Select((p, i) =>
                {
                    var raw = i < rawArgs.Length ? rawArgs[i] : "";
                    if (string.IsNullOrEmpty(raw) && p.HasDefaultValue)
                        return p.DefaultValue;
                    try { return (object?)Convert.ChangeType(raw, p.ParameterType); }
                    catch { return p.HasDefaultValue ? p.DefaultValue : raw; }
                }).ToArray();

                var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                method.Invoke(instance, args);
                return;
            }
        }

        throw new InvalidOperationException($"Function '{functionName}' not found in any loaded module");
    }

    // ── Compilation ───────────────────────────────────────────────────────────

    private Assembly Compile(string source, string name)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var unique = $"{name}_{Guid.NewGuid():N}"[..24];

        var compilation = CSharpCompilation.Create(
            unique,
            new[] { tree },
            _sharedRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join('\n', result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage()));
            throw new InvalidOperationException($"Compilation errors:\n{errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    private void ExtractMeta(Assembly asm, ModuleInfo info)
    {
        foreach (var type in asm.GetTypes())
        {
            var ma = type.GetCustomAttribute<FMainModuleAttribute>();
            if (ma != null)
            {
                info.Name = ma.Name;
                info.Description = ma.Description;
                info.Category = ma.Category;
                info.Version = ma.Version;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var fa = method.GetCustomAttribute<FMainFunctionAttribute>();
                if (fa == null) continue;

                info.Functions.Add(new FunctionInfo
                {
                    Name = method.Name,
                    Description = fa.Description,
                    Category = fa.Category,
                    ReturnType = method.ReturnType.Name,
                    Parameters = method.GetParameters().Select(p => new ParamInfo
                    {
                        Name = p.Name ?? "param",
                        Type = p.ParameterType.Name,
                        DefaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                    }).ToList()
                });
            }
        }
    }

    private static List<MetadataReference> BuildSharedReferences()
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(File).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.Marshal).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Diagnostics.Process).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.IO.Ports.SerialPort).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.GetExecutingAssembly().Location)
        };

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll",
                                    "System.Runtime.InteropServices.dll", "System.Diagnostics.Process.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }
}
