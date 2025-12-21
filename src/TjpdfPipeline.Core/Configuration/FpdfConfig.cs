using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace FilterPDF.Configuration
{
    /// <summary>
    /// Central configuration management for fpdf
    /// Supports multiple configuration sources: JSON file, .env file, environment variables
    /// </summary>
    public class FpdfConfig
    {
        private static FpdfConfig? _instance;
        private static readonly object _lock = new object();
        
        public static FpdfConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new FpdfConfig();
                            _instance.Load();
                        }
                    }
                }
                return _instance;
            }
        }
        
        // Configuration properties
        public SecurityConfig Security { get; set; } = new SecurityConfig();
        public PerformanceConfig Performance { get; set; } = new PerformanceConfig();
        public CacheConfig Cache { get; set; } = new CacheConfig();
        
        private FpdfConfig() { }
        
        /// <summary>
        /// Load configuration from all sources in priority order:
        /// 1. Environment variables (highest priority)
        /// 2. .env file
        /// 3. fpdf.config.json
        /// 4. Default values (lowest priority)
        /// </summary>
        public void Load()
        {
            // Load from JSON config file
            LoadFromJsonFile();
            
            // Load from .env file (overrides JSON)
            LoadFromEnvFile();
            
            // Load from environment variables (overrides everything)
            LoadFromEnvironment();
            
            // Validate and expand paths
            ValidateConfiguration();
        }
        
        private void LoadFromJsonFile()
        {
            var configPaths = new[]
            {
                "fpdf.config.json",
                ".fpdf/config.json",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fpdf", "config.json")
            };
            
            foreach (var configPath in configPaths)
            {
                if (File.Exists(configPath))
                {
                    try
                    {
                        var json = File.ReadAllText(configPath);
                        var config = JsonConvert.DeserializeObject<FpdfConfig>(json);
                        if (config != null)
                        {
                            // Merge with current config
                            if (config.Security?.AllowedDirectories != null)
                            {
                                foreach (var dir in config.Security.AllowedDirectories)
                                {
                                    Security.AllowedDirectories.Add(dir);
                                }
                            }
                            
                            if (config.Security?.DisablePathValidation != null)
                                Security.DisablePathValidation = config.Security.DisablePathValidation;
                            
                            if (config.Security?.MaxFileSize != null)
                                Security.MaxFileSize = config.Security.MaxFileSize;
                            
                            if (config.Performance?.DefaultWorkers != null)
                                Performance.DefaultWorkers = config.Performance.DefaultWorkers;
                            
                            if (config.Cache?.DefaultDirectory != null)
                                Cache.DefaultDirectory = config.Cache.DefaultDirectory;
                            
                            Console.WriteLine($"[INFO] Configuration loaded from: {configPath}");
                            break; // Use first found config file
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to load config from {configPath}: {ex.Message}");
                    }
                }
            }
        }
        
        private void LoadFromEnvFile()
        {
            var envPaths = new[]
            {
                ".env",
                ".fpdf.env",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fpdf.env")
            };
            
            foreach (var envPath in envPaths)
            {
                if (File.Exists(envPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(envPath);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                                continue;
                            
                            var parts = line.Split('=', 2);
                            if (parts.Length == 2)
                            {
                                var key = parts[0].Trim();
                                var value = parts[1].Trim().Trim('"', '\'');
                                
                                ProcessConfigValue(key, value);
                            }
                        }
                        
                        Console.WriteLine($"[INFO] Environment file loaded from: {envPath}");
                        break; // Use first found .env file
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to load .env from {envPath}: {ex.Message}");
                    }
                }
            }
        }
        
        private void LoadFromEnvironment()
        {
            // Security settings
            var allowedDirs = Environment.GetEnvironmentVariable("FPDF_ALLOWED_DIRS");
            if (!string.IsNullOrWhiteSpace(allowedDirs))
            {
                foreach (var dir in allowedDirs.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Security.AllowedDirectories.Add(dir.Trim());
                    }
                }
            }
            
            var disableValidation = Environment.GetEnvironmentVariable("FPDF_DISABLE_PATH_VALIDATION");
            if (bool.TryParse(disableValidation, out bool disabled))
            {
                Security.DisablePathValidation = disabled;
            }
            
            var maxFileSize = Environment.GetEnvironmentVariable("FPDF_MAX_FILE_SIZE_MB");
            if (long.TryParse(maxFileSize, out long maxSize))
            {
                Security.MaxFileSize = maxSize * 1024 * 1024;
            }
            
            // Performance settings
            var defaultWorkers = Environment.GetEnvironmentVariable("FPDF_DEFAULT_WORKERS");
            if (int.TryParse(defaultWorkers, out int workers))
            {
                Performance.DefaultWorkers = workers;
            }
            
            // Cache settings
            var cacheDir = Environment.GetEnvironmentVariable("FPDF_CACHE_DIR");
            if (!string.IsNullOrWhiteSpace(cacheDir))
            {
                Cache.DefaultDirectory = cacheDir;
            }
        }
        
        private void ProcessConfigValue(string key, string value)
        {
            switch (key.ToUpper())
            {
                case "FPDF_ALLOWED_DIRS":
                case "ALLOWED_DIRS":
                    foreach (var dir in value.Split(Path.PathSeparator))
                    {
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            Security.AllowedDirectories.Add(dir.Trim());
                        }
                    }
                    break;
                    
                case "FPDF_DISABLE_PATH_VALIDATION":
                case "DISABLE_PATH_VALIDATION":
                    if (bool.TryParse(value, out bool disabled))
                    {
                        Security.DisablePathValidation = disabled;
                    }
                    break;
                    
                case "FPDF_MAX_FILE_SIZE_MB":
                case "MAX_FILE_SIZE_MB":
                    if (long.TryParse(value, out long maxSize))
                    {
                        Security.MaxFileSize = maxSize * 1024 * 1024;
                    }
                    break;
                    
                case "FPDF_DEFAULT_WORKERS":
                case "DEFAULT_WORKERS":
                    if (int.TryParse(value, out int workers))
                    {
                        Performance.DefaultWorkers = workers;
                    }
                    break;
                    
                case "FPDF_CACHE_DIR":
                case "CACHE_DIR":
                    Cache.DefaultDirectory = value;
                    break;
            }
        }
        
        private void ValidateConfiguration()
        {
            // Expand and validate allowed directories
            var validatedDirs = new HashSet<string>();
            foreach (var dir in Security.AllowedDirectories)
            {
                try
                {
                    var fullPath = Path.GetFullPath(dir);
                    if (Directory.Exists(fullPath))
                    {
                        validatedDirs.Add(fullPath);
                    }
                    else
                    {
                        Console.WriteLine($"[WARNING] Allowed directory does not exist: {dir}");
                    }
                }
                catch
                {
                    Console.WriteLine($"[WARNING] Invalid directory path: {dir}");
                }
            }
            Security.AllowedDirectories = validatedDirs;
            
            // Ensure cache directory exists
            if (!string.IsNullOrWhiteSpace(Cache.DefaultDirectory))
            {
                try
                {
                    Directory.CreateDirectory(Cache.DefaultDirectory);
                }
                catch
                {
                    Console.WriteLine($"[WARNING] Could not create cache directory: {Cache.DefaultDirectory}");
                }
            }
        }
        
        /// <summary>
        /// Save current configuration to JSON file
        /// </summary>
        public void SaveToFile(string path = "fpdf.config.json")
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(path, json);
                Console.WriteLine($"[INFO] Configuration saved to: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to save configuration: {ex.Message}");
            }
        }
    }
    
    public class SecurityConfig
    {
        public HashSet<string> AllowedDirectories { get; set; } = new HashSet<string>();
        public bool DisablePathValidation { get; set; } = false;
        public long MaxFileSize { get; set; } = 500 * 1024 * 1024; // 500MB default
    }
    
    public class PerformanceConfig
    {
        public int DefaultWorkers { get; set; } = 4;
        public int BatchSize { get; set; } = 100;
        public int MaxConcurrency { get; set; } = 16;
    }
    
    public class CacheConfig
    {
        public string DefaultDirectory { get; set; } = ".cache";
        public bool AutoCleanup { get; set; } = false;
        public int MaxCacheSizeMB { get; set; } = 1024; // 1GB default
    }
}