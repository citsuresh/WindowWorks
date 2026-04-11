using System;

namespace WindowWorks.App.Models
{
    /// <summary>
    /// Simple preset model representing window modifications.
    /// </summary>
    public class Preset
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Opacity { get; set; } = 100; // 0-100
        public bool Topmost { get; set; } = false;
    }
}
