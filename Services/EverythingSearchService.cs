using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ApexUninstaller.Models;

namespace ApexUninstaller.Services
{
    public static class EverythingSearchService
    {
        #region Win32 API

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetFileAttributesEx(string lpFileName, int fInfoLevelId, ref WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900b3;

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        private const uint WM_COPYDATA = 0x004A;
        private const uint EVERYTHING_IPC_COPYDATAQUERYW = 2;
        private const uint MSGFLT_ALLOW = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        #endregion

        #region Native NTFS USN Engine

        private class DirEntry
        {
            public ulong ParentFrn;
            public string Name;
        }

        private struct UsnFileEntry
        {
            public ulong ParentFrn;
            public string Name;
            public bool IsDir;
            public string DriveRoot;
        }

        private static readonly object _indexLock = new object();
        private static Dictionary<string, Dictionary<ulong, DirEntry>> _driveDirMaps = new Dictionary<string, Dictionary<ulong, DirEntry>>(StringComparer.OrdinalIgnoreCase);
        private static List<UsnFileEntry> _cachedEntries = null;
        private static Task _indexTask = null;

        public static void WarmUpNativeIndex()
        {
            lock (_indexLock)
            {
                if (_cachedEntries == null && _indexTask == null)
                {
                    _indexTask = Task.Run(() => BuildNativeIndex());
                }
            }
        }

        public static void InvalidateNativeIndex()
        {
            lock (_indexLock)
            {
                _cachedEntries = null;
                _indexTask = null;
                WarmUpNativeIndex();
            }
        }

        private static void BuildNativeIndex()
        {
            var newEntries = new List<UsnFileEntry>(300000);
            var newDirMaps = new Dictionary<string, Dictionary<ulong, DirEntry>>(StringComparer.OrdinalIgnoreCase);

            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                string driveLetter = drive.Name.TrimEnd('\\', ':');
                string volumePath = @"\\.\" + driveLetter + ":";
                string rootPrefix = driveLetter + @":\";

                SafeFileHandle h = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;

                try
                {
                    int bytesReturned;
                    IntPtr journalBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(USN_JOURNAL_DATA)));
                    bool ok = DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, journalBuf, Marshal.SizeOf(typeof(USN_JOURNAL_DATA)), out bytesReturned, IntPtr.Zero);
                    if (!ok)
                    {
                        Marshal.FreeHGlobal(journalBuf);
                        continue;
                    }

                    USN_JOURNAL_DATA ujd = (USN_JOURNAL_DATA)Marshal.PtrToStructure(journalBuf, typeof(USN_JOURNAL_DATA));
                    Marshal.FreeHGlobal(journalBuf);

                    int bufferSize = 64 * 1024;
                    IntPtr outBuf = Marshal.AllocHGlobal(bufferSize);

                    MFT_ENUM_DATA_V0 med = new MFT_ENUM_DATA_V0();
                    med.StartFileReferenceNumber = 0;
                    med.LowUsn = 0;
                    med.HighUsn = ujd.NextUsn;

                    int medSize = Marshal.SizeOf(typeof(MFT_ENUM_DATA_V0));
                    IntPtr inBuf = Marshal.AllocHGlobal(medSize);
                    Marshal.StructureToPtr(med, inBuf, true);

                    var dirMap = new Dictionary<ulong, DirEntry>(70000);

                    while (true)
                    {
                        ok = DeviceIoControl(h, FSCTL_ENUM_USN_DATA, inBuf, medSize, outBuf, bufferSize, out bytesReturned, IntPtr.Zero);
                        if (!ok || bytesReturned <= 8) break;

                        ulong nextFrn = (ulong)Marshal.ReadInt64(outBuf, 0);
                        Marshal.WriteInt64(inBuf, 0, (long)nextFrn);

                        int offset = 8;
                        while (offset < bytesReturned)
                        {
                            IntPtr pRecord = new IntPtr(outBuf.ToInt64() + offset);
                            int recordLen = Marshal.ReadInt32(pRecord, 0);
                            if (recordLen <= 0) break;

                            short major = Marshal.ReadInt16(pRecord, 4);
                            if (major == 2)
                            {
                                ulong frn = (ulong)Marshal.ReadInt64(pRecord, 8);
                                ulong parentFrn = (ulong)Marshal.ReadInt64(pRecord, 16);
                                uint fileAttrs = (uint)Marshal.ReadInt32(pRecord, 52);
                                short fnLen = Marshal.ReadInt16(pRecord, 56);
                                short fnOffset = Marshal.ReadInt16(pRecord, 58);

                                string name = Marshal.PtrToStringUni(new IntPtr(pRecord.ToInt64() + fnOffset), fnLen / 2);
                                bool isDir = (fileAttrs & 0x10) != 0;

                                if (isDir)
                                {
                                    dirMap[frn] = new DirEntry { ParentFrn = parentFrn, Name = name };
                                }

                                newEntries.Add(new UsnFileEntry
                                {
                                    ParentFrn = parentFrn,
                                    Name = name,
                                    IsDir = isDir,
                                    DriveRoot = rootPrefix
                                });
                            }

                            offset += recordLen;
                        }
                    }

                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);

