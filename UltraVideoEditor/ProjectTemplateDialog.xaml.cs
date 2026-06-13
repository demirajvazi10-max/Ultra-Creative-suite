using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;

using MessageBox     = System.Windows.MessageBox;
using Button         = System.Windows.Controls.Button;
using CheckBox       = System.Windows.Controls.CheckBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace UltraVideoEditor
{
    public partial class ProjectTemplateDialog : Window
    {
        private static readonly string TemplatesFolder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Templates");

        private List<ProjectTemplate> _templates = new();
        private ProjectTemplate       _current   = null;

        // Callback koji MainWindow koristi da primeni template
        public Action<ProjectTemplate> OnApply { get; set; }

        public ProjectTemplateDialog()
        {
            InitializeComponent();
            PopulateExportProfiles();
            PopulateGradePresets();
            LoadTemplates();
        }

        // ── Inicijalizacija combo boxova ──────────────────────────────

        private void PopulateExportProfiles()
        {
            foreach (var p in ExportProfiles.GetProfiles())
                CmbExportProfile.Items.Add(new ComboBoxItem
                {
                    Content = p.Name,
                    Tag     = p.Name,
                });
            CmbExportProfile.SelectedIndex = 0;
        }

        private void PopulateGradePresets()
        {
            foreach (var kvp in ColorGradingEngine.PresetDescriptions)
                CmbGradePreset.Items.Add(new ComboBoxItem
                {
                    Content = $"{kvp.Key}  —  {kvp.Value}",
                    Tag     = kvp.Key.ToString(),
                });
            CmbGradePreset.SelectedIndex = 0;
        }

        // ── Učitaj/čuvaj templatee ────────────────────────────────────

        private void LoadTemplates()
        {
            _templates.Clear();
            if (Directory.Exists(TemplatesFolder))
            {
                foreach (var file in Directory.GetFiles(TemplatesFolder, "*.iskrat"))
                {
                    try
                    {
                        var t = JsonConvert.DeserializeObject<ProjectTemplate>(
                            File.ReadAllText(file));
                        if (t != null) _templates.Add(t);
                    }
                    catch { }
                }
            }
            RefreshList();
        }

        private void SaveTemplateToFile(ProjectTemplate t)
        {
            Directory.CreateDirectory(TemplatesFolder);
            string safe = string.Concat(t.Name.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(TemplatesFolder, $"{safe}.iskrat");
            File.WriteAllText(path, JsonConvert.SerializeObject(t, Formatting.Indented));
        }

        private void DeleteTemplateFile(ProjectTemplate t)
        {
            string safe = string.Concat(t.Name.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(TemplatesFolder, $"{safe}.iskrat");
            if (File.Exists(path)) File.Delete(path);
        }

        private void RefreshList()
        {
            TemplateList.Items.Clear();
            foreach (var t in _templates)
                TemplateList.Items.Add(t.Name);
        }

        // ── Selekcija ─────────────────────────────────────────────────

        private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = TemplateList.SelectedIndex;
            if (idx < 0 || idx >= _templates.Count)
            {
                _current = null;
                BtnDeleteTemplate.IsEnabled  = false;
                BtnExportTemplate.IsEnabled  = false;
                BtnApplyTemplate.IsEnabled   = false;
                return;
            }
            _current = _templates[idx];
            PopulateFields(_current);
            BtnDeleteTemplate.IsEnabled = true;
            BtnExportTemplate.IsEnabled = true;
            BtnApplyTemplate.IsEnabled  = true;
        }

        private void PopulateFields(ProjectTemplate t)
        {
            TxtTemplateName.Text = t.Name;
            TxtTemplateDesc.Text = t.Description;

            // Export profil
            for (int i = 0; i < CmbExportProfile.Items.Count; i++)
                if (CmbExportProfile.Items[i] is ComboBoxItem ci &&
                    ci.Tag?.ToString() == t.ExportResolution)
                { CmbExportProfile.SelectedIndex = i; break; }

            // Jezik
            for (int i = 0; i < CmbLanguage.Items.Count; i++)
                if (CmbLanguage.Items[i] is ComboBoxItem ci &&
                    ci.Tag?.ToString() == t.Language)
                { CmbLanguage.SelectedIndex = i; break; }

            // Grade preset
            for (int i = 0; i < CmbGradePreset.Items.Count; i++)
                if (CmbGradePreset.Items[i] is ComboBoxItem ci &&
                    ci.Tag?.ToString() == t.ColorGradePreset)
                { CmbGradePreset.SelectedIndex = i; break; }

            ChkYouTube.IsChecked   = t.ExportYouTube;
            ChkReels.IsChecked     = t.ExportReels;
            ChkMP3.IsChecked       = t.ExportMP3;
            ChkSubtitles.IsChecked = t.EnableSubtitles;
            ChkGPU.IsChecked       = t.UseGPU;
            ChkFastRender.IsChecked= t.FastRender;
        }

        private ProjectTemplate ReadFields(ProjectTemplate t = null)
        {
            t ??= new ProjectTemplate();
            t.Name             = TxtTemplateName.Text.Trim();
            t.Description      = TxtTemplateDesc.Text.Trim();
            t.Language         = (CmbLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "sr";
            t.ColorGradePreset = (CmbGradePreset.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
            t.ExportYouTube    = ChkYouTube.IsChecked  == true;
            t.ExportReels      = ChkReels.IsChecked    == true;
            t.ExportMP3        = ChkMP3.IsChecked      == true;
            t.EnableSubtitles  = ChkSubtitles.IsChecked== true;
            t.UseGPU           = ChkGPU.IsChecked      == true;
            t.FastRender       = ChkFastRender.IsChecked == true;

            // Export profil → rezolucija
            if (CmbExportProfile.SelectedItem is ComboBoxItem ci)
            {
                var profile = ExportProfiles.GetProfiles()
                    .FirstOrDefault(p => p.Name == ci.Tag?.ToString());
                if (profile != null)
                {
                    t.ExportResolution = profile.Resolution;
                    t.ExportWidth      = profile.Width;
                    t.ExportHeight     = profile.Height;
                    t.ExportBitrate    = profile.Bitrate;
                    t.ExportFrameRate  = profile.FrameRate;
                }
            }
            return t;
        }

        // ── CRUD akcije ───────────────────────────────────────────────

        private void BtnNewTemplate_Click(object sender, RoutedEventArgs e)
        {
            TxtTemplateName.Text = $"Template {_templates.Count + 1}";
            TxtTemplateDesc.Text = "";
            CmbExportProfile.SelectedIndex = 0;
            CmbLanguage.SelectedIndex      = 0;
            CmbGradePreset.SelectedIndex   = 0;
            ChkYouTube.IsChecked    = true;
            ChkReels.IsChecked      = false;
            ChkMP3.IsChecked        = false;
            ChkSubtitles.IsChecked  = false;
            ChkGPU.IsChecked        = false;
            ChkFastRender.IsChecked = false;
            _current = null;
            TemplateList.SelectedIndex = -1;
            TxtTemplateName.Focus();
        }

        private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtTemplateName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Unesite naziv templatea.", "Template",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Ažuriraj postojeći ili dodaj novi
            var existing = _templates.FirstOrDefault(t => t.Name == name);
            if (existing != null)
            {
                ReadFields(existing);
                SaveTemplateToFile(existing);
            }
            else
            {
                var t = ReadFields();
                _templates.Add(t);
                SaveTemplateToFile(t);
                _current = t;
            }

            RefreshList();
            int idx = _templates.FindIndex(t => t.Name == name);
            if (idx >= 0) TemplateList.SelectedIndex = idx;
            MessageBox.Show($"Template \"{name}\" sačuvan.", "Template",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var r = MessageBox.Show($"Obrisati template \"{_current.Name}\"?",
                "Potvrda", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            DeleteTemplateFile(_current);
            _templates.Remove(_current);
            _current = null;
            RefreshList();
            BtnDeleteTemplate.IsEnabled = false;
            BtnExportTemplate.IsEnabled = false;
            BtnApplyTemplate.IsEnabled  = false;
        }

        // ── Uvoz / Izvoz ──────────────────────────────────────────────

        private void BtnImportTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Uvezi template",
                Filter = "Iskra Template|*.iskrat|Svi fajlovi|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var t = JsonConvert.DeserializeObject<ProjectTemplate>(
                    File.ReadAllText(dlg.FileName));
                if (t == null) throw new Exception("Nečitljiv fajl.");
                // Izbjegni duplikat naziva
                if (_templates.Any(x => x.Name == t.Name))
                    t.Name = t.Name + " (uvezeno)";
                _templates.Add(t);
                SaveTemplateToFile(t);
                RefreshList();
                TemplateList.SelectedIndex = _templates.Count - 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greška uvoza: {ex.Message}", "Greška",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var dlg = new SaveFileDialog
            {
                Title      = "Izvezi template",
                Filter     = "Iskra Template|*.iskrat",
                FileName   = _current.Name,
                DefaultExt = "iskrat",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName,
                    JsonConvert.SerializeObject(_current, Formatting.Indented));
                MessageBox.Show("Template izvezen.", "Template",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greška izvoza: {ex.Message}", "Greška",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Primena na projekat ───────────────────────────────────────

        private void BtnApplyTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            OnApply?.Invoke(_current);
            MessageBox.Show(
                $"Template \"{_current.Name}\" primenjen na trenutni projekat.\n\n" +
                $"Color grade: {_current.ColorGradePreset}\n" +
                $"Jezik: {_current.Language}\n" +
                $"YouTube: {(_current.ExportYouTube ? "✅" : "❌")}  " +
                $"Reels: {(_current.ExportReels ? "✅" : "❌")}  " +
                $"MP3: {(_current.ExportMP3 ? "✅" : "❌")}",
                "Template primenjen",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
