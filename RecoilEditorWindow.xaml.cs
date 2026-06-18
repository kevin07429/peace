using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace PakToolGUI;

public partial class RecoilEditorWindow : Window
{
    private UAssetParser? _parser;
    private string? _currentUassetPath;
    private readonly List<RecoilCurveData> _curves = new();
    private readonly List<WeaponEntry> _weapons = new();
    private bool _hasModified;
    private int _modifiedKeyCount;

    // Default recoil directory from unpacked PAK
    private static readonly string DefaultRecoilDir = 
        @"C:\Users\Kevin\source\repos\kevin07429\pak\bin\Release\net10.0-windows\Output\Output\Unpack\map_lobby_1.36.11.15210_1608332248\ShadowTrackerExtra\Content\Arts_PlayerBluePrints\Weapon\RecoilCurves";

    public RecoilEditorWindow()
    {
        InitializeComponent();
        WeaponList.ItemsSource = _weapons;
    }

    // ==================== Single File Mode ====================

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Recoil Curve (.uasset)",
            Filter = "UE Asset (*.uasset)|*.uasset|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadSingleFile(dlg.FileName);
    }

    private void LoadSingleFile(string path)
    {
        try
        {
            _parser = new UAssetParser(path);
            _currentUassetPath = path;
            TxtFilePath.Text = path;
            TxtTitle.Text = $"Recoil: {Path.GetFileNameWithoutExtension(path)}";

            _curves.Clear();
            _weapons.Clear();
            _curves.AddRange(_parser.ExtractRecoilCurves());

            if (_curves.Count == 0)
            {
                TxtStatus.Text = "No RichCurve data found in this file";
                return;
            }

            string wname = Path.GetFileNameWithoutExtension(path);
            _weapons.Add(new WeaponEntry { Name = wname, FilePath = path, Curves = _curves });
            WeaponList.SelectedIndex = 0;
            TxtStatus.Text = $"Loaded: {_curves.Count} curves, {_curves.Sum(c => c.Keys.Count)} keys total";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to parse: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== Batch Mode ====================

    private void BtnBatchLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(DefaultRecoilDir))
        {
            MessageBox.Show($"Recoil directory not found:\n{DefaultRecoilDir}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        TxtFilePath.Text = DefaultRecoilDir;
        TxtTitle.Text = "Recoil Curve Editor - All Weapons";

        _weapons.Clear();
        _curves.Clear();
        _parser = null;
        _currentUassetPath = null;

        var files = Directory.GetFiles(DefaultRecoilDir, "*.uasset").OrderBy(f => f).ToList();

        foreach (var file in files)
        {
            try
            {
                var parser = new UAssetParser(file);
                var fileCurves = parser.ExtractRecoilCurves();

                string wname = Path.GetFileNameWithoutExtension(file);
                _weapons.Add(new WeaponEntry
                {
                    Name = wname,
                    FilePath = file,
                    Curves = fileCurves
                });
            }
            catch { /* skip broken files */ }
        }

        WeaponList.Items.Refresh();
        if (_weapons.Count > 0)
            WeaponList.SelectedIndex = 0;

        int totalKeys = _weapons.Sum(w => w.Curves.Sum(c => c.Keys.Count));
        TxtStatus.Text = $"Batch loaded: {_weapons.Count} weapons, {totalKeys} keys total";
    }

    // ==================== Weapon Selection ====================

    private void WeaponList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WeaponList.SelectedItem is not WeaponEntry weapon) return;

        _curves.Clear();
        _curves.AddRange(weapon.Curves);
        _currentUassetPath = weapon.FilePath;
        _parser = new UAssetParser(weapon.FilePath);

        TxtTitle.Text = $"Recoil: {weapon.Name}";

        // Populate curve tabs
        CurveTabs.Items.Clear();
        foreach (var curve in _curves)
        {
            CurveTabs.Items.Add(new TabItem
            {
                Header = curve.DisplayLabel,
                Tag = curve,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x24, 0x28, 0x3b))
            });
        }

        if (CurveTabs.Items.Count > 0)
            CurveTabs.SelectedIndex = 0;

        TxtStatus.Text = $"Weapon: {weapon.Name} - {_curves.Count} curves";
    }

    private void CurveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurveTabs.SelectedItem is TabItem tab && tab.Tag is RecoilCurveData curve)
        {
            KeyframeGrid.ItemsSource = curve.Keys;
            curve.Keys.ForEach(k => k.PropertyChanged += OnKeyModified);
        }
    }

    private void OnKeyModified(object? sender, PropertyChangedEventArgs e)
    {
        _hasModified = true;
        _modifiedKeyCount++;
        UpdateButtonState();
    }

    private void KeyframeGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditingElement is TextBox tb && e.Row.Item is RecoilKeyEntry key)
        {
            string col = e.Column.Header?.ToString() ?? "";
            if (float.TryParse(tb.Text, out float val))
            {
                switch (col)
                {
                    case "Time": key.Time = val; break;
                    case "Value": key.Value = val; break;
                    case "ArriveTangent": key.ArriveTangent = val; break;
                    case "LeaveTangent": key.LeaveTangent = val; break;
                }
            }
        }
    }

    // ==================== Apply & Save ====================

    private void BtnApply_Click(object sender, RoutedEventArgs e) => ApplyChanges();

    private void ApplyChanges()
    {
        if (_parser == null || _currentUassetPath == null) return;

        int applied = 0;
        foreach (var curve in _curves)
        {
            foreach (var key in curve.Keys)
            {
                _parser.WriteRecoilKey(curve.CurveName, key.Index,
                    key.Time, key.Value, key.ArriveTangent, key.LeaveTangent);
                applied++;
            }
        }

        TxtStatus.Text = $"Applied {applied} key changes (not yet saved to disk)";
        _hasModified = false;
        _modifiedKeyCount = 0;
        UpdateButtonState();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_parser == null) return;

        try
        {
            if (_hasModified) ApplyChanges();
            _parser.Save();
            TxtStatus.Text = $"Saved to: {_parser.AssetPath}";
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x9e, 0xce, 0x6a));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateButtonState()
    {
        BtnApply.IsEnabled = _hasModified;
        BtnSave.IsEnabled = _hasModified;
        TxtModCount.Text = _modifiedKeyCount > 0 ? $"{_modifiedKeyCount} keys modified" : "";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_hasModified)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Save before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                ApplyChanges();
                _parser?.Save();
            }
            else if (result == MessageBoxResult.Cancel)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }
}

public class WeaponEntry
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public List<RecoilCurveData> Curves { get; set; } = new();
    public override string ToString() => Name;
}
