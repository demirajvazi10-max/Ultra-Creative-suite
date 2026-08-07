using System;
using System.Collections.Generic;
using System.IO;

namespace UltraStudio.Services
{
    /// <summary>
    /// Čita ollama_models.cfg koji installer piše u {app} folder posle
    /// preuzimanja modela — installer bira TAG modela prema hardveru
    /// (npr. "qwen2.5vl:7b" na jačoj GPU, "qwen2.5vl:3b" na slabijoj), pa app
    /// mora da traži TAČNO taj tag, ne generičko ":latest" (Ollama ne pravi
    /// automatski alias — pull "qwen2.5vl:7b" ne znači da "qwen2.5vl:latest"
    /// odjednom postoji). Ako fajla nema (dev build pokrenut bez installera),
    /// vraćamo se na ":latest" kao razuman fallback za ručnu instalaciju.
    /// </summary>
    public static class OllamaModelConfig
    {
        private static readonly Dictionary<string, string> _values = Load();

        public static string VisionModel => _values.TryGetValue("vision", out var v) && !string.IsNullOrWhiteSpace(v) ? v : "qwen2.5vl:latest";
        public static string TextModel => _values.TryGetValue("text", out var v) && !string.IsNullOrWhiteSpace(v) ? v : "qwen2.5:latest";

        private static Dictionary<string, string> Load()
        {
            var dict = new Dictionary<string, string>();
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "ollama_models.cfg");
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                    }
                }
            }
            catch { /* fallback vrednosti su dovoljne ako čitanje ne uspe */ }
            return dict;
        }
    }
}
