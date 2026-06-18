using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace PakToolGUI;

public partial class AssetEditorWindow : Window
{
    private UAssetParser? _parser;
    private string? _currentUassetPath;
    private readonly List<FloatPropertyEntry> _allProperties = new();
    private readonly List<CollisionPropertyEntry> _collisionProperties = new();
    private bool _hasModified;
    private bool _isCollisionMode;
    private RecoilEditorWindow? _recoilEditor;

    public AssetEditorWindow()
    {
        InitializeComponent();
        PropertyGrid.ItemsSource = _allProperties;
    }

    public void OpenAsset(string uassetPath)
    {
        LoadAsset(uassetPath);
    }

    private void LoadAsset(string uassetPath)
    {
        try
        {
            _parser = new UAssetParser(uassetPath);
            _currentUassetPath = uassetPath;

            TxtFilePath.Text = uassetPath;
            TxtTitle.Text = "Asset Property Editor";
            TxtFilter.Text = "";

            _isCollisionMode = false;
            RefreshList();

            TxtStatus.Text = $"Loaded: {_parser.GetStatus()}";
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x9e, 0xce, 0x6a));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to parse asset:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshList(string? textFilter = null, float? minVal = null, float? maxVal = null)
    {
        _allProperties.Clear();
        _collisionProperties.Clear();
        if (_parser == null) return;

        if (_isCollisionMode)
        {
            // Deep scan mode - find all collision-related struct floats
            var props = _parser.ExtractCollisionProperties();

            foreach (var p in props)
            {
                if (textFilter != null && !p.FullPath.Contains(textFilter, StringComparison.OrdinalIgnoreCase)
                    && !p.ExportName.Contains(textFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (minVal.HasValue && p.CurrentValue < minVal.Value) continue;
                if (maxVal.HasValue && p.CurrentValue > maxVal.Value) continue;

                p.PropertyChanged += OnCollisionValueChanged;
                _collisionProperties.Add(p);
            }

            // Convert to FloatPropertyEntry for display in grid
            foreach (var cp in _collisionProperties)
            {
                _allProperties.Add(new FloatPropertyEntry
                {
                    ExportName = cp.ExportName,
                    PropertyName = cp.FullPath,
                    CurrentValue = cp.CurrentValue,
                    NewValue = cp.NewValue
                });
            }

            PropertyGrid.ItemsSource = _allProperties;
            PropertyGrid.Items.Refresh();
            TxtCount.Text = $"{_collisionProperties.Count} collision properties (deep scan)";
        }
        else
        {
            // Normal float property scan
            var props = _parser.ExtractFloatProperties(textFilter);

            foreach (var p in props)
            {
                if (minVal.HasValue && p.CurrentValue < minVal.Value) continue;
                if (maxVal.HasValue && p.CurrentValue > maxVal.Value) continue;
                p.PropertyChanged += OnPropertyValueChanged;
                _allProperties.Add(p);
            }

            PropertyGrid.ItemsSource = _allProperties;
            PropertyGrid.Items.Refresh();
            TxtCount.Text = $"{_allProperties.Count} float properties";
        }

        UpdateButtons();
    }

    private void OnPropertyValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatPropertyEntry.NewValue))
        {
            _hasModified = true;
            UpdateButtons();
        }
    }

    private void OnCollisionValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollisionPropertyEntry.NewValue))
        {
            _hasModified = true;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        BtnApply.IsEnabled = _hasModified;
        BtnSave.IsEnabled = _hasModified;
    }

    private void ApplyChanges()
    {
        if (_parser == null) return;

        int changed = 0;

        if (_isCollisionMode)
        {
            foreach (var prop in _collisionProperties.Where(p => p.IsModified))
            {
                _parser.WriteStructFloat(prop.ExportName, prop.FullPath, prop.NewValue);
                prop.CurrentValue = prop.NewValue;
                prop.OnPropertyChanged(nameof(FloatPropertyEntry.CurrentValue));
                changed++;
            }

            // Sync back to display list
            for (int i = 0; i < _allProperties.Count && i < _collisionProperties.Count; i++)
            {
                _allProperties[i].CurrentValue = _collisionProperties[i].CurrentValue;
                _allProperties[i].NewValue = _collisionProperties[i].NewValue;
            }
        }
        else
        {
            foreach (var prop in _allProperties.Where(p => p.IsModified))
            {
                _parser.WriteFloatValue(prop.PropertyName, prop.ExportName, prop.NewValue);
                prop.CurrentValue = prop.NewValue;
                prop.OnPropertyChanged(nameof(FloatPropertyEntry.ValueDisplay));
                prop.OnPropertyChanged("");
                changed++;
            }
        }

        PropertyGrid.Items.Refresh();
        TxtStatus.Text = $"Applied {changed} changes (not yet saved to disk)";
        TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xe0, 0xaf, 0x68));
        _hasModified = false;
        UpdateButtons();
    }

    // ==================== Button Handlers ====================

    private void BtnRecoilEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_recoilEditor == null || !_recoilEditor.IsLoaded)
        {
            _recoilEditor = new RecoilEditorWindow();
            _recoilEditor.Closed += (_, _) => _recoilEditor = null;
            _recoilEditor.Show();
        }
        else
        {
            _recoilEditor.Focus();
        }
    }

    private void BtnCollisionScan_Click(object sender, RoutedEventArgs e)
    {
        if (_parser == null)
        {
            MessageBox.Show("Please open a .uasset file first.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isCollisionMode = !_isCollisionMode;
        BtnCollisionScan.Content = _isCollisionMode ? "&#x2705; Collision Scan (ON)" : "&#x1F9AC; Collision Scan";

        if (_isCollisionMode)
        {
            BtnCollisionScan.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3d, 0x59, 0xa1));
        }
        else
        {
            BtnCollisionScan.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2f, 0x33, 0x48));
        }

        RefreshList();
    }

    private void BtnShowAll_Click(object sender, RoutedEventArgs e)
    {
        TxtFilter.Text = "";
        RefreshList();
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open UE Asset (.uasset)",
            Filter = "UE Asset (*.uasset)|*.uasset|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadAsset(dlg.FileName);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUassetPath != null)
            LoadAsset(_currentUassetPath);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Save();
    }

    private void Save()
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

    private void BtnApply_Click(object sender, RoutedEventArgs e) => ApplyChanges();

    private void TxtFilter_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string filter = TxtFilter.Text.Trim();
            RefreshList(string.IsNullOrEmpty(filter) ? null : filter);
        }
    }

    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            string tag = btn.Tag?.ToString() ?? "";
            TxtFilter.Text = tag;
            RefreshList(textFilter: string.IsNullOrEmpty(tag) ? null : tag);
        }
    }

    private void BtnRangeFilter_Click(object sender, RoutedEventArgs e)
    {
        float? min = null, max = null;
        if (float.TryParse(TxtMinVal.Text, out float m1)) min = m1;
        if (float.TryParse(TxtMaxVal.Text, out float m2)) max = m2;
        RefreshList(minVal: min, maxVal: max);
    }

    private void BtnResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isCollisionMode)
        {
            foreach (var prop in _collisionProperties)
                prop.NewValue = prop.CurrentValue;

            for (int i = 0; i < _allProperties.Count && i < _collisionProperties.Count; i++)
                _allProperties[i].NewValue = _collisionProperties[i].NewValue;
        }
        else
        {
            foreach (var prop in _allProperties)
                prop.NewValue = prop.CurrentValue;
        }

        PropertyGrid.Items.Refresh();
        _hasModified = false;
        UpdateButtons();
    }

    private void PropertyGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FloatPropertyEntry prop && prop.IsModified)
        {
            e.Row.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(40, 0xe0, 0xaf, 0x68));
        }
    }

    private void PropertyGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditingElement is TextBox tb && e.Row.Item is FloatPropertyEntry prop)
        {
            if (float.TryParse(tb.Text, out float val))
                prop.NewValue = val;

            // Sync to collision entry if in collision mode
            if (_isCollisionMode)
            {
                int idx = _allProperties.IndexOf(prop);
                if (idx >= 0 && idx < _collisionProperties.Count)
                    _collisionProperties[idx].NewValue = val;
            }
        }
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
            if (result == MessageBoxResult.Yes) Save();
            else if (result == MessageBoxResult.Cancel) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
