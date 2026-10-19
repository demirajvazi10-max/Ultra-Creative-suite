using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Newtonsoft.Json.Linq;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace UltraVideoEditor
{
    public partial class PixabaySearchDialog : Window
    {
        // Language helper
        private string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private string _apiKey;
        private List<PixabayMediaItem> _currentResults;
        private bool _isVideoMode = false;

        public List<string> DownloadedMediaPaths { get; private set; } = new List<string>();

        public PixabaySearchDialog()
        {
            InitializeComponent();
            UiScaling.Register(this);
            LoadSavedApiKey();
        }

        private void LoadSavedApiKey()
        {
            var savedKey = GetPixabayApiKey();
            if (!string.IsNullOrEmpty(savedKey))
            {
                _apiKey = savedKey;
                // v1.4: the field stays usable — typing a new key replaces the saved one
                // (it used to be locked, so a wrong or expired key could not be changed here).
                System.Windows.Automation.AutomationProperties.SetHelpText(txtApiKey,
                    "A Pixabay key is already saved. Type a new key here only if you want to replace it.");
                txtSearch.Focus();
            }
        }

        // v1.4: one shared Pixabay key store (see MediaProviderSettings.LoadPixabayKey)
        private string GetPixabayApiKey() => MediaProviderSettings.LoadPixabayKey();

        private void SavePixabayApiKey(string key) => MediaProviderSettings.SavePixabayKey(key);

        private void cmbMediaType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = cmbMediaType.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            _isVideoMode = selected.Tag?.ToString() == "video";

            // Show/hide video filters
            if (videoFilters != null)
                videoFilters.Visibility = _isVideoMode ? Visibility.Visible : Visibility.Collapsed;

            if (cmbColor != null)
                cmbColor.IsEnabled = !_isVideoMode;

            if (!string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                _ = SearchMedia();
            }
        }

        private async void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: a key typed in the field always wins and replaces the saved one
            if (!string.IsNullOrWhiteSpace(txtApiKey.Password))
            {
                _apiKey = txtApiKey.Password.Trim();
                SavePixabayApiKey(_apiKey);
                txtApiKey.Password = "";
            }

            if (string.IsNullOrEmpty(_apiKey))
            {
                WpfMessageBox.Show(L("psd_enter_key"),
                    L("psd_key_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                WpfMessageBox.Show("Enter a search term", "Search", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await SearchMedia();
        }

        private async Task SearchMedia()
        {
            try
            {
                lstResults.Items.Clear();
                lstResults.Items.Add(L("psd_searching_msg"));

                string orientation = GetSelectedTag(cmbOrientation, "all");
                string category = GetSelectedTag(cmbCategory, "");
                int minWidth = int.Parse(GetSelectedTag(cmbMinWidth, "0"));
                bool editorsChoice = chkEditorsChoice.IsChecked == true;

                string url;

                if (_isVideoMode)
                {
                    int minDuration = int.Parse(GetSelectedTag(cmbMinDuration, "0"));
                    string videoQuality = GetSelectedTag(cmbVideoQuality, "all");

                    url = $"https://pixabay.com/api/videos/?key={_apiKey}" +
                          $"&q={Uri.EscapeDataString(txtSearch.Text)}" +
                          $"&safesearch=true" +
                          $"&per_page=50" +
                          (minDuration > 0 ? $"&duration={minDuration}" : "") +
                          (editorsChoice ? "&editors_choice=true" : "") +
                          (!string.IsNullOrEmpty(category) ? $"&category={category}" : "");
                }
                else
                {
                    string color = GetSelectedTag(cmbColor, "");

                    url = $"https://pixabay.com/api/?key={_apiKey}" +
                          $"&q={Uri.EscapeDataString(txtSearch.Text)}" +
                          $"&image_type=photo" +
                          $"&orientation={orientation}" +
                          $"&safesearch=true" +
                          $"&per_page=50" +
                          $"&order=popular" +
                          (minWidth > 0 ? $"&min_width={minWidth}" : "") +
                          (editorsChoice ? "&editors_choice=true" : "") +
                          (!string.IsNullOrEmpty(category) ? $"&category={category}" : "") +
                          (!string.IsNullOrEmpty(color) ? $"&colors={color}" : "");
                }

                using (var client = new HttpClient())
                {
                    var response = await client.GetStringAsync(url);
                    var json = JObject.Parse(response);
                    var hits = json["hits"] as JArray;

                    lstResults.Items.Clear();
                    _currentResults = new List<PixabayMediaItem>();

                    if (hits != null && hits.Count > 0)
                    {
                        foreach (var hit in hits)
                        {
                            var item = new PixabayMediaItem();
                            item.Id = (int)hit["id"];
                            item.IsVideo = _isVideoMode;
                            item.Likes = (int)hit["likes"];
                            item.Views = (int)hit["views"];
                            item.Downloads = (int)hit["downloads"];
                            item.User = hit["user"]?.ToString();

                            if (_isVideoMode)
                            {
                                item.Title = hit["tags"]?.ToString() ?? "";
                                var videos = hit["videos"] as JObject;
                                if (videos != null)
                                {
                                    var smallVideo = videos["small"]?["url"]?.ToString();
                                    var mediumVideo = videos["medium"]?["url"]?.ToString();
                                    var largeVideo = videos["large"]?["url"]?.ToString();

                                    item.ThumbnailURL = hit["videos"]?["tiny"]?["thumbnail"]?.ToString() ?? "";
                                    item.DownloadURL = largeVideo ?? mediumVideo ?? smallVideo;
                                    item.Dimensions = $"{hit["width"]}x{hit["height"]}";

                                    if (hit["duration"] != null)
                                    {
                                        double duration = (double)hit["duration"];
                                        item.Duration = TimeSpan.FromSeconds(duration).ToString(@"mm\:ss");
                                    }
                                }
                            }
                            else
                            {
                                item.Title = hit["tags"]?.ToString() ?? "";
                                item.ThumbnailURL = hit["previewURL"]?.ToString();
                                item.DownloadURL = hit["largeImageURL"]?.ToString() ?? hit["webformatURL"]?.ToString();
                                item.Dimensions = $"{hit["imageWidth"]}x{hit["imageHeight"]}";
                                item.Duration = "";
                            }

                            _currentResults.Add(item);
                            lstResults.Items.Add(item);
                        }

                        AnnounceInfo(LF("psd_found_results", hits.Count));
                    }
                    else
                    {
                        lstResults.Items.Add(L("psd_no_results_msg"));
                        AnnounceInfo(L("psd_no_results_msg"));
                    }
                }
            }
            catch (Exception ex)
            {
                lstResults.Items.Clear();
                lstResults.Items.Add(LF("psd_error_item", ex.Message));
                AnnounceInfo(LF("generic_error", ex.Message));
            }
        }

        /// <summary>v1.4: search results / errors are spoken (the text used to change silently).</summary>
        private void AnnounceInfo(string message)
        {
            txtSelectedInfo.Text = message;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(txtSelectedInfo);
            peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }

        private string GetSelectedTag(ComboBox combo, string defaultValue)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag != null)
                return item.Tag.ToString();
            return defaultValue;
        }

        private void lstResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int selectedCount = lstResults.SelectedItems.Count;
            if (selectedCount > 0)
            {
                btnDownload.IsEnabled = true;
                btnDownload.Content = _isVideoMode ? $"📥 DOWNLOAD VIDEO ({selectedCount})" : $"📥 DOWNLOAD IMAGES ({selectedCount})";

                if (selectedCount == 1 && lstResults.SelectedItem is PixabayMediaItem item)
                {
                    string type = _isVideoMode ? "🎥 Video" : "📷 Image";
                    txtSelectedInfo.Text = $"{type}: {item.Title}\n👍 {item.Likes} likes | 👁️ {item.Views} views | 📥 {item.Downloads} downloads\n👤 Author: {item.User}\n📏 {item.Dimensions}";
                    if (!string.IsNullOrEmpty(item.Duration))
                        txtSelectedInfo.Text += $"\n⏱️ Duration: {item.Duration}";
                }
                else
                {
                    txtSelectedInfo.Text = $"✅ Selected {selectedCount} items. Press the download button.";
                }
            }
            else
            {
                btnDownload.IsEnabled = false;
                btnDownload.Content = _isVideoMode ? "📥 DOWNLOAD VIDEO" : "📥 DOWNLOAD IMAGES";
                txtSelectedInfo.Text = L("psd_select_hint");
            }
        }

        private async void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = lstResults.SelectedItems.Cast<PixabayMediaItem>().ToList();
            if (selectedItems.Count == 0)
            {
                WpfMessageBox.Show(L("psd_select_for_download"), L("warning"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                btnDownload.IsEnabled = false;
                btnDownload.Content = LanguageManager.F("loc_downloading_n", selectedItems.Count);

                string projectFolder = GetCurrentProjectFolder();
                DownloadedMediaPaths.Clear();
                int downloaded = 0;

                using (var client = new HttpClient())
                {
                    foreach (var item in selectedItems)
                    {
                        string extension = _isVideoMode ? ".mp4" : ".jpg";
                        string fileName = $"Pixabay_{item.Id}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}{extension}";
                        string fullPath = Path.Combine(projectFolder, fileName);

                        var mediaData = await client.GetByteArrayAsync(item.DownloadURL);
                        await File.WriteAllBytesAsync(fullPath, mediaData);
                        DownloadedMediaPaths.Add(fullPath);
                        downloaded++;

                        btnDownload.Content = $"⏳ Preuzeto {downloaded}/{selectedItems.Count}...";
                    }
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LF("psd_download_error2", ex.Message), L("error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                btnDownload.IsEnabled = true;
                btnDownload.Content = _isVideoMode ? "📥 DOWNLOAD VIDEO" : "📥 DOWNLOAD IMAGES";
            }
        }

        private string GetCurrentProjectFolder()
        {
            if (WpfApplication.Current.MainWindow is MainWindow mainWin)
            {
                return mainWin.GetCurrentProjectFolder();
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                btnSearch_Click(null, null);
            }
        }
    }

    public class PixabayMediaItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string ThumbnailURL { get; set; }
        public string DownloadURL { get; set; }
        public string Dimensions { get; set; }
        public string Duration { get; set; }
        public int Likes { get; set; }
        public int Views { get; set; }
        public int Downloads { get; set; }
        public string User { get; set; }
        public bool IsVideo { get; set; }

        // v1.4: list items use ToString() as their screen-reader name (otherwise the class name is read)
        public override string ToString() =>
            $"{(IsVideo ? "Video" : "Image")}: {Title}" +
            (string.IsNullOrEmpty(Duration) ? "" : $", {Duration}") +
            (string.IsNullOrEmpty(Dimensions) ? "" : $", {Dimensions}") +
            $", {Likes} likes, {Downloads} downloads, author {User}";
    
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is Visibility visibility && visibility == Visibility.Visible;
        }
    }
}