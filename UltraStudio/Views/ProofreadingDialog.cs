using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using UltraStudio.Localization;
using UltraStudio.Services;
using WF = System.Windows.Forms;
using WFI = System.Windows.Forms.Integration;

namespace UltraStudio.Views
{
    /// <summary>
    /// Jedan prozor za lekturu — koristi se i za tekst jednog sloja (kratak
    /// tekst) i za ceo uvezeni dokument (dug tekst), isti tok u oba slučaja:
    /// pokreni analizu -> pregledaj predloge -> primeni izabrano/sve/AI
    /// prepisanu verziju -> OK vraća konačan tekst pozivaocu.
    /// Lista predloga je native WinForms ListView (isti razlog kao svuda u
    /// Ultra Studiu — pouzdanije čitanje sa JAWS-om od običnog WPF ListBox-a),
    /// dok je sam tekst obična pristupačna WPF TextBox (multiline edit polja
    /// JAWS čita nativno dobro, tu WindowsFormsHost nije potreban).
    /// </summary>
    public partial class ProofreadingDialog : Window
    {
        private readonly ProofreadingClient _client = new();
        private ProofreadingResult? _lastResult;

        private readonly TextBox _textBox;
        private readonly TextBlock _status;
        private readonly Button _btnRun, _btnCancel, _btnApplySelected, _btnApplyAll, _btnUseRewrite, _btnOk;
        private readonly UltraStudio.Controls.TabAwareListView _issueList;
        private bool _proofBusy; // zamena za _btnRun.IsEnabled=false — videti BtnRun_Click
        private CancellationTokenSource? _proofCts;

        public string ResultText { get; private set; } = "";

        public ProofreadingDialog(string initialText, string title)
        {
            Title = title;
            Width = 640; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgDark"];

            var root = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txtLbl = Label(Lang.T("proof_text_label"));
            Grid.SetRow(txtLbl, 0);
            root.Children.Add(txtLbl);

            _textBox = new TextBox
            {
                Text = initialText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 160,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _textBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, Lang.T("proof_text_label"));
            Grid.SetRow(_textBox, 1);
            root.Children.Add(_textBox);

            var runRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _btnRun = new Button { Content = Lang.T("proof_run"), Width = 150, Height = 30, Style = (Style)Application.Current.Resources["AIButton"] };
            _btnRun.Click += BtnRun_Click;
            _btnCancel = new Button { Content = Lang.T("proof_cancel"), Width = 90, Height = 30, Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)Application.Current.Resources["StdButton"], Visibility = Visibility.Collapsed };
            _btnCancel.Click += (_, __) => _proofCts?.Cancel();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrTextMuted"] };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
            runRow.Children.Add(_btnRun); runRow.Children.Add(_btnCancel); runRow.Children.Add(_status);
            Grid.SetRow(runRow, 2);
            root.Children.Add(runRow);

            var issuesLbl = Label(Lang.T("proof_issues_label"));
            Grid.SetRow(issuesLbl, 3);
            root.Children.Add(issuesLbl);

            _issueList = new UltraStudio.Controls.TabAwareListView
            {
                View = WF.View.Details, FullRowSelect = true, GridLines = false, HideSelection = false, MultiSelect = false,
                BackColor = System.Drawing.Color.FromArgb(20, 20, 34), ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Segoe UI", 9)
            };
            _issueList.Columns.Add(Lang.T("proof_col_type"), 70);
            _issueList.Columns.Add(Lang.T("proof_col_original"), 150);
            _issueList.Columns.Add(Lang.T("proof_col_suggestion"), 150);
            _issueList.Columns.Add(Lang.T("proof_col_explanation"), 160);
            _issueList.AccessibleName = Lang.T("proof_issues_label");
            _issueList.HandleCreated += (s, e) => NativeTheme.DisableListViewHeaderTheme(_issueList);

            var wfHost = new WFI.WindowsFormsHost { Height = 200, Margin = new Thickness(0, 0, 0, 8), Child = _issueList };
            _issueList.Host = wfHost;
            Grid.SetRow(wfHost, 4);
            root.Children.Add(wfHost);

            var applyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            _btnApplySelected = new Button { Content = Lang.T("proof_apply_selected"), Width = 150, Height = 28, Margin = new Thickness(0, 0, 6, 0), Style = (Style)Application.Current.Resources["StdButton"] };
            _btnApplyAll = new Button { Content = Lang.T("proof_apply_all"), Width = 130, Height = 28, Margin = new Thickness(0, 0, 6, 0), Style = (Style)Application.Current.Resources["StdButton"] };
            _btnUseRewrite = new Button { Content = Lang.T("proof_use_rewrite"), Width = 170, Height = 28, Style = (Style)Application.Current.Resources["StdButton"] };
            _btnApplySelected.Click += (_, __) => ApplyIssue(SelectedIssue());
            _btnApplyAll.Click += (_, __) => { foreach (var i in _issueList.Items) ApplyIssueItem((WF.ListViewItem)i); RefreshIssueList(keepText: true); };
            _btnUseRewrite.Click += (_, __) => { if (_lastResult != null) _textBox.Text = _lastResult.Rewritten; };
            applyRow.Children.Add(_btnApplySelected); applyRow.Children.Add(_btnApplyAll); applyRow.Children.Add(_btnUseRewrite);
            Grid.SetRow(applyRow, 5);
            root.Children.Add(applyRow);

