using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using SP = UltraComposer.App.SoundParameters;

namespace UltraComposer.App;

/// <summary>
/// Sound library ("Biblioteka zvukova"): every bank and sound the user has,
/// sorted into categories, ready to load with one keypress or double-click -
/// no file dialogs, no hunting for folders.
///
/// Categories are simply folders inside the library folder
/// (Documents\Ultra Composer\Library). The program creates the starting
/// categories on first run; the user can add their own folders there, or
/// use "Add a sound to the library..." which copies a file into the
/// selected category. The bundled GeneralUser GS bank always appears under
/// "Standard", and the sounds shipped in src/App/Library (copied next to the
/// exe) appear in their categories alongside the user's own. Supported files: SoundFont banks (.sf2, loaded as the main
/// bank), FM8 sounds (.nfm8, imported into Ultra FM) and VST3 instruments
/// (.vst3, loaded as the main instrument).
/// </summary>
public partial class MainWindow
{
    private static readonly string[] DefaultLibraryCategories = { "Standard", "FM8", "Oriental", "My sounds" };
    private static readonly string[] LibraryExtensions = { ".sf2", ".nfm8", ".vst3" };

    private sealed record LibraryItem(string Name, string Path, string Kind)
    {
        public override string ToString() => $"{Name} ({Kind})";
    }

    public static string LibraryRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ultra Composer", "Library");

    private string BundledBankPath => Path.Combine(AppContext.BaseDirectory, "GeneralUser-GS.sf2");

    /// <summary>Sounds shipped with the app (src/App/Library, copied next to the exe).</summary>
    private static string BundledLibraryRoot => Path.Combine(AppContext.BaseDirectory, "Library");

    /// <summary>Creates the library folder and starting categories (first run) and fills the category list.</summary>
    private void InitLibrary()
    {
        try
        {
            foreach (string c in DefaultLibraryCategories) Directory.CreateDirectory(Path.Combine(LibraryRoot, c));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Library folder not writable - the bundled bank still shows up.
        }
        RefreshLibraryCategories();
    }

