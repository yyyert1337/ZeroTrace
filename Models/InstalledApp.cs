using System;
using System.ComponentModel;
using System.Windows.Media;

namespace ApexUninstaller.Models
{
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string DisplayVersion { get; set; }
        public string Publisher { get; set; }
        public DateTime? InstallDate { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }
        public string DisplayIcon { get; set; }
        public string RegistryKeyPath { get; set; }
        public string RegistryHiveName { get; set; }
        public long EstimatedSizeBytes { get; set; }
        public AppArchitecture Architecture { get; set; }
        public bool IsUwp { get; set; }
        public string PackageFullName { get; set; }

        public string DisplayPublisher
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Publisher)) return "";
                string p = Publisher.Trim();
                if (p.StartsWith("{") || (p.Length >= 32 && p.Contains("-") && !p.Contains(" ")))
                    return "";
                return p;
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

        public string ArchitectureBadge
        {
            get
            {
                switch (Architecture)
                {
                    case AppArchitecture.X64:
                        return "64-bit";
                    case AppArchitecture.X86:
                        return "32-bit";
                    case AppArchitecture.UWP:
                        return "UWP";
                    default:
                        return "App";
                }
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "—";
            bool isRu = Services.AppLang.Instance.IsRussian;
            if (bytes < 1024 * 1024)
            {
                return string.Format("{0:0.#} " + (isRu ? "КБ" : "KB"), bytes / 1024.0);
            }
            if (bytes < 1024 * 1024 * 1024)
            {
                return string.Format("{0:0.#} " + (isRu ? "МБ" : "MB"), bytes / (1024.0 * 1024.0));
            }
            return string.Format("{0:0.##} " + (isRu ? "ГБ" : "GB"), bytes / (1024.0 * 1024.0 * 1024.0));
        }

        public string FormattedSize
        {
            get
            {
                return FormatBytes(EstimatedSizeBytes);
            }
        }

        public void RefreshDisplay()
        {
            OnPropertyChanged("FormattedSize");
        }

        public string FormattedDate
        {
            get
            {
                if (!InstallDate.HasValue) return "—";
                return InstallDate.Value.ToString("dd.MM.yyyy");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public override string ToString()
        {
            return DisplayName + " (" + (DisplayVersion ?? "Unknown") + ")";
        }
    }
}