            var okRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _btnOk = new Button { Content = Lang.T("btn_apply"), Width = 100, Height = 30, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["AIButton"], IsDefault = true };
            var cancel = new Button { Content = Lang.T("btn_cancel"), Width = 100, Height = 30, Style = (Style)Application.Current.Resources["StdButton"], IsCancel = true };
            _btnOk.Click += (_, __) => { ResultText = _textBox.Text; DialogResult = true; };
            okRow.Children.Add(_btnOk); okRow.Children.Add(cancel);
            Grid.SetRow(okRow, 6);
            root.Children.Add(okRow);

            Content = root;
            Closed += (_, __) => _proofCts?.Cancel();
        }

        private TextBlock Label(string text) => new TextBlock
        {
            Text = text, Margin = new Thickness(0, 6, 0, 4), FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"]
        };

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            // NAMERNO se ne koristi _btnRun.IsEnabled=false ovde — isti razlog kao
            // svuda drugde u Ultra Studiu: onesposobljavanje dugmeta koje TRENUTNO
            // ima fokus tera WPF da SAM premesti fokus, što je u kombinaciji sa
            // WindowsFormsHost listom (_issueList) ranije pravilo zaglavljivanje
            // tastature. Obična bool zastavica daje isti efekat bez diranja fokusa.
            if (_proofBusy) return;
            _proofBusy = true;
            _btnCancel.Visibility = Visibility.Visible;

            _proofCts?.Dispose();
            _proofCts = new CancellationTokenSource();
            SetStatusLive(Lang.T("proof_running"));

            try
            {
                _lastResult = await _client.ProofreadAsync(
                    _textBox.Text,
                    onProgress: (chunkIndex, total, text) =>
                        Dispatcher.Invoke(() => SetStatusLive(text)),
                    ct: _proofCts.Token);

                RefreshIssueList(keepText: true);
                SetStatusLive(_lastResult.Issues.Count > 0
                    ? string.Format(Lang.T("proof_found_issues"), _lastResult.Issues.Count)
                    : Lang.T("proof_no_issues"));
            }
            catch (OperationCanceledException)
            {
                SetStatusLive(Lang.T("proof_cancelled"));
            }
            catch (Exception ex)
            {
                SetStatusLive(string.Format(Lang.T("ai_error"), ex.Message));
            }
            finally
            {
                _proofBusy = false;
                _btnCancel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// AutomationProperties.LiveSetting u XAML-u/kodu SAM ne najavljuje ništa
        /// — tek eksplicitno podizanje LiveRegionChanged čini da JAWS/NVDA
        /// pročitaju promenu (isti obrazac kao svuda drugde u Ultra Studiu).
        /// Bez ovoga, korak-po-korak progres ("Analiziram deo 2 od 5...") bi se
        /// menjao tiho — tačno ono što je traženo da se popravi.
        /// </summary>
        private void SetStatusLive(string text)
        {
            _status.Text = text;
            var peer = UIElementAutomationPeer.FromElement(_status) ?? UIElementAutomationPeer.CreatePeerForElement(_status);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void RefreshIssueList(bool keepText)
        {
            _issueList.Items.Clear();
            if (_lastResult == null) return;

            foreach (var issue in _lastResult.Issues)
            {
                var lvi = new WF.ListViewItem(new[]
                {
                    Lang.T(issue.Type switch { "grammar" => "proof_type_grammar", "style" => "proof_type_style", _ => "proof_type_spelling" }),
                    issue.Original, issue.Suggestion, issue.Explanation
                })
                { Tag = issue };
                _issueList.Items.Add(lvi);
            }
        }

        private WF.ListViewItem? SelectedIssue() =>
            _issueList.SelectedItems.Count > 0 ? _issueList.SelectedItems[0] : null;

        private void ApplyIssue(WF.ListViewItem? item)
        {
            if (item == null) return;
            ApplyIssueItem(item);
            RefreshIssueList(keepText: true);
        }

        // Prosta zamena PRVOG pojavljivanja — dovoljno pouzdano jer AI vraća
        // kratke, karakteristične fraze kao "original", ne cele rečenice.
        private void ApplyIssueItem(WF.ListViewItem item)
        {
            if (item.Tag is not ProofreadingIssue issue) return;
            if (string.IsNullOrEmpty(issue.Original)) return;

            int idx = _textBox.Text.IndexOf(issue.Original, StringComparison.Ordinal);
            if (idx >= 0)
                _textBox.Text = _textBox.Text[..idx] + issue.Suggestion + _textBox.Text[(idx + issue.Original.Length)..];
        }
    }
}
