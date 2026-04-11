using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WindowWorks.App
{
    /// <summary>
    /// Handles reading/writing presets and simple settings to %APPDATA%/WindowWorks.
    /// Also exposes embedded defaults (seed) which are included as EmbeddedResource in the project.
    /// </summary>
    public class Persistence : IDisposable
    {
        private readonly string _appFolder;
        public Persistence()
        {
            _appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowWorks");
            Directory.CreateDirectory(_appFolder);
        }

        public void OpenAppFolder()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo() { FileName = _appFolder, UseShellExecute = true }); } catch { }
        }

        public Models.AppSettings LoadSettings()
        {
            var path = Path.Combine(_appFolder, "settings.json");
            try
            {
                if (!File.Exists(path)) return new Models.AppSettings();
                var json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var s = JsonSerializer.Deserialize<Models.AppSettings>(json, opts);
                return s ?? new Models.AppSettings();
            }
            catch
            {
                return new Models.AppSettings();
            }
        }

        public void SaveSettings(Models.AppSettings settings)
        {
            try
            {
                var path = Path.Combine(_appFolder, "settings.json");
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public IEnumerable<Models.Preset>? LoadEmbeddedDefaultPresets()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = "docs.default_presets.json";
            // Try common manifest name patterns
            foreach (var rn in asm.GetManifestResourceNames())
            {
                if (rn.EndsWith("default_presets.json", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = rn; break;
                }
            }
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var sr = new StreamReader(stream);
            var json = sr.ReadToEnd();
            return JsonSerializer.Deserialize<IEnumerable<Models.Preset>>(json) ?? null;
        }

        public void SavePresets(IEnumerable<Models.Preset> presets)
        {
            var path = Path.Combine(_appFolder, "presets.json");
            var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(path, json);
        }

        public IEnumerable<Models.Preset>? LoadPresetsFromAppFolder()
        {
            var path = Path.Combine(_appFolder, "presets.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IEnumerable<Models.Preset>>(json);
        }

        public void Dispose()
        {
            // no unmanaged resources for now
        }
    }
}
