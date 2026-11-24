using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ApexUninstaller.Models;
using ApexUninstaller.Services;

namespace ApexUninstaller
{
    public enum AppDisplayMode
    {
        Apps,
        Autoruns,
        Everything
    }

    public partial class MainWindow : Window
    {
        private readonly InstalledAppsScanner _scanner;
        private readonly LeftoverScanner _leftoverScanner;
        private readonly UninstallerService _uninstallerService;
        private readonly ForceRemovalService _forceRemovalService;
        private readonly AutorunsScanner _autorunsScanner;

        private List<InstalledApp> _allApps = new List<InstalledApp>();
        private ObservableCollection<InstalledApp> _displayedApps = new ObservableCollection<InstalledApp>();
        private AppFilterType _currentFilter = AppFilterType.All;
        private bool _isLoading = false;
        private bool _isInitialized = false;

        private List<AutorunItem> _allAutoruns = new List<AutorunItem>();
        private ObservableCollection<AutorunItem> _displayedAutoruns = new ObservableCollection<AutorunItem>();
        private AutorunCategory _currentAutorunCategory = AutorunCategory.All;
        private bool _hideMicrosoft = true;
        private bool _hideMissingFiles = true;
        private bool _isAutorunsLoaded = false;

        private AppDisplayMode _currentMode = AppDisplayMode.Apps;
        private bool _isAutorunsMode { get { return _currentMode == AppDisplayMode.Autoruns; } }
        private bool _isSearchMode { get { return _currentMode == AppDisplayMode.Everything; } }

        private List<SearchResultItem> _allSearchResults = new List<SearchResultItem>();
        private ObservableCollection<SearchResultItem> _displayedSearchResults = new ObservableCollection<SearchResultItem>();
        private DispatcherTimer _searchDebounceTimer;
        private CancellationTokenSource _searchCts;
        private string _currentSearchFilter = "all";
        private string _searchSortColumn = "Name";
        private ListSortDirection _searchSortDirection = ListSortDirection.Ascending;

        private string _lastAutorunSortColumn = "EntryName";
        private ListSortDirection _lastAutorunSortDirection = ListSortDirection.Ascending;

        private string _lastSortColumn = "DisplayName";
        private ListSortDirection _lastSortDirection = ListSortDirection.Ascending;

