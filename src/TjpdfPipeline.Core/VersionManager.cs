using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace FilterPDF
{
    /// <summary>
    /// Dynamic version management system - reads version from assembly or project file.
    /// </summary>
    public static class VersionManager
    {
        private static string? _currentVersion;
        private static readonly object _lock = new object();

        public static string Current
        {
            get
            {
                if (_currentVersion == null)
                {
                    lock (_lock)
                    {
                        if (_currentVersion == null)
                            _currentVersion = GetVersion();
                    }
                }
                return _currentVersion;
            }
        }

        private static string GetVersion()
        {
            // Prefer assembly version at runtime.
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version != null && version.Major > 0)
                    return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                // Continue to project file lookup
            }

            // Try to read <Version> from any nearby .csproj.
            try
            {
                var proj = FindNearestProjectFile();
                if (!string.IsNullOrWhiteSpace(proj) && File.Exists(proj))
                {
                    var doc = XDocument.Load(proj);
                    var versionElement = doc.Descendants("Version").FirstOrDefault();
                    if (versionElement != null && !string.IsNullOrWhiteSpace(versionElement.Value))
                        return versionElement.Value.Trim();
                }
            }
            catch
            {
                // Ignore and fallback
            }

            return "0.1.0";
        }

        private static string? FindNearestProjectFile()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var direct = Path.Combine(dir.FullName, "TjpdfPipeline.Core.csproj");
                if (File.Exists(direct)) return direct;

                var cli = Path.Combine(dir.FullName, "TjpdfPipeline.Cli.csproj");
                if (File.Exists(cli)) return cli;

                var any = dir.GetFiles("*.csproj").FirstOrDefault();
                if (any != null) return any.FullName;

                dir = dir.Parent;
            }
            return null;
        }

        public static string FullVersion
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var version = assembly.GetName().Version;
                    if (version != null)
                        return version.ToString();
                }
                catch
                {
                    // ignore
                }
                return Current + ".0";
            }
        }

        public static void RefreshVersion()
        {
            lock (_lock)
            {
                _currentVersion = null;
            }
        }
    }
}
