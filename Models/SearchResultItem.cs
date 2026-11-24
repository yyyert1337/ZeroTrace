using System;
using System.ComponentModel;
using System.Windows.Media;

namespace ApexUninstaller.Models
{
    public class SearchResultItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public string FormattedSize { get; set; }
        public DateTime? DateModified { get; set; }
        public string FormattedDate { get; set; }
        public string Extension { get; set; }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get { return _icon; }
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    OnPropertyChanged("Icon");
                }
            }
        }

        public string TypeBadge
        {
            get
            {
                if (IsDirectory) return ApexUninstaller.Services.AppLang.Instance.IsRussian ? "Папка" : "Folder";
                if (string.IsNullOrEmpty(Extension)) return ApexUninstaller.Services.AppLang.Instance.IsRussian ? "Файл" : "File";
                return Extension.TrimStart('.').ToUpperInvariant();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