        public MainWindow()
        {
            ThemeHelper.ApplyDarkTitleBar(this);
            InitializeComponent();

            _scanner = new InstalledAppsScanner();
            _leftoverScanner = new LeftoverScanner();
            _uninstallerService = new UninstallerService();
            _forceRemovalService = new ForceRemovalService();
            _autorunsScanner = new AutorunsScanner();

            AppsListView.ItemsSource = _displayedApps;
            AutorunsListView.ItemsSource = _displayedAutoruns;
            SearchListView.ItemsSource = _displayedSearchResults;
            SmoothScrollHelper.Register(AppsListView);
            SmoothScrollHelper.Register(AutorunsListView);
            SmoothScrollHelper.Register(SearchListView);

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            PreviewKeyDown += MainWindow_PreviewKeyDown;
            _isInitialized = true;
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var op = Dispatcher.BeginInvoke(new Action(() =>
            {
                RadioButton targetMode = ModeAppsRadio;
                if (_isSearchMode) targetMode = ModeSearchRadio;
                else if (_isAutorunsMode) targetMode = ModeAutorunsRadio;
                AnimatePillIndicator(ModeIndicator, ModeIndicatorTranslate, targetMode, ModeGrid, false);
                AnimatePillIndicator(FilterIndicator, FilterIndicatorTranslate, GetActiveFilterRadio(), FilterGrid, false);
                AnimatePillIndicator(AutorunFilterIndicator, AutorunFilterIndicatorTranslate, AutorunAllRadio, AutorunFilterGrid, false);
                AnimatePillIndicator(SearchFilterIndicator, SearchFilterIndicatorTranslate, GetActiveSearchFilterRadio(), SearchFilterGrid, false);
                AnimatePillIndicator(LangIndicator, LangIndicatorTranslate, GetActiveLangRadio(), LangGrid, false);
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            EverythingSearchService.WarmUpNativeIndex();
            await LoadAppsAsync();
            AdjustColumnWidths();
            AdjustAutorunsColumnWidths();
            AdjustSearchColumnWidths();
        }

        private void AppsListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AdjustColumnWidths();
        }

        private double GetScrollbarReservation(ListView listView)
        {
            if (listView == null) return 2;
            var sv = FindVisualChild<ScrollViewer>(listView);
            if (sv != null && sv.ScrollableHeight > 0)
            {
                return 8;
            }
            return 2;
        }

        private void AdjustColumnWidths()
        {
            if (ColNameView != null && ColSizeView != null && ColDateView != null && ColVersionView != null && ColTypeView != null && AppsListView != null)
            {
                double others = ColSizeView.Width + ColDateView.Width + ColVersionView.Width + ColTypeView.Width;
                double available = AppsListView.ActualWidth - others - GetScrollbarReservation(AppsListView);
                if (available > 100)
                {
                    ColNameView.Width = available;
                }
            }
        }

        private async Task LoadAppsAsync()
        {
            if (_isLoading) return;
            _isLoading = true;

            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.IsIndeterminate = true;
            StatusSelectedInfo.Text = AppLang.Instance.StatusScanning;
            RefreshBtn.IsEnabled = false;

            var progress = new Progress<int>(pct =>
            {
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Value = pct;
            });

            try
            {
                var apps = await _scanner.ScanAllAsync(progress);
                _allApps = apps;
                if (_currentMode == AppDisplayMode.Apps)
                {
                    ApplyFilterAndSearch();
                    UpdateStats();
                    UpdateSelectionUI();
                    PlayAppsAppearanceAnimation();
                }
            }
            catch (Exception ex)
            {
                StatusSelectedInfo.Text = "Error: " + ex.Message;
            }
            finally
            {
                ScanProgressBar.Visibility = Visibility.Collapsed;
                RefreshBtn.IsEnabled = true;
                _isLoading = false;
            }
        }

        private void UpdateStats()
        {
            if (StatusStatsText == null || _currentMode != AppDisplayMode.Apps) return;

            int total = _allApps.Count;
            long totalBytes = _allApps.Sum(a => a.EstimatedSizeBytes);

            string sizeStr;
            if (totalBytes > 1024 * 1024 * 1024)
            {
                sizeStr = string.Format("{0:0.##} " + (AppLang.Instance.IsRussian ? "ГБ" : "GB"), totalBytes / (1024.0 * 1024.0 * 1024.0));
            }
            else
            {
                sizeStr = string.Format("{0:0.#} " + (AppLang.Instance.IsRussian ? "МБ" : "MB"), totalBytes / (1024.0 * 1024.0));
            }

            string label = AppLang.Instance.IsRussian ? "программ" : "apps";
            StatusStatsText.Text = string.Format("{0} {1} • {2}", total, label, sizeStr);
        }

        private void ApplyFilterAndSearch()
        {
            if (!_isInitialized || SearchBox == null || SearchPlaceholder == null || ClearSearchBtn == null)
                return;

            string query = (SearchBox.Text ?? "").Trim();
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

            var filtered = _allApps.AsEnumerable();

            switch (_currentFilter)
            {
                case AppFilterType.Win32:
                    filtered = filtered.Where(a => !a.IsUwp);
                    break;
                case AppFilterType.Store:
                    filtered = filtered.Where(a => a.IsUwp);
                    break;
            }

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(a =>
                    (a.DisplayName != null && a.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Publisher != null && a.Publisher.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.InstallLocation != null && a.InstallLocation.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            _displayedApps.Clear();
            foreach (var app in filtered)
            {
                _displayedApps.Add(app);
            }

            SortData(_lastSortColumn, _lastSortDirection);
        }

        private void ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = sender as GridViewColumnHeader;
            if (header == null || header.Tag == null) return;

            string sortBy = header.Tag.ToString();
            ListSortDirection direction = ListSortDirection.Ascending;

            if (_lastSortColumn == sortBy)
            {
                direction = _lastSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }

            _lastSortColumn = sortBy;
            _lastSortDirection = direction;

            SortData(sortBy, direction);
        }

        private void SortData(string sortBy, ListSortDirection direction)
        {
            ICollectionView dataView = CollectionViewSource.GetDefaultView(AppsListView.ItemsSource);
            if (dataView != null)
            {
                dataView.SortDescriptions.Clear();
                dataView.SortDescriptions.Add(new SortDescription(sortBy, direction));
                dataView.Refresh();
            }
            UpdateSortGlyph(sortBy, direction);
        }

        private static readonly Geometry ChevronUpGeom = Geometry.Parse("M 1.5,5 L 4.5,2 L 7.5,5");
        private static readonly Geometry ChevronDownGeom = Geometry.Parse("M 1.5,2 L 4.5,5 L 7.5,2");

        private void UpdateSortGlyph(string sortBy, ListSortDirection direction)
        {
            Geometry geom = direction == ListSortDirection.Ascending ? ChevronUpGeom : ChevronDownGeom;

            if (SortArrowName != null) { SortArrowName.Visibility = Visibility.Collapsed; }
            if (SortArrowSize != null) { SortArrowSize.Visibility = Visibility.Collapsed; }
            if (SortArrowDate != null) { SortArrowDate.Visibility = Visibility.Collapsed; }
            if (SortArrowVersion != null) { SortArrowVersion.Visibility = Visibility.Collapsed; }
            if (SortArrowType != null) { SortArrowType.Visibility = Visibility.Collapsed; }

            switch (sortBy)
            {
                case "DisplayName":
                    if (SortArrowName != null) { SortArrowName.Data = geom; SortArrowName.Visibility = Visibility.Visible; }
                    break;
                case "EstimatedSizeBytes":
                    if (SortArrowSize != null) { SortArrowSize.Data = geom; SortArrowSize.Visibility = Visibility.Visible; }
                    break;
                case "InstallDate":
                    if (SortArrowDate != null) { SortArrowDate.Data = geom; SortArrowDate.Visibility = Visibility.Visible; }
                    break;
                case "DisplayVersion":
                    if (SortArrowVersion != null) { SortArrowVersion.Data = geom; SortArrowVersion.Visibility = Visibility.Visible; }
                    break;
                case "Architecture":
                    if (SortArrowType != null) { SortArrowType.Data = geom; SortArrowType.Visibility = Visibility.Visible; }
                    break;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;

            string query = (SearchBox.Text ?? "").Trim();
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

            if (_isSearchMode)
            {
                if (_searchDebounceTimer != null)
                {
                    _searchDebounceTimer.Stop();
                }

                if (_searchCts != null)
                {
                    _searchCts.Cancel();
                    _searchCts.Dispose();
                    _searchCts = null;
                }

                if (string.IsNullOrEmpty(query))
                {
                    _allSearchResults.Clear();
                    _displayedSearchResults = new ObservableCollection<SearchResultItem>();
                    if (SearchListView != null) SearchListView.ItemsSource = _displayedSearchResults;

                    if (SearchEmptyState != null)
                    {
                        SearchEmptyState.Visibility = Visibility.Visible;
                    }
                    if (SearchEmptyStateText != null)
                    {
                        SearchEmptyStateText.Text = AppLang.Instance.IsRussian ? "Введите запрос для поиска..." : "Enter query to search...";
                    }
                    UpdateSearchStats();
                    UpdateSearchSelectionUI();
                }
                else
                {
                    if (SearchEmptyState != null)
                    {
                        SearchEmptyState.Visibility = Visibility.Collapsed;
                    }
                    _searchDebounceTimer.Start();
                }
            }
            else if (_isAutorunsMode)
            {
                ApplyAutorunsFilterAndSearch();
            }
            else
            {
                ApplyFilterAndSearch();
            }
        }

        private void ClearSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            if (_isSearchMode)
            {
                if (_searchCts != null)
                {
                    _searchCts.Cancel();
                    _searchCts.Dispose();
                    _searchCts = null;
                }
                _allSearchResults.Clear();
                _displayedSearchResults = new ObservableCollection<SearchResultItem>();
                if (SearchListView != null) SearchListView.ItemsSource = _displayedSearchResults;

                if (SearchEmptyState != null)
                {
                    SearchEmptyState.Visibility = Visibility.Visible;
                }
                if (SearchEmptyStateText != null)
                {
                    SearchEmptyStateText.Text = AppLang.Instance.IsRussian ? "Введите запрос для поиска..." : "Enter query to search...";
                }
                UpdateSearchStats();
                UpdateSearchSelectionUI();
            }
            SearchBox.Focus();
        }

        private void FilterRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            if (FilterAllRadio != null && FilterAllRadio.IsChecked == true)
                _currentFilter = AppFilterType.All;
            else if (FilterWin32Radio != null && FilterWin32Radio.IsChecked == true)
                _currentFilter = AppFilterType.Win32;
            else if (FilterStoreRadio != null && FilterStoreRadio.IsChecked == true)
                _currentFilter = AppFilterType.Store;

            AnimatePillIndicator(FilterIndicator, FilterIndicatorTranslate, sender as RadioButton, FilterGrid, true);
            ApplyFilterAndSearch();

            var sv = FindVisualChild<ScrollViewer>(AppsListView);
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(0);
            }

            PlayAppsAppearanceAnimation();
        }

        private ItemsPresenter _appsItemsPresenter;

        private ItemsPresenter GetItemsPresenter()
        {
            if (_appsItemsPresenter != null) return _appsItemsPresenter;
            if (AppsListView == null) return null;
            _appsItemsPresenter = FindVisualChild<ItemsPresenter>(AppsListView);
            return _appsItemsPresenter;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var typed = child as T;
                if (typed != null) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void PlayAppsAppearanceAnimation()
        {
            var ip = GetItemsPresenter();
            if (ip == null) return;

            var trans = ip.RenderTransform as TranslateTransform;
            if (trans == null)
            {
                trans = new TranslateTransform(0, 8);
                ip.RenderTransform = trans;
            }
            else
            {
                trans.BeginAnimation(TranslateTransform.YProperty, null);
                trans.Y = 8;
            }

            ip.BeginAnimation(UIElement.OpacityProperty, null);
            ip.Opacity = 0.0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fadeAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = ease
            };

            var slideAnim = new DoubleAnimation
            {
                From = 8.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = ease
            };

            ip.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            trans.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private async void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            AnimatePillIndicator(ModeIndicator, ModeIndicatorTranslate, sender as RadioButton, ModeGrid, true);

            if (ModeSearchRadio != null && ModeSearchRadio.IsChecked == true)
            {
                _currentMode = AppDisplayMode.Everything;
                EverythingSearchService.WarmUpNativeIndex();

                AppsSubBar.Visibility = Visibility.Collapsed;
                AutorunsSubBar.Visibility = Visibility.Collapsed;
                SearchSubBar.Visibility = Visibility.Visible;

                AppsListView.Visibility = Visibility.Collapsed;
                AutorunsListView.Visibility = Visibility.Collapsed;
                SearchListView.Visibility = Visibility.Visible;

                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholderEverything;

                var opSearch = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AnimatePillIndicator(SearchFilterIndicator, SearchFilterIndicatorTranslate, GetActiveSearchFilterRadio(), SearchFilterGrid, false);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                string query = (SearchBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(query))
                {
                    await PerformEverythingSearchAsync(query);
                }
                else
                {
                    SearchEmptyState.Visibility = _displayedSearchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (SearchEmptyStateText != null)
                    {
                        SearchEmptyStateText.Text = AppLang.Instance.IsRussian ? "Введите запрос для поиска..." : "Enter query to search...";
                    }
                    UpdateSearchStats();
                }
                UpdateSearchSelectionUI();
                AdjustSearchColumnWidths();
            }
            else if (ModeAutorunsRadio != null && ModeAutorunsRadio.IsChecked == true)
            {
                _currentMode = AppDisplayMode.Autoruns;

                AppsSubBar.Visibility = Visibility.Collapsed;
                SearchSubBar.Visibility = Visibility.Collapsed;
                AutorunsSubBar.Visibility = Visibility.Visible;

                AppsListView.Visibility = Visibility.Collapsed;
                SearchListView.Visibility = Visibility.Collapsed;
                AutorunsListView.Visibility = Visibility.Visible;

                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholder;
                SearchEmptyState.Visibility = Visibility.Collapsed;

                var op1 = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AnimatePillIndicator(AutorunFilterIndicator, AutorunFilterIndicatorTranslate, GetActiveAutorunFilterRadio(), AutorunFilterGrid, false);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                if (!_isAutorunsLoaded)
                {
                    await LoadAutorunsAsync();
                }
                else
                {
                    ApplyAutorunsFilterAndSearch();
                    UpdateAutorunsStats();
                    UpdateAutorunsSelectionUI();
                    PlayAutorunsAppearanceAnimation();
                }
                AdjustAutorunsColumnWidths();
            }
            else
            {
                _currentMode = AppDisplayMode.Apps;

                AutorunsSubBar.Visibility = Visibility.Collapsed;
                SearchSubBar.Visibility = Visibility.Collapsed;
                AppsSubBar.Visibility = Visibility.Visible;

                AutorunsListView.Visibility = Visibility.Collapsed;
                SearchListView.Visibility = Visibility.Collapsed;
                AppsListView.Visibility = Visibility.Visible;

                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholder;
                SearchEmptyState.Visibility = Visibility.Collapsed;

                var op2 = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AnimatePillIndicator(FilterIndicator, FilterIndicatorTranslate, GetActiveFilterRadio(), FilterGrid, false);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                ApplyFilterAndSearch();
                UpdateStats();
                UpdateSelectionUI();
                PlayAppsAppearanceAnimation();
                AdjustColumnWidths();
            }
        }

        private void LanguageRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            if (LangRuRadio != null && LangRuRadio.IsChecked == true)
            {
                AppLang.Instance.CurrentLanguage = AppLanguage.Russian;
            }
            else if (LangEnRadio != null && LangEnRadio.IsChecked == true)
            {
                AppLang.Instance.CurrentLanguage = AppLanguage.English;
            }

            AnimatePillIndicator(LangIndicator, LangIndicatorTranslate, sender as RadioButton, LangGrid, true);

            foreach (var a in _allApps)
            {
                a.RefreshDisplay();
            }

            if (_isSearchMode)
            {
                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholderEverything;
                UpdateSearchStats();
                UpdateSearchSelectionUI();
                ApplySearchFilterAndSort();
            }
            else if (_isAutorunsMode)
            {
                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholder;
                UpdateAutorunsStats();
                UpdateAutorunsSelectionUI();
                ApplyAutorunsFilterAndSearch();
            }
            else
            {
                SearchPlaceholder.Text = AppLang.Instance.SearchPlaceholder;
                UpdateStats();
                UpdateSelectionUI();
                ApplyFilterAndSearch();
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RadioButton targetModeRadio = ModeAppsRadio;
                if (_isSearchMode) targetModeRadio = ModeSearchRadio;
                else if (_isAutorunsMode) targetModeRadio = ModeAutorunsRadio;
                AnimatePillIndicator(ModeIndicator, ModeIndicatorTranslate, targetModeRadio, ModeGrid, false);

                if (_isSearchMode)
                {
                    AnimatePillIndicator(SearchFilterIndicator, SearchFilterIndicatorTranslate, GetActiveSearchFilterRadio(), SearchFilterGrid, false);
                }
                else if (_isAutorunsMode)
                {
                    AnimatePillIndicator(AutorunFilterIndicator, AutorunFilterIndicatorTranslate, GetActiveAutorunFilterRadio(), AutorunFilterGrid, false);
                }
                else
                {
                    AnimatePillIndicator(FilterIndicator, FilterIndicatorTranslate, GetActiveFilterRadio(), FilterGrid, false);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private RadioButton GetActiveFilterRadio()
        {
            if (FilterWin32Radio != null && FilterWin32Radio.IsChecked == true) return FilterWin32Radio;
            if (FilterStoreRadio != null && FilterStoreRadio.IsChecked == true) return FilterStoreRadio;
            return FilterAllRadio;
        }

        private RadioButton GetActiveLangRadio()
        {
            if (LangEnRadio != null && LangEnRadio.IsChecked == true) return LangEnRadio;
            return LangRuRadio;
        }

        private void AnimatePillIndicator(Border indicator, TranslateTransform translate, RadioButton targetRadio, UIElement parentContainer, bool animate)
        {
            if (indicator == null || translate == null || targetRadio == null || parentContainer == null) return;

            targetRadio.UpdateLayout();
            double targetWidth = targetRadio.ActualWidth;
            if (targetWidth <= 0) return;

            Point pos = targetRadio.TranslatePoint(new Point(0, 0), parentContainer);
            double targetX = pos.X;

            if (!animate)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                indicator.BeginAnimation(FrameworkElement.WidthProperty, null);
                translate.X = targetX;
                indicator.Width = targetWidth;
            }
            else
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var animX = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
                var animW = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };

                translate.BeginAnimation(TranslateTransform.XProperty, animX);
                indicator.BeginAnimation(FrameworkElement.WidthProperty, animW);
            }
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshIconRotate != null)
            {
                var rotateAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(650))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                };
                RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
            }

            if (_isSearchMode)
            {
                EverythingSearchService.InvalidateNativeIndex();
                string query = (SearchBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(query))
                {
                    await PerformEverythingSearchAsync(query);
                }
            }
            else if (_isAutorunsMode)
            {
                await LoadAutorunsAsync();
            }
            else
            {
                await LoadAppsAsync();
            }
        }

        private void AppsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            UpdateSelectionUI();
        }

        private void UpdateSelectionUI()
        {
            if (!_isInitialized || _currentMode != AppDisplayMode.Apps) return;
            var selectedApps = AppsListView.SelectedItems.OfType<InstalledApp>().ToList();
            int count = selectedApps.Count;
            bool hasSelection = count > 0;

            if (NoSelectionPanel != null)
            {
                NoSelectionPanel.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            }

            if (SelectedAppPanel != null)
            {
                SelectedAppPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            }

            bool isRu = AppLang.Instance.IsRussian;

            if (count == 1)
            {
                var app = selectedApps[0];
                if (UninstallBtn != null)
                    UninstallBtn.Content = AppLang.Instance.BtnUninstall;
                if (ForceUninstallBtn != null)
                    ForceUninstallBtn.Content = AppLang.Instance.BtnForce;

                if (OpenFolderBtn != null)
                {
                    OpenFolderBtn.Visibility = Visibility.Visible;
                    OpenFolderBtn.IsEnabled = true;
                }
                if (OpenRegeditBtn != null)
                {
                    OpenRegeditBtn.Visibility = Visibility.Visible;
                    OpenRegeditBtn.IsEnabled = !app.IsUwp && !string.IsNullOrWhiteSpace(app.RegistryKeyPath);
                }
                if (SearchGoogleBtn != null)
                {
                    SearchGoogleBtn.Visibility = Visibility.Visible;
                }

                string info = app.DisplayName;
                if (!string.IsNullOrWhiteSpace(app.DisplayVersion))
                {
                    info += " v" + app.DisplayVersion;
                }
                if (!string.IsNullOrWhiteSpace(app.InstallLocation))
                {
                    info += "  •  " + app.InstallLocation;
                }
                StatusSelectedInfo.Text = info;
            }
            else if (count > 1)
            {
                long totalBytes = selectedApps.Sum(a => a.EstimatedSizeBytes);
                string totalStr = InstalledApp.FormatBytes(totalBytes);

                if (UninstallBtn != null)
                    UninstallBtn.Content = (isRu ? "Удалить (" : "Uninstall (") + count + ")";
                if (ForceUninstallBtn != null)
                    ForceUninstallBtn.Content = (isRu ? "Принудительно (" : "Force (") + count + ")";

                if (OpenFolderBtn != null) OpenFolderBtn.Visibility = Visibility.Collapsed;
                if (OpenRegeditBtn != null) OpenRegeditBtn.Visibility = Visibility.Collapsed;
                if (SearchGoogleBtn != null) SearchGoogleBtn.Visibility = Visibility.Collapsed;

                StatusSelectedInfo.Text = isRu
                    ? string.Format("Выбрано программ: {0}  •  Общий объем: {1}", count, totalStr)
                    : string.Format("Selected programs: {0}  •  Total footprint: {1}", count, totalStr);
            }
            else
            {
                StatusSelectedInfo.Text = "";
            }
        }

        private void AppsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AppsListView.SelectedItem is InstalledApp)
            {
                UninstallBtn_Click(sender, e);
            }
        }

        private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = AppsListView.SelectedItems.OfType<InstalledApp>().ToList();
            if (selectedApps.Count == 0) return;

            bool isRu = AppLang.Instance.IsRussian;

            if (selectedApps.Count == 1)
            {
                var app = selectedApps[0];
                string title = isRu ? "Удаление программы" : "Uninstall Application";
                string prompt = isRu
                    ? string.Format("Запустить стандартное удаление \"{0}\"?\nПосле удаления будет выполнен поиск и очистка всех оставшихся файлов и ключей реестра.", app.DisplayName)
                    : string.Format("Run standard uninstall for \"{0}\"?\nLeftover files and registry traces will be cleaned up automatically after uninstallation.", app.DisplayName);
                string btnText = isRu ? "Удалить" : "Uninstall";

                var confirm = new ConfirmDialog(title, prompt, btnText, false);
                confirm.Owner = this;

                if (confirm.ShowDialog() != true) return;

                await RunSingleUninstallAsync(app);
            }
            else
            {
                string title = isRu ? "Пакетное удаление программ" : "Batch Uninstall";
                string prompt = isRu
                    ? string.Format("Будет выполнено последовательное удаление выбранных программ ({0} шт.).\nПосле деинсталляции каждой программы будет выполнен поиск и автоматическая очистка оставшихся следов.\n\nНачать пакетное удаление?", selectedApps.Count)
                    : string.Format("Sequential uninstallation of {0} selected applications will be performed, followed by automatic leftover trace removal.\n\nStart batch uninstall?", selectedApps.Count);
                string btnText = (isRu ? "Удалить все (" : "Uninstall All (") + selectedApps.Count + ")";

                var confirm = new ConfirmDialog(title, prompt, btnText, true);
                confirm.Owner = this;
                if (confirm.ShowDialog() != true) return;

                ScanProgressBar.Visibility = Visibility.Visible;
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Maximum = selectedApps.Count;
                ScanProgressBar.Value = 0;

                var allLeftovers = new List<LeftoverItem>();

                for (int i = 0; i < selectedApps.Count; i++)
                {
                    var app = selectedApps[i];
                    ScanProgressBar.Value = i;
                    StatusSelectedInfo.Text = (isRu
                        ? string.Format("Деинсталляция ({0}/{1}): ", i + 1, selectedApps.Count)
                        : string.Format("Uninstalling ({0}/{1}): ", i + 1, selectedApps.Count)) + app.DisplayName;

                    try
                    {
                        await _uninstallerService.RunStandardUninstallAsync(app);
                    }
                    catch { }

                    _allApps.Remove(app);
                    _displayedApps.Remove(app);
                }

                StatusSelectedInfo.Text = isRu ? "Поиск остаточных следов программ..." : "Scanning for leftover traces...";
                ScanProgressBar.IsIndeterminate = true;

                foreach (var app in selectedApps)
                {
                    try
                    {
                        var leftovers = await _leftoverScanner.ScanLeftoversAsync(app);
                        if (leftovers != null && leftovers.Count > 0)
                        {
                            allLeftovers.AddRange(leftovers);
                        }
                    }
                    catch { }
                }

                ScanProgressBar.Visibility = Visibility.Collapsed;

                if (allLeftovers.Count > 0)
                {
                    var dummyBatchApp = new InstalledApp
                    {
                        DisplayName = isRu
                            ? string.Format("{0} программ ({1})", selectedApps.Count, string.Join(", ", selectedApps.Take(2).Select(a => a.DisplayName)) + (selectedApps.Count > 2 ? "..." : ""))
                            : string.Format("{0} applications", selectedApps.Count)
                    };
                    var cleanupWin = new CleanupWindow(dummyBatchApp, allLeftovers);
                    cleanupWin.Owner = this;
                    cleanupWin.ShowDialog();
                }
                else
                {
                    string okMsg = isRu
                        ? "Все выбранные программы успешно деинсталлированы. Остаточных следов не обнаружено."
                        : "All selected applications uninstalled. No leftover traces found.";
                    MessageBox.Show(this, okMsg, isRu ? "Готово" : "Ready", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                UpdateStats();
                UpdateSelectionUI();
                ApplyFilterAndSearch();
                StatusSelectedInfo.Text = isRu ? "Пакетное удаление успешно завершено." : "Batch uninstallation completed.";
            }
        }

        private async Task RunSingleUninstallAsync(InstalledApp app)
        {
            bool isRu = AppLang.Instance.IsRussian;
            StatusSelectedInfo.Text = (isRu ? "Выполняется деинсталляция: " : "Uninstalling: ") + app.DisplayName + "...";
            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.IsIndeterminate = true;

            try
            {
                await _uninstallerService.RunStandardUninstallAsync(app);
            }
            catch (Exception ex)
            {
                ScanProgressBar.Visibility = Visibility.Collapsed;
                string errTitle = isRu ? "Ошибка деинсталлятора" : "Uninstaller Error";
                string errPrompt = isRu
                    ? string.Format("Не удалось запустить деинсталлятор:\n{0}\n\nВыполнить ПРИНУДИТЕЛЬНОЕ УДАЛЕНИЕ программы со всеми файлами?", ex.Message)
                    : string.Format("Failed to start uninstaller:\n{0}\n\nPerform FORCE REMOVAL of this application and all traces?", ex.Message);
                string forceBtn = isRu ? "Принудительно удалить" : "Force Removal";

                var errConfirm = new ConfirmDialog(errTitle, errPrompt, forceBtn, true);
                errConfirm.Owner = this;
                if (errConfirm.ShowDialog() == true)
                {
                    ExecuteForceUninstall(app);
                }
                return;
            }

            bool stillInstalled = false;
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            {
                try
                {
                    string[] allFiles = Directory.GetFiles(app.InstallLocation, "*.*", SearchOption.TopDirectoryOnly);
                    var mainExes = allFiles.Where(f =>
                    {
                        string ext = Path.GetExtension(f);
                        if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                        string fn = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                        return !fn.Contains("uninst") && !fn.Contains("setup");
                    }).ToList();

                    if (mainExes.Count > 0)
                    {
                        stillInstalled = true;
                    }
                }
                catch { }
            }
            else if (!string.IsNullOrWhiteSpace(app.UninstallString))
            {
                string uninstExe, uninstArgs;
                UninstallerService.ParseCommand(app.UninstallString, out uninstExe, out uninstArgs);
                if (!string.IsNullOrEmpty(uninstExe) && File.Exists(uninstExe))
                {
                    stillInstalled = true;
                }
            }

            if (stillInstalled)
            {
                ScanProgressBar.Visibility = Visibility.Collapsed;
                StatusSelectedInfo.Text = isRu ? "Деинсталляция была отменена пользователем." : "Uninstallation was cancelled by user.";
                return;
            }

            StatusSelectedInfo.Text = AppLang.Instance.StatusTraces;
            var leftovers = await _leftoverScanner.ScanLeftoversAsync(app);

            ScanProgressBar.Visibility = Visibility.Collapsed;

            if (leftovers != null && leftovers.Count > 0)
            {
                var cleanupWin = new CleanupWindow(app, leftovers);
                cleanupWin.Owner = this;
                cleanupWin.ShowDialog();
            }
            else
            {
                string okMsg = isRu ? "Программа удалена. Остаточных следов не обнаружено." : "Application uninstalled. No leftover traces found.";
                MessageBox.Show(this, okMsg, isRu ? "Готово" : "Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _allApps.Remove(app);
            _displayedApps.Remove(app);
            UpdateStats();
            UpdateSelectionUI();
            ApplyFilterAndSearch();
        }

        private async void ForceUninstallBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = AppsListView.SelectedItems.OfType<InstalledApp>().ToList();
            if (selectedApps.Count == 0) return;

            bool isRu = AppLang.Instance.IsRussian;

            if (selectedApps.Count == 1)
            {
                var app = selectedApps[0];
                string title = isRu ? "Принудительное удаление" : "Force Removal";
                string prompt = isRu
                    ? string.Format("Будут принудительно завершены процессы программы \"{0}\", после чего все её файлы, папки и ключи реестра будут найдены и стёрты.\n\nПродолжить?", app.DisplayName)
                    : string.Format("Processes for \"{0}\" will be terminated, and all residual files, folders, and registry entries will be discovered and deleted.\n\nContinue?", app.DisplayName);
                string btnText = isRu ? "Принудительно удалить" : "Force Removal";

                var confirm = new ConfirmDialog(title, prompt, btnText, true);
                confirm.Owner = this;

                if (confirm.ShowDialog() == true)
                {
                    ExecuteForceUninstall(app);
                }
            }
            else
            {
                string title = isRu ? "Пакетное принудительное удаление" : "Batch Force Removal";
                string prompt = isRu
                    ? string.Format("Будет выполнено принудительное удаление всех выбранных программ ({0} шт.) без запуска деинсталляторов.\nВсе связанные папки, файлы и ветки реестра будут найдены и стёрты.\n\nПродолжить?", selectedApps.Count)
                    : string.Format("Force removal will be performed for {0} selected applications without running their uninstallers.\nAll residual folders, files, and registry entries will be erased.\n\nProceed?", selectedApps.Count);
                string btnText = (isRu ? "Стереть все (" : "Force Erase All (") + selectedApps.Count + ")";

                var confirm = new ConfirmDialog(title, prompt, btnText, true);
                confirm.Owner = this;
                if (confirm.ShowDialog() != true) return;

                ScanProgressBar.Visibility = Visibility.Visible;
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Maximum = selectedApps.Count;
                ScanProgressBar.Value = 0;

                var allForceLeftovers = new List<LeftoverItem>();

                for (int i = 0; i < selectedApps.Count; i++)
                {
                    var app = selectedApps[i];
                    ScanProgressBar.Value = i;
                    StatusSelectedInfo.Text = (isRu
                        ? string.Format("Принудительное удаление ({0}/{1}): ", i + 1, selectedApps.Count)
                        : string.Format("Force removing ({0}/{1}): ", i + 1, selectedApps.Count)) + app.DisplayName;

                    try
                    {
                        await _forceRemovalService.TerminateAppProcessesAsync(app);
                        var leftovers = await _leftoverScanner.ScanLeftoversAsync(app);
                        if (leftovers != null && leftovers.Count > 0)
                        {
                            allForceLeftovers.AddRange(leftovers);
                        }
                    }
                    catch { }
                }

                ScanProgressBar.Visibility = Visibility.Collapsed;

                if (allForceLeftovers.Count > 0)
                {
                    var dummyBatchApp = new InstalledApp
                    {
                        DisplayName = isRu
                            ? string.Format("{0} выбранных программ", selectedApps.Count)
                            : string.Format("{0} selected applications", selectedApps.Count)
                    };
                    var cleanupWin = new CleanupWindow(dummyBatchApp, allForceLeftovers);
                    cleanupWin.Owner = this;
                    if (cleanupWin.ShowDialog() == true)
                    {
                        foreach (var app in selectedApps)
                        {
                            string regErr;
                            _forceRemovalService.RemoveEntryFromRegistry(app, out regErr);
                            _allApps.Remove(app);
                            _displayedApps.Remove(app);
                        }
                    }
                }
                else
                {
                    foreach (var app in selectedApps)
                    {
                        string regErr;
                        _forceRemovalService.RemoveEntryFromRegistry(app, out regErr);
                        _allApps.Remove(app);
                        _displayedApps.Remove(app);
                    }
                }

                UpdateStats();
                UpdateSelectionUI();
                ApplyFilterAndSearch();
                StatusSelectedInfo.Text = isRu ? "Пакетное принудительное удаление завершено." : "Batch force removal completed.";
            }
        }

        private async void ExecuteForceUninstall(InstalledApp app)
        {
            StatusSelectedInfo.Text = AppLang.Instance.StatusKilled;
            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.IsIndeterminate = true;

            await _forceRemovalService.TerminateAppProcessesAsync(app);

            StatusSelectedInfo.Text = AppLang.Instance.StatusTraces;
            var leftovers = await _leftoverScanner.ScanLeftoversAsync(app);

            ScanProgressBar.Visibility = Visibility.Collapsed;

            if (leftovers != null && leftovers.Count > 0)
            {
                var cleanupWin = new CleanupWindow(app, leftovers);
                cleanupWin.Owner = this;
                if (cleanupWin.ShowDialog() == true)
                {
                    string err;
                    _forceRemovalService.RemoveEntryFromRegistry(app, out err);

                    _allApps.Remove(app);
                    _displayedApps.Remove(app);
                    UpdateStats();
                    UpdateSelectionUI();
                    ApplyFilterAndSearch();
                }
            }
            else
            {
                string err;
                _forceRemovalService.RemoveEntryFromRegistry(app, out err);
                _allApps.Remove(app);
                _displayedApps.Remove(app);
                UpdateStats();
                UpdateSelectionUI();
                ApplyFilterAndSearch();

                bool isRu = AppLang.Instance.IsRussian;
                string msg = isRu ? "Запись программы удалена из реестра." : "Application entry removed from registry.";
                MessageBox.Show(this, msg, isRu ? "Готово" : "Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsListView.SelectedItem as InstalledApp;
            if (app == null) return;

            string folder = app.InstallLocation;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                folder = InstalledAppsScanner.TryFindAppFolder(app);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    app.InstallLocation = folder;
                }
            }

            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                try
                {
                    Process.Start("explorer.exe", "\"" + folder + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                string msg = AppLang.Instance.IsRussian ? "Папка установки не найдена на диске." : "Installation directory not found on disk.";
                MessageBox.Show(this, msg, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OpenRegeditBtn_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsListView.SelectedItem as InstalledApp;
            if (app == null || app.IsUwp || string.IsNullOrWhiteSpace(app.RegistryKeyPath)) return;

            RegeditHelper.OpenKeyInRegedit(app.RegistryHiveName, app.RegistryKeyPath);
        }

        private void SearchGoogleBtn_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsListView.SelectedItem as InstalledApp;
            if (app == null) return;

            try
            {
                string query = Uri.EscapeDataString(app.DisplayName + " " + (app.Publisher ?? ""));
                Process.Start("https://www.google.com/search?q=" + query);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveEntryBtn_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsListView.SelectedItem as InstalledApp;
            if (app == null || app.IsUwp) return;

            bool isRu = AppLang.Instance.IsRussian;
            string title = isRu ? "Удалить запись из списка" : "Remove Entry";
            string prompt = isRu
                ? string.Format("Удалить запись о \"{0}\" из реестра Windows?\nФайлы программы затронуты не будут.", app.DisplayName)
                : string.Format("Remove entry for \"{0}\" from Windows registry?\nProgram files will remain untouched.", app.DisplayName);
            string btnText = isRu ? "Удалить запись" : "Remove Entry";

            var confirm = new ConfirmDialog(title, prompt, btnText, true);
            confirm.Owner = this;

            if (confirm.ShowDialog() == true)
            {
                string err;
                if (_forceRemovalService.RemoveEntryFromRegistry(app, out err))
                {
                    _allApps.Remove(app);
                    _displayedApps.Remove(app);
                    UpdateStats();
                    UpdateSelectionUI();
                    ApplyFilterAndSearch();
                }
                else
                {
                    MessageBox.Show(this, "Error: " + err, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            var app = AppsListView.SelectedItem as InstalledApp;
            if (app != null && !string.IsNullOrWhiteSpace(app.DisplayName))
            {
                Clipboard.SetText(app.DisplayName);
            }
        }

        #region Autoruns Features

        private async Task LoadAutorunsAsync()
        {
            if (_isLoading) return;
            _isLoading = true;

            ScanProgressBar.Visibility = Visibility.Visible;
            ScanProgressBar.IsIndeterminate = true;
            StatusSelectedInfo.Text = AppLang.Instance.IsRussian ? "Сканирование автозагрузки..." : "Scanning autoruns...";
            RefreshBtn.IsEnabled = false;

            var progress = new Progress<int>(pct =>
            {
                ScanProgressBar.IsIndeterminate = false;
                ScanProgressBar.Value = pct;
            });

            try
            {
                var items = await _autorunsScanner.ScanAllAutorunsAsync(progress);
                _allAutoruns = items;
                _isAutorunsLoaded = true;
                ApplyAutorunsFilterAndSearch();
                UpdateAutorunsStats();
                UpdateAutorunsSelectionUI();
                PlayAutorunsAppearanceAnimation();
            }
            catch (Exception ex)
            {
                StatusSelectedInfo.Text = "Error: " + ex.Message;
            }
            finally
            {
                ScanProgressBar.Visibility = Visibility.Collapsed;
                RefreshBtn.IsEnabled = true;
                _isLoading = false;
            }
        }

        private void UpdateAutorunsStats()
        {
            if (StatusStatsText == null || !_isAutorunsMode) return;

            int displayed = _displayedAutoruns.Count;
            int enabled = _displayedAutoruns.Count(x => x.IsEnabled);
            int disabled = displayed - enabled;
            int missing = _displayedAutoruns.Count(x => !x.FileExists);

            string itemsLabel = AppLang.Instance.IsRussian ? "записей" : "items";
            string enabledLabel = AppLang.Instance.IsRussian ? "включено" : "enabled";

            string stats = string.Format("{0} {1} • {2} {3}", displayed, itemsLabel, enabled, enabledLabel);
            if (disabled > 0)
            {
                string disabledLabel = AppLang.Instance.IsRussian ? "отключено" : "disabled";
                stats += string.Format(" • {0} {1}", disabled, disabledLabel);
            }
            if (!_hideMissingFiles && missing > 0)
            {
                string missingLabel = AppLang.Instance.IsRussian ? "не найдено" : "missing";
                stats += string.Format(" • {0} {1}", missing, missingLabel);
            }
            StatusStatsText.Text = stats;
        }

        private void ApplyAutorunsFilterAndSearch()
        {
            if (!_isInitialized || SearchBox == null || SearchPlaceholder == null || ClearSearchBtn == null)
                return;

            string query = (SearchBox.Text ?? "").Trim();
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

            var filtered = _allAutoruns.AsEnumerable();

            if (_hideMissingFiles)
            {
                filtered = filtered.Where(x => x.FileExists);
            }

            if (_hideMicrosoft)
            {
                filtered = filtered.Where(x => !x.IsMicrosoft);
            }

            if (_currentAutorunCategory != AutorunCategory.All)
            {
                filtered = filtered.Where(x => x.Category == _currentAutorunCategory);
            }

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(x =>
                    (x.EntryName != null && x.EntryName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (x.Description != null && x.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (x.Publisher != null && x.Publisher.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (x.ImagePath != null && x.ImagePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (x.RegistryLocation != null && x.RegistryLocation.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            _displayedAutoruns.Clear();
            foreach (var item in filtered)
            {
                _displayedAutoruns.Add(item);
            }

            SortAutorunsData(_lastAutorunSortColumn, _lastAutorunSortDirection);

            if (AutorunsEmptyState != null)
            {
                AutorunsEmptyState.Visibility = (_displayedAutoruns.Count == 0 && _isAutorunsMode)
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateAutorunsStats();
        }

        private void AutorunFilterRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            if (AutorunAllRadio != null && AutorunAllRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.All;
            else if (AutorunLogonRadio != null && AutorunLogonRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.Logon;
            else if (AutorunTasksRadio != null && AutorunTasksRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.Task;
            else if (AutorunServicesRadio != null && AutorunServicesRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.Service;
            else if (AutorunExplorerRadio != null && AutorunExplorerRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.Explorer;
            else if (AutorunDriversRadio != null && AutorunDriversRadio.IsChecked == true)
                _currentAutorunCategory = AutorunCategory.Driver;

            AnimatePillIndicator(AutorunFilterIndicator, AutorunFilterIndicatorTranslate, sender as RadioButton, AutorunFilterGrid, true);
            ApplyAutorunsFilterAndSearch();

            var sv = FindVisualChild<ScrollViewer>(AutorunsListView);
            if (sv != null) sv.ScrollToVerticalOffset(0);

            PlayAutorunsAppearanceAnimation();
        }


        private RadioButton GetActiveAutorunFilterRadio()
        {
            if (AutorunLogonRadio != null && AutorunLogonRadio.IsChecked == true) return AutorunLogonRadio;
            if (AutorunTasksRadio != null && AutorunTasksRadio.IsChecked == true) return AutorunTasksRadio;
            if (AutorunServicesRadio != null && AutorunServicesRadio.IsChecked == true) return AutorunServicesRadio;
            if (AutorunExplorerRadio != null && AutorunExplorerRadio.IsChecked == true) return AutorunExplorerRadio;
            if (AutorunDriversRadio != null && AutorunDriversRadio.IsChecked == true) return AutorunDriversRadio;
            return AutorunAllRadio;
        }

        private void AutorunColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = sender as GridViewColumnHeader;
            if (header == null || header.Tag == null) return;

            string sortBy = header.Tag.ToString();
            ListSortDirection direction = ListSortDirection.Ascending;

            if (_lastAutorunSortColumn == sortBy)
            {
                direction = _lastAutorunSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }

            _lastAutorunSortColumn = sortBy;
            _lastAutorunSortDirection = direction;

            SortAutorunsData(sortBy, direction);
        }

        private void SortAutorunsData(string sortBy, ListSortDirection direction)
        {
            ICollectionView dataView = CollectionViewSource.GetDefaultView(AutorunsListView.ItemsSource);
            if (dataView != null)
            {
                dataView.SortDescriptions.Clear();
                dataView.SortDescriptions.Add(new SortDescription(sortBy, direction));
                dataView.Refresh();
            }
            UpdateAutorunSortGlyph(sortBy, direction);
        }

        private void UpdateAutorunSortGlyph(string sortBy, ListSortDirection direction)
        {
            Geometry geom = direction == ListSortDirection.Ascending ? ChevronUpGeom : ChevronDownGeom;

            if (SortArrowAutorunEntry != null) SortArrowAutorunEntry.Visibility = Visibility.Collapsed;
            if (SortArrowAutorunDesc != null) SortArrowAutorunDesc.Visibility = Visibility.Collapsed;
            if (SortArrowAutorunPub != null) SortArrowAutorunPub.Visibility = Visibility.Collapsed;
            if (SortArrowAutorunPath != null) SortArrowAutorunPath.Visibility = Visibility.Collapsed;
            if (SortArrowAutorunCat != null) SortArrowAutorunCat.Visibility = Visibility.Collapsed;

            switch (sortBy)
            {
                case "EntryName":
                    if (SortArrowAutorunEntry != null) { SortArrowAutorunEntry.Data = geom; SortArrowAutorunEntry.Visibility = Visibility.Visible; }
                    break;
                case "Description":
                    if (SortArrowAutorunDesc != null) { SortArrowAutorunDesc.Data = geom; SortArrowAutorunDesc.Visibility = Visibility.Visible; }
                    break;
                case "Publisher":
                    if (SortArrowAutorunPub != null) { SortArrowAutorunPub.Data = geom; SortArrowAutorunPub.Visibility = Visibility.Visible; }
                    break;
                case "ImagePath":
                    if (SortArrowAutorunPath != null) { SortArrowAutorunPath.Data = geom; SortArrowAutorunPath.Visibility = Visibility.Visible; }
                    break;
                case "Category":
                    if (SortArrowAutorunCat != null) { SortArrowAutorunCat.Data = geom; SortArrowAutorunCat.Visibility = Visibility.Visible; }
                    break;
            }
        }

        private void AutorunsListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AdjustAutorunsColumnWidths();
        }

        private void AdjustAutorunsColumnWidths()
        {
            if (ColAutorunCheck != null && ColAutorunEntryView != null && ColAutorunDescView != null &&
                ColAutorunPubView != null && ColAutorunPathView != null && ColAutorunCatView != null && AutorunsListView != null)
            {
                double fixedWidths = ColAutorunCheck.Width + ColAutorunEntryView.Width + ColAutorunDescView.Width +
                                     ColAutorunPubView.Width + ColAutorunCatView.Width;
                double available = AutorunsListView.ActualWidth - fixedWidths - GetScrollbarReservation(AutorunsListView);
                if (available > 100)
                {
                    ColAutorunPathView.Width = available;
                }
            }
        }

        private ItemsPresenter _autorunsItemsPresenter;
        private ItemsPresenter GetAutorunsItemsPresenter()
        {
            if (_autorunsItemsPresenter != null) return _autorunsItemsPresenter;
            if (AutorunsListView == null) return null;
            _autorunsItemsPresenter = FindVisualChild<ItemsPresenter>(AutorunsListView);
            return _autorunsItemsPresenter;
        }

        private void PlayAutorunsAppearanceAnimation()
        {
            var ip = GetAutorunsItemsPresenter();
            if (ip == null) return;

            var trans = ip.RenderTransform as TranslateTransform;
            if (trans == null)
            {
                trans = new TranslateTransform(0, 8);
                ip.RenderTransform = trans;
            }
            else
            {
                trans.BeginAnimation(TranslateTransform.YProperty, null);
                trans.Y = 8;
            }

            ip.BeginAnimation(UIElement.OpacityProperty, null);
            ip.Opacity = 0.0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = ease };
            var slideAnim = new DoubleAnimation { From = 8.0, To = 0.0, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = ease };

            ip.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            trans.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private void AutorunsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            UpdateAutorunsSelectionUI();
        }

        private void UpdateAutorunsSelectionUI()
        {
            var selectedItems = AutorunsListView.SelectedItems.OfType<AutorunItem>().ToList();
            int count = selectedItems.Count;
            bool hasSelection = count > 0;

            if (NoSelectionAutorunPanel != null)
                NoSelectionAutorunPanel.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            if (SelectedAutorunPanel != null)
                SelectedAutorunPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            bool isRu = AppLang.Instance.IsRussian;

            if (count == 1)
            {
                var item = selectedItems[0];
                if (ToggleAutorunText != null)
                {
                    ToggleAutorunText.Text = item.IsEnabled ? AppLang.Instance.MenuDisable : AppLang.Instance.MenuEnable;
                }
                if (CtxMenuToggleAutorun != null)
                {
                    CtxMenuToggleAutorun.Header = item.IsEnabled ? AppLang.Instance.MenuDisable : AppLang.Instance.MenuEnable;
                }

                string info = item.EntryName;
                if (!string.IsNullOrWhiteSpace(item.Description))
                    info += "  •  " + item.Description;
                if (!string.IsNullOrWhiteSpace(item.ImagePath))
                    info += "  •  " + item.ImagePath;
                StatusSelectedInfo.Text = info;
            }
            else if (count > 1)
            {
                if (ToggleAutorunText != null)
                {
                    ToggleAutorunText.Text = (isRu ? "Переключить (" : "Toggle (") + count + ")";
                }
                if (CtxMenuToggleAutorun != null)
                {
                    CtxMenuToggleAutorun.Header = (isRu ? "Переключить (" : "Toggle (") + count + ")";
                }

                StatusSelectedInfo.Text = isRu
                    ? string.Format("Выбрано записей автозагрузки: {0}", count)
                    : string.Format("Selected autorun entries: {0}", count);
            }
            else if (_isAutorunsMode)
            {
                StatusSelectedInfo.Text = "";
            }
        }

        private void AutorunItem_CheckBoxClick(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            if (cb == null) return;
            var item = cb.DataContext as AutorunItem;
            if (item == null) return;

            string error;
            bool desired = cb.IsChecked == true;
            bool success = _autorunsScanner.SetItemEnabled(item, desired, out error);
            if (!success)
            {
                item.IsEnabled = !desired;
                cb.IsChecked = item.IsEnabled;
                MessageBox.Show(this, "Failed to change autorun state:\n" + error, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateAutorunsStats();
            UpdateAutorunsSelectionUI();
        }

        private void ToggleAutorunBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = AutorunsListView.SelectedItems.OfType<AutorunItem>().ToList();
            if (selectedItems.Count == 0) return;

            foreach (var item in selectedItems)
            {
                string error;
                _autorunsScanner.ToggleItem(item, out error);
            }
            UpdateAutorunsStats();
            UpdateAutorunsSelectionUI();
        }

        private void DeleteAutorunBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = AutorunsListView.SelectedItems.OfType<AutorunItem>().ToList();
            if (selectedItems.Count == 0) return;

            bool isRu = AppLang.Instance.IsRussian;
            string title = AppLang.Instance.DeleteAutorunConfirmTitle;
            string msg = selectedItems.Count == 1
                ? string.Format(AppLang.Instance.DeleteAutorunConfirmMsg, selectedItems[0].EntryName)
                : (isRu
                    ? string.Format("Удалить выбранные записи автозагрузки ({0} шт.)?\nЭто действие необратимо.", selectedItems.Count)
                    : string.Format("Permanently delete {0} selected autorun entries?", selectedItems.Count));
            string btnText = selectedItems.Count == 1
                ? AppLang.Instance.MenuDeleteAutorun
                : (isRu ? "Удалить все (" + selectedItems.Count + ")" : "Delete All (" + selectedItems.Count + ")");

            var dlg = new ConfirmDialog(title, msg, btnText, true);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                foreach (var item in selectedItems)
                {
                    string error;
                    if (_autorunsScanner.DeleteItem(item, out error))
                    {
                        _allAutoruns.Remove(item);
                        _displayedAutoruns.Remove(item);
                    }
                }
                UpdateAutorunsStats();
                UpdateAutorunsSelectionUI();
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                RefreshBtn_Click(this, null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (SearchBox != null)
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (SearchBox != null && (SearchBox.IsFocused || !string.IsNullOrEmpty(SearchBox.Text)))
                {
                    SearchBox.Text = "";
                    Keyboard.ClearFocus();
                    e.Handled = true;
                    return;
                }
                if (_isSearchMode)
                {
                    SearchListView.UnselectAll();
                }
                else if (_isAutorunsMode)
                {
                    AutorunsListView.UnselectAll();
                }
                else
                {
                    AppsListView.UnselectAll();
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space && _isAutorunsMode && !SearchBox.IsFocused)
            {
                if (AutorunsListView.SelectedItems.Count > 0)
                {
                    ToggleAutorunBtn_Click(this, null);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter && _isSearchMode)
            {
                if (SearchBox != null && SearchBox.IsFocused)
                {
                    if (_searchDebounceTimer != null) _searchDebounceTimer.Stop();
                    string query = (SearchBox.Text ?? "").Trim();
                    if (!string.IsNullOrEmpty(query))
                    {
                        var _ = PerformEverythingSearchAsync(query);
                    }
                    e.Handled = true;
                    return;
                }
                else if (SearchListView != null && SearchListView.SelectedItem != null)
                {
                    OpenSearchItemBtn_Click(this, null);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Delete && !SearchBox.IsFocused)
            {
                if (_isSearchMode && SearchListView.SelectedItems.Count > 0)
                {
                    DeleteSearchItemBtn_Click(this, null);
                    e.Handled = true;
                    return;
                }
                else if (_isAutorunsMode && AutorunsListView.SelectedItems.Count > 0)
                {
                    DeleteAutorunBtn_Click(this, null);
                    e.Handled = true;
                    return;
                }
                else if (!_isAutorunsMode && AppsListView.SelectedItems.Count > 0)
                {
                    UninstallBtn_Click(this, null);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void OpenAutorunFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var item = AutorunsListView.SelectedItem as AutorunItem;
            if (item == null || string.IsNullOrWhiteSpace(item.ImagePath)) return;

            try
            {
                if (File.Exists(item.ImagePath))
                {
                    Process.Start("explorer.exe", "/select,\"" + item.ImagePath + "\"");
                }
                else if (Directory.Exists(item.ImagePath))
                {
                    Process.Start("explorer.exe", "\"" + item.ImagePath + "\"");
                }
                else
                {
                    string dir = Path.GetDirectoryName(item.ImagePath);
                    if (Directory.Exists(dir))
                    {
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                    }
                    else
                    {
                        MessageBox.Show(this, AppLang.Instance.StatusFileNotFound, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void JumpAutorunRegistryBtn_Click(object sender, RoutedEventArgs e)
        {
            var item = AutorunsListView.SelectedItem as AutorunItem;
            if (item == null) return;

            if (!string.IsNullOrWhiteSpace(item.RegistryHive) && !string.IsNullOrWhiteSpace(item.RegistrySubKey))
            {
                RegeditHelper.OpenKeyInRegedit(item.RegistryHive, item.RegistrySubKey);
            }
            else if (item.Category == AutorunCategory.Task)
            {
                try { Process.Start("taskschd.msc"); } catch { }
            }
            else if (item.Category == AutorunCategory.Service || item.Category == AutorunCategory.Driver)
            {
                try { Process.Start("services.msc"); } catch { }
            }
        }

        private void SearchAutorunGoogleBtn_Click(object sender, RoutedEventArgs e)
        {
            var item = AutorunsListView.SelectedItem as AutorunItem;
            if (item == null) return;

            try
            {
                string query = Uri.EscapeDataString(item.EntryName + " " + (item.Publisher ?? ""));
                Process.Start("https://www.google.com/search?q=" + query);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CheckVirusTotalBtn_Click(object sender, RoutedEventArgs e)
        {
            var item = AutorunsListView.SelectedItem as AutorunItem;
            if (item == null) return;

            string path = item.ImagePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                StatusSelectedInfo.Text = (AppLang.Instance.IsRussian ? "Вычисление хэша для VirusTotal: " : "Calculating hash for VirusTotal: ") + Path.GetFileName(path);
                try
                {
                    string sha256 = await Task.Run(() => ComputeSha256(path));
                    if (!string.IsNullOrEmpty(sha256))
                    {
                        Process.Start("https://www.virustotal.com/gui/file/" + sha256);
                        StatusSelectedInfo.Text = (AppLang.Instance.IsRussian ? "Открыт отчёт VirusTotal для " : "Opened VirusTotal report for ") + Path.GetFileName(path);
                        return;
                    }
                }
                catch { }
            }

            string query = !string.IsNullOrWhiteSpace(path) ? Path.GetFileName(path) : item.EntryName;
            try
            {
                Process.Start("https://www.virustotal.com/gui/search/" + Uri.EscapeDataString(query));
                StatusSelectedInfo.Text = (AppLang.Instance.IsRussian ? "Поиск на VirusTotal: " : "Searching on VirusTotal: ") + query;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private void CopyAutorunPath_Click(object sender, RoutedEventArgs e)
        {
            var item = AutorunsListView.SelectedItem as AutorunItem;
            if (item != null && !string.IsNullOrWhiteSpace(item.ImagePath))
            {
                Clipboard.SetText(item.ImagePath);
            }
        }

        private void AutorunsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenAutorunFolderBtn_Click(sender, e);
        }

        #endregion

        #region Everything Search Mode

        private async void SearchDebounceTimer_Tick(object sender, EventArgs e)
        {
            if (_searchDebounceTimer != null) _searchDebounceTimer.Stop();
            string query = (SearchBox != null ? SearchBox.Text ?? "" : "").Trim();
            if (!string.IsNullOrEmpty(query))
            {
                await PerformEverythingSearchAsync(query);
            }
        }

        private async Task PerformEverythingSearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;

            if (_searchCts != null)
            {
                _searchCts.Cancel();
                _searchCts.Dispose();
            }
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                var results = await EverythingSearchService.SearchAsync(query, 250, token);
                if (token.IsCancellationRequested) return;

                _allSearchResults = results;
                ApplySearchFilterAndSort();

                if (_displayedSearchResults.Count == 0)
                {
                    if (SearchEmptyState != null)
                    {
                        SearchEmptyState.Visibility = Visibility.Visible;
                    }
                    if (SearchEmptyStateText != null)
                    {
                        SearchEmptyStateText.Text = AppLang.Instance.IsRussian ? "Ничего не найдено" : "Nothing found";
                    }
                }
                else
                {
                    if (SearchEmptyState != null)
                    {
                        SearchEmptyState.Visibility = Visibility.Collapsed;
                    }
                }

                UpdateSearchStats();
                UpdateSearchSelectionUI();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusSelectedInfo.Text = "Error: " + ex.Message;
            }
        }

        private void ApplySearchFilterAndSort()
        {
            if (_allSearchResults == null) return;

            IEnumerable<SearchResultItem> filtered = _allSearchResults;

            switch (_currentSearchFilter)
            {
                case "files":
                    filtered = filtered.Where(x => !x.IsDirectory);
                    break;
                case "folders":
                    filtered = filtered.Where(x => x.IsDirectory);
                    break;
                case "apps":
                    filtered = filtered.Where(x => !x.IsDirectory && (x.Extension == ".exe" || x.Extension == ".msi" || x.Extension == ".bat" || x.Extension == ".cmd"));
                    break;
                case "all":
                default:
                    break;
            }

            switch (_searchSortColumn)
            {
                case "Name":
                    filtered = _searchSortDirection == ListSortDirection.Ascending
                        ? filtered.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        : filtered.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase);
                    break;
                case "Path":
                    filtered = _searchSortDirection == ListSortDirection.Ascending
                        ? filtered.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                        : filtered.OrderByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase);
                    break;
                case "SizeBytes":
                    filtered = _searchSortDirection == ListSortDirection.Ascending
                        ? filtered.OrderBy(x => x.SizeBytes)
                        : filtered.OrderByDescending(x => x.SizeBytes);
                    break;
                case "DateModified":
                    filtered = _searchSortDirection == ListSortDirection.Ascending
                        ? filtered.OrderBy(x => x.DateModified ?? DateTime.MinValue)
                        : filtered.OrderByDescending(x => x.DateModified ?? DateTime.MinValue);
                    break;
            }

            var list = filtered.ToList();
            _displayedSearchResults = new ObservableCollection<SearchResultItem>(list);
            if (SearchListView != null) SearchListView.ItemsSource = _displayedSearchResults;

            UpdateSearchSortArrows(_searchSortColumn, _searchSortDirection);
        }

        private void SearchFilterRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            if (sender == SearchFilesRadio)
                _currentSearchFilter = "files";
            else if (sender == SearchFoldersRadio)
                _currentSearchFilter = "folders";
            else if (sender == SearchAppsRadio)
                _currentSearchFilter = "apps";
            else
                _currentSearchFilter = "all";

            AnimatePillIndicator(SearchFilterIndicator, SearchFilterIndicatorTranslate, sender as RadioButton, SearchFilterGrid, true);
            ApplySearchFilterAndSort();
            UpdateSearchStats();
        }

        private RadioButton GetActiveSearchFilterRadio()
        {
            if (SearchFilesRadio != null && SearchFilesRadio.IsChecked == true) return SearchFilesRadio;
            if (SearchFoldersRadio != null && SearchFoldersRadio.IsChecked == true) return SearchFoldersRadio;
            if (SearchAppsRadio != null && SearchAppsRadio.IsChecked == true) return SearchAppsRadio;
            return SearchAllRadio;
        }

        private void SearchColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = sender as GridViewColumnHeader;
            if (header == null || header.Tag == null) return;

            string col = header.Tag.ToString();
            if (_searchSortColumn == col)
            {
                _searchSortDirection = _searchSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
            else
            {
                _searchSortColumn = col;
                _searchSortDirection = ListSortDirection.Ascending;
            }

            ApplySearchFilterAndSort();
        }

        private void UpdateSearchSortArrows(string sortBy, ListSortDirection direction)
        {
            Geometry geom = direction == ListSortDirection.Ascending ? ChevronUpGeom : ChevronDownGeom;

            if (SortArrowSearchName != null) SortArrowSearchName.Visibility = Visibility.Collapsed;
            if (SortArrowSearchPath != null) SortArrowSearchPath.Visibility = Visibility.Collapsed;
            if (SortArrowSearchSize != null) SortArrowSearchSize.Visibility = Visibility.Collapsed;
            if (SortArrowSearchDate != null) SortArrowSearchDate.Visibility = Visibility.Collapsed;

            switch (sortBy)
            {
                case "Name":
                    if (SortArrowSearchName != null) { SortArrowSearchName.Data = geom; SortArrowSearchName.Visibility = Visibility.Visible; }
                    break;
                case "Path":
                    if (SortArrowSearchPath != null) { SortArrowSearchPath.Data = geom; SortArrowSearchPath.Visibility = Visibility.Visible; }
                    break;
                case "SizeBytes":
                    if (SortArrowSearchSize != null) { SortArrowSearchSize.Data = geom; SortArrowSearchSize.Visibility = Visibility.Visible; }
                    break;
                case "DateModified":
                    if (SortArrowSearchDate != null) { SortArrowSearchDate.Data = geom; SortArrowSearchDate.Visibility = Visibility.Visible; }
                    break;
            }
        }

        private void SearchListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AdjustSearchColumnWidths();
        }

        private void AdjustSearchColumnWidths()
        {
            if (ColSearchNameView != null && ColSearchPathView != null && ColSearchSizeView != null && ColSearchDateView != null && SearchListView != null)
            {
                double fixedWidths = ColSearchNameView.Width + ColSearchSizeView.Width + ColSearchDateView.Width;
                double available = SearchListView.ActualWidth - fixedWidths - GetScrollbarReservation(SearchListView);
                if (available > 100)
                {
                    ColSearchPathView.Width = available;
                }
            }
        }

        private void SearchListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            UpdateSearchSelectionUI();
        }

        private void UpdateSearchSelectionUI()
        {
            var selectedItems = SearchListView != null ? SearchListView.SelectedItems.OfType<SearchResultItem>().ToList() : new List<SearchResultItem>();
            int count = selectedItems.Count;
            bool hasSelection = count > 0;

            if (SelectedSearchActionsPanel != null)
            {
                SelectedSearchActionsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            }
            if (NoSearchSelectionHint != null)
            {
                NoSearchSelectionHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            }

            if (count == 1)
            {
                StatusSelectedInfo.Text = selectedItems[0].FullPath;
            }
            else if (count > 1)
            {
                bool isRu = AppLang.Instance.IsRussian;
                string label = isRu ? "Выбрано объектов: " : "Selected objects: ";
                StatusSelectedInfo.Text = label + count;
            }
            else
            {
                StatusSelectedInfo.Text = "";
            }
        }

        private void UpdateSearchStats()
        {
            if (StatusStatsText == null || !_isSearchMode) return;
            int count = _displayedSearchResults.Count;
            bool isRu = AppLang.Instance.IsRussian;

            if (count == 0)
            {
                StatusStatsText.Text = isRu ? "0 объектов" : "0 objects";
            }
            else
            {
                string label;
                if (isRu)
                {
                    int rem10 = count % 10;
                    int rem100 = count % 100;
                    if (rem100 >= 11 && rem100 <= 19) label = "объектов";
                    else if (rem10 == 1) label = "объект";
                    else if (rem10 >= 2 && rem10 <= 4) label = "объекта";
                    else label = "объектов";
                }
                else
                {
                    label = count == 1 ? "object" : "objects";
                }
                StatusStatsText.Text = string.Format("{0:N0} {1}", count, label);
            }
        }

        private void OpenSearchItemBtn_Click(object sender, RoutedEventArgs e)
        {
            var items = SearchListView != null ? SearchListView.SelectedItems.OfType<SearchResultItem>().ToList() : new List<SearchResultItem>();
            if (items.Count == 0) return;

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.FullPath)) continue;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.FullPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SearchListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSearchItemBtn_Click(sender, e);
        }

        private void SearchListView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSearchItemBtn_Click(sender, e);
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                DeleteSearchItemBtn_Click(sender, e);
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                CopySearchPathBtn_Click(sender, e);
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                if (SearchListView != null) SearchListView.SelectAll();
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (SearchBox != null) SearchBox.Text = string.Empty;
            }
            else if (e.Key == Key.Down)
            {
                if (SearchListView != null && SearchListView.Items.Count > 0)
                {
                    e.Handled = true;
                    SearchListView.Focus();
                    if (SearchListView.SelectedIndex < 0)
                    {
                        SearchListView.SelectedIndex = 0;
                    }
                }
            }
        }

        private void OpenSearchFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var item = SearchListView != null ? SearchListView.SelectedItem as SearchResultItem : null;
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath)) return;

            try
            {
                if (item.IsDirectory && Directory.Exists(item.FullPath))
                {
                    Process.Start("explorer.exe", "\"" + item.FullPath + "\"");
                }
                else if (File.Exists(item.FullPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + item.FullPath + "\"");
                }
                else if (Directory.Exists(item.Path))
                {
                    Process.Start("explorer.exe", "\"" + item.Path + "\"");
                }
                else
                {
                    MessageBox.Show(this, AppLang.Instance.StatusFileNotFound, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopySearchPathBtn_Click(object sender, RoutedEventArgs e)
        {
            var items = SearchListView != null ? SearchListView.SelectedItems.OfType<SearchResultItem>().ToList() : new List<SearchResultItem>();
            if (items.Count == 0) return;

            string text = string.Join(Environment.NewLine, items.Select(x => x.FullPath));
            Clipboard.SetText(text);

            bool isRu = AppLang.Instance.IsRussian;
            if (items.Count == 1)
            {
                StatusSelectedInfo.Text = (isRu ? "Скопирован путь: " : "Copied path: ") + items[0].FullPath;
            }
            else
            {
                StatusSelectedInfo.Text = isRu
                    ? string.Format("Скопировано путей: {0}", items.Count)
                    : string.Format("Copied {0} paths", items.Count);
            }
        }

        private void CopySearchNameBtn_Click(object sender, RoutedEventArgs e)
        {
            var items = SearchListView != null ? SearchListView.SelectedItems.OfType<SearchResultItem>().ToList() : new List<SearchResultItem>();
            if (items.Count == 0) return;

            string text = string.Join(Environment.NewLine, items.Select(x => x.Name));
            Clipboard.SetText(text);

            bool isRu = AppLang.Instance.IsRussian;
            if (items.Count == 1)
            {
                StatusSelectedInfo.Text = (isRu ? "Скопировано имя: " : "Copied name: ") + items[0].Name;
            }
            else
            {
                StatusSelectedInfo.Text = isRu
                    ? string.Format("Скопировано имён: {0}", items.Count)
                    : string.Format("Copied {0} names", items.Count);
            }
        }

        private void DeleteSearchItemBtn_Click(object sender, RoutedEventArgs e)
        {
            var items = SearchListView != null ? SearchListView.SelectedItems.OfType<SearchResultItem>().ToList() : new List<SearchResultItem>();
            if (items.Count == 0) return;

            bool isRu = AppLang.Instance.IsRussian;
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            string title;
            string msg;
            string btnText;

            if (isShift)
            {
                title = isRu ? "Принудительное безвозвратное удаление" : "Permanent Force Delete";
                if (items.Count == 1)
                {
                    msg = string.Format(isRu
                        ? "Безвозвратно и принудительно удалить файл без перемещения в Корзину?\n\n{0}"
                        : "Permanently and forcibly delete this item without moving to Recycle Bin?\n\n{0}", items[0].FullPath);
                }
                else
                {
                    msg = string.Format(isRu
                        ? "Безвозвратно и принудительно удалить {0} выбранных объектов?"
                        : "Permanently and forcibly delete {0} selected objects?", items.Count);
                }
                btnText = isRu ? "Удалить навсегда" : "Delete Permanently";
            }
            else
            {
                title = AppLang.Instance.SearchDeleteConfirmTitle;
                if (items.Count == 1)
                {
                    msg = string.Format("{0}\n\n{1}", AppLang.Instance.SearchDeleteConfirmMsg, items[0].FullPath);
                }
                else
                {
                    msg = isRu
                        ? string.Format("Вы действительно хотите переместить {0} выбранных объектов в корзину?", items.Count)
                        : string.Format("Are you sure you want to move {0} selected items to the Recycle Bin?", items.Count);
                }
                btnText = AppLang.Instance.SearchDelete;
            }

            var dlg = new ConfirmDialog(title, msg, btnText, true);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                int successCount = 0;
                int failedCount = 0;

                foreach (var item in items)
                {
                    bool deleted = false;

                    if (!isShift)
                    {
                        deleted = SendToRecycleBin(item.FullPath);
                    }

                    if (!deleted)
                    {
                        string err;
                        if (item.IsDirectory)
                        {
                            deleted = LeftoverScanner.ForceDeleteDirectory(item.FullPath, out err);
                        }
                        else
                        {
                            deleted = LeftoverScanner.ForceDeleteFile(item.FullPath, out err);
                        }
                    }

                    if (deleted)
                    {
                        successCount++;
                        _allSearchResults.Remove(item);
                        _displayedSearchResults.Remove(item);
                    }
                    else
                    {
                        failedCount++;
                    }
                }

                UpdateSearchStats();
                UpdateSearchSelectionUI();

                if (failedCount == 0)
                {
                    StatusSelectedInfo.Text = isRu
                        ? string.Format("Успешно удалено объектов: {0}", successCount)
                        : string.Format("Successfully deleted {0} objects", successCount);
                }
                else
                {
                    StatusSelectedInfo.Text = isRu
                        ? string.Format("Удалено: {0}, заблокировано: {1}", successCount, failedCount)
                        : string.Format("Deleted: {0}, locked: {1}", successCount, failedCount);
                }
            }
        }

        private void SearchItemProperties_Click(object sender, RoutedEventArgs e)
        {
            var item = SearchListView != null ? SearchListView.SelectedItem as SearchResultItem : null;
            if (item != null && !string.IsNullOrWhiteSpace(item.FullPath))
            {
                ShowFileProperties(item.FullPath);
            }
        }

        #endregion

        #region Win32 Shell Helpers

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        private static bool SendToRecycleBin(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                int res = SHFileOperation(ref shf);
                return res == 0 && !shf.fAnyOperationsAborted;
            }
            catch
            {
                return false;
            }
        }

        private void ShowFileProperties(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var sei = new SHELLEXECUTEINFO();
                sei.cbSize = Marshal.SizeOf(sei);
                sei.lpVerb = "properties";
                sei.lpFile = path;
                sei.nShow = 5; // SW_SHOW
                sei.fMask = SEE_MASK_INVOKEIDLIST;
                ShellExecuteEx(ref sei);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ZeroTrace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
