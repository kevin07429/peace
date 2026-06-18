using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PakToolGUI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PakFileItem> _selectedFiles = new();
    private readonly string _exeDir;
    private readonly string _exePath;
    private int _logLineCount;
    private volatile bool _isProcessing;
    private PakMetaInfo? _lastPakMeta;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly string EditorPath = Path.Combine("Tools", "UAssetGUI", "UAssetGUI.exe");
    private static readonly string TestRoot = @"D:\test";
    private static readonly string ManagedOutputRoot = Path.Combine(TestRoot, "PAKTool_Output");
    private static readonly string ManagedPakOutputDir = Path.Combine(ManagedOutputRoot, "Paks");
    private static readonly string ManagedRecipeOutputDir = Path.Combine(ManagedOutputRoot, "Recipes");
    private static readonly string ManagedMetadataOutputDir = Path.Combine(ManagedOutputRoot, "Metadata");
    private static readonly string ManagedUnpackOutputDir = Path.Combine(ManagedOutputRoot, "Unpack");

    public MainWindow()
    {
        InitializeComponent();
        FileListBox.ItemsSource = _selectedFiles;
        _exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _exePath = Path.Combine(_exeDir, "PRPakTool.exe");
        ConfigureDefaultOutputPaths();
        Log("PAK Tool v3.0 started", LogType.Accent);
        DetectOodle();
        CheckEditorAsync();
        UpdateCommanderKeyGlowState();
    }

    public class PakFileItem(string name, string fullPath, long size)
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public string SizeText { get; } = FormatSize(size);
    }

    public class PakMetaInfo
    {
        public int Version { get; set; }
        public bool EncryptedIndex { get; set; }
        public bool HasEncryptedData { get; set; }
        public List<string> CompressionMethods { get; set; } = new();
        public string PakPath { get; set; } = "";
        public int FooterSize { get; set; } = 44;
        public string MountPoint { get; set; } = "../../../";
        public uint PakMagic { get; set; } = 0x5A6F12E1;
        public PakPacker.CompressionType ResolvedCompression { get; set; }
    }

    private enum LogType { Info, Success, Warning, Error, Accent }

    static string FormatSize(long b) => b switch
    {
        >= 1_000_000_000 => (b / 1_000_000_000.0).ToString("F2") + " GB",
        >= 1_000_000 => (b / 1_000_000.0).ToString("F2") + " MB",
        >= 1_000 => (b / 1_000.0).ToString("F2") + " KB",
        _ => b + " B"
    };
    static SolidColorBrush LogBrush(LogType t) => t switch
    {
        LogType.Success => new(Color.FromRgb(0x9e, 0xce, 0x6a)),
        LogType.Warning => new(Color.FromRgb(0xe0, 0xaf, 0x68)),
        LogType.Error => new(Color.FromRgb(0xf7, 0x76, 0x8e)),
        LogType.Accent => new(Color.FromRgb(0x7a, 0xa2, 0xf7)),
        _ => new(Color.FromRgb(0xa9, 0xb1, 0xd6))
    };

    void Log(string msg, LogType type = LogType.Info) => Dispatcher.Invoke(() =>
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var run = new Run("[" + ts + "] " + msg)
        {
            Foreground = LogBrush(type), FontFamily = new FontFamily("Consolas"), FontSize = 11
        };
        TxtLog.Document.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0), LineHeight = 18 });
        _logLineCount++;
        TxtLogCount.Text = "(" + _logLineCount + ")";
        TxtLog.ScrollToEnd();
    });

    void UpdateFileListUI()
    {
        var c = _selectedFiles.Count;
        FileListScroll.Visibility = c > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnUnpack.IsEnabled = c > 0 && !_isProcessing;
        TxtFileCount.Text = c > 0 ? "Selected: " + c + " files" : "";
        TxtStatus.Text = c > 0 ? "Ready - " + c + " files" : "Ready";
    }

    void UpdatePackState()
    {
        BtnPack.IsEnabled = !string.IsNullOrWhiteSpace(TxtPackSource.Text) && Directory.Exists(TxtPackSource.Text) && !_isProcessing;
    }

    void UpdateCommanderKeyGlowState()
    {
        var hasRecipe = File.Exists(CommanderKeyBinaryRecipePath);
        BtnCommanderKeyGlow.IsEnabled = !_isProcessing;
        TxtCommanderKeyGlowStatus.Text = hasRecipe
            ? "Commander Key recipe ready: " + Path.GetFileName(CommanderKeyBinaryRecipePath)
            : "Commander Key will auto-search the current safe PAKs. Learn Key Glow is still preferred when you have a verified sample.";
        TxtCommanderKeyGlowStatus.Foreground = hasRecipe ? LogBrush(LogType.Success) : LogBrush(LogType.Warning);
    }

    void ConfigureDefaultOutputPaths()
    {
        Directory.CreateDirectory(ManagedPakOutputDir);
        Directory.CreateDirectory(ManagedRecipeOutputDir);
        Directory.CreateDirectory(ManagedMetadataOutputDir);
        Directory.CreateDirectory(ManagedUnpackOutputDir);
        TxtOutputDir.Text = ManagedUnpackOutputDir;
    }

    void DetectAndApply(string pakPath)
    {
        if (_lastPakMeta != null) return;
        try
        {
            _lastPakMeta = ReadPakMeta(pakPath);
            TryWriteCompressionManifest(pakPath, _lastPakMeta);
            Log("Detected: v" + _lastPakMeta.Version + " comp=" + _lastPakMeta.ResolvedCompression + " enc=" + _lastPakMeta.HasEncryptedData + " footer=" + _lastPakMeta.FooterSize + "B", LogType.Accent);
            Dispatcher.Invoke(() =>
            {
                ExpanderPack.IsExpanded = true;
                TxtPackDetected.Text = "Detected: Compression=" + _lastPakMeta.ResolvedCompression + ", Encrypted=" + _lastPakMeta.HasEncryptedData;
                TxtPackDetected.Foreground = LogBrush(LogType.Success);
                TxtMountPoint.Text = _lastPakMeta.MountPoint;
                UpdatePackState();
            });
        }
        catch (Exception ex) { Log("Cannot read PAK meta: " + ex.Message, LogType.Warning); }
    }

    void TryWriteCompressionManifest(string pakPath, PakMetaInfo meta)
    {
        try
        {
            var manifest = PakIndexReader.ReadPakIndex(pakPath);
            if (manifest.FileCompression.Count == 0) return;
            if (!string.IsNullOrWhiteSpace(manifest.MountPoint))
                meta.MountPoint = manifest.MountPoint;

            Directory.CreateDirectory(ManagedMetadataOutputDir);
            var manifestPath = Path.Combine(ManagedMetadataOutputDir, Path.GetFileNameWithoutExtension(pakPath) + ".manifest.json");
            PakIndexReader.SaveManifest(manifest, manifestPath);
            var mostUsedMethod = manifest.FileCompression.Values
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            meta.ResolvedCompression = PakIndexReader.MethodToType(mostUsedMethod);
            Log("Saved compression manifest: " + manifest.FileCompression.Count + " files -> " + manifestPath, LogType.Success);
        }
        catch (Exception ex)
        {
            Log("Compression manifest skipped: " + ex.Message, LogType.Warning);
        }
    }

    static PakMetaInfo ReadPakMeta(string path)
    {
        const uint Magic = 0x5A6F12E1;
        byte[] pubgmCnZucKey = { 0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37 };
        byte[] pubgmCnZucIv  = { 0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45 };
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs);
        var meta = new PakMetaInfo { PakPath = path };

        // PUBGM_CN: EncIdx(1)+Magic(4)+Ver(4)+Hash(20)+Size(8)+Off(8)=45 bytes
        const uint PubgmCnMagic = 0xFF67FF70;
        const int PubgmFooter = 45;
        if (fs.Length >= PubgmFooter)
        {
            fs.Seek(-PubgmFooter, SeekOrigin.End);
            byte encIdx = r.ReadByte();
            if (r.ReadUInt32() == PubgmCnMagic)
            {
                meta.PakMagic = PubgmCnMagic;
                meta.Version = (int)r.ReadUInt32();
                meta.FooterSize = PubgmFooter;
                var zucKeys = ZucCipher.GenerateKeyArray(pubgmCnZucKey, pubgmCnZucIv, 16);
                var decodedEncIdx = (byte)(encIdx ^ (byte)(zucKeys[3] & 0xFF));
                meta.EncryptedIndex = decodedEncIdx != 0;
                meta.HasEncryptedData = meta.EncryptedIndex;
                meta.CompressionMethods.AddRange(new[] { "Zlib", "Gzip", "Oodle", "Zstd", "LZ4" });
                meta.ResolvedCompression = PakPacker.CompressionType.Zlib;
                meta.MountPoint = "../../../";
                return meta;
            }
        }

        foreach (var ver in new[] { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 })
        {
            int fsize = 4 + 4 + 8 + 8 + 20;
            if (ver >= 4) fsize += 1;
            if (ver >= 7) fsize += 16;
            if (ver == 9) fsize += 1;
            if (ver >= 8) fsize += 32 * 5;
            if (fs.Length < fsize) continue;
            fs.Seek(-fsize, SeekOrigin.End);
            if (r.ReadUInt32() != Magic) continue;
            if (r.ReadUInt32() != ver) continue;
            meta.PakMagic = Magic;
            meta.Version = (int)ver;
            meta.FooterSize = fsize;
            r.ReadInt64(); r.ReadInt64();
            r.ReadBytes(20);
            if (ver >= 4) meta.EncryptedIndex = r.ReadByte() != 0;
            if (ver >= 7) r.ReadBytes(16);
            if (ver == 9) r.ReadByte();
            if (ver >= 8)
                for (int i = 0; i < 5; i++)
                {
                    var n = Encoding.ASCII.GetString(r.ReadBytes(32)).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(n)) meta.CompressionMethods.Add(n);
                }
            else meta.CompressionMethods.AddRange(new[] { "Zlib", "Gzip", "Oodle" });
            meta.HasEncryptedData = meta.EncryptedIndex;
            var m = meta.CompressionMethods;
            if (m.Contains("Zstd")) meta.ResolvedCompression = PakPacker.CompressionType.Zstd;
            else if (m.Contains("Oodle")) meta.ResolvedCompression = PakPacker.CompressionType.Oodle;
            else if (m.Contains("Zlib") || m.Contains("Gzip")) meta.ResolvedCompression = PakPacker.CompressionType.Zlib;
            else meta.ResolvedCompression = PakPacker.CompressionType.None;
            return meta;
        }
        meta.PakMagic = Magic;
        meta.Version = 8;
        meta.FooterSize = 44;
        meta.CompressionMethods.AddRange(new[] { "Zlib", "Gzip", "Oodle", "Zstd", "LZ4" });
        meta.ResolvedCompression = PakPacker.CompressionType.Zlib;
        return meta;
    }

    void DetectOodle()
    {
        foreach (var dll in new[] { "oo2core_9_win64.dll", "oo2core_8_win64.dll", "oo2core_6_win64.dll" })
        {
            var p = Path.Combine(_exeDir, dll);
            if (File.Exists(p)) { TxtOodlePath.Text = p; Log("Found Oodle: " + dll, LogType.Success); return; }
            foreach (var dir in new[]
                     {
                         Path.Combine(_exeDir, "Tools", "Native"),
                         Path.Combine(_exeDir, "..", "Tools", "Native"),
                         Path.Combine(_exeDir, "..", "..", "Tools", "Native"),
                         Path.Combine(_exeDir, "publish")
                     })
            {
                p = Path.GetFullPath(Path.Combine(dir, dll));
                if (File.Exists(p)) { TxtOodlePath.Text = p; Log("Found Oodle: " + dll, LogType.Success); return; }
            }
        }
        Log("Oodle DLL not found", LogType.Warning);
    }

    async void CheckEditorAsync()
    {
        var ep = Path.Combine(_exeDir, EditorPath);
        if (File.Exists(ep)) { BtnLaunchEditor.IsEnabled = true; return; }
        Log("Downloading UAssetGUI...", LogType.Warning);
        BtnLaunchEditor.IsEnabled = false;
        try { await DownloadEditorAsync(); }
        catch (Exception ex) { Log("Editor download failed: " + ex.Message, LogType.Error); }
    }

    async Task DownloadEditorAsync()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PakToolGUI/3.0");
        var dir = Path.Combine(_exeDir, "Tools", "UAssetGUI");
        Directory.CreateDirectory(dir);
        var asset = await FindEditorAssetAsync();
        if (asset.Url == null) throw new Exception("No download URL");
        var bytes = await _http.GetByteArrayAsync(asset.Url);
        if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tmp = Path.Combine(ManagedOutputRoot, "editor_temp.zip");
            Directory.CreateDirectory(ManagedOutputRoot);
            await File.WriteAllBytesAsync(tmp, bytes);
            ZipFile.ExtractToDirectory(tmp, dir, true);
            File.Delete(tmp);
        }
        else if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "UAssetGUI.exe"), bytes);
        }
        else
        {
            throw new Exception("Unsupported editor asset: " + asset.Name);
        }
        BtnLaunchEditor.IsEnabled = true;
        Log("UAssetGUI ready", LogType.Success);
    }

    async Task<(string? Url, string Name)> FindEditorAssetAsync()
    {
        foreach (var endpoint in new[]
        {
            "https://api.github.com/repos/atenfyr/UAssetGUI/releases/latest",
            "https://api.github.com/repos/atenfyr/UAssetGUI/releases/tags/experimental-latest"
        })
        {
            var asset = await FindEditorAssetInReleaseAsync(endpoint);
            if (asset.Url != null) return asset;
        }

        var json = await _http.GetStringAsync("https://api.github.com/repos/atenfyr/UAssetGUI/releases");
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var asset = FindEditorAssetInJson(release);
            if (asset.Url != null) return asset;
        }

        return (null, "");
    }

    async Task<(string? Url, string Name)> FindEditorAssetInReleaseAsync(string endpoint)
    {
        var json = await _http.GetStringAsync(endpoint);
        using var doc = JsonDocument.Parse(json);
        return FindEditorAssetInJson(doc.RootElement);
    }

    static (string? Url, string Name) FindEditorAssetInJson(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets))
            return (null, "");

        (string? Url, string Name) fallback = (null, "");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url == null) continue;
            if (name.Equals("UAssetGUI.exe", StringComparison.OrdinalIgnoreCase))
                return (url, name);
            if (fallback.Url == null && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                fallback = (url, name);
        }

        return fallback;
    }

    void Window_DragEnter(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy; }
    void Window_Drop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files); }

    void AddFiles(string[] paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Path.GetExtension(p).ToLowerInvariant() != ".pak") continue;
            var info = new FileInfo(p);
            if (_selectedFiles.Any(f => f.FullPath == info.FullName)) continue;
            _selectedFiles.Add(new PakFileItem(info.Name, info.FullName, info.Length));
            Log("Added: " + info.Name + " (" + FormatSize(info.Length) + ")", LogType.Success);
            DetectAndApply(p);
        }
        UpdateFileListUI();
    }

    void BtnBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select PAK", Filter = "PAK (*.pak)|*.pak|All|*.*", Multiselect = true, InitialDirectory = _exeDir };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }
    void BtnClear_Click(object s, RoutedEventArgs e) { _selectedFiles.Clear(); _lastPakMeta = null; UpdateFileListUI(); Log("Cleared"); }
    void BtnBrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Output", InitialDirectory = ManagedOutputRoot };
        if (dlg.ShowDialog() == true) TxtOutputDir.Text = dlg.FolderName;
    }
    void BtnOpenOutput_Click(object s, RoutedEventArgs e)
    {
        var p = ResolvePath(TxtOutputDir.Text.Trim(), ManagedUnpackOutputDir);
        Directory.CreateDirectory(p);
        Process.Start("explorer.exe", "\"" + p + "\"");
    }
    void BtnUnpack_Click(object s, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        _ = UnpackAsync();
    }

    async Task UnpackAsync()
    {
        _isProcessing = true;
        Dispatcher.Invoke(() => { BtnUnpack.IsEnabled = false; BtnPack.IsEnabled = false; });
        var sw = Stopwatch.StartNew();
        var outputBase = ResolvePath(TxtOutputDir.Text.Trim(), ManagedUnpackOutputDir);
        Directory.CreateDirectory(outputBase);
        Log("Output: " + outputBase, LogType.Accent);

        var total = _selectedFiles.Count;
        var done = 0;
        foreach (var file in _selectedFiles.ToList())
        {
            done++;
            var pakDir = outputBase;
            Directory.CreateDirectory(pakDir);
            Log("Unpacking (" + done + "/" + total + "): " + file.Name, LogType.Info);

            var args = "\"" + file.FullPath + "\" -output \"" + pakDir + "\"";
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = args,
                WorkingDirectory = pakDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                var proc = Process.Start(psi);
                if (proc == null) { Log("Failed to start PRPakTool", LogType.Error); continue; }

                var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await proc.WaitForExitAsync(cts.Token);

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();

                if (proc.ExitCode == 0)
                    Log("Unpacked: " + Path.GetFileName(pakDir) + " (" + FormatSize(new DirectoryInfo(pakDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)) + ")", LogType.Success);
                else
                    Log("Unpack failed (exit " + proc.ExitCode + "): " + stderr, LogType.Error);

                var pct = (int)(done / (double)total * 100);
                Dispatcher.Invoke(() => { ProgressBar.Value = pct; TxtProgress.Text = "Unpacking " + done + "/" + total; TxtProgressPercent.Text = pct + "%"; });
            }
            catch (Exception ex) { Log("Error: " + ex.Message, LogType.Error); }
        }

        _isProcessing = false;
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = 100;
            TxtProgress.Text = "Done - " + done + " PAKs in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s";
            TxtProgressPercent.Text = "100%";
            BtnUnpack.IsEnabled = _selectedFiles.Count > 0;
            BtnPack.IsEnabled = !string.IsNullOrWhiteSpace(TxtPackSource.Text) && Directory.Exists(TxtPackSource.Text);
            TxtStatus.Text = "Unpack complete";
        });
        Log("Unpack finished in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s", LogType.Success);
    }

    void BtnPackBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select source folder to pack", InitialDirectory = ManagedUnpackOutputDir };
        if (dlg.ShowDialog() == true)
        {
            TxtPackSource.Text = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(TxtPackOutput.Text) && _lastPakMeta != null)
            {
                var srcName = Path.GetFileName(dlg.FolderName);
                TxtPackOutput.Text = Path.Combine(ManagedPakOutputDir, srcName + ".pak");
            }
        }
    }

    void BtnPackOutputBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Save PAK as", Filter = "PAK (*.pak)|*.pak|All|*.*", InitialDirectory = ManagedPakOutputDir, FileName = "output.pak" };
        if (dlg.ShowDialog() == true) TxtPackOutput.Text = dlg.FileName;
    }

    void TxtPackSource_TextChanged(object s, TextChangedEventArgs e) => UpdatePackState();

    void BtnPack_Click(object s, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        _ = DoPack();
    }

    static readonly string MapLobbyPak = Path.Combine(TestRoot, "map_lobby_1.36.11.15210.pak");
    static readonly string SampleRoot = Path.Combine(TestRoot, "\u4fee\u6539\u8fc7\u7684");
    static readonly string GamePakRoot = @"D:\WeGameApps\rail_apps\wgprojectm(2002291)\ShadowTrackerExtra\Saved\Paks";
    static string RecoilSamplePak => Path.Combine(SampleRoot, "\u540e\u5750\u529b", "map_lobby_1.36.11.15210.pak");
    static string GlowOriginalPak => Path.Combine(SampleRoot, "\u6a21\u578b\u53d1\u5149", "\u539f\u6587\u4ef6.pak");
    static string GlowSamplePak => Path.Combine(SampleRoot, "\u6a21\u578b\u53d1\u5149", "game_patch_1.36.11.15380.pak");
    static string GlowGamePak => Path.Combine(GamePakRoot, "game_patch_1.36.11.15380.pak");
    static string GlowRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_glow_recipe.json");
    static string GlowBinaryRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_glow_binary_patch.json");
    static string RangeRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_range_recipe.json");
    static string RangeBinaryRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_range_binary_patch.json");
    static string RecoilRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_recoil_recipe.json");
    static string RecoilBinaryRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_recoil_binary_patch.json");
    static string CommanderKeyBinaryRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_commander_key_binary_patch.json");
    static string CommanderKeyScaleGlowRecipePath => Path.Combine(ManagedRecipeOutputDir, "learned_commander_key_scale_glow_assets.json");
    static string LegacyGlowRecipePath => Path.Combine(TestRoot, "learned_glow_recipe.json");
    static string CommanderKeyPak => Path.Combine(GamePakRoot, "game_patch_1.36.11.15320.pak");
    static string SkinMaterialCsvPath => Path.Combine(ManagedMetadataOutputDir, "player_skin_material_parameters.csv");
    const float DefaultMaterialGlowPower = 2f;
    const float CommanderKeyGlowPower = 5f;

    void BtnPresetRange_Click(object s, RoutedEventArgs e)
    {
        var multiplier = ParseFloatOrDefault(TxtRangeMultiplier.Text, 2f);
        var targetPak = File.Exists(RangeBinaryRecipePath)
            ? FindBinaryPatchSourcePak(RangeBinaryRecipePath)
            : FindBestPakContainingAsset(PakPatchPacker.RangePhysicsAssetPath) ?? MapLobbyPak;
        if (!File.Exists(targetPak))
        {
            Log("Range target PAK not found. Keep the original map_lobby that matches learned_range_binary_patch.json under D:\\test, or select it manually.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(targetPak, "range.pak");
        _ = RunPresetAsync("Range auto x" + multiplier.ToString("0.###"), targetPak, outputPak,
            () => ApplyAutoRangePatch(targetPak, outputPak, multiplier));
    }

    void BtnPresetRecoil_Click(object s, RoutedEventArgs e)
    {
        var scale = ParseFloatOrDefault(TxtRecoilScale.Text, 0.125f);
        var targetPak = FindBestRecoilTargetPak();
        if (!File.Exists(targetPak))
        {
            Log("Recoil target PAK not found. Keep the original map_lobby that matches learned_recoil_binary_patch.json under D:\\test, or select it manually.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(targetPak, "recoil.pak");
        _ = RunPresetAsync("Recoil auto x" + scale.ToString("0.###"), targetPak, outputPak,
            () => ApplyAutoRecoilPatch(targetPak, outputPak, scale));
    }

    void BtnPresetCombined_Click(object s, RoutedEventArgs e)
    {
        var multiplier = ParseFloatOrDefault(TxtRangeMultiplier.Text, 2f);
        var scale = ParseFloatOrDefault(TxtRecoilScale.Text, 0.125f);
        var targetPak = FindBestRangeAndRecoilTargetPak() ?? MapLobbyPak;
        if (!File.Exists(targetPak))
        {
            Log("Combined Range + Recoil target PAK not found. Select a PAK that contains the required assets.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(targetPak, "range_recoil.pak");
        _ = RunPresetAsync("Range auto x" + multiplier.ToString("0.###") + " + Recoil auto x" + scale.ToString("0.###"),
            targetPak,
            outputPak,
            () => ApplyAutoRangeAndRecoilPatch(targetPak, outputPak, multiplier, scale));
    }

    void BtnPresetGlow_Click(object s, RoutedEventArgs e)
    {
        var colorName = SanitizeFileToken(TxtGlowColor.Text.Trim().TrimStart('#'), "fixed");
        var glowTargetPak = FindBestBasicGlowTargetPak();
        if (!File.Exists(glowTargetPak))
        {
            Log("Glow target PAK not found. Select or install a game_patch_*.pak first.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(glowTargetPak, "glow_" + colorName + ".pak");
        Log("Basic game_patch Glow target: " + Path.GetFileName(glowTargetPak), LogType.Accent);
        _ = RunPresetAsync("Basic game_patch Glow #" + colorName, glowTargetPak, outputPak,
            () => ApplyAutoGlowPatch(glowTargetPak, outputPak));
    }

    string? FindBestBasicGlowTargetPak()
    {
        if (File.Exists(GlowBinaryRecipePath))
        {
            var binaryTarget = FindBinaryPatchSourcePak(GlowBinaryRecipePath);
            if (binaryTarget != null)
            {
                Log("Basic Glow target matched learned binary recipe: " + Path.GetFileName(binaryTarget), LogType.Accent);
                return binaryTarget;
            }

            LogBinaryRecipeSourceMismatch(GlowBinaryRecipePath, "Basic Glow");
            Log("No game_patch matches learned binary glow recipe SHA; falling back to latest game_patch for relocatable glow.", LogType.Warning);
        }

        return FindLatestOriginalGamePatch() ?? (File.Exists(GlowGamePak) ? GlowGamePak : GlowOriginalPak);
    }

    void LogBinaryRecipeSourceMismatch(string recipePath, string label)
    {
        try
        {
            var recipe = JsonSerializer.Deserialize<PakPatchPacker.BinaryPatchRecipe>(
                File.ReadAllText(recipePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
            if (recipe == null) return;
            Log($"{label} binary recipe expects source size={recipe.SourceSize}, sha1={recipe.SourceSha1}. Current candidate did not match.", LogType.Warning);
        }
        catch
        {
            // Non-fatal diagnostics only.
        }
    }

    PakPatchPacker.Result ApplyAutoGlowPatch(string targetPak, string outputPak)
    {
        Exception? binaryPatchError = null;
        if (File.Exists(GlowBinaryRecipePath))
        {
            try
            {
                Log("Glow mode: exact binary patch recipe.", LogType.Accent);
                return PakPatchPacker.ApplyBinaryPatchRecipe(targetPak, GlowBinaryRecipePath, outputPak);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                binaryPatchError = ex;
                Log("Exact binary glow patch did not match this version: " + ex.Message, LogType.Warning);
            }
        }

        var sourceRecipePath = File.Exists(GlowRecipePath)
            ? GlowRecipePath
            : File.Exists(LegacyGlowRecipePath) ? LegacyGlowRecipePath : null;
        if (sourceRecipePath == null)
        {
            if (binaryPatchError != null)
                throw new InvalidOperationException("No relocatable glow recipe found after binary patch mismatch. Learn Glow from an original PAK and a verified glow PAK first.", binaryPatchError);
            throw new InvalidOperationException("No learned glow recipe found. Use Learn Glow with an original PAK and a verified glow PAK first.");
        }

        Directory.CreateDirectory(ManagedRecipeOutputDir);
        var relocatedRecipePath = Path.Combine(
            ManagedRecipeOutputDir,
            Path.GetFileNameWithoutExtension(targetPak) + "_glow_relocated.json");

        Log("Glow mode: relocate blueprint recipe for this PAK.", LogType.Accent);
        var relocate = PakPatchPacker.RelocateRecipe(targetPak, sourceRecipePath, relocatedRecipePath);
        Log($"Glow recipe relocate: moved={relocate.RelocatedOperations}, same={relocate.UnchangedOperations}, failed={relocate.FailedOperations}", LogType.Info);
        if (relocate.FailedOperations > 0)
            throw new InvalidOperationException($"Glow recipe relocation failed for {relocate.FailedOperations}/{relocate.TotalOperations} operations. Learn Glow again for this version.");

        Log("Glow mode: applying relocated recipe with in-place PAK patch only.", LogType.Accent);
        return PakPatchPacker.ApplyJsonRecipeInPlaceOnly(targetPak, relocatedRecipePath, outputPak);
    }

    PakPatchPacker.Result ApplyAutoRangePatch(string targetPak, string outputPak, float multiplier)
    {
        if (File.Exists(RangeBinaryRecipePath) && Math.Abs(multiplier - 2f) < 0.0001f)
        {
            Log("Range mode: exact binary patch recipe.", LogType.Accent);
            return PakPatchPacker.ApplyBinaryPatchRecipe(targetPak, RangeBinaryRecipePath, outputPak);
        }

        throw new InvalidOperationException("Range auto currently only enables the verified x2 recipe. x5 PhysicsAsset candidates caused map_lobby load crashes and are blocked until a stable field is found.");
    }

    PakPatchPacker.Result ApplyAutoRecoilPatch(string targetPak, string outputPak, float scale)
    {
        if (File.Exists(RecoilBinaryRecipePath) && Math.Abs(scale - 0.125f) < 0.0001f)
        {
            Log("Recoil mode: exact binary patch recipe.", LogType.Accent);
            return PakPatchPacker.ApplyBinaryPatchRecipe(targetPak, RecoilBinaryRecipePath, outputPak);
        }

        throw new InvalidOperationException("Recoil auto requires the exact learned binary recipe and matching original map_lobby PAK. Do not apply recoil to small game_patch PAKs.");
    }

    PakPatchPacker.Result ApplyAutoRangeAndRecoilPatch(string targetPak, string outputPak, float rangeMultiplier, float recoilScale)
    {
        if (Math.Abs(rangeMultiplier - 2f) >= 0.0001f)
            throw new InvalidOperationException("Range + Recoil currently only enables the verified Range x2 recipe. x5 PhysicsAsset candidates caused map_lobby load crashes and are blocked.");
        var rangeRecipePath = RangeBinaryRecipePath;
        if (!File.Exists(rangeRecipePath))
            throw new InvalidOperationException("Range + Recoil requires the learned Range x2 binary recipe.");
        if (!File.Exists(RecoilBinaryRecipePath) || Math.Abs(recoilScale - 0.125f) >= 0.0001f)
            throw new InvalidOperationException("Range + Recoil requires the learned Recoil 0.125 binary recipe.");

        Log("Range + Recoil mode: exact binary recipe merge.", LogType.Accent);
        return PakPatchPacker.ApplyBinaryPatchRecipes(
            targetPak,
            new[] { rangeRecipePath, RecoilBinaryRecipePath },
            outputPak);
    }

    string RelocateRecipeForTarget(string targetPak, string sourceRecipePath, string label)
    {
        Directory.CreateDirectory(ManagedRecipeOutputDir);
        var relocatedRecipePath = Path.Combine(
            ManagedRecipeOutputDir,
            Path.GetFileNameWithoutExtension(targetPak) + "_" + label + "_relocated.json");
        var result = PakPatchPacker.RelocateRecipe(targetPak, sourceRecipePath, relocatedRecipePath);
        Log($"{label} recipe relocate: moved={result.RelocatedOperations}, same={result.UnchangedOperations}, failed={result.FailedOperations}", LogType.Info);
        if (result.FailedOperations > 0)
            throw new InvalidOperationException($"{label} recipe relocation failed for {result.FailedOperations}/{result.TotalOperations} operations. Re-learn this recipe for the selected game version.");
        return relocatedRecipePath;
    }

    PakPatchPacker.PatchRecipe LoadRelocatedAndScaledRecipe(string targetPak, string sourceRecipePath, string label, Action<PakPatchPacker.PatchRecipeOperation> adjust)
    {
        var relocatedRecipePath = RelocateRecipeForTarget(targetPak, sourceRecipePath, label);
        var recipe = JsonSerializer.Deserialize<PakPatchPacker.PatchRecipe>(
            File.ReadAllText(relocatedRecipePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException(label + " recipe JSON is empty or invalid.");
        foreach (var op in recipe.Operations)
            adjust(op);
        return recipe;
    }

    string WriteScaledRecipe(string relocatedRecipePath, string targetPak, string label, Action<PakPatchPacker.PatchRecipeOperation> adjust)
    {
        var recipe = JsonSerializer.Deserialize<PakPatchPacker.PatchRecipe>(
            File.ReadAllText(relocatedRecipePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException(label + " recipe JSON is empty or invalid.");
        foreach (var op in recipe.Operations)
            adjust(op);

        var scaledRecipePath = Path.Combine(
            ManagedRecipeOutputDir,
            Path.GetFileNameWithoutExtension(targetPak) + "_" + label + "_scaled.json");
        File.WriteAllText(scaledRecipePath, JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        return scaledRecipePath;
    }

    static string AppendUiComment(string? comment, string note)
    {
        return string.IsNullOrWhiteSpace(comment) ? note : comment + "; " + note;
    }

    static bool TryParseGamePatchVersion(string fileNameNoExtension, out Version? version)
    {
        version = null;
        const string prefix = "game_patch_";
        if (!fileNameNoExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var text = fileNameNoExtension[prefix.Length..];
        return Version.TryParse(text, out version);
    }

    string? FindLatestOriginalGamePatch()
    {
        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Where(path => Path.GetFileName(path).StartsWith("game_patch_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var roots = new[]
            {
                GamePakRoot,
                @"D:\WeGameApps\rail_apps\wgprojectm(2002291)\ShadowTrackerExtra\Content\Paks",
                TestRoot
            }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "game_patch_*.pak", SearchOption.TopDirectoryOnly));

        return selected
            .Concat(roots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                Path = path,
                IsSelected = selected.Contains(path, StringComparer.OrdinalIgnoreCase),
                Size = new FileInfo(path).Length,
                Version = TryParseGamePatchVersion(Path.GetFileNameWithoutExtension(path), out var version) ? version : null
            })
            .Where(x => x.Version != null && x.Size >= 10_000_000)
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    IEnumerable<string> EnumerateKnownOriginalPakCandidates()
    {
        foreach (var file in _selectedFiles.Select(item => item.FullPath).Where(File.Exists))
            yield return file;
        if (File.Exists(MapLobbyPak))
            yield return MapLobbyPak;
        if (File.Exists(RecoilSamplePak))
            yield return RecoilSamplePak;

        foreach (var root in new[]
                 {
                     GamePakRoot,
                     @"D:\WeGameApps\rail_apps\wgprojectm(2002291)\ShadowTrackerExtra\Content\Paks"
                 }.Where(Directory.Exists))
        {
            foreach (var pak in Directory.GetFiles(root, "*.pak", SearchOption.TopDirectoryOnly))
                yield return pak;
        }
    }

    string? FindBestPakContainingAsset(string assetPath)
    {
        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = selected
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = PakPatchPacker.FindPaksContainingAsset(candidates, assetPath);
        return matches
            .Select(path => new
            {
                Path = path,
                IsSelected = selected.Contains(path, StringComparer.OrdinalIgnoreCase),
                Version = TryParseGamePatchVersion(Path.GetFileNameWithoutExtension(path), out var version) ? version : null,
                LastWrite = File.GetLastWriteTimeUtc(path),
                Size = new FileInfo(path).Length
            })
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.LastWrite)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    string? FindBestRecoilTargetPak()
    {
        if (File.Exists(RecoilBinaryRecipePath))
        {
            var binaryTarget = FindBinaryPatchSourcePak(RecoilBinaryRecipePath);
            if (binaryTarget != null)
                return binaryTarget;
        }

        if (File.Exists(RecoilRecipePath))
            return FindBestPakContainingAnyAsset(LoadRecipeAssetPaths(RecoilRecipePath), minSize: 100_000_000);

        return null;
    }

    string? FindBestRangeAndRecoilTargetPak()
    {
        if (!File.Exists(RecoilRecipePath))
            return null;

        var required = LoadRecipeAssetPaths(RecoilRecipePath)
            .Concat(new[] { PakPatchPacker.RangePhysicsAssetPath })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = selected
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates
            .Where(path => required.All(asset => PakPatchPacker.PakContainsAsset(path, asset)))
            .Select(path => BuildPakCandidateRank(path, selected))
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.LastWrite)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    static IReadOnlyList<string> LoadRecipeAssetPaths(string recipePath)
    {
        var recipe = JsonSerializer.Deserialize<PakPatchPacker.PatchRecipe>(
            File.ReadAllText(recipePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException("Recipe JSON is empty or invalid: " + recipePath);
        return recipe.Operations
            .Select(op => op.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    string? FindBestPakContainingAnyAsset(IEnumerable<string> assetPaths, long minSize = 0)
    {
        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = selected
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = PakPatchPacker.FindPaksContainingAnyAsset(candidates, assetPaths);
        return matches
            .Where(path => new FileInfo(path).Length >= minSize)
            .Select(path => BuildPakCandidateRank(path, selected))
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.LastWrite)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    string? FindBinaryPatchSourcePak(string binaryRecipePath)
    {
        var recipe = JsonSerializer.Deserialize<PakPatchPacker.BinaryPatchRecipe>(
            File.ReadAllText(binaryRecipePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException("Binary recipe JSON is empty or invalid: " + binaryRecipePath);

        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = selected
            .Concat(new[] { MapLobbyPak })
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Where(path => new FileInfo(path).Length == recipe.SourceSize)
            .Select(path => BuildPakCandidateRank(path, selected))
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.LastWrite)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .ToList();

        foreach (var candidate in candidates)
        {
            var hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(candidate)));
            if (hash.Equals(recipe.SourceSha1, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    static PakCandidateRank BuildPakCandidateRank(string path, IReadOnlyCollection<string> selected)
    {
        return new PakCandidateRank(
            path,
            selected.Contains(path, StringComparer.OrdinalIgnoreCase),
            TryParseGamePatchVersion(Path.GetFileNameWithoutExtension(path), out var version) ? version : null,
            File.GetLastWriteTimeUtc(path),
            new FileInfo(path).Length);
    }

    private sealed record PakCandidateRank(string Path, bool IsSelected, Version? Version, DateTime LastWrite, long Size);

    async Task RunSkinMaterialGlowBatchAsync(IReadOnlyList<string> targetPaks, string colorName, float red, float green, float blue, float intensity)
    {
        if (_isProcessing) return;
        _isProcessing = true;
        Dispatcher.Invoke(() =>
        {
            BtnUnpack.IsEnabled = false;
            BtnPack.IsEnabled = false;
            BtnPresetRange.IsEnabled = false;
            BtnPresetRecoil.IsEnabled = false;
            BtnCommanderKeyGlow.IsEnabled = false;
            BtnLearnCommanderKeyGlow.IsEnabled = false;
            BtnSkinMaterialGlow.IsEnabled = false;
            BtnLearnGlowBinary.IsEnabled = false;
            BtnPresetGlow.IsEnabled = false;
            BtnPresetCombined.IsEnabled = false;
            BtnRecipeBrowse.IsEnabled = false;
            BtnRecipeExample.IsEnabled = false;
            BtnRecipeLearn.IsEnabled = false;
            BtnRecipeRelocate.IsEnabled = false;
            BtnRecipeApply.IsEnabled = false;
            TxtPresetStatus.Text = "Skin / Character Glow batch running...";
            TxtStatus.Text = "Processing skin/material glow batch";
        });

        Log("Skin / Character Glow batch: " + targetPaks.Count + " PAKs", LogType.Accent);
        var sw = Stopwatch.StartNew();
        var completed = 0;
        var changed = 0;
        var failed = 0;
        var configuredOutput = Dispatcher.Invoke(() => TxtPackOutput.Text.Trim());

        try
        {
            await Task.Run(() =>
            {
                foreach (var targetPak in targetPaks)
                {
                    completed++;
                    var defaultOutputName = "skin_material_glow_" + colorName + ".pak";
                    var outputPak = ResolveBatchPresetOutputPak(targetPak, defaultOutputName, configuredOutput, targetPaks.Count == 1);
                    Log($"Skin glow {completed}/{targetPaks.Count}: {Path.GetFileName(targetPak)}", LogType.Info);
                    try
                    {
                        var result = PakPatchPacker.ApplySkinMaterialGlowFromCsv(targetPak, SkinMaterialCsvPath, outputPak, red, green, blue, intensity);
                        changed += result.ChangedFiles;
                        Log($"Skin glow complete: {Path.GetFileName(outputPak)} ({result.ChangedFiles} files changed)", LogType.Success);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log($"Skin glow skipped: {Path.GetFileName(targetPak)} - {ex.Message}", LogType.Warning);
                    }

                    var percent = targetPaks.Count == 0 ? 100 : completed * 100.0 / targetPaks.Count;
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value = percent;
                        TxtProgress.Text = "Skin Glow batch " + completed + "/" + targetPaks.Count;
                        TxtProgressPercent.Text = percent.ToString("F0") + "%";
                    });
                }
            });

            Log($"Skin / Character Glow batch complete: {targetPaks.Count - failed}/{targetPaks.Count} PAKs, {changed} files changed", LogType.Success);
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = 100;
                TxtProgress.Text = "Skin Glow batch complete";
                TxtProgressPercent.Text = "100%";
                TxtPresetStatus.Text = $"Skin / Character Glow batch complete: {targetPaks.Count - failed}/{targetPaks.Count} PAKs";
                TxtStatus.Text = "Batch finished in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s";
            });
        }
        finally
        {
            _isProcessing = false;
            Dispatcher.Invoke(() =>
            {
                BtnUnpack.IsEnabled = _selectedFiles.Count > 0;
                BtnPack.IsEnabled = !string.IsNullOrWhiteSpace(TxtPackSource.Text) && Directory.Exists(TxtPackSource.Text);
                BtnPresetRange.IsEnabled = true;
                BtnPresetRecoil.IsEnabled = true;
                BtnCommanderKeyGlow.IsEnabled = true;
                BtnLearnCommanderKeyGlow.IsEnabled = true;
                BtnSkinMaterialGlow.IsEnabled = true;
                BtnLearnGlowBinary.IsEnabled = true;
                BtnPresetGlow.IsEnabled = true;
                BtnPresetCombined.IsEnabled = true;
                BtnRecipeBrowse.IsEnabled = true;
                BtnRecipeExample.IsEnabled = true;
                BtnRecipeLearn.IsEnabled = true;
                BtnRecipeRelocate.IsEnabled = true;
                BtnRecipeApply.IsEnabled = true;
                TxtPackStatus.Text = "Ready";
                UpdateCommanderKeyGlowState();
            });
        }
    }

    string ResolveBatchPresetOutputPak(string targetPak, string defaultOutputName, string configuredOutput, bool allowExactConfiguredPak)
    {
        if (!string.IsNullOrWhiteSpace(configuredOutput))
        {
            var configured = ResolvePath(configuredOutput, Path.Combine(ManagedPakOutputDir, Path.GetFileName(configuredOutput)));
            if (Path.GetExtension(configured).Equals(".pak", StringComparison.OrdinalIgnoreCase) && allowExactConfiguredPak)
                return configured;

            var outputDir = Path.GetExtension(configured).Equals(".pak", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(configured) ?? ManagedPakOutputDir
                : configured;
            Directory.CreateDirectory(outputDir);
            return Path.Combine(outputDir, Path.GetFileNameWithoutExtension(targetPak) + "_" + Path.GetFileName(defaultOutputName));
        }

        Directory.CreateDirectory(ManagedPakOutputDir);
        return Path.Combine(ManagedPakOutputDir, Path.GetFileNameWithoutExtension(targetPak) + "_" + Path.GetFileName(defaultOutputName));
    }

    void BtnCommanderKeyGlow_Click(object s, RoutedEventArgs e)
    {
        if (!TryParseHexColor(TxtGlowColor.Text, out var red, out var green, out var blue))
        {
            Log("Commander Key color must be #RRGGBB, for example #00FF66.", LogType.Error);
            return;
        }

        var colorName = SanitizeFileToken(TxtGlowColor.Text.Trim().TrimStart('#'), "fixed");
        var targetPak = FindBestCommanderKeyTargetPak();
        if (!File.Exists(targetPak))
        {
            Log("Commander Key target PAK not found. Select the PAK that contains the commander key material, or learn from a verified key-glow sample.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(targetPak, "commander_key_glow_" + colorName + ".pak");
        _ = RunPresetAsync("Commander Key Glow #" + colorName, targetPak, outputPak,
            () =>
            {
                if (File.Exists(CommanderKeyBinaryRecipePath))
                {
                    var exactTarget = FindBinaryPatchSourcePak(CommanderKeyBinaryRecipePath);
                    if (exactTarget != null && exactTarget.Equals(targetPak, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Commander Key mode: exact blueprint binary recipe from verified sample. This preserves model scale and glow exactly as learned.", LogType.Accent);
                        return PakPatchPacker.ApplyBinaryPatchRecipe(targetPak, CommanderKeyBinaryRecipePath, outputPak);
                    }
                }

                if (File.Exists(CommanderKeyScaleGlowRecipePath))
                {
                    Log("Commander Key mode: blueprint scale/glow asset recipe. Commander model x5, other key models x2; glow is kept from the learned sample.", LogType.Accent);
                    try
                    {
                        return PakPatchPacker.ApplyAssetReplacementRecipe(targetPak, CommanderKeyScaleGlowRecipePath, outputPak);
                    }
                    catch (Exception ex)
                    {
                        Log("Commander Key asset recipe failed: " + ex.Message, LogType.Warning);
                    }
                }

                Log("Commander Key mode: KEY material glow fallback only. This changes glow parameters, not model scale.", LogType.Warning);
                try
                {
                    return PakPatchPacker.ApplyCommanderKeyGlow(targetPak, outputPak, red, green, blue, DefaultMaterialGlowPower);
                }
                catch when (File.Exists(CommanderKeyBinaryRecipePath))
                {
                    var exactTarget = FindBinaryPatchSourcePak(CommanderKeyBinaryRecipePath);
                    if (exactTarget == null || !exactTarget.Equals(targetPak, StringComparison.OrdinalIgnoreCase))
                        throw;

                    Log("Commander Key material patch failed; falling back to exact binary recipe. Binary recipe effect is fixed by the learned sample.", LogType.Warning);
                    return PakPatchPacker.ApplyBinaryPatchRecipe(targetPak, CommanderKeyBinaryRecipePath, outputPak);
                }
            });
    }

    string? FindBestCommanderKeyTargetPak()
    {
        if (File.Exists(CommanderKeyBinaryRecipePath))
        {
            var binaryTarget = FindBinaryPatchSourcePak(CommanderKeyBinaryRecipePath);
            if (binaryTarget != null)
            {
                Log("Commander Key target matched learned binary recipe: " + Path.GetFileName(binaryTarget), LogType.Accent);
                return binaryTarget;
            }

            LogBinaryRecipeSourceMismatch(CommanderKeyBinaryRecipePath, "Commander Key");
        }

        var selected = _selectedFiles
            .Select(item => item.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = selected
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Concat(EnumerateExtraPakSearchRoots())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !IsUnsafeAutoSkinTarget(path))
            .ToList();

        var exactMatches = PakPatchPacker.FindPaksContainingAsset(candidates, PakPatchPacker.CommanderKeyMaterialPath);
        var fuzzyMatches = exactMatches.Count > 0
            ? exactMatches
            : FindCommanderKeyFuzzyMatches(candidates);

        return fuzzyMatches
            .Select(path => BuildPakCandidateRank(path, selected))
            .OrderByDescending(x => x.IsSelected)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.LastWrite)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    IReadOnlyList<string> FindCommanderKeyFuzzyMatches(IEnumerable<string> candidates)
    {
        var tokenSets = new[]
        {
            new[] { "CG036", "KEY", "M_Sale" },
            new[] { "CG036", "KEY", "Zhihuiguan" },
            new[] { "KEY", "Art_Player", "Material" },
            new[] { "Goods", "KEY", "Material" },
            new[] { "Zhihuiguan" }
        };

        foreach (var tokens in tokenSets)
        {
            var matches = PakPatchPacker.FindPaksContainingPathTokens(candidates, tokens);
            if (matches.Count > 0)
            {
                Log("Commander Key fuzzy search matched tokens: " + string.Join(" + ", tokens), LogType.Accent);
                return matches;
            }
        }

        return Array.Empty<string>();
    }

    void BtnLearnCommanderKeyGlow_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var originalDlg = new OpenFileDialog
            {
                Title = "Select original Commander Key PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(GamePakRoot) ? GamePakRoot : _exeDir
            };
            if (originalDlg.ShowDialog() != true) return;

            var modifiedDlg = new OpenFileDialog
            {
                Title = "Select verified Commander Key glow PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
            };
            if (modifiedDlg.ShowDialog() != true) return;

            var count = PakPatchPacker.ExportBinaryPatchRecipe(
                originalDlg.FileName,
                modifiedDlg.FileName,
                CommanderKeyBinaryRecipePath,
                "learned_commander_key_glow_" + Path.GetFileNameWithoutExtension(originalDlg.FileName));

            TxtRecipePath.Text = CommanderKeyBinaryRecipePath;
            Log($"Learned Commander Key glow recipe: {CommanderKeyBinaryRecipePath} ({count} byte ranges)", LogType.Success);
            UpdateCommanderKeyGlowState();
        }
        catch (Exception ex)
        {
            Log("Learn Commander Key glow failed: " + ex.Message, LogType.Error);
            UpdateCommanderKeyGlowState();
        }
    }

    void BtnSkinMaterialGlow_Click(object s, RoutedEventArgs e)
    {
        if (!TryParseHexColor(TxtGlowColor.Text, out var red, out var green, out var blue))
        {
            Log("Skin glow color must be #RRGGBB, for example #00FF66.", LogType.Error);
            return;
        }

        if (!File.Exists(SkinMaterialCsvPath))
        {
            Log("Skin material CSV missing: " + SkinMaterialCsvPath, LogType.Error);
            return;
        }

        var intensity = DefaultMaterialGlowPower;
        var targets = ResolveSkinMaterialGlowTargets(red, green, blue, intensity);
        if (targets.Count == 0)
        {
            Log("No patchable skin/material PAKs found. Auto scan skips core_*.pak and map_*.pak, and filters out PAKs that would change 0 files.", LogType.Error);
            return;
        }
        Log("Skin / Character Glow only patches CSV material PAKs at x" + intensity.ToString("0.###") + ". Use Basic game_patch Glow for the base player outline glow.", LogType.Accent);

        var colorName = TxtGlowColor.Text.Trim().TrimStart('#');
        if (targets.Count == 1)
        {
            var targetPak = targets[0];
            var outputPak = ResolvePresetOutputPak(targetPak, "skin_material_glow_" + colorName + ".pak");
            _ = RunPresetAsync("Skin Material Glow #" + colorName, targetPak, outputPak,
                () => PakPatchPacker.ApplySkinMaterialGlowFromCsv(targetPak, SkinMaterialCsvPath, outputPak, red, green, blue, intensity));
            return;
        }

        _ = RunSkinMaterialGlowBatchAsync(targets, colorName, red, green, blue, intensity);
    }

    List<string> ResolveSkinMaterialGlowTargets(float red, float green, float blue, float intensity)
    {
        var selected = _selectedFiles
            .Select(file => file.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count > 0)
        {
            var selectedMatches = PakPatchPacker.FindSkinMaterialGlowTargetsFromCsv(SkinMaterialCsvPath, selected).ToList();
            if (selectedMatches.Count > 0)
            {
                var unsafeTargets = selectedMatches.Where(IsUnsafeAutoSkinTarget).ToList();
                if (unsafeTargets.Count > 0)
                {
                    var message =
                        "You selected core/map resource PAKs for Skin / Character Glow:\n\n" +
                        string.Join("\n", unsafeTargets.Select(Path.GetFileName)) +
                        "\n\nThese packages can break startup or trigger game repair/download if the version does not match. Continue only if these are current original files and you intend to patch them.";
                    var confirm = MessageBox.Show(this, message, "Confirm core/map resource patch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes)
                        return selectedMatches.Where(path => !unsafeTargets.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
                }

                return FilterPatchableSkinTargets(selectedMatches, red, green, blue, intensity, selectedOnly: true);
            }
        }

        var candidates = PakPatchPacker.GetSkinMaterialPakHintsFromCsv(SkinMaterialCsvPath)
            .Concat(EnumerateKnownOriginalPakCandidates())
            .Concat(EnumerateExtraPakSearchRoots())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !IsUnsafeAutoSkinTarget(path))
            .ToList();
        var csvMatches = PakPatchPacker.FindSkinMaterialGlowTargetsFromCsv(SkinMaterialCsvPath, candidates);
        var patchableCsvMatches = FilterPatchableSkinTargets(csvMatches, red, green, blue, intensity, selectedOnly: false);
        if (patchableCsvMatches.Count > 0)
            return patchableCsvMatches;

        Log("CSV skin targets did not produce patchable changes; scanning current safe PAKs for character material assets.", LogType.Warning);
        return FilterPatchableSkinTargets(candidates, red, green, blue, intensity, selectedOnly: false, restrictToCsvAssets: false);
    }

    List<string> FilterPatchableSkinTargets(IEnumerable<string> candidates, float red, float green, float blue, float intensity, bool selectedOnly, bool restrictToCsvAssets = true)
    {
        var selected = _selectedFiles
            .Select(file => file.FullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<(string Path, int Changes, PakCandidateRank Rank)>();
        foreach (var candidate in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var changes = PakPatchPacker.CountPatchableSkinMaterialGlow(candidate, SkinMaterialCsvPath, red, green, blue, intensity, restrictToCsvAssets);
                if (changes <= 0)
                {
                    Log("Skin target skipped (0 patchable materials): " + Path.GetFileName(candidate), LogType.Warning);
                    continue;
                }

                Log($"Skin target ready: {Path.GetFileName(candidate)} ({changes} patchable materials)", LogType.Success);
                results.Add((candidate, changes, BuildPakCandidateRank(candidate, selected)));
            }
            catch (Exception ex)
            {
                Log("Skin target skipped: " + Path.GetFileName(candidate) + " - " + ex.Message, LogType.Warning);
            }
        }

        return results
            .OrderByDescending(x => x.Rank.IsSelected)
            .ThenByDescending(x => x.Rank.Version)
            .ThenByDescending(x => x.Changes)
            .ThenByDescending(x => x.Rank.LastWrite)
            .ThenByDescending(x => x.Rank.Size)
            .Select(x => x.Path)
            .Take(selectedOnly ? results.Count : 8)
            .ToList();
    }

    IEnumerable<string> EnumerateExtraPakSearchRoots()
    {
        foreach (var root in new[] { TestRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads" }.Where(Directory.Exists))
        {
            foreach (var pak in Directory.EnumerateFiles(root, "*.pak", SearchOption.TopDirectoryOnly))
                yield return pak;
        }
    }

    static bool IsUnsafeAutoSkinTarget(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("map_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("core_", StringComparison.OrdinalIgnoreCase);
    }

    void BtnLearnGlowBinary_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var originalDlg = new OpenFileDialog
            {
                Title = "Select original game_patch PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(GamePakRoot) ? GamePakRoot : _exeDir
            };
            if (originalDlg.ShowDialog() != true) return;

            var glowDlg = new OpenFileDialog
            {
                Title = "Select verified glow game_patch PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
            };
            if (glowDlg.ShowDialog() != true) return;

            var count = PakPatchPacker.ExportBinaryPatchRecipe(
                originalDlg.FileName,
                glowDlg.FileName,
                GlowBinaryRecipePath,
                "learned_glow_binary_" + Path.GetFileNameWithoutExtension(originalDlg.FileName));

            TxtRecipePath.Text = GlowBinaryRecipePath;
            Log($"Learned binary glow recipe: {GlowBinaryRecipePath} ({count} byte ranges)", LogType.Success);
        }
        catch (Exception ex)
        {
            Log("Learn binary glow failed: " + ex.Message, LogType.Error);
        }
    }

    void BtnRecipeBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select custom recipe JSON",
            Filter = "JSON recipe (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(ManagedRecipeOutputDir) ? ManagedRecipeOutputDir : _exeDir
        };
        if (dlg.ShowDialog() == true)
            TxtRecipePath.Text = dlg.FileName;
    }

    void BtnRecipeExample_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save example recipe",
            Filter = "JSON recipe (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = ManagedRecipeOutputDir,
            FileName = "custom_patch_recipe.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            PakPatchPacker.WriteExampleRecipe(dlg.FileName);
            TxtRecipePath.Text = dlg.FileName;
            Log("Example recipe saved: " + dlg.FileName, LogType.Success);
        }
        catch (Exception ex)
        {
            Log("Example recipe failed: " + ex.Message, LogType.Error);
        }
    }

    void BtnRecipeLearn_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var originalDlg = new OpenFileDialog
            {
                Title = "Select original PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(TestRoot) ? TestRoot : _exeDir
            };
            if (originalDlg.ShowDialog() != true) return;

            var modifiedDlg = new OpenFileDialog
            {
                Title = "Select modified PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(originalDlg.FileName) ?? _exeDir
            };
            if (modifiedDlg.ShowDialog() != true) return;

            var saveDlg = new SaveFileDialog
            {
                Title = "Save learned recipe JSON",
                Filter = "JSON recipe (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = ManagedRecipeOutputDir,
                FileName = Path.GetFileNameWithoutExtension(modifiedDlg.FileName) + "_learned_recipe.json"
            };
            if (saveDlg.ShowDialog() != true) return;

            var count = PakPatchPacker.ExportTemplateRecipe(
                originalDlg.FileName,
                modifiedDlg.FileName,
                saveDlg.FileName,
                Path.GetFileNameWithoutExtension(saveDlg.FileName));

            TxtRecipePath.Text = saveDlg.FileName;
            Log($"Learned recipe saved: {saveDlg.FileName} ({count} operations)", LogType.Success);
        }
        catch (Exception ex)
        {
            Log("Learn recipe failed: " + ex.Message, LogType.Error);
        }
    }

    void BtnRecipeRelocate_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var recipePath = TxtRecipePath.Text.Trim();
            if (!File.Exists(recipePath))
            {
                Log("Select the old recipe JSON first.", LogType.Error);
                return;
            }

            var pakDlg = new OpenFileDialog
            {
                Title = "Select new version original PAK",
                Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(TestRoot) ? TestRoot : _exeDir
            };
            if (pakDlg.ShowDialog() != true) return;

            var saveDlg = new SaveFileDialog
            {
                Title = "Save relocated recipe JSON",
                Filter = "JSON recipe (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = ManagedRecipeOutputDir,
                FileName = Path.GetFileNameWithoutExtension(recipePath) + "_relocated.json"
            };
            if (saveDlg.ShowDialog() != true) return;

            var result = PakPatchPacker.RelocateRecipe(pakDlg.FileName, recipePath, saveDlg.FileName);
            TxtRecipePath.Text = saveDlg.FileName;
            Log($"Relocated recipe saved: {saveDlg.FileName} (moved={result.RelocatedOperations}, same={result.UnchangedOperations}, failed={result.FailedOperations})", LogType.Success);
        }
        catch (Exception ex)
        {
            Log("Relocate recipe failed: " + ex.Message, LogType.Error);
        }
    }

    void BtnRecipeApply_Click(object s, RoutedEventArgs e)
    {
        var recipePath = TxtRecipePath.Text.Trim();
        if (!File.Exists(recipePath))
        {
            Log("Recipe JSON not found", LogType.Error);
            return;
        }

        if (_isProcessing) return;

        var targetPak = _selectedFiles.FirstOrDefault()?.FullPath ?? MapLobbyPak;
        if (!File.Exists(targetPak))
        {
            Log("Select a target PAK first, or place the default map_lobby PAK under D:\\test.", LogType.Error);
            return;
        }

        var outputPak = ResolvePresetOutputPak(targetPak, "output_custom.pak");
        _ = RunPresetAsync("Custom recipe", targetPak, outputPak,
            () => PakPatchPacker.ApplyJsonRecipe(targetPak, recipePath, outputPak));
    }

    static float ParseFloatOrDefault(string text, float fallback)
    {
        return float.TryParse(text, out var value) && float.IsFinite(value) ? value : fallback;
    }

    async Task ApplyRecipePresetAsync(string presetName, string templateOriginalPak, string defaultOutputName, string recipePath)
    {
        await ApplyRecipePresetAsync(presetName, templateOriginalPak, defaultOutputName, recipePath,
            (targetPak, outputPak) => PakPatchPacker.ApplyJsonRecipe(targetPak, recipePath, outputPak));
    }

    async Task ApplyRecipePresetAsync(string presetName, string templateOriginalPak, string defaultOutputName, string recipePath, Func<string, string, PakPatchPacker.Result> apply)
    {
        if (_isProcessing) return;
        if (!File.Exists(templateOriginalPak))
        {
            Log("Preset original pak missing: " + presetName, LogType.Error);
            return;
        }

        if (!File.Exists(recipePath))
        {
            Log("Preset recipe missing: " + recipePath, LogType.Error);
            return;
        }

        var targetPak = ResolvePresetTargetPak(templateOriginalPak);
        var outputPak = ResolvePresetOutputPak(targetPak, defaultOutputName);
        await RunPresetAsync(presetName, targetPak, outputPak, () => apply(targetPak, outputPak));
    }

    static bool TryParseHexColor(string text, out float red, out float green, out float blue)
    {
        red = green = blue = 0f;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        red = ((rgb >> 16) & 0xFF) / 255f;
        green = ((rgb >> 8) & 0xFF) / 255f;
        blue = (rgb & 0xFF) / 255f;
        return true;
    }

    static string SanitizeFileToken(string text, string fallback)
    {
        var token = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(token) ? fallback : token;
    }

    async Task ApplyValuePresetAsync(string presetName, string templateOriginalPak, string defaultOutputName, Func<string, string, PakPatchPacker.Result> apply)
    {
        if (_isProcessing) return;
        if (!File.Exists(templateOriginalPak))
        {
            Log("Preset original pak missing: " + presetName, LogType.Error);
            return;
        }

        var targetPak = ResolvePresetTargetPak(templateOriginalPak);
        var outputPak = ResolvePresetOutputPak(targetPak, defaultOutputName);
        await RunPresetAsync(presetName, targetPak, outputPak, () => apply(targetPak, outputPak));
    }

    async Task ApplyPresetAsync(string presetName, string templateOriginalPak, string templateModifiedPak, string defaultOutputName)
    {
        if (_isProcessing) return;
        if (!File.Exists(templateOriginalPak) || !File.Exists(templateModifiedPak))
        {
            Log("Preset files missing: " + presetName, LogType.Error);
            return;
        }

        var targetPak = ResolvePresetTargetPak(templateOriginalPak);
        var outputPak = ResolvePresetOutputPak(targetPak, defaultOutputName);
        await RunPresetAsync(presetName, targetPak, outputPak,
            () => PakPatchPacker.ApplyTemplatePatch(targetPak, templateOriginalPak, templateModifiedPak, outputPak));
    }

    async Task RunPresetAsync(string presetName, string targetPak, string outputPak, Func<PakPatchPacker.Result> apply)
    {
        _isProcessing = true;
        Dispatcher.Invoke(() =>
        {
            BtnPack.IsEnabled = false;
            BtnUnpack.IsEnabled = false;
            BtnPresetRange.IsEnabled = false;
            BtnPresetRecoil.IsEnabled = false;
            BtnCommanderKeyGlow.IsEnabled = false;
            BtnLearnCommanderKeyGlow.IsEnabled = false;
            BtnSkinMaterialGlow.IsEnabled = false;
            BtnLearnGlowBinary.IsEnabled = false;
            BtnPresetGlow.IsEnabled = false;
            BtnPresetCombined.IsEnabled = false;
            BtnRecipeBrowse.IsEnabled = false;
            BtnRecipeExample.IsEnabled = false;
            BtnRecipeLearn.IsEnabled = false;
            BtnRecipeRelocate.IsEnabled = false;
            BtnRecipeApply.IsEnabled = false;
            TxtPresetStatus.Text = presetName + " running...";
            TxtPackStatus.Text = "Applying template...";
        });

        var sw = Stopwatch.StartNew();
        Log("Applying template: " + presetName, LogType.Accent);
        Log("Target: " + targetPak, LogType.Info);
        Log("Output: " + outputPak, LogType.Info);

        try
        {
            var result = await Task.Run(apply);
            Log("Template complete: " + result.ChangedFiles + " files changed (" + FormatSize(result.OutputSize) + ")", LogType.Success);
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = 100;
                TxtProgress.Text = presetName + " complete";
                TxtProgressPercent.Text = "100%";
                TxtPresetStatus.Text = presetName + " complete: " + Path.GetFileName(outputPak);
                TxtStatus.Text = "Template finished in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s";
            });
        }
        catch (Exception ex)
        {
            Log("Template failed: " + ex.Message, LogType.Error);
            Dispatcher.Invoke(() =>
            {
                TxtPresetStatus.Text = presetName + " failed";
                TxtStatus.Text = "Template failed";
            });
        }
        finally
        {
            _isProcessing = false;
            Dispatcher.Invoke(() =>
            {
                BtnUnpack.IsEnabled = _selectedFiles.Count > 0;
                BtnPack.IsEnabled = !string.IsNullOrWhiteSpace(TxtPackSource.Text) && Directory.Exists(TxtPackSource.Text);
                BtnPresetRange.IsEnabled = true;
                BtnPresetRecoil.IsEnabled = true;
                BtnCommanderKeyGlow.IsEnabled = true;
                BtnLearnCommanderKeyGlow.IsEnabled = true;
                BtnSkinMaterialGlow.IsEnabled = true;
                BtnLearnGlowBinary.IsEnabled = true;
                BtnPresetGlow.IsEnabled = true;
                BtnPresetCombined.IsEnabled = true;
                BtnRecipeBrowse.IsEnabled = true;
                BtnRecipeExample.IsEnabled = true;
                BtnRecipeLearn.IsEnabled = true;
                BtnRecipeRelocate.IsEnabled = true;
                BtnRecipeApply.IsEnabled = true;
                TxtPackStatus.Text = "Ready";
                UpdateCommanderKeyGlowState();
            });
        }
    }

    string ResolvePresetTargetPak(string templateOriginalPak)
    {
        var templateSize = new FileInfo(templateOriginalPak).Length;
        var selected = _selectedFiles.FirstOrDefault(f => File.Exists(f.FullPath) && new FileInfo(f.FullPath).Length == templateSize);
        if (selected != null)
            return selected.FullPath;
        return templateOriginalPak;
    }

    string ResolvePresetOutputPak(string targetPak, string defaultOutputName)
    {
        var configured = TxtPackOutput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return ResolvePath(configured, Path.Combine(ManagedPakOutputDir, Path.GetFileName(configured)));

        Directory.CreateDirectory(ManagedPakOutputDir);
        var prefix = Path.GetFileNameWithoutExtension(targetPak);
        var suffix = Path.GetFileName(defaultOutputName);
        return Path.Combine(ManagedPakOutputDir, prefix + "_" + suffix);
    }

    string ResolvePath(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        return Path.IsPathRooted(text) ? Path.GetFullPath(text) : Path.GetFullPath(Path.Combine(_exeDir, text));
    }

    async Task DoPack()
    {
        var sourceDir = TxtPackSource.Text.Trim();
        var outputPak = TxtPackOutput.Text.Trim();

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            Log("Invalid source directory", LogType.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(outputPak))
        {
            outputPak = Path.Combine(ManagedPakOutputDir, Path.GetFileName(sourceDir) + ".pak");
            TxtPackOutput.Text = outputPak;
        }
        else
        {
            outputPak = ResolvePath(outputPak, Path.Combine(ManagedPakOutputDir, Path.GetFileName(outputPak)));
            TxtPackOutput.Text = outputPak;
        }

        _isProcessing = true;
        Dispatcher.Invoke(() => { BtnPack.IsEnabled = false; BtnUnpack.IsEnabled = false; TxtPackStatus.Text = "Packing..."; });
        Log("Packing: " + sourceDir + " -> " + outputPak, LogType.Accent);

        var sw = Stopwatch.StartNew();
        try
        {
            byte[] aesKey = Array.Empty<byte>();
            if (!string.IsNullOrWhiteSpace(TxtAesKey.Text) && TxtAesKey.Text.Length == 64)
            {
                try { aesKey = Convert.FromHexString(TxtAesKey.Text); }
                catch { aesKey = Array.Empty<byte>(); Log("Invalid AES key hex", LogType.Warning); }
            }

            var compression = _lastPakMeta?.ResolvedCompression ?? PakPacker.CompressionType.Zlib;
            var mountPoint = _lastPakMeta?.MountPoint ?? "../../../";
            var pakVersion = _lastPakMeta?.Version ?? 8;
            var pakMagic = _lastPakMeta?.PakMagic ?? 0x5A6F12E1;
            var footerSize = _lastPakMeta?.FooterSize ?? 45;
            var encryptIndex = _lastPakMeta?.EncryptedIndex ?? false;
            Log("Compression: " + compression + ", Encrypted: " + (aesKey.Length > 0) + ", Mount: " + mountPoint, LogType.Accent);

            if (_lastPakMeta?.PakMagic == 0xFF67FF70 && _lastPakMeta.Version >= 10 && File.Exists(_lastPakMeta.PakPath))
            {
                Log("Patch mode: preserving original pak bytes for unchanged files", LogType.Accent);
                var patchResult = await Task.Run(() => PakPatchPacker.WritePatchedPak(_lastPakMeta.PakPath, sourceDir, outputPak));
                Log("Patch complete: " + patchResult.ChangedFiles + "/" + patchResult.TotalFiles + " files changed (" + FormatSize(patchResult.OutputSize) + ")", LogType.Success);
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.Value = 100;
                    TxtProgress.Text = "Patch packing complete";
                    TxtProgressPercent.Text = "100%";
                });
                goto PackDone;
            }

            // Load compression manifest if available
            Dictionary<string, PakPacker.CompressionType>? perFile = null;
            if (_lastPakMeta != null)
            {
                var manifestPath = Path.ChangeExtension(_lastPakMeta.PakPath, ".manifest.json");
                var manifest = PakIndexReader.LoadManifest(manifestPath);
                if (manifest != null && manifest.FileCompression.Count > 0)
                {
                    perFile = manifest.FileCompression.ToDictionary(
                        kvp => kvp.Key,
                        kvp => PakIndexReader.MethodToType(kvp.Value));
                    Log("Loaded per-file compression manifest: " + perFile.Count + " files", LogType.Success);
                }
            }

            // Load Oodle if needed
            if (compression == PakPacker.CompressionType.Oodle && !NativeMethods.IsOodleLoaded)
            {
                if (!string.IsNullOrWhiteSpace(TxtOodlePath.Text) && File.Exists(TxtOodlePath.Text))
                {
                    if (!NativeMethods.LoadOodle(TxtOodlePath.Text))
                        throw new InvalidOperationException("Failed to load Oodle DLL");
                }
                else
                {
                    throw new InvalidOperationException("Oodle DLL not found. Please configure Oodle path in Keys panel.");
                }
            }

            var packer = new PakPacker(outputPak, aesKey, compression, mountPoint, encryptIndex, perFile, pakVersion, pakMagic, footerSize);

            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var total = files.Length;
            var added = 0;
            var progress = new Progress<int>(pct =>
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.Value = pct;
                    TxtProgress.Text = "Packing " + added + "/" + total;
                    TxtProgressPercent.Text = pct + "%";
                });
            });

            Log("Adding " + total + " files...", LogType.Info);
            foreach (var file in files)
            {
                var relPath = Path.GetRelativePath(sourceDir, file);
                if (packer.AddFile(relPath, file))
                    added++;
                ((IProgress<int>)progress).Report((int)(added / (double)total * 90));
            }

            if (packer.Errors.Count > 0)
                Log("Warnings: " + packer.Errors.Count + " files skipped", LogType.Warning);

            await Task.Run(() => packer.Write(progress));
            Log("Pack complete: " + added + " files in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s (" + FormatSize(new FileInfo(outputPak).Length) + ")", LogType.Success);
PackDone:
            ;
        }
        catch (Exception ex)
        {
            Log("Pack failed: " + ex.Message, LogType.Error);
        }

        _isProcessing = false;
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = 100;
            TxtProgress.Text = "Done";
            TxtProgressPercent.Text = "100%";
            BtnUnpack.IsEnabled = _selectedFiles.Count > 0;
            BtnPack.IsEnabled = !string.IsNullOrWhiteSpace(TxtPackSource.Text) && Directory.Exists(TxtPackSource.Text);
            TxtPackStatus.Text = "Complete";
            TxtStatus.Text = "Pack finished in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s";
        });
    }

    // ==================== Keys ====================

    void ExpanderKeys_Expanded(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtAesKey.Text) || TxtAesKey.Text == "01010101010101010101010101010101")
        {
            if (_lastPakMeta != null)
                TryAutoDetectKey(_lastPakMeta.PakPath);
        }
    }

    void BtnBrowseOodle_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select Oodle DLL", Filter = "DLL (*.dll)|*.dll|All|*.*", InitialDirectory = _exeDir };
        if (dlg.ShowDialog() == true)
        {
            TxtOodlePath.Text = dlg.FileName;
            if (NativeMethods.LoadOodle(dlg.FileName))
                Log("Oodle loaded: " + dlg.FileName, LogType.Success);
            else
                Log("Failed to load Oodle DLL", LogType.Error);
        }
    }

    void BtnSaveConfig_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var config = new
            {
                AesKey = TxtAesKey.Text,
                OodlePath = TxtOodlePath.Text
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            var configPath = Path.Combine(_exeDir, "keys.json");
            File.WriteAllText(configPath, json);
            Log("Config saved: keys.json", LogType.Success);
        }
        catch (Exception ex) { Log("Save config failed: " + ex.Message, LogType.Error); }
    }

    void BtnLoadConfig_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(_exeDir, "keys.json");
            if (!File.Exists(configPath)) { Log("No config found", LogType.Warning); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("AesKey", out var ak)) TxtAesKey.Text = ak.GetString() ?? "";
            if (root.TryGetProperty("OodlePath", out var op)) TxtOodlePath.Text = op.GetString() ?? "";
            Log("Config loaded: keys.json", LogType.Success);
        }
        catch (Exception ex) { Log("Load config failed: " + ex.Message, LogType.Error); }
    }

    void BtnResetKeys_Click(object s, RoutedEventArgs e)
    {
        TxtAesKey.Text = "01010101010101010101010101010101";
        TxtOodlePath.Text = "";
        DetectOodle();
        Log("Keys reset", LogType.Info);
    }

    // ==================== AES Key Auto-Detect ====================

    private static readonly Dictionary<string, string> KnownAesKeys = new()
    {
        ["PUBG Mobile 3.5"] = "AB3483AB3483AB3483AB3483AB3483AB",
        ["PUBG Mobile 3.6"] = "0000000000000000000000000000000000000000000000000000000000000000",
        ["PUBG Mobile 3.0"] = "3A3A3A3A3A3A3A3A3A3A3A3A3A3A3A3A",
        ["PUBG KR"] = "AB3483CD3483AB3483AB3483AB3483AB",
        ["Generic UE4 0x01"] = "0101010101010101010101010101010101010101010101010101010101010101",
        ["Generic UE4 0xA1"] = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1",
        ["Valorant"] = "0x4BE71AF2459CF83899EC9DC2CB60E22AC4B3047E0211034BBABE9D174C069DD6",
        ["Fortnite"] = "0x39C1BD2F52E643D5B47B2A0FBE3E9F0B6B0D1B8E2D81C3F1D99DC6B9D90B3D8A",
        ["Lost Ark"] = "0xAB3483AB3483AB3483AB3483AB3483AB3483AB3483AB3483AB3483AB3483AB",
    };

    void TryAutoDetectKey(string pakPath)
    {
        try
        {
            // Try online key database first
            _ = TryOnlineKeyLookupAsync(pakPath);

            // Try local known keys
            foreach (var (name, keyHex) in KnownAesKeys)
            {
                try
                {
                    var key = Convert.FromHexString(keyHex.Replace("0x", ""));
                    if (key.Length != 32) continue;
                    if (TryKeyOnPak(pakPath, key))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TxtAesKey.Text = Convert.ToHexString(key).ToLowerInvariant();
                            Log("AES key found: " + name, LogType.Success);
                        });
                        return;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    async Task TryOnlineKeyLookupAsync(string pakPath)
    {
        try
        {
            var url = "https://raw.githubusercontent.com/atenfyr/UAssetGUI/master/resources/AESKeys.json";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("key", out var keyEl)) continue;
                var keyStr = (keyEl.GetString() ?? "").Replace("0x", "");
                if (keyStr.Length != 64) continue;
                try
                {
                    var key = Convert.FromHexString(keyStr);
                    if (TryKeyOnPak(pakPath, key))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TxtAesKey.Text = keyStr.ToLowerInvariant();
                            Log("AES key found online!", LogType.Success);
                        });
                        return;
                    }
                }
                catch { }
            }
        }
        catch { /* silent - offline fallback */ }
    }

    static bool TryKeyOnPak(string pakPath, byte[] key)
    {
        try
        {
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Try to decrypt and verify magic at the start
            if (fs.Length < 16) return false;
            var block = new byte[16];
            fs.ReadExactly(block, 0, 16);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(block, 0, 16);
            return decrypted[0] == 0xE1 && decrypted[1] == 0x12 && decrypted[2] == 0x6F && decrypted[3] == 0x5A;
        }
        catch { return false; }
    }

    // ==================== Editor ====================

    async void BtnLaunchEditor_Click(object s, RoutedEventArgs e)
    {
        BtnLaunchEditor.IsEnabled = false;
        try
        {
            var editorFull = Path.Combine(_exeDir, EditorPath);
            if (File.Exists(editorFull))
            {
                Process.Start(new ProcessStartInfo { FileName = editorFull, UseShellExecute = true });
                Log("Editor launched", LogType.Success);
                BtnLaunchEditor.IsEnabled = true;
            }
            else
            {
                Log("Editor not found. Downloading UAssetGUI...", LogType.Warning);
                await DownloadEditorAsync();
                if (File.Exists(editorFull))
                {
                    Process.Start(new ProcessStartInfo { FileName = editorFull, UseShellExecute = true });
                    Log("Editor launched", LogType.Success);
                }
            }
        }
        catch (Exception ex) { Log("Editor error: " + ex.Message, LogType.Error); }
        BtnLaunchEditor.IsEnabled = true;
    }

    // ==================== Window Events ====================

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isProcessing)
        {
            var result = MessageBox.Show("Processing in progress. Really exit?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
}
    void BtnDetectKey_Click(object s, RoutedEventArgs e)
    {
        if (_lastPakMeta != null)
        {
            Log("Auto-detecting AES key...", LogType.Info);
            TryAutoDetectKey(_lastPakMeta.PakPath);
        }
        else
        {
            Log("Load a PAK file first to auto-detect its key", LogType.Warning);
        }
    }


    // ==================== Internal Editors ====================

    void BtnAssetEditor_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var editor = new AssetEditorWindow();
            editor.Owner = this;
            editor.Show();
            Log("Asset Editor opened", LogType.Success);
        }
        catch (Exception ex) { Log("Asset Editor error: " + ex.Message, LogType.Error); }
    }

    void BtnRecoilEditor_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var editor = new RecoilEditorWindow();
            editor.Owner = this;
            editor.Show();
            Log("Recoil Editor opened", LogType.Success);
        }
        catch (Exception ex) { Log("Recoil Editor error: " + ex.Message, LogType.Error); }
    }
}
