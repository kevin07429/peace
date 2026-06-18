using System.IO;
using System.Text;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace PakToolGUI;

/// <summary>
/// UE4 asset parser using UAssetAPI for proper name/export resolution.
/// Uses EngineVersion.VER_UE4_16 for PUBG Mobile (ShadowTrackerExtra).
/// </summary>
public class UAssetParser
{
    private UAsset? _asset;
    private string _assetPath;

    public bool IsLoaded => _asset != null;
    public string AssetPath => _assetPath;

    public UAssetParser(string uassetPath)
    {
        _assetPath = uassetPath;
        try
        {
            _asset = new UAsset(uassetPath, EngineVersion.VER_UE4_16);
        }
        catch (Exception)
        {
            _asset = null;
        }
    }

    public string GetStatus()
    {
        if (_asset == null) return "Failed to parse";
        var names = _asset.GetNameMapIndexList();
        return $"{names.Count} names, {_asset.Exports?.Count ?? 0} exports, {_asset.Imports?.Count ?? 0} imports";
    }

    // ==================== Float Properties ====================

    public List<FloatPropertyEntry> ExtractFloatProperties(string? nameFilter = null)
    {
        var results = new List<FloatPropertyEntry>();
        if (_asset?.Exports == null) return results;

        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            string ename = export.ObjectName?.Value?.Value ?? "(export)";

            foreach (var prop in ne.Data)
            {
                if (prop is not FloatPropertyData fp) continue;
                
                string pname = prop.Name?.Value?.Value ?? "(unnamed)";
                float value = fp.Value;

                if (float.IsNaN(value) || float.IsInfinity(value)) continue;

                if (nameFilter == null ||
                    pname.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
                    ename.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new FloatPropertyEntry
                    {
                        ExportName = ename,
                        PropertyName = pname,
                        CurrentValue = value,
                        NewValue = value
                    });
                }
            }
        }
        return results;
    }

    public void WriteFloatValue(string propertyName, string exportName, float newValue)
    {
        if (_asset?.Exports == null) return;
        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            if ((export.ObjectName?.Value?.Value ?? "") != exportName) continue;
            foreach (var prop in ne.Data)
            {
                if (prop is FloatPropertyData fp && (prop.Name?.Value?.Value ?? "") == propertyName)
                {
                    fp.Value = newValue;
                    return;
                }
            }
        }
    }

    // ==================== Recoil Curve ====================

    public List<RecoilCurveData> ExtractRecoilCurves()
    {
        var results = new List<RecoilCurveData>();
        if (_asset?.Exports == null) return results;

        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            string ename = export.ObjectName?.Value?.Value ?? "(export)";

            foreach (var prop in ne.Data)
            {
                if (prop is StructPropertyData sp)
                {
                    string stype = sp.StructType?.Value?.Value ?? "";
                    if (stype == "RichCurve")
                    {
                        var curveData = new RecoilCurveData
                        {
                            ExportName = ename,
                            CurveName = prop.Name?.Value?.Value ?? "Curve"
                        };
                        ExtractRichCurveKeys(sp, curveData);
                        if (curveData.Keys.Count > 0)
                            results.Add(curveData);
                    }
                }
            }
        }
        return results;
    }

    private void ExtractRichCurveKeys(StructPropertyData sp, RecoilCurveData curveData)
    {
        if (sp.Value == null) return;
        foreach (var inner in sp.Value)
        {
            string iname = inner.Name?.Value?.Value ?? "";
            if (inner is ArrayPropertyData arr && arr.Value != null && iname == "Keys")
            {
                for (int i = 0; i < arr.Value.Length; i++)
                {
                    if (arr.Value[i] is StructPropertyData keySp && keySp.Value != null)
                    {
                        foreach (var kf in keySp.Value)
                        {
                            if (kf is RichCurveKeyPropertyData rck)
                            {
                                FRichCurveKey key = rck.Value;
                                curveData.Keys.Add(new RecoilKeyEntry
                                {
                                    Index = i,
                                    Time = key.Time,
                                    Value = key.Value,
                                    ArriveTangent = key.ArriveTangent,
                                    LeaveTangent = key.LeaveTangent,
                                    InterpMode = key.InterpMode.ToString(),
                                    TangentMode = key.TangentMode.ToString()
                                });
                            }
                        }
                    }
                }
            }
        }
    }

    public void WriteRecoilKey(string curveName, int keyIndex, float? time, float? value, float? arriveTangent, float? leaveTangent)
    {
        if (_asset?.Exports == null) return;
        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            foreach (var prop in ne.Data)
            {
                if (prop is StructPropertyData sp && 
                    (sp.StructType?.Value?.Value ?? "") == "RichCurve" &&
                    (prop.Name?.Value?.Value ?? "") == curveName)
                {
                    UpdateRichCurveKey(sp, keyIndex, time, value, arriveTangent, leaveTangent);
                    return;
                }
            }
        }
    }

    private void UpdateRichCurveKey(StructPropertyData sp, int keyIndex, float? time, float? value, float? arriveTangent, float? leaveTangent)
    {
        if (sp.Value == null) return;
        foreach (var inner in sp.Value)
        {
            if (inner is ArrayPropertyData arr && arr.Value != null &&
                (inner.Name?.Value?.Value ?? "") == "Keys" && keyIndex < arr.Value.Length)
            {
                if (arr.Value[keyIndex] is StructPropertyData keySp && keySp.Value != null)
                {
                    foreach (var kf in keySp.Value)
                    {
                        if (kf is RichCurveKeyPropertyData rck)
                        {
                            var key = rck.Value;
                            if (time.HasValue) key.Time = time.Value;
                            if (value.HasValue) key.Value = value.Value;
                            if (arriveTangent.HasValue) key.ArriveTangent = arriveTangent.Value;
                            if (leaveTangent.HasValue) key.LeaveTangent = leaveTangent.Value;
                            rck.Value = key;
                        }
                    }
                }
                return;
            }
        }
    }

    // ==================== Collision Struct Properties ====================

    public List<CollisionPropertyEntry> ExtractCollisionProperties()
    {
        var results = new List<CollisionPropertyEntry>();
        if (_asset?.Exports == null) return results;

        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            string ename = export.ObjectName?.Value?.Value ?? "(export)";
            ScanStructForFloats(ne.Data, ename, "", results);
        }
        return results;
    }

    private void ScanStructForFloats(List<PropertyData> props, string exportName, string path, List<CollisionPropertyEntry> results)
    {
        if (props == null) return;
        foreach (var prop in props)
        {
            string pname = prop.Name?.Value?.Value ?? "";
            string fullPath = string.IsNullOrEmpty(path) ? pname : $"{path}.{pname}";

            if (prop is FloatPropertyData fp && IsCollisionRelated(pname))
            {
                results.Add(new CollisionPropertyEntry
                {
                    ExportName = exportName,
                    FullPath = fullPath,
                    PropertyName = pname,
                    CurrentValue = fp.Value,
                    NewValue = fp.Value
                });
            }
            else if (prop is StructPropertyData sp && sp.Value != null)
            {
                ScanStructForFloats(sp.Value, exportName, fullPath, results);
            }
            else if (prop is ArrayPropertyData arr && arr.Value != null)
            {
                for (int i = 0; i < arr.Value.Length && i < 50; i++)
                {
                    if (arr.Value[i] is StructPropertyData itemSp && itemSp.Value != null)
                        ScanStructForFloats(itemSp.Value, exportName, $"{fullPath}[{i}]", results);
                }
            }
        }
    }

    public void WriteStructFloat(string exportName, string fullPath, float newValue)
    {
        if (_asset?.Exports == null) return;
        foreach (var export in _asset.Exports)
        {
            if (export is not NormalExport ne) continue;
            if ((export.ObjectName?.Value?.Value ?? "") != exportName) continue;
            WriteStructFloatRecursive(ne.Data, fullPath.Split('.'), 0, newValue);
            return;
        }
    }

    private bool WriteStructFloatRecursive(List<PropertyData> props, string[] pathParts, int depth, float newValue)
    {
        if (props == null || depth >= pathParts.Length) return false;
        string targetName = pathParts[depth];

        int arrayIndex = -1;
        string cleanName = targetName;
        int bracketPos = targetName.IndexOf('[');
        if (bracketPos > 0)
        {
            cleanName = targetName.Substring(0, bracketPos);
            int endBracket = targetName.IndexOf(']', bracketPos);
            if (endBracket > bracketPos)
                int.TryParse(targetName.Substring(bracketPos + 1, endBracket - bracketPos - 1), out arrayIndex);
        }

        foreach (var prop in props)
        {
            string pname = prop.Name?.Value?.Value ?? "";
            if (pname != cleanName) continue;

            if (depth == pathParts.Length - 1 && prop is FloatPropertyData fp)
            {
                fp.Value = newValue;
                return true;
            }

            if (arrayIndex >= 0 && prop is ArrayPropertyData arr && arr.Value != null && arrayIndex < arr.Value.Length)
            {
                if (arr.Value[arrayIndex] is StructPropertyData itemSp && itemSp.Value != null)
                    return WriteStructFloatRecursive(itemSp.Value, pathParts, depth + 1, newValue);
            }

            if (prop is StructPropertyData sp && sp.Value != null)
                return WriteStructFloatRecursive(sp.Value, pathParts, depth + 1, newValue);
        }
        return false;
    }

    private static bool IsCollisionRelated(string name)
    {
        string lower = name.ToLower();
        return lower.Contains("capsule") || lower.Contains("radius") || lower.Contains("halfheight")
            || lower.Contains("collision") || lower.Contains("hitbox") || lower.Contains("cylinder")
            || lower.Contains("agent") || lower.Contains("crowd");
    }

    public void Save(string? outputPath = null)
    {
        _asset?.Write(outputPath ?? _assetPath);
    }

    public string GetAssetPath() => _assetPath;

    public static bool TryApplyMaterialGlow(byte[] assetData, string assetName, float red, float green, float blue, float intensity, out byte[] patchedData, out int changedValues)
    {
        patchedData = assetData;
        changedValues = 0;

        var tempDir = Path.Combine(Path.GetTempPath(), "PakToolGUI", "material_glow");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Path.GetFileName(assetName));
        if (!tempPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            tempPath += ".uasset";

        File.WriteAllBytes(tempPath, assetData);
        try
        {
            var asset = new UAsset(tempPath, EngineVersion.VER_UE4_16);
            if (asset.Exports == null) return false;

            foreach (var export in asset.Exports)
            {
                if (export is not NormalExport ne) continue;
                var exportName = export.ObjectName?.Value?.Value ?? "";
                changedValues += ApplyMaterialGlowToProperties(ne.Data, exportName, red, green, blue, intensity);
            }

            if (changedValues == 0) return false;

            asset.Write(tempPath);
            patchedData = File.ReadAllBytes(tempPath);
            return true;
        }
        catch
        {
            return TryApplyRawMaterialGlow(assetData, red, green, blue, intensity, out patchedData, out changedValues);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static int ApplyMaterialGlowToProperties(List<PropertyData>? props, string context, float red, float green, float blue, float intensity)
    {
        if (props == null) return 0;
        var changed = 0;

        foreach (var prop in props)
        {
            var name = prop.Name?.Value?.Value ?? "";
            var nextContext = string.IsNullOrEmpty(context) ? name : context + "." + name;
            var glowContext = IsGlowRelated(nextContext);

            switch (prop)
            {
                case BoolPropertyData bp when glowContext:
                    if (!bp.Value)
                    {
                        bp.Value = true;
                        changed++;
                    }
                    break;

                case FloatPropertyData fp when ShouldBoostGlowFloat(nextContext):
                    var target = Math.Max(fp.Value, intensity);
                    if (Math.Abs(fp.Value - target) > 0.0001f)
                    {
                        fp.Value = target;
                        changed++;
                    }
                    break;

                case StructPropertyData sp:
                    nextContext = AppendNameHints(nextContext, sp.Value);
                    glowContext = IsGlowRelated(nextContext);
                    if (TrySetLinearColorStruct(sp, nextContext, red * intensity, green * intensity, blue * intensity, glowContext))
                        changed++;
                    changed += ApplyMaterialGlowToProperties(sp.Value, nextContext, red, green, blue, intensity);
                    break;

                case ArrayPropertyData arr when arr.Value != null:
                    for (var i = 0; i < arr.Value.Length; i++)
                    {
                        if (arr.Value[i] is StructPropertyData itemSp)
                        {
                            var itemContext = AppendNameHints(nextContext + "[" + i + "]", itemSp.Value);
                            if (TrySetLinearColorStruct(itemSp, itemContext, red * intensity, green * intensity, blue * intensity, IsGlowRelated(itemContext)))
                                changed++;
                            changed += ApplyMaterialGlowToProperties(itemSp.Value, itemContext, red, green, blue, intensity);
                        }
                        else if (arr.Value[i] is FloatPropertyData itemFp && ShouldBoostGlowFloat(nextContext))
                        {
                            var boosted = Math.Max(itemFp.Value, intensity);
                            if (Math.Abs(itemFp.Value - boosted) > 0.0001f)
                            {
                                itemFp.Value = boosted;
                                changed++;
                            }
                        }
                    }
                    break;
            }
        }

        return changed;
    }

    private static string AppendNameHints(string context, List<PropertyData>? props)
    {
        if (props == null) return context;

        var hints = new List<string>();
        CollectNameHints(props, hints);
        if (hints.Count == 0) return context;
        return context + "." + string.Join(".", hints.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
    }

    private static void CollectNameHints(IEnumerable<PropertyData> props, List<string> hints)
    {
        foreach (var prop in props)
        {
            if (prop is NamePropertyData np)
            {
                var value = np.Value?.Value?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    hints.Add(value);
                continue;
            }

            if (prop is StructPropertyData sp)
                CollectNameHints(sp.Value ?? new List<PropertyData>(), hints);
        }
    }

    private static bool TrySetLinearColorStruct(StructPropertyData sp, string context, float red, float green, float blue, bool inheritedGlowContext)
    {
        var structType = sp.StructType?.Value?.Value ?? "";
        if (!inheritedGlowContext && !IsGlowRelated(context) && !IsColorStructType(structType))
            return false;

        var values = sp.Value;
        if (values == null) return false;

        var r = FindFloatProperty(values, "R") ?? FindFloatProperty(values, "r");
        var g = FindFloatProperty(values, "G") ?? FindFloatProperty(values, "g");
        var b = FindFloatProperty(values, "B") ?? FindFloatProperty(values, "b");
        var a = FindFloatProperty(values, "A") ?? FindFloatProperty(values, "a");
        if (r == null || g == null || b == null)
            return false;

        var changed = false;
        changed |= SetFloatIfDifferent(r, red);
        changed |= SetFloatIfDifferent(g, green);
        changed |= SetFloatIfDifferent(b, blue);
        if (a != null) changed |= SetFloatIfDifferent(a, 1f);
        return changed;
    }

    private static FloatPropertyData? FindFloatProperty(IEnumerable<PropertyData> props, string name)
    {
        return props.OfType<FloatPropertyData>().FirstOrDefault(p => string.Equals(p.Name?.Value?.Value, name, StringComparison.Ordinal));
    }

    private static bool SetFloatIfDifferent(FloatPropertyData prop, float value)
    {
        if (Math.Abs(prop.Value - value) <= 0.0001f) return false;
        prop.Value = value;
        return true;
    }

    private static bool IsGlowRelated(string name)
    {
        return name.Contains("emissive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("emssive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("glow", StringComparison.OrdinalIgnoreCase)
            || name.Contains("hdr", StringComparison.OrdinalIgnoreCase)
            || name.Contains("light", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldBoostGlowFloat(string name)
    {
        if (!IsGlowRelated(name)) return false;
        return name.Contains("intensity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("power", StringComparison.OrdinalIgnoreCase)
            || name.Contains("brightness", StringComparison.OrdinalIgnoreCase)
            || name.Contains("multiplier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("emissive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("emssive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("glow", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsColorStructType(string structType)
    {
        return structType.Contains("LinearColor", StringComparison.OrdinalIgnoreCase)
            || structType.Equals("Color", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryApplyRawMaterialGlow(byte[] assetData, float red, float green, float blue, float intensity, out byte[] patchedData, out int changedValues)
    {
        patchedData = (byte[])assetData.Clone();
        changedValues = 0;

        foreach (var keyword in new[]
                 {
                     "Emissive", "Emssive", "EmissionColor", "EmissionIntensity",
                     "Glow", "HDRinputColor", "LDRinputColor",
                     "LightColor", "lightcolor", "FXLight_Color",
                     "BaseColor", "ColorTint", "FresnelColor", "OutLineColor",
                     "SelectionColor", "LinearColor", "InvincibleColor2"
                 })
        {
            foreach (var index in FindAsciiOccurrences(patchedData, keyword))
            {
                if (keyword.Contains("Color", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Emissive", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Emssive", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Glow", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("HDR", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("LDR", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryPatchFirstColorTupleAfter(patchedData, index + keyword.Length, 256, red * intensity, green * intensity, blue * intensity))
                        changedValues++;
                }

                if (keyword.Contains("Intensity", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Power", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Brightness", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Light", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Glow", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Emissive", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Emssive", StringComparison.OrdinalIgnoreCase)
                    || keyword.Contains("Emission", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryBoostFirstFloatAfter(patchedData, index + keyword.Length, 256, intensity))
                        changedValues++;
                }
            }
        }

        if (changedValues > 0) return true;
        patchedData = assetData;
        return false;
    }

    private static IEnumerable<int> FindAsciiOccurrences(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        if (needle.Length == 0 || data.Length < needle.Length) yield break;

        for (var i = 0; i <= data.Length - needle.Length; i++)
        {
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle))
                yield return i;
        }
    }

    private static bool TryPatchFirstColorTupleAfter(byte[] data, int start, int window, float red, float green, float blue)
    {
        var end = Math.Min(data.Length - 16, start + window);
        for (var offset = Math.Max(0, start); offset <= end; offset++)
        {
            var r = BitConverter.ToSingle(data, offset);
            var g = BitConverter.ToSingle(data, offset + 4);
            var b = BitConverter.ToSingle(data, offset + 8);
            var a = BitConverter.ToSingle(data, offset + 12);
            if (!IsLikelyMaterialFloat(r) || !IsLikelyMaterialFloat(g) || !IsLikelyMaterialFloat(b)) continue;
            if (Math.Abs(r) < 0.000001f && Math.Abs(g) < 0.000001f && Math.Abs(b) < 0.000001f) continue;
            WriteFloat(data, offset, red);
            WriteFloat(data, offset + 4, green);
            WriteFloat(data, offset + 8, blue);
            if (IsLikelyMaterialFloat(a))
                WriteFloat(data, offset + 12, 1f);
            return true;
        }

        return false;
    }

    private static bool TryBoostFirstFloatAfter(byte[] data, int start, int window, float intensity)
    {
        var end = Math.Min(data.Length - 4, start + window);
        for (var offset = Math.Max(0, start); offset <= end; offset++)
        {
            var value = BitConverter.ToSingle(data, offset);
            if (!IsLikelyMaterialFloat(value)) continue;
            if (value < 0f || value > 64f) continue;
            WriteFloat(data, offset, Math.Max(value, intensity));
            return true;
        }

        return false;
    }

    private static bool IsLikelyMaterialFloat(float value)
    {
        if (!float.IsFinite(value)) return false;
        var abs = Math.Abs(value);
        return abs <= 64f;
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }
}

// ==================== Data Classes ====================

public class FloatPropertyEntry : System.ComponentModel.INotifyPropertyChanged
{
    private float _newValue, _currentValue;
    public string ExportName { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public int UexpOffset { get; set; }
    public float CurrentValue { get => _currentValue; set { _currentValue = value; OnPropertyChanged(nameof(CurrentValue)); OnPropertyChanged(nameof(ValueDisplay)); } }
    public float NewValue { get => _newValue; set { _newValue = value; OnPropertyChanged(nameof(NewValue)); OnPropertyChanged(nameof(IsModified)); } }
    public bool IsModified => Math.Abs(CurrentValue - NewValue) > 0.0001f;
    public string ValueDisplay => $"{CurrentValue:F4}";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public class RecoilCurveData
{
    public string ExportName { get; set; } = "";
    public string CurveName { get; set; } = "";
    public List<RecoilKeyEntry> Keys { get; set; } = new();
    public string DisplayLabel => $"{CurveName} ({Keys.Count} keys)";
}

public class RecoilKeyEntry : System.ComponentModel.INotifyPropertyChanged
{
    private float _time, _value, _arriveTangent, _leaveTangent;
    public int Index { get; set; }
    public float Time { get => _time; set { _time = value; OnPropertyChanged(nameof(Time)); } }
    public float Value { get => _value; set { _value = value; OnPropertyChanged(nameof(Value)); } }
    public float ArriveTangent { get => _arriveTangent; set { _arriveTangent = value; OnPropertyChanged(nameof(ArriveTangent)); } }
    public float LeaveTangent { get => _leaveTangent; set { _leaveTangent = value; OnPropertyChanged(nameof(LeaveTangent)); } }
    public string InterpMode { get; set; } = "";
    public string TangentMode { get; set; } = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public class CollisionPropertyEntry : System.ComponentModel.INotifyPropertyChanged
{
    private float _newValue, _currentValue;
    public string ExportName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public float CurrentValue { get => _currentValue; set { _currentValue = value; OnPropertyChanged(nameof(CurrentValue)); } }
    public float NewValue { get => _newValue; set { _newValue = value; OnPropertyChanged(nameof(NewValue)); OnPropertyChanged(nameof(IsModified)); } }
    public bool IsModified => Math.Abs(CurrentValue - NewValue) > 0.0001f;
    public string ValueDisplay => $"{CurrentValue:F4}";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