    private void RefreshLibraryCategories()
    {
        string? keep = LibraryCategoryListBox.SelectedItem as string;
        LibraryCategoryListBox.Items.Clear();
        var names = new List<string>();
        try
        {
            foreach (string root in new[] { BundledLibraryRoot, LibraryRoot })
                if (Directory.Exists(root))
                    foreach (string n in Directory.GetDirectories(root).Select(d => Path.GetFileName(d)).OfType<string>())
                        if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!names.Contains("Standard", StringComparer.OrdinalIgnoreCase)) names.Add("Standard");
        // Starting categories first in their usual order, then the user's own alphabetically.
        var ordered = DefaultLibraryCategories.Where(d => names.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Concat(names.Where(n => !DefaultLibraryCategories.Contains(n, StringComparer.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        foreach (string n in ordered) LibraryCategoryListBox.Items.Add(n);
        LibraryCategoryListBox.SelectedItem = keep != null && LibraryCategoryListBox.Items.Contains(keep) ? keep : LibraryCategoryListBox.Items.Cast<object>().FirstOrDefault();
        RefreshLibraryItems();
    }

    private void RefreshLibraryItems()
    {
        LibrarySoundsListBox.Items.Clear();
        if (LibraryCategoryListBox.SelectedItem is not string category) return;

        if (category.Equals("Standard", StringComparison.OrdinalIgnoreCase) && File.Exists(BundledBankPath))
            LibrarySoundsListBox.Items.Add(new LibraryItem("GeneralUser GS", BundledBankPath, KindName(".sf2")));

        // Shipped sounds first, then the user's; a user copy with the same file name replaces the shipped one.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { BundledLibraryRoot, LibraryRoot })
        {
            string folder = Path.Combine(root, category);
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(f => LibraryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
                    byName[Path.GetFileName(f)] = f;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var kv in byName.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
            LibrarySoundsListBox.Items.Add(new LibraryItem(Path.GetFileNameWithoutExtension(kv.Value), kv.Value, KindName(Path.GetExtension(kv.Value))));

        if (LibrarySoundsListBox.Items.Count > 0) LibrarySoundsListBox.SelectedIndex = 0;
        AutomationProperties.SetName(LibrarySoundsListBox,
            SP.L($"Zvuci u kategoriji {category}, {LibrarySoundsListBox.Items.Count}", $"Sounds in {category}, {LibrarySoundsListBox.Items.Count}"));
        LibraryStatusText.Text = LibrarySoundsListBox.Items.Count == 0
            ? SP.L($"Kategorija {category} je prazna. Dodaj zvuk dugmetom \"Dodaj zvuk u biblioteku\" ili otvori folder biblioteke.",
                   $"{category} is empty. Add a sound with \"Add a sound to the library\" or open the library folder.")
            : "";
    }

    private static string KindName(string ext) => ext.ToLowerInvariant() switch
    {
        ".sf2" => SP.L("SoundFont banka", "SoundFont bank"),
        ".nfm8" => SP.L("FM8 zvuk", "FM8 sound"),
        ".vst3" => "VST3",
        _ => ext,
    };

    /// <summary>Loads the selected library item the same way the Browse/Load buttons would.</summary>
    private void LoadSelectedLibraryItem()
    {
        if (LibrarySoundsListBox.SelectedItem is not LibraryItem item) return;
        string ext = Path.GetExtension(item.Path).ToLowerInvariant();
        string message;
        switch (ext)
        {
            case ".sf2":
                SoundFontPathTextBox.Text = item.Path;
                LoadSoundFontButton_Click(this, new RoutedEventArgs());
                message = SP.L($"Učitano: {item.Name}. ", $"Loaded: {item.Name}. ") + SoundFontStatusText.Text;
                break;
            case ".vst3":
                VstPathTextBox.Text = item.Path;
                LoadVstButton_Click(this, new RoutedEventArgs());
                message = SP.L($"Učitano: {item.Name}. ", $"Loaded: {item.Name}. ") + VstStatusText.Text;
                break;
            case ".nfm8":
                // Ultra FM is only heard when Layer 1 has no bank or VST, so make room for it.
                if (UnloadSoundFontButton.IsEnabled) UnloadSoundFontButton_Click(this, new RoutedEventArgs());
                if (UnloadVstButton.IsEnabled) UnloadVstButton_Click(this, new RoutedEventArgs());
                message = ImportFm8File(item.Path);
                break;
            default:
                return;
        }
        LibraryStatusText.Text = message;
        AnnounceLibrary(message);
    }

    private void AnnounceLibrary(string text)
    {
        var peer = UIElementAutomationPeer.FromElement(LibrarySoundsListBox) ?? UIElementAutomationPeer.CreatePeerForElement(LibrarySoundsListBox);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "UltraComposerLibrary");
    }

    private void LibraryCategoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLibraryItems();

    private void LibrarySoundsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            LoadSelectedLibraryItem();
            e.Handled = true;
        }
    }

    private void LibrarySoundsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelectedLibraryItem();

    private void LibraryLoadButton_Click(object sender, RoutedEventArgs e) => LoadSelectedLibraryItem();

    private void LibraryRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLibraryCategories();
        AnnounceLibrary(SP.L("Biblioteka osvežena.", "Library refreshed."));
    }

    private void LibraryOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LibraryRoot);
            string target = LibraryCategoryListBox.SelectedItem is string c ? Path.Combine(LibraryRoot, c) : LibraryRoot;
            if (!Directory.Exists(target)) target = LibraryRoot;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = ex.Message;
        }
    }

    /// <summary>Copies a chosen file into the selected category and selects it.</summary>
    private void LibraryAddButton_Click(object sender, RoutedEventArgs e)
    {
        string category = LibraryCategoryListBox.SelectedItem as string ?? "My sounds";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = SP.L($"Dodaj zvuk u kategoriju {category}", $"Add a sound to {category}"),
            Filter = SP.L("Zvuci (*.sf2;*.nfm8;*.vst3)|*.sf2;*.nfm8;*.vst3|Svi fajlovi (*.*)|*.*",
                          "Sounds (*.sf2;*.nfm8;*.vst3)|*.sf2;*.nfm8;*.vst3|All files (*.*)|*.*"),
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        int copied = 0;
        string? last = null;
        try
        {
            string folder = Path.Combine(LibraryRoot, category);
            Directory.CreateDirectory(folder);
            foreach (string f in dialog.FileNames)
            {
                if (!LibraryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(folder, Path.GetFileName(f));
                if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(f, dest, overwrite: true);
                copied++;
                last = dest;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LibraryStatusText.Text = ex.Message;
            AnnounceLibrary(ex.Message);
            return;
        }

        RefreshLibraryItems();
        if (last != null)
        {
            foreach (var item in LibrarySoundsListBox.Items.OfType<LibraryItem>())
                if (string.Equals(item.Path, last, StringComparison.OrdinalIgnoreCase)) { LibrarySoundsListBox.SelectedItem = item; break; }
        }
        string msg = SP.L($"Dodato u kategoriju {category}: {copied}.", $"Added to {category}: {copied}.");
        LibraryStatusText.Text = msg;
        AnnounceLibrary(msg);
    }

    private void ApplyLibraryLanguage()
    {
        bool sr = SP.Serbian;
        LibraryTitleText.Text = sr ? "Biblioteka zvukova" : "Sound library";
        LibraryIntroText.Text = sr
            ? "Svi tvoji zvuci na jednom mestu, po kategorijama. Izaberi kategoriju, pa u listi zvukova Enter, razmak ili dvoklik učitava zvuk. " +
              "SoundFont banke se učitavaju kao glavna banka, FM8 zvukovi u Ultra FM (banka i VST se tada isključe, da bi se čuo), a VST3 kao glavni instrument. " +
              "Kategorije su folderi u Dokumenti\\Ultra Composer\\Library - možeš da dodaš i svoje."
            : "All your sounds in one place, by category. Pick a category, then in the sound list press Enter, Space or double-click to load. " +
              "SoundFont banks load as the main bank, FM8 sounds into Ultra FM (the bank and VST are unloaded so it can be heard) and VST3 as the main instrument. " +
              "Categories are folders in Documents\\Ultra Composer\\Library - you can add your own.";
        LibraryCategoryLabel.Content = sr ? "_Kategorija:" : "_Category:";
        LibrarySoundsLabel.Content = sr ? "Z_vuci:" : "Sou_nds:";
        AutomationProperties.SetName(LibraryCategoryListBox, sr ? "Kategorije biblioteke zvukova" : "Sound library categories");
        LibraryLoadButton.Content = sr ? "_Učitaj izabrani zvuk" : "_Load selected sound";
        LibraryAddButton.Content = sr ? "_Dodaj zvuk u biblioteku..." : "_Add a sound to the library...";
        LibraryOpenFolderButton.Content = sr ? "Otvori _folder biblioteke" : "Open library _folder";
        LibraryRefreshButton.Content = sr ? "Osveži _biblioteku" : "Refresh _library";
        if (LibraryCategoryListBox.Items.Count > 0) RefreshLibraryItems(); // kind names are translated
    }
}