                    newDirMaps[rootPrefix] = dirMap;
                }
                catch { }
                finally
                {
                    h.Close();
                }
            }

            lock (_indexLock)
            {
                _cachedEntries = newEntries;
                _driveDirMaps = newDirMaps;
                _indexTask = null;
            }
        }

        private static List<SearchResultItem> SearchNativeNtfs(string query, int maxResults, CancellationToken ct)
        {
            List<UsnFileEntry> snapshot;
            Dictionary<string, Dictionary<ulong, DirEntry>> dirMaps;

            lock (_indexLock)
            {
                if (_cachedEntries == null)
                {
                    BuildNativeIndex();
                }
                snapshot = _cachedEntries;
                dirMaps = _driveDirMaps;
            }

            if (ct.IsCancellationRequested)
            {
                return new List<SearchResultItem>();
            }

            if (snapshot == null || snapshot.Count == 0)
            {
                return null;
            }

            var results = new List<SearchResultItem>();

            Func<string, ulong, string> resolveDir = (rootPrefix, pFrn) =>
            {
                Dictionary<ulong, DirEntry> map;
                if (!dirMaps.TryGetValue(rootPrefix, out map) || map == null) return rootPrefix;

                var parts = new List<string>();
                ulong cur = pFrn;
                int depth = 0;
                while (depth++ < 40)
                {
                    DirEntry de;
                    if (!map.TryGetValue(cur, out de)) break;
                    if (string.IsNullOrEmpty(de.Name)) break;
                    parts.Add(de.Name);
                    if (de.ParentFrn == cur) break;
                    cur = de.ParentFrn;
                }
                parts.Reverse();
                if (parts.Count == 0) return rootPrefix.TrimEnd('\\');
                return rootPrefix + string.Join(@"\", parts);
            };

            string[] rawTokens = query.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawTokens == null || rawTokens.Length == 0) return new List<SearchResultItem>();

            var nameTokens = new List<string>();
            var pathTokens = new List<string>();
            for (int t = 0; t < rawTokens.Length; t++)
            {
                string tok = rawTokens[t];
                if (tok.IndexOf('\\') >= 0 || tok.IndexOf('/') >= 0)
                {
                    pathTokens.Add(tok.Replace('/', '\\'));
                }
                else
                {
                    nameTokens.Add(tok);
                }
            }

            int nameTokCount = nameTokens.Count;
            int pathTokCount = pathTokens.Count;

            int total = snapshot.Count;
            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested || results.Count >= maxResults) break;

                var entry = snapshot[i];
                if (entry.Name == null) continue;

                // 1. Fast match against file name for non-path tokens
                bool nameMatch = true;
                for (int t = 0; t < nameTokCount; t++)
                {
                    if (entry.Name.IndexOf(nameTokens[t], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        nameMatch = false;
                        break;
                    }
                }
                if (!nameMatch) continue;

                // 2. Resolve directory and full path
                string dirPath = null;
                string fullPath = null;

                if (pathTokCount > 0)
                {
                    dirPath = resolveDir(entry.DriveRoot, entry.ParentFrn);
                    fullPath = Path.Combine(dirPath, entry.Name);

                    bool pathMatch = true;
                    for (int t = 0; t < pathTokCount; t++)
                    {
                        if (fullPath.IndexOf(pathTokens[t], StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            pathMatch = false;
                            break;
                        }
                    }
                    if (!pathMatch) continue;
                }

                if (dirPath == null)
                {
                    dirPath = resolveDir(entry.DriveRoot, entry.ParentFrn);
                    fullPath = Path.Combine(dirPath, entry.Name);
                }

                long sizeBytes = -1;
                DateTime? dateMod = null;

                WIN32_FILE_ATTRIBUTE_DATA data = new WIN32_FILE_ATTRIBUTE_DATA();
                if (GetFileAttributesEx(fullPath, 0, ref data))
                {
                    if (!entry.IsDir)
                    {
                        sizeBytes = ((long)data.nFileSizeHigh << 32) | (long)data.nFileSizeLow;
                    }
                    long ft = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | (uint)data.ftLastWriteTime.dwLowDateTime;
                    try { dateMod = DateTime.FromFileTime(ft); } catch { }
                }

                string sizeDisp = (sizeBytes >= 0 && !entry.IsDir)
                    ? string.Format("{0:N0} КБ", (sizeBytes + 1023) / 1024).Replace(',', ' ')
                    : string.Empty;
                string dateDisp = dateMod.HasValue ? dateMod.Value.ToString("dd.MM.yyyy H:mm") : string.Empty;
                string ext = entry.IsDir ? string.Empty : Path.GetExtension(entry.Name);

                results.Add(new SearchResultItem
                {
                    Name = entry.Name,
                    Path = dirPath,
                    FullPath = fullPath,
                    IsDirectory = entry.IsDir,
                    SizeBytes = sizeBytes,
                    FormattedSize = sizeDisp,
                    DateModified = dateMod,
                    FormattedDate = dateDisp,
                    Extension = ext,
                    Icon = IconHelper.GetFileSystemIcon(fullPath, entry.IsDir)
                });
            }

            return results;
        }

        #endregion

        #region Everything IPC Worker (Used if Everything is already running)

        private static readonly object _workerLock = new object();
        private static IntPtr _replyHwnd = IntPtr.Zero;
        private static System.Windows.Threading.Dispatcher _workerDispatcher;
        private static int _queryCounter = 0;
        private static TaskCompletionSource<List<SearchResultItem>> _currentTcs;
        private static int _currentQueryId = 0;
        private static int _currentMaxResults = 250;
        private static CancellationToken _currentCt;

        private static void EnsureWorkerRunning()
        {
            lock (_workerLock)
            {
                if (_replyHwnd != IntPtr.Zero && _workerDispatcher != null) return;

                var readyEvent = new ManualResetEvent(false);

                Thread workerThread = new Thread(() =>
                {
                    try
                    {
                        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                        if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);

                        ChangeWindowMessageFilter(WM_COPYDATA, MSGFLT_ALLOW);

                        // Pure message-only window: ParentWindow = HWND_MESSAGE (-3), WindowStyle = 0
                        HwndSourceParameters p = new HwndSourceParameters("ZT_SilentEverythingWorker");
                        p.ParentWindow = new IntPtr(-3);
                        p.WindowStyle = 0;

                        HwndSource src = new HwndSource(p);
                        _replyHwnd = src.Handle;

                        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        {
                            if (msg == (int)WM_COPYDATA)
                            {
                                try
                                {
                                    COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                                    if (cds.lpData != IntPtr.Zero)
                                    {
                                        var items = ParseEverythingList(cds.lpData, _currentMaxResults, _currentCt);
                                        var tcs = _currentTcs;
                                        if (tcs != null)
                                        {
                                            tcs.TrySetResult(items);
                                        }
                                        handled = true;
                                        return (IntPtr)1;
                                    }
                                }
                                catch
                                {
                                    var tcs = _currentTcs;
                                    if (tcs != null) tcs.TrySetResult(null);
                                }
                            }
                            return IntPtr.Zero;
                        });

                        _workerDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        readyEvent.Set();
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch
                    {
                        readyEvent.Set();
                    }
                });

                workerThread.SetApartmentState(ApartmentState.STA);
                workerThread.IsBackground = true;
                workerThread.Name = "ZeroTrace_EverythingWorker";
                workerThread.Start();

                readyEvent.WaitOne(1500);
            }
        }

        public static bool IsEverythingRunning()
        {
            IntPtr ev = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
            if (ev != IntPtr.Zero) return true;

            try
            {
                IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    bool found = false;
                    Thread t = new Thread(() =>
                    {
                        if (SetThreadDesktop(hDesk))
                        {
                            found = (FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) != IntPtr.Zero);
                        }
                    });
                    t.Start();
                    t.Join(150);
                    return found;
                }
            }
            catch { }

            return false;
        }

        public static async Task<List<SearchResultItem>> SearchAsync(string query, int maxResults = 250, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<SearchResultItem>();
            }

            query = query.Trim();
            if (ct.IsCancellationRequested) return new List<SearchResultItem>();

            // 1. If Everything is running, query its in-memory index via IPC (1 ms)
            if (IsEverythingRunning())
            {
                try
                {
                    var evResults = await QueryEverythingIpcAsync(query, maxResults, ct);
                    if (evResults != null && !ct.IsCancellationRequested)
                    {
                        return evResults;
                    }
                }
                catch { }
            }

            if (ct.IsCancellationRequested) return new List<SearchResultItem>();

            try
            {
                var nativeResults = await Task.Run(() => SearchNativeNtfs(query, maxResults, ct), ct);
                if (nativeResults != null)
                {
                    return nativeResults;
                }
            }
            catch { }

            if (ct.IsCancellationRequested) return new List<SearchResultItem>();

            // 3. Fallback for non-NTFS drives (FAT32/exFAT)
            return await Task.Run(() => FallbackFileSystemSearch(query, maxResults, ct), ct);
        }

        private static Task<List<SearchResultItem>> QueryEverythingIpcAsync(string query, int maxResults, CancellationToken ct)
        {
            EnsureWorkerRunning();
            if (_replyHwnd == IntPtr.Zero || _workerDispatcher == null)
            {
                return Task.FromResult<List<SearchResultItem>>(null);
            }

            int queryId = Interlocked.Increment(ref _queryCounter);
            _currentQueryId = queryId;
            _currentMaxResults = maxResults;
            _currentCt = ct;

            var tcs = new TaskCompletionSource<List<SearchResultItem>>();
            _currentTcs = tcs;

            _workerDispatcher.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested || _currentQueryId != queryId)
                {
                    tcs.TrySetCanceled();
                    return;
                }

                IntPtr evWnd = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
                if (evWnd == IntPtr.Zero)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                byte[] searchBytes = Encoding.Unicode.GetBytes(query + "\0");
                int headerSize = 20;
                int totalSize = headerSize + searchBytes.Length;
                IntPtr pQuery = Marshal.AllocHGlobal(totalSize);

                try
                {
                    Marshal.WriteInt32(pQuery, 0, (int)_replyHwnd.ToInt64());
                    Marshal.WriteInt32(pQuery, 4, 1001);
                    Marshal.WriteInt32(pQuery, 8, 0);
                    Marshal.WriteInt32(pQuery, 12, 0);
                    Marshal.WriteInt32(pQuery, 16, maxResults);
                    Marshal.Copy(searchBytes, 0, new IntPtr(pQuery.ToInt64() + headerSize), searchBytes.Length);

                    COPYDATASTRUCT sendCds = new COPYDATASTRUCT();
                    sendCds.dwData = (IntPtr)EVERYTHING_IPC_COPYDATAQUERYW;
                    sendCds.cbData = totalSize;
                    sendCds.lpData = pQuery;

                    SendMessage(evWnd, WM_COPYDATA, _replyHwnd, ref sendCds);
                }
                finally
                {
                    Marshal.FreeHGlobal(pQuery);
                }
            }));

            var timeoutCts = new CancellationTokenSource(1200);
            timeoutCts.Token.Register(() =>
            {
                if (!tcs.Task.IsCompleted)
                {
                    tcs.TrySetResult(null);
                }
            });

            return tcs.Task;
        }

        private static List<SearchResultItem> ParseEverythingList(IntPtr pList, int maxResults, CancellationToken ct)
        {
            var list = new List<SearchResultItem>();
            if (pList == IntPtr.Zero) return list;

            uint numitems = (uint)Marshal.ReadInt32(pList, 20);
            int count = Math.Min((int)numitems, maxResults);

            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) return list;

                IntPtr pItem = new IntPtr(pList.ToInt64() + 28 + (i * 12));
                uint flags = (uint)Marshal.ReadInt32(pItem, 0);
                int fnOffset = Marshal.ReadInt32(pItem, 4);
                int pathOffset = Marshal.ReadInt32(pItem, 8);

                string filename = Marshal.PtrToStringUni(new IntPtr(pList.ToInt64() + fnOffset));
                string path = Marshal.PtrToStringUni(new IntPtr(pList.ToInt64() + pathOffset));
                bool isFolder = (flags & 1) != 0;
                string fullPath = Path.Combine(path, filename);

                long sizeBytes = -1;
                DateTime? dateMod = null;

                WIN32_FILE_ATTRIBUTE_DATA data = new WIN32_FILE_ATTRIBUTE_DATA();
                if (GetFileAttributesEx(fullPath, 0, ref data))
                {
                    if (!isFolder)
                    {
                        sizeBytes = ((long)data.nFileSizeHigh << 32) | (long)data.nFileSizeLow;
                    }
                    long fileTime = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | (uint)data.ftLastWriteTime.dwLowDateTime;
                    try
                    {
                        dateMod = DateTime.FromFileTime(fileTime);
                    }
                    catch { }
                }

                string sizeDisp = string.Empty;
                if (!isFolder && sizeBytes >= 0)
                {
                    long kb = (sizeBytes + 1023) / 1024;
                    sizeDisp = string.Format("{0:N0} КБ", kb).Replace(',', ' ');
                }

                string dateDisp = dateMod.HasValue ? dateMod.Value.ToString("dd.MM.yyyy H:mm") : string.Empty;
                string ext = isFolder ? string.Empty : Path.GetExtension(filename);

                list.Add(new SearchResultItem
                {
                    Name = filename,
                    Path = path,
                    FullPath = fullPath,
                    IsDirectory = isFolder,
                    SizeBytes = sizeBytes,
                    FormattedSize = sizeDisp,
                    DateModified = dateMod,
                    FormattedDate = dateDisp,
                    Extension = ext,
                    Icon = IconHelper.GetFileSystemIcon(fullPath, isFolder)
                });
            }

            return list;
        }

        private static List<SearchResultItem> FallbackFileSystemSearch(string query, int maxResults, CancellationToken ct)
        {
            var results = new List<SearchResultItem>();
            string pattern = "*" + query + "*";

            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (ct.IsCancellationRequested || results.Count >= maxResults) break;
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                var queue = new Queue<string>();
                queue.Enqueue(drive.RootDirectory.FullName);

                while (queue.Count > 0 && results.Count < maxResults && !ct.IsCancellationRequested)
                {
                    string currentDir = queue.Dequeue();

                    try
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                        {
                            if (ct.IsCancellationRequested || results.Count >= maxResults) break;
                            try
                            {
                                var di = new DirectoryInfo(subDir);
                                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                                string name = di.Name;
                                if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    results.Add(new SearchResultItem
                                    {
                                        Name = name,
                                        Path = di.Parent != null ? di.Parent.FullName : currentDir,
                                        FullPath = subDir,
                                        IsDirectory = true,
                                        SizeBytes = -1,
                                        FormattedSize = string.Empty,
                                        DateModified = di.LastWriteTime,
                                        FormattedDate = di.LastWriteTime.ToString("dd.MM.yyyy H:mm"),
                                        Extension = string.Empty,
                                        Icon = IconHelper.GetFileSystemIcon(subDir, true)
                                    });
                                }

                                queue.Enqueue(subDir);
                            }
                            catch { }
                        }

                        foreach (var file in Directory.EnumerateFiles(currentDir, pattern))
                        {
                            if (ct.IsCancellationRequested || results.Count >= maxResults) break;
                            try
                            {
                                var fi = new FileInfo(file);
                                long sizeBytes = fi.Length;
                                long kb = (sizeBytes + 1023) / 1024;
                                string sizeDisp = string.Format("{0:N0} КБ", kb).Replace(',', ' ');

                                results.Add(new SearchResultItem
                                {
                                    Name = fi.Name,
                                    Path = fi.DirectoryName,
                                    FullPath = file,
                                    IsDirectory = false,
                                    SizeBytes = sizeBytes,
                                    FormattedSize = sizeDisp,
                                    DateModified = fi.LastWriteTime,
                                    FormattedDate = fi.LastWriteTime.ToString("dd.MM.yyyy H:mm"),
                                    Extension = fi.Extension,
                                    Icon = IconHelper.GetFileSystemIcon(file, false)
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            return results;
        }

        #endregion
    }
}
