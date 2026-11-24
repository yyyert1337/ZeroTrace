using System.ComponentModel;

namespace ApexUninstaller.Models
{
    public enum LeftoverType
    {
        Directory,
        File,
        RegistryKey,
        RegistryValue,
        Shortcut,
        Task,
        Service
    }

    public class LeftoverItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        public LeftoverType Type { get; set; }
        public string Path { get; set; }

        private string _description;
        public string DescriptionRu { get; set; }
        public string DescriptionEn { get; set; }
        public string Description
        {
            get
            {
                bool isRu = Services.AppLang.Instance.IsRussian;
                if (isRu && !string.IsNullOrEmpty(DescriptionRu)) return DescriptionRu;
                if (!isRu && !string.IsNullOrEmpty(DescriptionEn)) return DescriptionEn;
                return _description ?? "";
            }
            set
            {
                _description = value;
            }
        }

        public long SizeBytes { get; set; }

        public string RegistryHive { get; set; }
        public string RegistrySubKey { get; set; }
        public string RegistryValueName { get; set; }

        public string TypeName
        {
            get
            {
                bool isRu = Services.AppLang.Instance.IsRussian;
                switch (Type)
                {
                    case LeftoverType.Directory:
                        return isRu ? "Папка" : "Folder";
                    case LeftoverType.File:
                        return isRu ? "Файл" : "File";
                    case LeftoverType.RegistryKey:
                        return isRu ? "Ключ реестра" : "Registry Key";
                    case LeftoverType.RegistryValue:
                        return isRu ? "Параметр реестра" : "Registry Value";
                    case LeftoverType.Shortcut:
                        return isRu ? "Ярлык" : "Shortcut";
                    case LeftoverType.Task:
                        return isRu ? "Задача" : "Task";
                    case LeftoverType.Service:
                        return isRu ? "Служба" : "Service";
                    default:
                        return isRu ? "Элемент" : "Item";
                }
            }
        }

        public string FormattedSize
        {
            get
            {
                bool isRu = Services.AppLang.Instance.IsRussian;
                if (SizeBytes <= 0)
                {
                    if (Type == LeftoverType.RegistryKey || Type == LeftoverType.RegistryValue)
                        return isRu ? "Реестр" : "Registry";
                    if (Type == LeftoverType.Task || Type == LeftoverType.Service)
                        return isRu ? "Система" : "System";
                    return "—";
                }
                double bytes = SizeBytes;
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
    }
}
