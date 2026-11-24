using System;
using System.ComponentModel;

namespace ApexUninstaller.Services
{
    public enum AppLanguage
    {
        Russian,
        English
    }

    public class AppLang : INotifyPropertyChanged
    {
        private static AppLang _instance;
        public static AppLang Instance
        {
            get
            {
                if (_instance == null) _instance = new AppLang();
                return _instance;
            }
        }

        private AppLanguage _currentLanguage = AppLanguage.Russian;
        public AppLanguage CurrentLanguage
        {
            get { return _currentLanguage; }
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged("");
                }
            }
        }

        public bool IsRussian
        {
            get { return _currentLanguage == AppLanguage.Russian; }
        }

        // Primary Navigation Modes
        public string NavApps
        {
            get { return IsRussian ? "Программы" : "Apps"; }
        }

        public string NavAutoruns
        {
            get { return IsRussian ? "Автозагрузка" : "Autoruns"; }
        }

        public string NavEverything
        {
            get { return "Everything"; }
        }

        // Autoruns Filter Tabs
        public string AutorunsAll
        {
            get { return IsRussian ? "Все" : "All"; }
        }

        public string AutorunsLogon
        {
            get { return "Logon"; }
        }

        public string AutorunsTasks
        {
            get { return IsRussian ? "Планировщик" : "Tasks"; }
        }

        public string AutorunsServices
        {
            get { return IsRussian ? "Службы" : "Services"; }
        }

        public string AutorunsExplorer
        {
            get { return IsRussian ? "Проводник" : "Explorer"; }
        }

        public string AutorunsDrivers
        {
            get { return IsRussian ? "Драйверы" : "Drivers"; }
        }

        public string HideMicrosoft
        {
            get { return IsRussian ? "Скрывать системные записи Microsoft" : "Hide Microsoft entries"; }
        }

        public string HideMissingFiles
        {
            get { return IsRussian ? "Скрывать отсутствующие файлы" : "Hide missing files"; }
        }

        public string EmptyAutoruns
        {
            get { return IsRussian ? "Записей автозагрузки не найдено" : "No autorun entries found"; }
        }

        // Autoruns Table Headers
        public string HeaderEntry
        {
            get { return IsRussian ? "Запись" : "Entry"; }
        }

        public string HeaderDescription
        {
            get { return IsRussian ? "Описание" : "Description"; }
        }

        public string HeaderPublisher
        {
            get { return IsRussian ? "Издатель" : "Publisher"; }
        }

        public string HeaderImagePath
        {
            get { return IsRussian ? "Путь к файлу" : "Image Path"; }
        }

        public string HeaderCategory
        {
            get { return IsRussian ? "Тип" : "Type"; }
        }

        // Autoruns Context Menu
        public string MenuEnable
        {
            get { return IsRussian ? "Включить" : "Enable"; }
        }

        public string MenuDisable
        {
            get { return IsRussian ? "Отключить" : "Disable"; }
        }

        public string MenuDeleteAutorun
        {
            get { return IsRussian ? "Удалить запись" : "Delete Entry"; }
        }

        public string BtnDeleteAutorun
        {
            get { return IsRussian ? "Удалить" : "Delete"; }
        }

        public string MenuOpenFileLocation
        {
            get { return IsRussian ? "Открыть папку с файлом" : "Open File Location"; }
        }

        public string MenuJumpToEntry
        {
            get { return IsRussian ? "Перейти к записи" : "Jump to Entry"; }
        }

        public string MenuCopyPath
        {
            get { return IsRussian ? "Копировать путь" : "Copy Path"; }
        }

        public string MenuVirusTotal
        {
            get { return IsRussian ? "Проверить на VirusTotal" : "Check VirusTotal"; }
        }

        // Warnings / Status
        public string StatusFileNotFound
        {
            get { return IsRussian ? "Файл не найден" : "File not found"; }
        }

        public string TipSelectAutorun
        {
            get { return IsRussian ? "Выберите запись для управления" : "Select an entry to manage"; }
        }

        public string DeleteAutorunConfirmTitle
        {
            get { return IsRussian ? "Удаление из автозагрузки" : "Delete Startup Entry"; }
        }

        public string DeleteAutorunConfirmMsg
        {
            get { return IsRussian ? "Вы уверены, что хотите безвозвратно удалить эту запись из автозагрузки?" : "Are you sure you want to permanently delete this startup entry?"; }
        }

        // Search & Filters
        public string SearchPlaceholder
        {
            get { return IsRussian ? "Поиск..." : "Search..."; }
        }

        public string SearchPlaceholderEverything
        {
            get { return IsRussian ? "Поиск файлов и папок..." : "Search files and folders..."; }
        }

        public string HeaderSearchName
        {
            get { return IsRussian ? "Имя" : "Name"; }
        }

        public string HeaderSearchPath
        {
            get { return IsRussian ? "Путь" : "Path"; }
        }

        public string HeaderSearchSize
        {
            get { return IsRussian ? "Размер" : "Size"; }
        }

        public string HeaderSearchDate
        {
            get { return IsRussian ? "Дата изменения" : "Date Modified"; }
        }

        public string SearchFilterAll
        {
            get { return IsRussian ? "Все" : "All"; }
        }

        public string SearchFilterFiles
        {
            get { return IsRussian ? "Файлы" : "Files"; }
        }

        public string SearchFilterFolders
        {
            get { return IsRussian ? "Папки" : "Folders"; }
        }

        public string SearchFilterApps
        {
            get { return IsRussian ? "Программы" : "Executables"; }
        }

        public string SearchOpen
        {
            get { return IsRussian ? "Открыть" : "Open"; }
        }

        public string SearchOpenFolder
        {
            get { return IsRussian ? "Папка" : "Folder"; }
        }

        public string SearchCopyPath
        {
            get { return IsRussian ? "Копировать путь" : "Copy Path"; }
        }

        public string SearchDelete
        {
            get { return IsRussian ? "Удалить" : "Delete"; }
        }

        public string SearchProperties
        {
            get { return IsRussian ? "Свойства" : "Properties"; }
        }

        public string SearchDeleteConfirmTitle
        {
            get { return IsRussian ? "Удаление файла" : "Delete File"; }
        }

        public string SearchDeleteConfirmMsg
        {
            get { return IsRussian ? "Вы действительно хотите переместить этот файл/папку в корзину?" : "Are you sure you want to move this file/folder to the Recycle Bin?"; }
        }

        public string SearchStatusActive
        {
            get { return IsRussian ? "Everything: Активен" : "Everything: Active"; }
        }

        public string TipSelectSearch
        {
            get { return IsRussian ? "Выберите файл или папку" : "Select a file or folder"; }
        }

        public string FilterAll
        {
            get { return IsRussian ? "Все" : "All"; }
        }

        public string FilterWin32
        {
            get { return "Win32"; }
        }

        public string FilterStore
        {
            get { return "Store"; }
        }

        // Action Buttons
        public string BtnUninstall
        {
            get { return IsRussian ? "Удалить" : "Uninstall"; }
        }

        public string BtnForce
        {
            get { return IsRussian ? "Принудительно" : "Force"; }
        }

        public string BtnFolder
        {
            get { return IsRussian ? "Папка" : "Folder"; }
        }

        public string BtnRegistry
        {
            get { return IsRussian ? "Реестр" : "Registry"; }
        }

        public string BtnGoogle
        {
            get { return "Google"; }
        }

        public string BtnRemoveEntry
        {
            get { return IsRussian ? "Скрыть" : "Remove"; }
        }

        public string TipSelectApp
        {
            get { return IsRussian ? "Выберите программу" : "Select an app"; }
        }

        // Table Headers
        public string ColName
        {
            get { return IsRussian ? "Программа" : "Application"; }
        }

        public string ColSize
        {
            get { return IsRussian ? "Размер" : "Size"; }
        }

        public string ColDate
        {
            get { return IsRussian ? "Дата" : "Date"; }
        }

        public string ColVersion
        {
            get { return IsRussian ? "Версия" : "Version"; }
        }

        public string ColType
        {
            get { return IsRussian ? "Тип" : "Type"; }
        }

        // Context Menu
        public string MenuUninstall
        {
            get { return IsRussian ? "Обычное удаление" : "Standard Uninstall"; }
        }

        public string MenuForce
        {
            get { return IsRussian ? "Принудительное удаление" : "Force Removal"; }
        }

        public string MenuFolder
        {
            get { return IsRussian ? "Открыть папку установки" : "Open Install Location"; }
        }

        public string MenuRegistry
        {
            get { return IsRussian ? "Открыть раздел в Реестре" : "Open in Registry (Regedit)"; }
        }

        public string MenuGoogle
        {
            get { return IsRussian ? "Поиск в Google" : "Search in Google"; }
        }

        public string MenuRemove
        {
            get { return IsRussian ? "Удалить запись из реестра" : "Remove Entry from Registry"; }
        }

        public string MenuCopy
        {
            get { return IsRussian ? "Копировать название" : "Copy Name"; }
        }

        // Status Messages
        public string StatusReady
        {
            get { return IsRussian ? "Выберите программу для удаления" : "Select an application to uninstall"; }
        }

        public string StatusScanning
        {
            get { return IsRussian ? "Сканирование системы..." : "Scanning system..."; }
        }

        public string StatusKilled
        {
            get { return IsRussian ? "Завершение процессов..." : "Terminating processes..."; }
        }

        public string StatusTraces
        {
            get { return IsRussian ? "Поиск оставшихся следов..." : "Scanning leftover traces..."; }
        }

        // Cleanup Window
        public string CleanupTitle
        {
            get { return IsRussian ? "Очистка оставшихся следов" : "Leftover Traces Cleanup"; }
        }

        public string CleanupHeader
        {
            get { return IsRussian ? "Обнаружены оставшиеся следы программы" : "Residual traces detected"; }
        }

        public string CleanupSelectAll
        {
            get { return IsRussian ? "Выбрать все" : "Select all"; }
        }

        public string CleanupBtnDelete
        {
            get { return IsRussian ? "Удалить следы" : "Delete traces"; }
        }

        public string CleanupBtnSkip
        {
            get { return IsRussian ? "Пропустить" : "Skip"; }
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
