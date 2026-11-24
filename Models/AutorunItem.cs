using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ApexUninstaller.Services;

namespace ApexUninstaller.Models
{
    public enum AutorunCategory
    {
        All,
        Logon,
        Task,
        Service,
        Explorer,
        Driver
    }

    public class AutorunItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string EntryName { get; set; }
        public string Description { get; set; }
        public string Publisher { get; set; }
        public string ImagePath { get; set; }
        public string Arguments { get; set; }
        public string RegistryLocation { get; set; } // Display location e.g. "HKLM\...\Run"
        public string RegistryHive { get; set; }     // "HKCU" or "HKLM"
        public string RegistrySubKey { get; set; }   // subkey path
        public string ValueName { get; set; }        // value name
        public string TaskPath { get; set; }         // for scheduled tasks (e.g. "\WindhawkUpdateTask")
        public string ServiceName { get; set; }      // for services
        public string ShortcutPath { get; set; }     // for startup folder shortcuts
        public AutorunCategory Category { get; set; }
        public bool IsMicrosoft { get; set; }
        public bool FileExists { get; set; }
        public bool IsVerified { get; set; }
        public string SignerName { get; set; }

        public string PublisherDisplay
        {
            get
            {
                if (!FileExists) return "";
                if (IsVerified)
                {
                    string name = !string.IsNullOrWhiteSpace(SignerName) ? SignerName : Publisher;
                    return !string.IsNullOrWhiteSpace(name) ? name : (AppLang.Instance.IsRussian ? "Проверено" : "Verified");
                }
                if (!string.IsNullOrWhiteSpace(Publisher))
                {
                    return Publisher;
                }
                return AppLang.Instance.IsRussian ? "Без подписи" : "(Unsigned)";
            }
        }

        public Visibility VerifiedBadgeVisibility
        {
            get { return (FileExists && IsVerified) ? Visibility.Visible : Visibility.Collapsed; }
        }

        private static readonly SolidColorBrush NormalPublisherBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD6)); // Clean readable light gray
        private static readonly SolidColorBrush MutedPublisherBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x85, 0x96));  // Muted gray

        public Brush PublisherForegroundBrush
        {
            get
            {
                if (FileExists && IsVerified) return NormalPublisherBrush;
                return MutedPublisherBrush;
            }
        }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged("IsEnabled");
                    OnPropertyChanged("StatusBadge");
                }
            }
        }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get { return _icon; }
            set
            {
                _icon = value;
                OnPropertyChanged("Icon");
            }
        }

        public string CategoryBadge
        {
            get
            {
                switch (Category)
                {
                    case AutorunCategory.Logon: return "Logon";
                    case AutorunCategory.Task: return "Task";
                    case AutorunCategory.Service: return "Service";
                    case AutorunCategory.Explorer: return "Explorer";
                    case AutorunCategory.Driver: return "Driver";
                    default: return "Auto";
                }
            }
        }

        public string StatusBadge
        {
            get
            {
                if (!FileExists) return "Missing";
                return IsEnabled ? "Enabled" : "Disabled";
            }
        }

        public Visibility MissingBadgeVisibility
        {
            get { return FileExists ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility EnabledBadgeVisibility
        {
            get { return IsEnabled ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility DisabledBadgeVisibility
        {
            get { return IsEnabled ? Visibility.Collapsed : Visibility.Visible; }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
