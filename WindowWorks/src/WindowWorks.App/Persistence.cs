using System;
using System.IO;
using System.Text.Json;

namespace WindowWorks.App
{
    /// <summary>
    /// Handles reading/writing simple settings to %APPDATA%/WindowWorks.
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
                if (!File.Exists(path))
                {
                    // No settings file present on first run - create one with defaults immediately
                    var defaults = new Models.AppSettings();
                    try { SaveSettings(defaults); } catch { /* ignore save failures on startup */ }
                    return defaults;
                }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Persistence] SaveSettings failed: {ex}");
                throw;
            }
        }

        public void Dispose()
        {
            // no unmanaged resources for now
        }
    }
}
