using System.Windows;
using ApexUninstaller.Services;

namespace ApexUninstaller
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog(string title, string message, string confirmText = null, bool isDanger = true)
        {
            InitializeComponent();
            bool isRu = AppLang.Instance.IsRussian;
            Title = isRu ? "Подтверждение" : "Confirmation";
            TitleText.Text = title;
            MessageText.Text = message;
            CancelBtn.Content = isRu ? "Отмена" : "Cancel";
            ConfirmBtn.Content = !string.IsNullOrEmpty(confirmText) ? confirmText : (isRu ? "Подтвердить" : "Confirm");
        }

        private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
