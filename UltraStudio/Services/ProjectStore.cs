using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Folder sesije: svaka destruktivna izmena piše novi PNG ovde, pa Undo
    /// samo vraća staru putanju. Briše se pri sledećem pokretanju (stariji od
    /// 2 dana), nikad dok program radi.
    /// </summary>
    public static class Session
    {
        public static string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraStudio", "Session",
            DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);

        private static int _counter;

        public static string NewFile(string prefix, string ext = ".png")
        {
            Directory.CreateDirectory(Root);
            int n = System.Threading.Interlocked.Increment(ref _counter);
            return Path.Combine(Root, $"{prefix}_{n:0000}{ext}");
        }

        public static void CleanupOld()
        {
            try
            {
                var parent = Path.GetDirectoryName(Root)!;
                if (!Directory.Exists(parent)) return;
                foreach (var d in Directory.GetDirectories(parent))
                {
                    if (string.Equals(d, Root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-2))
                        try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>JSON snimak celog projekta — koristi ga i Undo i .ustudio fajl.</summary>
    public static class ProjectJson
    {
        /// <summary>Dozvoljava samo naše tipove slojeva — fajl sa strane ne može da napravi proizvoljan objekat.</summary>
        private class LayerBinder : ISerializationBinder
        {
            private static readonly Dictionary<string, Type> Allowed = new()
            {
                ["text"] = typeof(TextLayer), ["shape"] = typeof(ShapeLayer),
                ["image"] = typeof(ImageLayer), ["gradient"] = typeof(GradientLayer),
            };
            public Type BindToType(string? assemblyName, string typeName) =>
                Allowed.TryGetValue(typeName, out var t) ? t : throw new JsonSerializationException("Unknown layer type: " + typeName);
            public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
            {
                assemblyName = null;
                typeName = Allowed.FirstOrDefault(kv => kv.Value == serializedType).Key ?? serializedType.Name;
            }
        }

        public static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.Auto,
            SerializationBinder = new LayerBinder(),
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };

        public static string Serialize(ImageProject p) => JsonConvert.SerializeObject(p, Settings);

        /// <summary>Upisuje snimak u POSTOJEĆI projekat (MainWindow drži jednu instancu).</summary>
        public static void Restore(ImageProject target, string json)
        {
            target.Layers.Clear();
            target.ResetAdjustments();
            target.BasePath = null;
            target.Selection = new Selection();
            var tmp = JsonConvert.DeserializeObject<ImageProject>(json, Settings) ?? new ImageProject();
            target.OriginalPath = tmp.OriginalPath;
            target.OriginalWidth = tmp.OriginalWidth;
            target.OriginalHeight = tmp.OriginalHeight;
            target.BasePath = tmp.BasePath;
            target.CanvasWidth = tmp.CanvasWidth;
            target.CanvasHeight = tmp.CanvasHeight;
            target.CanvasBackgroundHex = tmp.CanvasBackgroundHex;
            target.BlankCanvas = tmp.BlankCanvas;
            target.NextLayerNumber = tmp.NextLayerNumber;
            target.Selection = tmp.Selection ?? new Selection();
            target.CopyAdjustmentsFrom(tmp);
            target.Dpi = tmp.Dpi > 0 ? tmp.Dpi : 300;
            target.Pages = tmp.Pages ?? new List<string>();
            target.CurrentPage = Math.Clamp(tmp.CurrentPage, 0, Math.Max(0, target.Pages.Count - 1));
            foreach (var l in tmp.Layers) target.Layers.Add(l);
        }

        /// <summary>Kopija trenutne strane (bez ostalih strana — brzo, za pregled i AI) ili celog dokumenta.</summary>
        public static ImageProject Clone(ImageProject p, bool includePages = false)
        {
            var c = new ImageProject();
            if (includePages) Restore(c, Serialize(p));
            else
            {
                var saved = p.Pages;
                p.Pages = new List<string>();
                try { Restore(c, Serialize(p)); }
                finally { p.Pages = saved; }
                c.CurrentPage = 0;
            }
            c.ProjectPath = p.ProjectPath;
            return c;
        }
    }

    /// <summary>
    /// Poništi/Ponovi nad JSON snimcima. Checkpoint se zove PRE svake izmene,
    /// sa kratkim opisom (to je i tekst u prozoru Istorija). Uzastopne izmene
    /// iste stvari (npr. strelica po strelica na istom slajderu) spajaju se u
    /// jedan korak, da Undo ne bi išao "po jedan klik".
    /// </summary>
    public class UndoManager
    {
        private const int Max = 100;
        private readonly List<(string label, string json)> _undo = new();
        private readonly List<(string label, string json)> _redo = new();
        private string? _lastLabel;
        private DateTime _lastTime;

        public bool Dirty { get; set; }
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public IReadOnlyList<(string label, string json)> UndoList => _undo;
        public IReadOnlyList<(string label, string json)> RedoList => _redo;
        public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].label : null;
        public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].label : null;

        public void Checkpoint(ImageProject p, string label, bool mergeSame = false)
        {
            Dirty = true;
            if (mergeSame && label == _lastLabel && (DateTime.Now - _lastTime).TotalSeconds < 2.5 && _undo.Count > 0)
            {
                _lastTime = DateTime.Now;
                return; // isti korak se nastavlja — stari snimak (pre prve izmene) ostaje
            }
            _undo.Add((label, ProjectJson.Serialize(p)));
            if (_undo.Count > Max) _undo.RemoveAt(0);
            _redo.Clear();
            _lastLabel = label; _lastTime = DateTime.Now;
        }

        public string? Undo(ImageProject p)
        {
            if (_undo.Count == 0) return null;
            var (label, json) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add((label, ProjectJson.Serialize(p)));
            ProjectJson.Restore(p, json);
            _lastLabel = null; Dirty = true;
            return label;
        }

        public string? Redo(ImageProject p)
        {
            if (_redo.Count == 0) return null;
            var (label, json) = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add((label, ProjectJson.Serialize(p)));
            ProjectJson.Restore(p, json);
            _lastLabel = null; Dirty = true;
            return label;
        }

        /// <summary>Operacija nije uspela ili je otkazana: vrati stanje pre poslednjeg Checkpoint-a, bez Redo koraka.</summary>
        public void Rollback(ImageProject p)
        {
            if (_undo.Count == 0) return;
            ProjectJson.Restore(p, _undo[^1].json);
            _undo.RemoveAt(_undo.Count - 1);
            _lastLabel = null;
        }

        public void Clear() { _undo.Clear(); _redo.Clear(); _lastLabel = null; Dirty = false; }
    }

    /// <summary>
    /// .ustudio = ZIP: project.json + assets/ (radna slika, original, slike
    /// slojeva, maska selekcije). Projekat se može preneti na drugi računar.
    /// </summary>
    public static class ProjectFile
    {
        public const string Extension = ".ustudio";
        private const string AssetPrefix = "asset:";

        public static void Save(ImageProject p, string path)
        {
            PageOps.Sync(p);
            var copy = ProjectJson.Clone(p, includePages: true);
            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // izvor -> ime u zipu

            string? Pack(string? src)
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return src;
                if (!assets.TryGetValue(src, out var name))
                {
                    name = $"assets/{assets.Count:000}_{Path.GetFileName(src)}";
                    assets[src] = name;
                }
                return AssetPrefix + name;
            }

            copy.OriginalPath = Pack(copy.OriginalPath);
            copy.BasePath = Pack(copy.BasePath);
            copy.Selection.MaskPath = Pack(copy.Selection.MaskPath);
            foreach (var l in copy.Layers.OfType<ImageLayer>()) l.SourcePath = Pack(l.SourcePath) ?? "";
            for (int i = 0; i < copy.Pages.Count; i++)
                copy.Pages[i] = PageOps.MapPaths(copy.Pages[i], Pack);

            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("project.json", CompressionLevel.Optimal);
                using (var w = new StreamWriter(entry.Open()))
                    w.Write(JsonConvert.SerializeObject(copy, Formatting.Indented, ProjectJson.Settings));
                foreach (var kv in assets)
                    zip.CreateEntryFromFile(kv.Key, kv.Value, CompressionLevel.Fastest);
            }
            File.Move(tmp, path, true);
            p.ProjectPath = path;
        }

        public static void Load(ImageProject target, string path)
        {
            string dir = Path.Combine(Session.Root, "project_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string json;
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("project.json missing");
                using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("assets/")))
                {
                    string dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip zaštita
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                }
            }

            string? Unpack(string? s) =>
                s != null && s.StartsWith(AssetPrefix) ? Path.Combine(dir, s[AssetPrefix.Length..].Replace('/', Path.DirectorySeparatorChar)) : s;

            ProjectJson.Restore(target, json);
            target.OriginalPath = Unpack(target.OriginalPath);
            target.BasePath = Unpack(target.BasePath);
            target.Selection.MaskPath = Unpack(target.Selection.MaskPath);
            foreach (var l in target.Layers.OfType<ImageLayer>()) l.SourcePath = Unpack(l.SourcePath) ?? "";
            for (int i = 0; i < target.Pages.Count; i++)
                target.Pages[i] = PageOps.MapPaths(target.Pages[i], Unpack);
            target.ProjectPath = path;
        }
    }
}
