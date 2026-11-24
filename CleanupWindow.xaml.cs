using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ApexUninstaller.Models;
using ApexUninstaller.Services;

namespace ApexUninstaller
{
    public partial class CleanupWindow : Window
    {
        private readonly InstalledApp _app;
        private readonly LeftoverScanner _scanner;
        private readonly ObservableCollection<LeftoverItem> _items;
        public bool TracesDeleted { get; private set; }

        public CleanupWindow(InstalledApp app, List<LeftoverItem> leftovers)
        {
            ThemeHelper.ApplyDarkTitleBar(this);
            _app = app;
            _scanner = new LeftoverScanner();
            _items = new ObservableCollection<LeftoverItem>(leftovers != null ? leftovers : new List<LeftoverItem>());

            InitializeComponent();

            Title = AppLang.Instance.CleanupTitle;
            LeftoversList.ItemsSource = _items;
            SmoothScrollHelper.Register(LeftoversList);
            HeadingText.Text = (AppLang.Instance.IsRussian ? "Следы программы: " : "Leftover traces: ") + (_app != null ? _app.DisplayName : "");
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_items == null || SummaryText == null) return;

            int count = _items.Count;
            long totalBytes = _items.Sum(i => i.SizeBytes);
            bool isRu = AppLang.Instance.IsRussian;

            string sizeStr;
            if (totalBytes > 1024 * 1024 * 1024)
            {
                sizeStr = string.Format("{0:0.##} " + (isRu ? "ГБ" : "GB"), totalBytes / (1024.0 * 1024.0 * 1024.0));
            }
            else if (totalBytes > 0)
            {
                sizeStr = string.Format("{0:0.#} " + (isRu ? "МБ" : "MB"), totalBytes / (1024.0 * 1024.0));
            }
            else
            {
                sizeStr = isRu ? "реестр" : "registry";
            }

            SummaryText.Text = isRu
                ? string.Format("Найдено следов: {0}  •  Общий размер: {1}", count, sizeStr)
                : string.Format("Traces found: {0}  •  Total size: {1}", count, sizeStr);
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_items == null) return;
            foreach (var item in _items)
            {
                item.IsSelected = true;
            }
        }

        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_items == null) return;
            foreach (var item in _items)
            {
                item.IsSelected = false;
            }
        }

        private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = _items.Where(i => i.IsSelected).ToList();
            bool isRu = AppLang.Instance.IsRussian;

            if (selected.Count == 0)
            {
                string warnMsg = isRu ? "Выберите хотя бы один элемент для удаления." : "Select at least one leftover trace to delete.";
                string warnTitle = isRu ? "Внимание" : "Warning";
                MessageBox.Show(this, warnMsg, warnTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string confirmTitle = isRu ? "Удаление следов" : "Delete Traces";
            string confirmMsg = isRu
                ? string.Format("Будет безвозвратно удалено {0} следов программы (файлы, папки и записи реестра). Продолжить?", selected.Count)
                : string.Format("Permanently delete {0} leftover trace(s) (files, folders, and registry entries)? Proceed?", selected.Count);
            string confirmBtnText = isRu ? "Удалить" : "Delete";

            var confirm = new ConfirmDialog(confirmTitle, confirmMsg, confirmBtnText, true);
            confirm.Owner = this;

            if (confirm.ShowDialog() != true) return;

            DeleteBtn.IsEnabled = false;
            CloseBtn.IsEnabled = false;
            DeleteProgressBar.Visibility = Visibility.Visible;
            DeleteProgressBar.IsIndeterminate = false;
            DeleteProgressBar.Maximum = selected.Count;
            DeleteProgressBar.Value = 0;

            int deletedCount = 0;
            int errorsCount = 0;

            await Task.Run(() =>
            {
                // Sort deeper subpaths first for clean bottom-up tree removal
                var sortedSelected = selected.OrderByDescending(x => x.Path != null ? x.Path.Length : 0).ToList();

                for (int i = 0; i < sortedSelected.Count; i++)
                {
                    var item = sortedSelected[i];
                    string err;
                    bool ok = _scanner.DeleteLeftoverItem(item, out err);
                    if (ok)
                    {
                        deletedCount++;
                        Dispatcher.Invoke(() => _items.Remove(item));
                    }
                    else
                    {
                        errorsCount++;
                    }

                    int progressVal = i + 1;
                    Dispatcher.Invoke(() =>
                    {
                        DeleteProgressBar.Value = progressVal;
                        StatusText.Text = isRu
                            ? string.Format("Удаление: {0} из {1}...", progressVal, sortedSelected.Count)
                            : string.Format("Deleting: {0} of {1}...", progressVal, sortedSelected.Count);
                    });
                }
            });

            DeleteProgressBar.Visibility = Visibility.Collapsed;
            TracesDeleted = true;

            if (errorsCount == 0)
            {
                StatusText.Text = isRu
                    ? string.Format("Успешно удалено следов: {0}", deletedCount)
                    : string.Format("Successfully deleted traces: {0}", deletedCount);
                DeleteBtn.Visibility = Visibility.Collapsed;
                CloseBtn.Content = isRu ? "Готово" : "Done";
                CloseBtn.IsEnabled = true;
                UpdateSummary();
            }
            else
            {
                StatusText.Text = isRu
                    ? string.Format("Удалено: {0}, заблокировано: {1}", deletedCount, errorsCount)
                    : string.Format("Deleted: {0}, locked: {1}", deletedCount, errorsCount);
                DeleteBtn.IsEnabled = true;
                CloseBtn.IsEnabled = true;
                UpdateSummary();
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = TracesDeleted;
            Close();
        }
    }
}
