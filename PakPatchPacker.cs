using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace PakToolGUI;

public static class PakPatchPacker
{
    private const uint PubgmCnMagic = 0xFF67FF70;
    private const int DefaultBlockSize = 0x10000;
    private const int PubgmCnNonCompressedHeaderSize = 0x4A;
    private const int PubgmCnFooterSize = 45;
    private const int ZlibLevel = 3;
    private const int Ph2None = 0;
    private const int Ph2LmSxr = 1;
    private const int Ph2LmStxr = 2;
    private const byte SLongMapKey = 0x7B;
    private const byte StXorKey = 0x31;
    private static readonly byte[] Ph2MatchBytes = { 0x4C, 0x69, 0x6E, 0x69, 0x6D, 0x61, 0x73, 0x73 };
    private static readonly byte[] PubgmCnZucKey = { 0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37 };
    private static readonly byte[] PubgmCnZucIv  = { 0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45 };

    public sealed record Result(int TotalFiles, int MatchedFiles, int ChangedFiles, long OutputSize);
    public sealed record RecipeRelocateResult(int TotalOperations, int RelocatedOperations, int UnchangedOperations, int FailedOperations);

    private sealed class Entry
    {
        public int Index { get; init; }
        public string Path { get; set; } = "";
        public byte[] Hash { get; set; } = new byte[20];
        public long Offset { get; set; }
        public long Size { get; set; }
        public int Method { get; set; }
        public long CompressedSize { get; set; }
        public byte Unknown { get; set; }
        public byte[] ContentHash { get; set; } = new byte[20];
        public List<(long Start, long End)> Blocks { get; set; } = new();
        public uint BlockSize { get; set; }
        public byte Encrypted { get; set; }
        public int Ph2Flag { get; set; }
    }

    private sealed class PakData
    {
        public required string MountPoint { get; init; }
        public required List<Entry> Entries { get; init; }
        public required long IndexOffset { get; init; }
        public required long IndexSize { get; init; }
        public required long FooterOffset { get; init; }
        public required int Version { get; init; }
        public required bool EncryptedIndex { get; init; }
    }

    private sealed record ChangedFile(Entry Entry, byte[] Data);
    private sealed record InPlacePatch(long Offset, byte[] Data);
    private sealed record RecoilPatchOp(string Path, int Offset, RecoilPatchKind Kind, byte[]? RawBytes);
    private enum RecoilPatchKind { FloatScale, Int32Zero, Raw }
    public const string RangePhysicsAssetPath = "ShadowTrackerExtra/Content/Arts_Player/Characters/Animation/Base_Skeleton/CH_Base_SK_PhysicsAsset.uexp";

    public sealed class PatchRecipe
    {
        public string Name { get; set; } = "";
        public List<PatchRecipeOperation> Operations { get; set; } = new();
    }

    public sealed class PatchRecipeOperation
    {
        public string Path { get; set; } = "";
        public int Offset { get; set; }
        public string Type { get; set; } = "float";
        public string Mode { get; set; } = "set";
        public float Value { get; set; }
        public float? OriginalValue { get; set; }
        public string? Hex { get; set; }
        public string? OriginalHex { get; set; }
        public string? Comment { get; set; }
    }

    public sealed class AssetReplacementRecipe
    {
        public string Name { get; set; } = "";
        public List<AssetReplacementOperation> Operations { get; set; } = new();
    }

    public sealed class AssetReplacementOperation
    {
        public string Path { get; set; } = "";
        public string OriginalSha1 { get; set; } = "";
        public string ModifiedSha1 { get; set; } = "";
        public string ModifiedBase64 { get; set; } = "";
    }

    public sealed class BinaryPatchRecipe
    {
        public string Name { get; set; } = "";
        public long SourceSize { get; set; }
        public string SourceSha1 { get; set; } = "";
        public string TargetSha1 { get; set; } = "";
        public List<BinaryPatchOperation> Operations { get; set; } = new();
    }

    public sealed class BinaryPatchOperation
    {
        public long Offset { get; set; }
        public string Hex { get; set; } = "";
        public string OriginalHex { get; set; } = "";
    }

    public static Result WritePatchedPak(string originalPak, string sourceDir, string outputPak)
    {
        var pak = ReadOriginalPak(originalPak);
        var sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => NormalizePath(Path.GetRelativePath(sourceDir, p)), p => p, StringComparer.Ordinal);
        var recentFiles = GetRecentEditBatch(sourceFiles);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        var matched = 0;
        var pendingRecent = new List<ChangedFile>();
        var changedFiles = new List<ChangedFile>();
        using (var input = new FileStream(originalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in pak.Entries)
            {
                var sourcePath = FindSourcePath(sourceFiles, pak.MountPoint, entry.Path);
                if (sourcePath == null) continue;
                matched++;

                var data = File.ReadAllBytes(sourcePath);
                var dataHash = SHA1.HashData(data);
                var originalContentHash = ComputeOriginalContentHash(input, entry);
                if (dataHash.SequenceEqual(originalContentHash))
                {
                    if (recentFiles.Contains(NormalizePath(Path.GetRelativePath(sourceDir, sourcePath))))
                        pendingRecent.Add(new ChangedFile(entry, data));
                    continue;
                }

                changedFiles.Add(new ChangedFile(entry, data));
            }
        }

        if (changedFiles.Count == 0 && pendingRecent.Count > 0)
            changedFiles.AddRange(pendingRecent);

        if (TryBuildInPlacePatches(pak, changedFiles, out var inPlacePatches, out var reason))
        {
            File.Copy(originalPak, outputPak, true);
            using var inPlaceOutput = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
            foreach (var patch in inPlacePatches)
            {
                inPlaceOutput.Position = patch.Offset;
                inPlaceOutput.Write(patch.Data);
            }

            return new Result(pak.Entries.Count, matched, changedFiles.Count, new FileInfo(outputPak).Length);
        }

        if (changedFiles.Count > 0)
            throw new InvalidOperationException("Changed files cannot be written as same-size in-place PUBGM_CN patches: " + reason);

        using var inputCopy = new FileStream(originalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(outputPak, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096 * 1024);

        CopyRange(inputCopy, output, 0, pak.IndexOffset);
        var indexOffset = output.Position;
        var indexRaw = BuildIndex(pak);
        output.Write(indexRaw);
        var indexSize = output.Position - indexOffset;

        var textSection = BuildTextSection(pak.MountPoint, pak.Entries);
        WriteI64(output, textSection.Length);
        output.Write(textSection);

        WriteFooter(output, pak.Version, indexOffset, indexSize, SHA1.HashData(indexRaw), pak.EncryptedIndex);
        return new Result(pak.Entries.Count, matched, changedFiles.Count, output.Length);
    }

    public static Result ApplyTemplatePatch(string targetOriginalPak, string templateOriginalPak, string templateModifiedPak, string outputPak)
    {
        var target = ReadOriginalPak(targetOriginalPak);
        var templateOriginal = ReadOriginalPak(templateOriginalPak);
        var templateModified = ReadOriginalPak(templateModifiedPak);

        var originalByPath = templateOriginal.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var modifiedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using var originalStream = new FileStream(templateOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var modifiedStream = new FileStream(templateModifiedPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var modifiedEntry in templateModified.Entries)
        {
            if (string.IsNullOrEmpty(modifiedEntry.Path)) continue;
            if (!originalByPath.TryGetValue(modifiedEntry.Path, out var originalEntry)) continue;
            if (originalEntry.Size != modifiedEntry.Size || originalEntry.Method != modifiedEntry.Method)
                continue;

            byte[] originalData;
            byte[] modifiedData;
            try
            {
                originalData = ReadEntryContent(originalStream, originalEntry);
                modifiedData = ReadEntryContent(modifiedStream, modifiedEntry);
            }
            catch (Exception ex) when (IsSkippableTemplateReadError(ex))
            {
                continue;
            }

            if (!originalData.SequenceEqual(modifiedData))
                modifiedFiles[modifiedEntry.Path] = modifiedData;
        }

        var changedFiles = new List<ChangedFile>();
        foreach (var entry in target.Entries)
        {
            if (modifiedFiles.TryGetValue(entry.Path, out var data))
                changedFiles.Add(new ChangedFile(entry, data));
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("Template patch did not contain any matching changed files for the target pak. Try learning or selecting a JSON recipe for this template.");

        using (var targetStream = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var changed in changedFiles)
                ComputeOriginalContentHash(targetStream, changed.Entry);
        }

        if (!TryBuildInPlacePatches(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("Template files cannot be written as same-size in-place PUBGM_CN patches: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, changedFiles.Count, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    public static int ExportAssetReplacementRecipe(string templateOriginalPak, string templateModifiedPak, string recipePath, IEnumerable<string> assetPaths, string recipeName)
    {
        var templateOriginal = ReadOriginalPak(templateOriginalPak);
        var templateModified = ReadOriginalPak(templateModifiedPak);
        var requested = assetPaths.Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        var originalByPath = templateOriginal.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var modifiedByPath = templateModified.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var recipe = new AssetReplacementRecipe { Name = recipeName };

        using var originalStream = new FileStream(templateOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var modifiedStream = new FileStream(templateModifiedPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var path in requested.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!originalByPath.TryGetValue(path, out var originalEntry) || !modifiedByPath.TryGetValue(path, out var modifiedEntry))
                continue;

            var originalData = ReadEntryContent(originalStream, originalEntry);
            var modifiedData = ReadEntryContent(modifiedStream, modifiedEntry);
            if (originalData.SequenceEqual(modifiedData))
                continue;

            recipe.Operations.Add(new AssetReplacementOperation
            {
                Path = path,
                OriginalSha1 = Convert.ToHexString(SHA1.HashData(originalData)),
                ModifiedSha1 = Convert.ToHexString(SHA1.HashData(modifiedData)),
                ModifiedBase64 = Convert.ToBase64String(modifiedData)
            });
        }

        if (recipe.Operations.Count == 0)
            throw new InvalidOperationException("No changed assets were found for this replacement recipe.");

        Directory.CreateDirectory(Path.GetDirectoryName(recipePath) ?? ".");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        return recipe.Operations.Count;
    }

    public static Result ApplyAssetReplacementRecipe(string targetOriginalPak, string recipePath, string outputPak)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<AssetReplacementRecipe>(File.ReadAllText(recipePath), options)
            ?? throw new InvalidDataException("Asset replacement recipe JSON is empty or invalid.");
        if (recipe.Operations.Count == 0)
            throw new InvalidDataException("Asset replacement recipe has no operations.");

        var target = ReadOriginalPak(targetOriginalPak);
        var byPath = target.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var changedFiles = new List<ChangedFile>();

        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var op in recipe.Operations)
            {
                var path = NormalizePath(op.Path);
                if (!byPath.TryGetValue(path, out var entry))
                    continue;

                var current = ReadEntryContent(input, entry);
                var currentSha1 = Convert.ToHexString(SHA1.HashData(current));
                if (!currentSha1.Equals(op.OriginalSha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Asset replacement source mismatch: {path} expected={op.OriginalSha1} actual={currentSha1}. Re-learn this recipe for the selected PAK version.");

                var modified = Convert.FromBase64String(op.ModifiedBase64);
                var modifiedSha1 = Convert.ToHexString(SHA1.HashData(modified));
                if (!modifiedSha1.Equals(op.ModifiedSha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Asset replacement recipe data is corrupt: {path} expected={op.ModifiedSha1} actual={modifiedSha1}.");

                changedFiles.Add(new ChangedFile(entry, modified));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("Asset replacement recipe did not match any files in the selected PAK.");

        if (!TryBuildInPlacePatches(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("Asset replacement in-place patch failed: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, recipe.Operations.Count, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    public static Result ApplyRangeMultiplier(string targetOriginalPak, string outputPak, float multiplier)
    {
        var offsets = new[] { 10673, 10702, 10731, 11147, 11176, 11205 };
        return ApplyContentMutationsInPlaceOnly(targetOriginalPak, outputPak, new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
        {
            [RangePhysicsAssetPath] = data =>
            {
                foreach (var offset in offsets)
                {
                    var value = BitConverter.ToSingle(data, offset);
                    ValidateRangePhysicsValue(value, offset);
                    WriteF32(data, offset, value * multiplier);
                }
            }
        });
    }

    public static Result ApplyRangeMultiplierAndJsonRecipeInPlaceOnly(string targetOriginalPak, string recipePath, string outputPak, float multiplier)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        var tempPak = Path.Combine(
            Path.GetDirectoryName(outputPak) ?? ".",
            Path.GetFileNameWithoutExtension(outputPak) + ".range.tmp.pak");

        try
        {
            var rangeResult = ApplyRangeMultiplier(targetOriginalPak, tempPak, multiplier);
            var recipeResult = ApplyJsonRecipeInPlaceOnly(tempPak, recipePath, outputPak);
            return new Result(
                recipeResult.TotalFiles,
                rangeResult.MatchedFiles + recipeResult.MatchedFiles,
                rangeResult.ChangedFiles + recipeResult.ChangedFiles,
                recipeResult.OutputSize);
        }
        finally
        {
            if (File.Exists(tempPak))
                File.Delete(tempPak);
        }
    }

    public static Result ApplyRangeFeatureMultiplier(string targetOriginalPak, string rangeRecipePath, string outputPak, float multiplier)
    {
        var signature = LoadRangeFeatureSignature(rangeRecipePath);
        return ApplyContentMutationsInPlaceOnly(targetOriginalPak, outputPak, new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
        {
            [RangePhysicsAssetPath] = data =>
            {
                var resolvedOffsets = ResolveRangeFeatureOffsets(data, signature);
                foreach (var item in resolvedOffsets)
                {
                    ValidateRangePhysicsValue(item.Value, item.Offset);
                    WriteF32(data, item.Offset, item.Value * multiplier);
                }
            }
        });
    }

    public static Result ApplyRecoilScale(string targetOriginalPak, string templateOriginalPak, string templateModifiedPak, string outputPak, float scale)
    {
        var ops = LearnRecoilPatchOps(templateOriginalPak, templateModifiedPak);
        var byPath = ops.GroupBy(op => op.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return ApplyContentMutationsInPlaceOnly(targetOriginalPak, outputPak, byPath.ToDictionary(
            kvp => kvp.Key,
            kvp => new Action<byte[]>(data =>
            {
                foreach (var op in kvp.Value)
                {
                    switch (op.Kind)
                    {
                        case RecoilPatchKind.FloatScale:
                            WriteF32(data, op.Offset, BitConverter.ToSingle(data, op.Offset) * scale);
                            break;
                        case RecoilPatchKind.Int32Zero:
                            Array.Clear(data, op.Offset, 4);
                            break;
                        case RecoilPatchKind.Raw:
                            op.RawBytes!.CopyTo(data, op.Offset);
                            break;
                    }
                }
            }),
            StringComparer.Ordinal));
    }

    public static Result ApplySemanticRecoilScale(string targetOriginalPak, string outputPak, float scale)
    {
        var target = ReadOriginalPak(targetOriginalPak);
        var entriesByPath = target.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var changedFiles = new List<ChangedFile>();
        var matched = 0;

        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in target.Entries)
            {
                if (!IsSemanticRecoilBlueprintUexp(entry.Path))
                    continue;

                var uassetPath = Path.ChangeExtension(entry.Path, ".uasset");
                if (!entriesByPath.TryGetValue(uassetPath, out var uassetEntry))
                    continue;

                var uassetData = ReadEntryContent(input, uassetEntry);
                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                var recoilNameIndexes = ExtractSemanticRecoilNameIndexes(uassetData, data, entry.Path);
                if (recoilNameIndexes.Count == 0)
                    continue;

                var changed = ApplySemanticRecoilFloats(data, recoilNameIndexes, scale);
                if (changed == 0)
                    continue;

                matched += changed;
                changedFiles.Add(new ChangedFile(entry, data));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("No semantic recoil FloatProperty values were found in the selected PAK.");

        if (!TryBuildInPlacePatchesPreserveIndex(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("Semantic recoil in-place patch failed: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, matched, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    public static IReadOnlyList<string> LearnRecoilAssetPaths(string templateOriginalPak, string templateModifiedPak)
    {
        return LearnRecoilPatchOps(templateOriginalPak, templateModifiedPak)
            .Select(op => op.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<string> FindPaksContainingAnyAsset(IEnumerable<string> pakPaths, IEnumerable<string> assetPaths)
    {
        var normalizedAssets = assetPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedAssets.Count == 0)
            return Array.Empty<string>();

        var matches = new List<string>();
        foreach (var pakPath in pakPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pak = ReadOriginalPak(pakPath);
                if (pak.Entries.Any(entry => normalizedAssets.Contains(entry.Path)))
                    matches.Add(pakPath);
            }
            catch
            {
                // Ignore unsupported or unreadable PAK files during auto-discovery.
            }
        }

        return matches;
    }

    public static Result ApplyRangeAndRecoil(
        string targetOriginalPak,
        string templateOriginalPak,
        string templateModifiedPak,
        string outputPak,
        float rangeMultiplier,
        float recoilScale)
    {
        var mutations = new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
        {
            [RangePhysicsAssetPath] = data =>
            {
                foreach (var offset in new[] { 10673, 10702, 10731, 11147, 11176, 11205 })
                {
                    var value = BitConverter.ToSingle(data, offset);
                    ValidateRangePhysicsValue(value, offset);
                    WriteF32(data, offset, value * rangeMultiplier);
                }
            }
        };

        var ops = LearnRecoilPatchOps(templateOriginalPak, templateModifiedPak);
        foreach (var group in ops.GroupBy(op => op.Path, StringComparer.Ordinal))
        {
            var localOps = group.ToList();
            mutations[group.Key] = data =>
            {
                foreach (var op in localOps)
                {
                    switch (op.Kind)
                    {
                        case RecoilPatchKind.FloatScale:
                            WriteF32(data, op.Offset, BitConverter.ToSingle(data, op.Offset) * recoilScale);
                            break;
                        case RecoilPatchKind.Int32Zero:
                            Array.Clear(data, op.Offset, 4);
                            break;
                        case RecoilPatchKind.Raw:
                            op.RawBytes!.CopyTo(data, op.Offset);
                            break;
                    }
                }
            };
        }

        return ApplyContentMutationsInPlaceOnly(targetOriginalPak, outputPak, mutations);
    }

    public static Result ApplyJsonRecipe(string targetOriginalPak, string recipePath, string outputPak)
    {
        return ApplyJsonRecipe(targetOriginalPak, recipePath, outputPak, Array.Empty<PatchRecipeOperation>(), inPlaceOnly: false);
    }

    public static Result ApplyJsonRecipeInPlaceOnly(string targetOriginalPak, string recipePath, string outputPak)
    {
        return ApplyJsonRecipe(targetOriginalPak, recipePath, outputPak, Array.Empty<PatchRecipeOperation>(), inPlaceOnly: true);
    }

    public static int ExportBinaryPatchRecipe(string originalPak, string modifiedPak, string recipePath, string recipeName)
    {
        var before = File.ReadAllBytes(originalPak);
        var after = File.ReadAllBytes(modifiedPak);
        if (before.Length != after.Length)
            throw new InvalidDataException("Binary glow learning requires original and modified PAK files to have the same size.");

        var recipe = new BinaryPatchRecipe
        {
            Name = recipeName,
            SourceSize = before.Length,
            SourceSha1 = Convert.ToHexString(SHA1.HashData(before)),
            TargetSha1 = Convert.ToHexString(SHA1.HashData(after))
        };

        for (var i = 0; i < before.Length;)
        {
            if (before[i] == after[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < before.Length && before[i] != after[i])
                i++;

            recipe.Operations.Add(new BinaryPatchOperation
            {
                Offset = start,
                Hex = Convert.ToHexString(after.AsSpan(start, i - start)),
                OriginalHex = Convert.ToHexString(before.AsSpan(start, i - start))
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(recipePath) ?? ".");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }));
        return recipe.Operations.Count;
    }

    public static Result ApplyBinaryPatchRecipe(string targetOriginalPak, string recipePath, string outputPak)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<BinaryPatchRecipe>(File.ReadAllText(recipePath), options)
            ?? throw new InvalidDataException("Binary patch recipe JSON is empty or invalid.");
        if (recipe.Operations.Count == 0)
            throw new InvalidDataException("Binary patch recipe does not contain any operations.");

        var data = File.ReadAllBytes(targetOriginalPak);
        if (data.Length != recipe.SourceSize)
            throw new InvalidDataException($"Binary patch source size mismatch. Expected {recipe.SourceSize}, actual {data.Length}.");

        var sourceHash = Convert.ToHexString(SHA1.HashData(data));
        if (!sourceHash.Equals(recipe.SourceSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Binary patch source hash mismatch. Learn a new binary glow patch for this game_patch version.");

        foreach (var op in recipe.Operations)
        {
            var original = Convert.FromHexString(op.OriginalHex);
            var replacement = Convert.FromHexString(op.Hex);
            if (op.Offset < 0 || op.Offset + original.Length > data.Length)
                throw new InvalidDataException($"Binary patch operation out of range at {op.Offset}.");
            if (!data.AsSpan((int)op.Offset, original.Length).SequenceEqual(original))
                throw new InvalidDataException($"Binary patch original bytes mismatch at {op.Offset}.");
            replacement.CopyTo(data.AsSpan((int)op.Offset));
        }

        var targetHash = Convert.ToHexString(SHA1.HashData(data));
        if (!string.IsNullOrWhiteSpace(recipe.TargetSha1)
            && !targetHash.Equals(recipe.TargetSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Binary patch target hash mismatch after applying operations.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.WriteAllBytes(outputPak, data);
        return new Result(1, 1, 1, data.Length);
    }

    public static Result ApplyBinaryPatchRecipes(string targetOriginalPak, IReadOnlyList<string> recipePaths, string outputPak)
    {
        if (recipePaths.Count == 0)
            throw new InvalidDataException("No binary patch recipes were provided.");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipes = recipePaths
            .Select(path => JsonSerializer.Deserialize<BinaryPatchRecipe>(File.ReadAllText(path), options)
                ?? throw new InvalidDataException($"Binary patch recipe JSON is empty or invalid: {path}"))
            .ToList();

        if (recipes.Any(recipe => recipe.Operations.Count == 0))
            throw new InvalidDataException("One or more binary patch recipes do not contain any operations.");

        var data = File.ReadAllBytes(targetOriginalPak);
        var sourceHash = Convert.ToHexString(SHA1.HashData(data));
        foreach (var recipe in recipes)
        {
            if (data.Length != recipe.SourceSize)
                throw new InvalidDataException($"Binary patch source size mismatch. Expected {recipe.SourceSize}, actual {data.Length}.");
            if (!sourceHash.Equals(recipe.SourceSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Binary patch source hash mismatch. Learn a new binary patch for this PAK version.");
        }

        var claimed = new Dictionary<long, byte>();
        var changedRanges = 0;
        foreach (var recipe in recipes)
        {
            foreach (var op in recipe.Operations)
            {
                var original = Convert.FromHexString(op.OriginalHex);
                var replacement = Convert.FromHexString(op.Hex);
                if (original.Length != replacement.Length)
                    throw new InvalidDataException($"Binary patch operation length mismatch at {op.Offset}.");
                if (op.Offset < 0 || op.Offset + original.Length > data.Length)
                    throw new InvalidDataException($"Binary patch operation out of range at {op.Offset}.");
                if (!data.AsSpan((int)op.Offset, original.Length).SequenceEqual(original))
                    throw new InvalidDataException($"Binary patch original bytes mismatch at {op.Offset}.");

                for (var i = 0; i < replacement.Length; i++)
                {
                    var offset = op.Offset + i;
                    if (claimed.TryGetValue(offset, out var existing) && existing != replacement[i])
                        throw new InvalidDataException($"Binary patch recipes conflict at offset {offset}.");
                    claimed[offset] = replacement[i];
                }

                changedRanges++;
            }
        }

        foreach (var item in claimed)
            data[(int)item.Key] = item.Value;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.WriteAllBytes(outputPak, data);
        return new Result(recipes.Count, recipes.Count, changedRanges, data.Length);
    }

    public static Result ApplyGlowColorRecipe(string targetOriginalPak, string recipePath, string outputPak, float red, float green, float blue, float intensity)
    {
        return ApplyJsonRecipe(targetOriginalPak, recipePath, outputPak);
    }

    public const string CommanderKeyMaterialPath = "ShadowTrackerExtra/Content/Mod/_Item_Common/_Goods/_YY/_CG036/KEY/Art_Player/Material/M_Sale_Zhihuiguan.uasset";
    private const string CommanderKeyMaterialFolder = "ShadowTrackerExtra/Content/Mod/_Item_Common/_Goods/_YY/_CG036/KEY/Art_Player/Material/";
    private const string CommanderKeyMaterialName = "M_Sale_Zhihuiguan.uasset";
    public const string SignalGunWrapperPath = "ShadowTrackerExtra/Content/Arts_PlayerBluePrints/Weapon/MainWeapon/Pistol/Flaregun/BP_Pistol_Flaregun_Wrapper.uexp";
    private const string SignalGunMaterialFolder = "ShadowTrackerExtra/Content/Arts_Player/Weapon/MainWeapon/Pistol/FlareGun/Texture/";
    private static readonly string[] SignalGunMaterialNames =
    {
        "M_WEP_FlareGun.uasset",
        "M_WEP_FlareGun_Lod.uasset",
        "M_WEP_FlareGun_Pickup.uasset"
    };

    public static Result ApplyCommanderKeyGlow(string targetOriginalPak, string outputPak, float red, float green, float blue, float intensity)
    {
        red = ClampColorComponent(red);
        green = ClampColorComponent(green);
        blue = ClampColorComponent(blue);
        intensity = Math.Max(0f, float.IsFinite(intensity) ? intensity : 0f);

        var target = ReadOriginalPak(targetOriginalPak);
        var entries = ResolveCommanderKeyEntries(target.Entries);
        if (entries.Count == 0)
            throw new InvalidOperationException("Commander Key materials were not found in the selected PAK.");

        var changedFiles = new List<ChangedFile>();
        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in entries)
            {
                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                var entryIntensity = IsCommanderKeyMaterial(entry.Path) ? intensity : Math.Min(intensity, 2f);
                if (!UAssetParser.TryApplyMaterialGlow(data, entry.Path, red, green, blue, entryIntensity, out var patched, out _))
                    continue;
                if (data.SequenceEqual(patched))
                    continue;

                changedFiles.Add(new ChangedFile(entry, patched));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("Commander Key materials were found, but none exposed supported glow/color parameters.");

        if (!TryBuildInPlacePatchesPreserveIndex(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("Commander Key in-place patch failed: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, entries.Count, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    public static Result ApplySignalGunGlowAndScale(string targetOriginalPak, string outputPak, float red, float green, float blue, float intensity, float scale)
    {
        return ApplySignalGunGlowAndScale(targetOriginalPak, outputPak, red, green, blue, intensity, scale, patchGlow: true, patchScale: true);
    }

    public static Result ApplySignalGunGlowOnly(string targetOriginalPak, string outputPak, float red, float green, float blue, float intensity)
    {
        return ApplySignalGunGlowAndScale(targetOriginalPak, outputPak, red, green, blue, intensity, scale: 1f, patchGlow: true, patchScale: false);
    }

    public static Result ApplySignalGunScaleOnly(string targetOriginalPak, string outputPak, float scale)
    {
        return ApplySignalGunGlowAndScale(targetOriginalPak, outputPak, red: 0f, green: 1f, blue: 0.4f, intensity: 0f, scale, patchGlow: false, patchScale: true);
    }

    private static Result ApplySignalGunGlowAndScale(string targetOriginalPak, string outputPak, float red, float green, float blue, float intensity, float scale, bool patchGlow, bool patchScale)
    {
        red = ClampColorComponent(red);
        green = ClampColorComponent(green);
        blue = ClampColorComponent(blue);
        intensity = Math.Max(0f, float.IsFinite(intensity) ? intensity : 0f);
        scale = Math.Max(0.01f, float.IsFinite(scale) ? scale : 1f);

        var target = ReadOriginalPak(targetOriginalPak);
        var entriesByPath = target.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var materialEntries = ResolveSignalGunMaterialEntries(target.Entries);
        var changedFiles = new List<ChangedFile>();

        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (patchGlow)
            {
                foreach (var entry in materialEntries)
                {
                    ComputeOriginalContentHash(input, entry);
                    var data = ReadEntryContent(input, entry);
                    if (!UAssetParser.TryApplyMaterialGlow(data, entry.Path, red, green, blue, intensity, out var patched, out _))
                        continue;
                    if (!data.SequenceEqual(patched))
                        changedFiles.Add(new ChangedFile(entry, patched));
                }
            }

            if (patchScale && entriesByPath.TryGetValue(SignalGunWrapperPath, out var wrapperEntry))
            {
                ComputeOriginalContentHash(input, wrapperEntry);
                var data = ReadEntryContent(input, wrapperEntry);
                if (TryPatchSignalGunWrapperScale(data, scale))
                    changedFiles.Add(new ChangedFile(wrapperEntry, data));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("Signal gun requested materials/scale were not found or could not be patched.");

        if (!TryBuildInPlacePatchesPreserveIndex(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("Signal gun glow/scale in-place patch failed: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, materialEntries.Count + 1, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    private static Entry? ResolveCommanderKeyEntry(IEnumerable<Entry> entries)
    {
        return ResolveCommanderKeyEntries(entries).FirstOrDefault();
    }

    private static List<Entry> ResolveCommanderKeyEntries(IEnumerable<Entry> entries)
    {
        var list = entries.ToList();
        var materialEntries = list
            .Where(entry => entry.Path.StartsWith(CommanderKeyMaterialFolder, StringComparison.OrdinalIgnoreCase)
                && entry.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => IsCommanderKeyMaterial(entry.Path) ? 0 : 1)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (materialEntries.Count > 0)
            return materialEntries;

        string[][] tokenSets =
        {
            new[] { "CG036", "KEY", "M_Sale" },
            new[] { "CG036", "KEY", "Zhihuiguan" },
            new[] { "KEY", "Art_Player", "Material" },
            new[] { "Goods", "KEY", "Material" },
            new[] { "Zhihuiguan" }
        };

        foreach (var tokens in tokenSets)
        {
            var matches = list
                .Where(entry => entry.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    && tokens.All(token => entry.Path.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => IsCommanderKeyMaterial(entry.Path) ? 0 : 1)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count > 0)
                return matches;
        }

        return new List<Entry>();
    }

    private static bool IsCommanderKeyMaterial(string path)
    {
        return path.EndsWith(CommanderKeyMaterialName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Entry> ResolveSignalGunMaterialEntries(IEnumerable<Entry> entries)
    {
        return entries
            .Where(entry => entry.Path.StartsWith(SignalGunMaterialFolder, StringComparison.OrdinalIgnoreCase)
                && SignalGunMaterialNames.Any(name => entry.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryPatchSignalGunWrapperScale(byte[] data, float scale)
    {
        var changed = false;
        for (var offset = 0; offset + 12 <= data.Length; offset++)
        {
            var x = BitConverter.ToSingle(data, offset);
            var y = BitConverter.ToSingle(data, offset + 4);
            var z = BitConverter.ToSingle(data, offset + 8);
            if (Math.Abs(x - 1.5f) > 0.0001f || Math.Abs(y - 1.5f) > 0.0001f || Math.Abs(z - 1.5f) > 0.0001f)
                continue;

            WriteF32(data, offset, scale);
            WriteF32(data, offset + 4, scale);
            WriteF32(data, offset + 8, scale);
            changed = true;
        }

        return changed;
    }

    public static Result ApplySkinMaterialGlowFromCsv(string targetOriginalPak, string csvPath, string outputPak, float red, float green, float blue, float intensity)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Skin material CSV not found.", csvPath);

        red = ClampColorComponent(red);
        green = ClampColorComponent(green);
        blue = ClampColorComponent(blue);
        intensity = Math.Max(0f, float.IsFinite(intensity) ? intensity : 0f);

        var requestedAssets = ReadMaterialScanCsv(csvPath)
            .Where(row => row.Asset.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(row => IsCharacterSkinMaterialAsset(row.Asset))
            .Select(row => NormalizePath(row.Asset))
            .ToHashSet(StringComparer.Ordinal);

        if (requestedAssets.Count == 0)
            throw new InvalidDataException("Skin material CSV does not contain any character/skin .uasset rows.");

        var target = ReadOriginalPak(targetOriginalPak);
        var changedFiles = new List<ChangedFile>();
        var matchedAssets = 0;
        var parsedAssets = 0;
        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in target.Entries)
            {
                if (!requestedAssets.Contains(entry.Path)) continue;
                matchedAssets++;

                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                if (!UAssetParser.TryApplyMaterialGlow(data, entry.Path, red, green, blue, intensity, out var patched, out _))
                    continue;
                parsedAssets++;
                if (data.SequenceEqual(patched))
                    continue;

                changedFiles.Add(new ChangedFile(entry, patched));
            }

            if (changedFiles.Count == 0)
            {
                input.Position = 0;
                foreach (var entry in target.Entries)
                {
                    if (!entry.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (requestedAssets.Contains(entry.Path))
                        continue;
                    if (!IsCharacterSkinMaterialAsset(entry.Path))
                        continue;

                    ComputeOriginalContentHash(input, entry);
                    var data = ReadEntryContent(input, entry);
                    if (!UAssetParser.TryApplyMaterialGlow(data, entry.Path, red, green, blue, intensity, out var patched, out _))
                        continue;
                    parsedAssets++;
                    if (data.SequenceEqual(patched))
                        continue;

                    changedFiles.Add(new ChangedFile(entry, patched));
                }
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException($"No matching skin material parameters were changed in the selected PAK. CSV assets matched={matchedAssets}, patched candidates={parsedAssets}. Automatic character-material fallback also found no changes.");

        if (!TryBuildBestEffortInPlacePatches(target, changedFiles, out var patches, out var appliedCount, out var skipped))
            throw new InvalidOperationException("In-place skin material glow patch failed for every changed material. First skipped: " + skipped.FirstOrDefault());

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, changedFiles.Count, appliedCount, new FileInfo(outputPak).Length);
    }

    public static int CountPatchableSkinMaterialGlow(string targetOriginalPak, string csvPath, float red, float green, float blue, float intensity, bool restrictToCsvAssets)
    {
        if (restrictToCsvAssets && !File.Exists(csvPath))
            throw new FileNotFoundException("Skin material CSV not found.", csvPath);

        red = ClampColorComponent(red);
        green = ClampColorComponent(green);
        blue = ClampColorComponent(blue);
        intensity = Math.Max(0f, float.IsFinite(intensity) ? intensity : 0f);

        HashSet<string>? requestedAssets = null;
        if (restrictToCsvAssets)
        {
            requestedAssets = ReadMaterialScanCsv(csvPath)
                .Where(row => row.Asset.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Where(row => IsCharacterSkinMaterialAsset(row.Asset))
                .Select(row => NormalizePath(row.Asset))
                .ToHashSet(StringComparer.Ordinal);
            if (requestedAssets.Count == 0)
                return 0;
        }

        var target = ReadOriginalPak(targetOriginalPak);
        var changed = 0;
        using var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var entry in target.Entries)
        {
            if (!entry.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                continue;
            if (requestedAssets != null)
            {
                if (!requestedAssets.Contains(entry.Path))
                    continue;
            }
            else if (!IsCharacterSkinMaterialAsset(entry.Path))
            {
                continue;
            }

            try
            {
                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                if (!UAssetParser.TryApplyMaterialGlow(data, entry.Path, red, green, blue, intensity, out var patched, out _))
                    continue;
                if (!data.SequenceEqual(patched))
                    changed++;
            }
            catch
            {
                // Ignore individual assets that cannot be read during target discovery.
            }
        }

        return changed;
    }

    public static bool PakContainsAsset(string pakPath, string assetPath)
    {
        var normalized = NormalizePath(assetPath);
        try
        {
            var pak = ReadOriginalPak(pakPath);
            return pak.Entries.Any(entry => entry.Path.Equals(normalized, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> FindPaksContainingAsset(IEnumerable<string> pakPaths, string assetPath)
    {
        var normalized = NormalizePath(assetPath);
        var matches = new List<string>();
        foreach (var pakPath in pakPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pak = ReadOriginalPak(pakPath);
                if (pak.Entries.Any(entry => entry.Path.Equals(normalized, StringComparison.Ordinal)))
                    matches.Add(pakPath);
            }
            catch
            {
                // Ignore PAKs that are not supported by this patcher.
            }
        }

        return matches;
    }

    public static IReadOnlyList<string> FindPaksContainingPathTokens(IEnumerable<string> pakPaths, IEnumerable<string> requiredTokens)
    {
        var tokens = requiredTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(NormalizePath)
            .ToList();
        if (tokens.Count == 0)
            return Array.Empty<string>();

        var matches = new List<string>();
        foreach (var pakPath in pakPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pak = ReadOriginalPak(pakPath);
                if (pak.Entries.Any(entry => tokens.All(token => entry.Path.Contains(token, StringComparison.OrdinalIgnoreCase))))
                    matches.Add(pakPath);
            }
            catch
            {
                // Ignore unsupported or unreadable PAK files during auto-discovery.
            }
        }

        return matches;
    }

    public static IReadOnlyList<string> GetSkinMaterialPakHintsFromCsv(string csvPath)
    {
        if (!File.Exists(csvPath))
            return Array.Empty<string>();

        return ReadMaterialScanCsv(csvPath)
            .Where(row => row.Asset.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(row => IsCharacterSkinMaterialAsset(row.Asset))
            .Select(row => row.Pak)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> FindSkinMaterialGlowTargetsFromCsv(string csvPath, IEnumerable<string> pakPaths)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Skin material CSV not found.", csvPath);

        var requestedAssets = ReadMaterialScanCsv(csvPath)
            .Where(row => row.Asset.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(row => IsCharacterSkinMaterialAsset(row.Asset))
            .Select(row => NormalizePath(row.Asset))
            .ToHashSet(StringComparer.Ordinal);
        if (requestedAssets.Count == 0)
            return Array.Empty<string>();

        var matches = new List<string>();
        foreach (var pakPath in pakPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pak = ReadOriginalPak(pakPath);
                if (pak.Entries.Any(entry => requestedAssets.Contains(entry.Path)))
                    matches.Add(pakPath);
            }
            catch
            {
                // Ignore unsupported or unreadable PAK files during auto-discovery.
            }
        }

        return matches;
    }

    private static Result ApplyJsonRecipe(string targetOriginalPak, string recipePath, string outputPak, IReadOnlyCollection<PatchRecipeOperation> extraOperations, bool inPlaceOnly)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<PatchRecipe>(File.ReadAllText(recipePath), options)
            ?? throw new InvalidDataException("Recipe JSON is empty or invalid.");
        if (recipe.Operations.Count == 0 && extraOperations.Count == 0)
            throw new InvalidDataException("Recipe does not contain any operations.");

        var operations = extraOperations.Count == 0
            ? recipe.Operations
            : recipe.Operations.Concat(extraOperations).ToList();

        var grouped = operations
            .Where(op => !string.IsNullOrWhiteSpace(op.Path))
            .GroupBy(op => NormalizePath(op.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var mutations = grouped.ToDictionary(
            kvp => kvp.Key,
            kvp => new Action<byte[]>(data =>
            {
                foreach (var op in kvp.Value)
                {
                    ValidateRecipeOperation(data, op);
                    ApplyRecipeOperation(data, op);
                }
            }),
            StringComparer.Ordinal);

        return inPlaceOnly
            ? ApplyContentMutationsInPlaceOnly(targetOriginalPak, outputPak, mutations)
            : ApplyContentMutations(targetOriginalPak, outputPak, mutations);
    }

    private sealed record MaterialScanRow(string Pak, string Asset, string Parameters);

    private static bool IsCharacterSkinMaterialAsset(string assetPath)
    {
        var path = NormalizePath(assetPath);
        if (path.Contains("/Vehicle/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Weapon/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Prop/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/PickUp/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/BluePrints/Vehicle/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Vehicle", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Car", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Bike", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Motor", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Boat", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Plane", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Contains("/Arts_Player/Characters/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Characters/Mesh/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Avatar/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/WholeBody/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Protagonist/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Mascot/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Pet/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Hair/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Cloth/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Jacket", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Pants", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Shoes", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Mask", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<MaterialScanRow> ReadMaterialScanCsv(string csvPath)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine();
        if (header == null) yield break;

        var columns = SplitCsvLine(header);
        var pakIndex = columns.FindIndex(c => c.Equals("Pak", StringComparison.OrdinalIgnoreCase));
        var assetIndex = columns.FindIndex(c => c.Equals("Asset", StringComparison.OrdinalIgnoreCase));
        var parametersIndex = columns.FindIndex(c => c.Equals("Parameters", StringComparison.OrdinalIgnoreCase));
        if (assetIndex < 0)
            throw new InvalidDataException("Skin material CSV must contain an Asset column.");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = SplitCsvLine(line);
            if (assetIndex >= values.Count) continue;

            yield return new MaterialScanRow(
                pakIndex >= 0 && pakIndex < values.Count ? values[pakIndex] : "",
                values[assetIndex],
                parametersIndex >= 0 && parametersIndex < values.Count ? values[parametersIndex] : "");
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(value.ToString());
                value.Clear();
                continue;
            }

            value.Append(ch);
        }

        values.Add(value.ToString());
        return values;
    }

    private static float ClampColorComponent(float value)
    {
        if (!float.IsFinite(value)) return 0f;
        return Math.Clamp(value, 0f, 1f);
    }

    public static void WriteExampleRecipe(string recipePath)
    {
        const string physicsAssetPath = "ShadowTrackerExtra/Content/Arts_Player/Characters/Animation/Base_Skeleton/CH_Base_SK_PhysicsAsset.uexp";
        var recipe = new PatchRecipe
        {
            Name = "custom_map_lobby_patch",
            Operations =
            {
                new PatchRecipeOperation
                {
                    Path = physicsAssetPath,
                    Offset = 10673,
                    Type = "float",
                    Mode = "multiply",
                    Value = 2,
                    Comment = "Range value: 35 -> 70 when Value is 2."
                },
                new PatchRecipeOperation { Path = physicsAssetPath, Offset = 10702, Type = "float", Mode = "multiply", Value = 2 },
                new PatchRecipeOperation { Path = physicsAssetPath, Offset = 10731, Type = "float", Mode = "multiply", Value = 2 },
                new PatchRecipeOperation { Path = physicsAssetPath, Offset = 11147, Type = "float", Mode = "multiply", Value = 2 },
                new PatchRecipeOperation { Path = physicsAssetPath, Offset = 11176, Type = "float", Mode = "multiply", Value = 2 },
                new PatchRecipeOperation { Path = physicsAssetPath, Offset = 11205, Type = "float", Mode = "multiply", Value = 2 }
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(recipePath) ?? ".");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, options));
    }

    public static int ExportTemplateRecipe(string templateOriginalPak, string templateModifiedPak, string recipePath, string recipeName)
    {
        var original = ReadOriginalPak(templateOriginalPak);
        var modified = ReadOriginalPak(templateModifiedPak);
        var modifiedByPath = modified.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var operations = new List<PatchRecipeOperation>();

        using var originalStream = new FileStream(templateOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var modifiedStream = new FileStream(templateModifiedPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var entry in original.Entries)
        {
            if (!modifiedByPath.TryGetValue(entry.Path, out var modifiedEntry)) continue;
            if (entry.Size != modifiedEntry.Size) continue;

            byte[] before;
            byte[] after;
            try
            {
                before = ReadEntryContent(originalStream, entry);
                after = ReadEntryContent(modifiedStream, modifiedEntry);
            }
            catch
            {
                continue;
            }

            if (before.SequenceEqual(after)) continue;

            operations.AddRange(BuildRecipeOperations(entry.Path, before, after));
        }

        var recipe = new PatchRecipe
        {
            Name = recipeName,
            Operations = operations
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(recipePath) ?? ".");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, options));
        return operations.Count;
    }

    public static RecipeRelocateResult RelocateRecipe(string newOriginalPak, string recipePath, string outputRecipePath, int searchWindow = 8192)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<PatchRecipe>(File.ReadAllText(recipePath), options)
            ?? throw new InvalidDataException("Recipe JSON is empty or invalid.");
        if (recipe.Operations.Count == 0)
            throw new InvalidDataException("Recipe does not contain any operations.");

        var pak = ReadOriginalPak(newOriginalPak);
        var entriesByPath = pak.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var relocated = 0;
        var unchanged = 0;
        var failed = 0;

        using var input = new FileStream(newOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var group in recipe.Operations.Where(op => !string.IsNullOrWhiteSpace(op.Path)).GroupBy(op => NormalizePath(op.Path), StringComparer.Ordinal))
        {
            var entry = ResolveRelocateEntry(pak.Entries, entriesByPath, group.Key);
            if (entry == null)
            {
                failed += group.Count();
                continue;
            }

            byte[] data;
            try
            {
                data = ReadEntryContent(input, entry);
            }
            catch
            {
                failed += group.Count();
                continue;
            }

            foreach (var op in group)
            {
                var needle = BuildOriginalNeedle(op);
                if (needle == null || needle.Length == 0)
                {
                    failed++;
                    continue;
                }

                var oldOffset = op.Offset;
                var newOffset = FindBestNeedleOffset(data, needle, oldOffset, searchWindow);
                if (newOffset < 0)
                {
                    failed++;
                    continue;
                }

                op.Path = entry.Path;
                op.Offset = newOffset;
                if (newOffset == oldOffset)
                {
                    unchanged++;
                }
                else
                {
                    relocated++;
                    op.Comment = AppendComment(op.Comment, $"relocated offset {oldOffset} -> {newOffset}");
                }
            }
        }

        recipe.Name = string.IsNullOrWhiteSpace(recipe.Name) ? "relocated_recipe" : recipe.Name + "_relocated";
        var writeOptions = new JsonSerializerOptions { WriteIndented = true };
        Directory.CreateDirectory(Path.GetDirectoryName(outputRecipePath) ?? ".");
        File.WriteAllText(outputRecipePath, JsonSerializer.Serialize(recipe, writeOptions));
        return new RecipeRelocateResult(recipe.Operations.Count, relocated, unchanged, failed);
    }

    private static Result ApplyContentMutations(string targetOriginalPak, string outputPak, IReadOnlyDictionary<string, Action<byte[]>> mutations)
    {
        var target = ReadOriginalPak(targetOriginalPak);
        var changedFiles = new List<ChangedFile>();
        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in target.Entries)
            {
                if (!mutations.TryGetValue(entry.Path, out var mutate)) continue;
                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                mutate(data);
                changedFiles.Add(new ChangedFile(entry, data));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("No matching files found for this value patch.");

        if (!TryBuildInPlacePatches(target, changedFiles, out var patches, out var reason))
            return WriteRebuiltPak(target, targetOriginalPak, changedFiles, outputPak);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, changedFiles.Count, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    private static Result ApplyContentMutationsInPlaceOnly(string targetOriginalPak, string outputPak, IReadOnlyDictionary<string, Action<byte[]>> mutations)
    {
        var target = ReadOriginalPak(targetOriginalPak);
        var changedFiles = new List<ChangedFile>();
        using (var input = new FileStream(targetOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var entry in target.Entries)
            {
                if (!mutations.TryGetValue(entry.Path, out var mutate)) continue;
                ComputeOriginalContentHash(input, entry);
                var data = ReadEntryContent(input, entry);
                mutate(data);
                changedFiles.Add(new ChangedFile(entry, data));
            }
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("No matching files found for this value patch.");

        if (!TryBuildInPlacePatchesPreserveIndex(target, changedFiles, out var patches, out var reason))
            throw new InvalidOperationException("In-place patch failed: " + reason);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");
        File.Copy(targetOriginalPak, outputPak, true);
        using var output = new FileStream(outputPak, FileMode.Open, FileAccess.Write, FileShare.None, 4096 * 1024);
        foreach (var patch in patches)
        {
            output.Position = patch.Offset;
            output.Write(patch.Data);
        }

        return new Result(target.Entries.Count, changedFiles.Count, changedFiles.Count, new FileInfo(outputPak).Length);
    }

    private static Result WriteRebuiltPak(PakData pak, string originalPak, IReadOnlyList<ChangedFile> changedFiles, string outputPak)
    {
        var changedByEntry = changedFiles.ToDictionary(c => c.Entry, c => c.Data);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPak) ?? ".");

        using var input = new FileStream(originalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(outputPak, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096 * 1024);

        foreach (var entry in pak.Entries.OrderBy(e => e.Offset))
        {
            if (changedByEntry.TryGetValue(entry, out var changedData))
            {
                WriteChangedEntry(output, entry, changedData);
                continue;
            }

            CopyUnchangedEntry(input, output, entry);
        }

        var indexOffset = output.Position;
        var indexRaw = BuildIndex(pak);
        output.Write(indexRaw);
        var indexSize = output.Position - indexOffset;

        var textSection = BuildTextSection(pak.MountPoint, pak.Entries);
        WriteI64(output, textSection.Length);
        output.Write(textSection);

        WriteFooter(output, pak.Version, indexOffset, indexSize, SHA1.HashData(indexRaw), pak.EncryptedIndex);
        return new Result(pak.Entries.Count, changedFiles.Count, changedFiles.Count, output.Length);
    }

    private static void ApplyRecipeOperation(byte[] data, PatchRecipeOperation op)
    {
        var type = op.Type.Trim().ToLowerInvariant();
        var mode = op.Mode.Trim().ToLowerInvariant();
        switch (type)
        {
            case "float":
            case "f32":
                EnsureRange(data, op.Offset, 4, op);
                var current = BitConverter.ToSingle(data, op.Offset);
                var next = mode switch
                {
                    "multiply" or "mul" or "scale" => current * op.Value,
                    "add" => current + op.Value,
                    "subtract" or "sub" => current - op.Value,
                    _ => op.Value
                };
                WriteF32(data, op.Offset, next);
                break;

            case "int32":
            case "i32":
                EnsureRange(data, op.Offset, 4, op);
                var intCurrent = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(op.Offset, 4));
                var intNext = mode switch
                {
                    "add" => intCurrent + (int)op.Value,
                    "subtract" or "sub" => intCurrent - (int)op.Value,
                    "multiply" or "mul" or "scale" => (int)(intCurrent * op.Value),
                    _ => (int)op.Value
                };
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(op.Offset, 4), intNext);
                break;

            case "bytes":
            case "hex":
                if (string.IsNullOrWhiteSpace(op.Hex))
                    throw new InvalidDataException($"Recipe operation for {op.Path} at {op.Offset} has no hex value.");
                var bytes = Convert.FromHexString(op.Hex.Replace(" ", "").Replace("-", ""));
                EnsureRange(data, op.Offset, bytes.Length, op);
                bytes.CopyTo(data, op.Offset);
                break;

            default:
                throw new InvalidDataException($"Unsupported recipe type '{op.Type}' for {op.Path} at {op.Offset}.");
        }
    }

    private static void ValidateRangePhysicsValue(float value, int offset)
    {
        if (!float.IsFinite(value) || value <= 0f || value > 500f)
            throw new InvalidDataException($"Range PhysicsAsset value at offset {offset} does not look valid: {value}. Use the learned range recipe and relocate it for this game version.");
    }

    private sealed record RangeFeaturePoint(int Offset, int Delta, float Value);

    private static List<RangeFeaturePoint> LoadRangeFeatureSignature(string rangeRecipePath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<PatchRecipe>(File.ReadAllText(rangeRecipePath), options)
            ?? throw new InvalidDataException("Range recipe JSON is empty or invalid.");

        var points = recipe.Operations
            .Where(op => NormalizePath(op.Path).Equals(RangePhysicsAssetPath, StringComparison.Ordinal))
            .Where(op => op.Type.Equals("float", StringComparison.OrdinalIgnoreCase) || op.Type.Equals("f32", StringComparison.OrdinalIgnoreCase))
            .Where(op => op.OriginalValue.HasValue)
            .Where(op => Math.Abs(op.OriginalValue!.Value) >= 0.001f)
            .OrderBy(op => op.Offset)
            .Select(op => new { op.Offset, Value = op.OriginalValue!.Value })
            .ToList();

        if (points.Count < 3)
            throw new InvalidDataException("Range recipe does not contain enough stable float feature points.");

        var baseOffset = points[0].Offset;
        return points
            .Select(point => new RangeFeaturePoint(point.Offset, point.Offset - baseOffset, point.Value))
            .ToList();
    }

    private static List<(int Offset, float Value)> ResolveRangeFeatureOffsets(byte[] data, IReadOnlyList<RangeFeaturePoint> signature)
    {
        const float tolerance = 0.001f;
        var first = signature[0];
        var matches = new List<List<(int Offset, float Value)>>();

        for (var candidate = 0; candidate + 4 < data.Length; candidate++)
        {
            var value = BitConverter.ToSingle(data, candidate);
            if (Math.Abs(value - first.Value) > tolerance)
                continue;

            var resolved = new List<(int Offset, float Value)>();
            var ok = true;
            foreach (var point in signature)
            {
                var offset = candidate + point.Delta;
                if (offset < 0 || offset + 4 > data.Length)
                {
                    ok = false;
                    break;
                }

                var current = BitConverter.ToSingle(data, offset);
                if (Math.Abs(current - point.Value) > tolerance)
                {
                    ok = false;
                    break;
                }

                resolved.Add((offset, current));
            }

            if (ok)
                matches.Add(resolved);
        }

        if (matches.Count == 0)
            throw new InvalidDataException("Range feature signature was not found in the target PhysicsAsset. Re-learn range from this game version.");
        if (matches.Count > 1)
            throw new InvalidDataException("Range feature signature matched multiple locations in the target PhysicsAsset. Refusing to guess.");

        return matches[0];
    }

    private static void ValidateRecipeOperation(byte[] data, PatchRecipeOperation op)
    {
        var type = (op.Type ?? "float").Trim().ToLowerInvariant();
        switch (type)
        {
            case "float":
            case "f32":
                if (!op.OriginalValue.HasValue) return;
                EnsureRange(data, op.Offset, 4, op);
                var current = BitConverter.ToSingle(data, op.Offset);
                if (Math.Abs(current - op.OriginalValue.Value) > 0.0001f)
                    throw new InvalidDataException($"Recipe original float mismatch: {op.Path} offset={op.Offset} expected={op.OriginalValue.Value} actual={current}. Relocate or relearn this recipe for the selected pak.");
                break;

            case "int32":
            case "i32":
                if (!op.OriginalValue.HasValue) return;
                EnsureRange(data, op.Offset, 4, op);
                var intCurrent = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(op.Offset, 4));
                if (intCurrent != (int)op.OriginalValue.Value)
                    throw new InvalidDataException($"Recipe original int mismatch: {op.Path} offset={op.Offset} expected={(int)op.OriginalValue.Value} actual={intCurrent}. Relocate or relearn this recipe for the selected pak.");
                break;

            case "bytes":
            case "hex":
                if (string.IsNullOrWhiteSpace(op.OriginalHex)) return;
                var originalBytes = Convert.FromHexString(op.OriginalHex.Replace(" ", "").Replace("-", ""));
                EnsureRange(data, op.Offset, originalBytes.Length, op);
                if (!data.AsSpan(op.Offset, originalBytes.Length).SequenceEqual(originalBytes))
                    throw new InvalidDataException($"Recipe original bytes mismatch: {op.Path} offset={op.Offset}. Relocate or relearn this recipe for the selected pak.");
                break;
        }
    }

    private static void EnsureRange(byte[] data, int offset, int length, PatchRecipeOperation op)
    {
        if (offset < 0 || offset + length > data.Length)
            throw new InvalidDataException($"Recipe operation is out of range: {op.Path} offset={offset} length={length} fileSize={data.Length}.");
    }

    private static Entry? ResolveRelocateEntry(IReadOnlyList<Entry> entries, IReadOnlyDictionary<string, Entry> entriesByPath, string recipePath)
    {
        if (entriesByPath.TryGetValue(recipePath, out var exact))
            return exact;

        var fileName = recipePath.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var matches = entries.Where(e => e.Path.EndsWith("/" + fileName, StringComparison.Ordinal) || e.Path == fileName).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static byte[]? BuildOriginalNeedle(PatchRecipeOperation op)
    {
        var type = (op.Type ?? "float").Trim().ToLowerInvariant();
        switch (type)
        {
            case "float":
            case "f32":
                return op.OriginalValue.HasValue ? BitConverter.GetBytes(op.OriginalValue.Value) : null;

            case "int32":
            case "i32":
                if (!op.OriginalValue.HasValue) return null;
                var intBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(intBytes, (int)op.OriginalValue.Value);
                return intBytes;

            case "bytes":
            case "hex":
                if (string.IsNullOrWhiteSpace(op.OriginalHex)) return null;
                return Convert.FromHexString(op.OriginalHex.Replace(" ", "").Replace("-", ""));

            default:
                return null;
        }
    }

    private static int FindBestNeedleOffset(byte[] data, byte[] needle, int oldOffset, int searchWindow)
    {
        if (NeedleMatchesAt(data, needle, oldOffset))
            return oldOffset;

        var near = FindNearestNeedleOffset(data, needle, oldOffset, Math.Max(0, oldOffset - searchWindow), Math.Min(data.Length - needle.Length, oldOffset + searchWindow));
        if (near >= 0)
            return near;

        var first = FindFirstNeedleOffset(data, needle, 0, data.Length - needle.Length);
        if (first < 0)
            return -1;

        var second = FindFirstNeedleOffset(data, needle, first + 1, data.Length - needle.Length);
        return second < 0 ? first : FindNearestNeedleOffset(data, needle, oldOffset, 0, data.Length - needle.Length);
    }

    private static int FindNearestNeedleOffset(byte[] data, byte[] needle, int oldOffset, int start, int end)
    {
        var bestOffset = -1;
        var bestDistance = int.MaxValue;
        for (var i = Math.Max(0, start); i <= end; i++)
        {
            if (!NeedleMatchesAt(data, needle, i)) continue;
            var distance = Math.Abs(i - oldOffset);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestOffset = i;
        }

        return bestOffset;
    }

    private static int FindFirstNeedleOffset(byte[] data, byte[] needle, int start, int end)
    {
        for (var i = Math.Max(0, start); i <= end; i++)
        {
            if (NeedleMatchesAt(data, needle, i))
                return i;
        }

        return -1;
    }

    private static bool NeedleMatchesAt(byte[] data, byte[] needle, int offset)
    {
        return offset >= 0
            && needle.Length > 0
            && offset + needle.Length <= data.Length
            && data.AsSpan(offset, needle.Length).SequenceEqual(needle);
    }

    private static string AppendComment(string? comment, string note)
    {
        return string.IsNullOrWhiteSpace(comment) ? note : comment + "; " + note;
    }

    private static IEnumerable<PatchRecipeOperation> BuildRecipeOperations(string path, byte[] before, byte[] after)
    {
        var length = Math.Min(before.Length, after.Length);
        for (var i = 0; i < length;)
        {
            if (before[i] == after[i])
            {
                i++;
                continue;
            }

            if (TryFindFloatChange(before, after, i, out var floatOffset, out var originalFloat, out var modifiedFloat))
            {
                yield return new PatchRecipeOperation
                {
                    Path = path,
                    Offset = floatOffset,
                    Type = "float",
                    Mode = "set",
                    Value = modifiedFloat,
                    OriginalValue = originalFloat,
                    Comment = $"learned float: {originalFloat:0.######} -> {modifiedFloat:0.######}"
                };
                i = floatOffset + 4;
                continue;
            }

            var start = i;
            while (i < length && before[i] != after[i])
                i++;

            if (i - start == 4)
            {
                var originalInt = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(start, 4));
                var modifiedInt = BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(start, 4));
                if (Math.Abs((long)originalInt) <= 1000000 && Math.Abs((long)modifiedInt) <= 1000000)
                {
                    yield return new PatchRecipeOperation
                    {
                        Path = path,
                        Offset = start,
                        Type = "int32",
                        Mode = "set",
                        Value = modifiedInt,
                        OriginalValue = originalInt,
                        Comment = $"learned int32: {originalInt} -> {modifiedInt}"
                    };
                    continue;
                }
            }

            var raw = after.AsSpan(start, i - start).ToArray();
            var originalRaw = before.AsSpan(start, i - start).ToArray();
            yield return new PatchRecipeOperation
            {
                Path = path,
                Offset = start,
                Type = "bytes",
                Mode = "set",
                Hex = Convert.ToHexString(raw),
                OriginalHex = Convert.ToHexString(originalRaw),
                Comment = $"learned raw bytes: {raw.Length} byte(s)"
            };
        }
    }

    private static bool TryFindFloatChange(byte[] before, byte[] after, int diffOffset, out int offset, out float original, out float modified)
    {
        offset = -1;
        original = 0;
        modified = 0;
        var bestScore = float.NegativeInfinity;

        for (var candidate = diffOffset - 3; candidate <= diffOffset; candidate++)
        {
            if (candidate < 0 || candidate + 4 > before.Length || candidate + 4 > after.Length)
                continue;
            if (before.AsSpan(candidate, 4).SequenceEqual(after.AsSpan(candidate, 4)))
                continue;

            var beforeFloat = BitConverter.ToSingle(before, candidate);
            var afterFloat = BitConverter.ToSingle(after, candidate);
            if (!IsLikelyEditableFloat(beforeFloat) || !IsLikelyEditableFloat(afterFloat))
                continue;

            var score = Math.Abs(beforeFloat) + Math.Abs(afterFloat);
            if (score <= bestScore) continue;
            bestScore = score;
            offset = candidate;
            original = beforeFloat;
            modified = afterFloat;
        }

        return offset >= 0;
    }

    private static bool IsLikelyEditableFloat(float value)
    {
        if (!float.IsFinite(value)) return false;
        var abs = Math.Abs(value);
        return abs == 0 || abs is >= 0.000001f and <= 100000f;
    }

    private static bool IsSemanticRecoilBlueprintUexp(string path)
    {
        if (!path.Contains("/Weapon/MainWeapon/", StringComparison.Ordinal)
            || !path.EndsWith(".uexp", StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = GetPakFileNameWithoutExtension(path);
        if (!fileName.StartsWith("BP_", StringComparison.Ordinal))
            return false;

        return !fileName.Contains("Wrapper", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains("BattleItemHandle", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains("SoundDataSet", StringComparison.OrdinalIgnoreCase)
            && !fileName.StartsWith("BP_Mag_", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> ExtractSemanticRecoilNameIndexes(byte[] uassetData, byte[] uexpData, string uexpPath)
    {
        var scratchDir = Path.Combine(Path.GetTempPath(), "PakToolGUI_RecoilNames_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);
        var assetName = GetPakFileNameWithoutExtension(uexpPath);
        var assetPath = Path.Combine(scratchDir, assetName + ".uasset");
        var uexpTempPath = Path.Combine(scratchDir, assetName + ".uexp");
        try
        {
            File.WriteAllBytes(assetPath, uassetData);
            File.WriteAllBytes(uexpTempPath, uexpData);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE4_16);
            var names = asset.GetNameMapIndexList();
            var result = new HashSet<int>();
            for (var i = 0; i < names.Count; i++)
            {
                if (IsSemanticRecoilFloatName(names[i].ToString()))
                    result.Add(i);
            }

            return result;
        }
        catch
        {
            return new HashSet<int>();
        }
        finally
        {
            try { Directory.Delete(scratchDir, true); } catch { }
        }
    }

    private static bool IsSemanticRecoilFloatName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return name.Equals("RecoilCurveArray", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilValueFail", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilModifierCrouch", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilModifierProne", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilHorizontalMinScalar", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilModifierStand", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilCurveEnd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilCurveStart", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RecoilCurveOneBurstStart", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationRecoilGainAim", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationBaseADS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationStanceJump", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationMoveMultiplier", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationRecoilGainADS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationRecoilGain", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationStanceProne", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationStanceCrouch", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationMax", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationBaseAim", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationMaxMove", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationStanceStand", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationMoveMaxRefrence", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeviationMoveMinRefrence", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPakFileNameWithoutExtension(string path)
    {
        var fileName = path.Split('/').LastOrDefault() ?? path;
        var extension = fileName.LastIndexOf('.');
        return extension > 0 ? fileName[..extension] : fileName;
    }

    private static int ApplySemanticRecoilFloats(byte[] data, IReadOnlySet<int> recoilNameIndexes, float scale)
    {
        var changed = 0;
        for (var valueOffset = 0; valueOffset + 8 <= data.Length; valueOffset++)
        {
            var nameIndexOffset = valueOffset + 4;
            var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(nameIndexOffset, 4));
            if (!recoilNameIndexes.Contains(nameIndex))
                continue;

            var current = BitConverter.ToSingle(data, valueOffset);
            if (!IsLikelySemanticRecoilValue(current))
                continue;
            if (!HasSerializedFloatPropertyPrefix(data, valueOffset))
                continue;

            WriteF32(data, valueOffset, current * scale);
            changed++;
        }

        return changed;
    }

    private static bool HasSerializedFloatPropertyPrefix(byte[] data, int valueOffset)
    {
        var start = Math.Max(0, valueOffset - 24);
        var end = Math.Max(0, valueOffset - 12);
        for (var marker = start; marker <= end; marker++)
        {
            if (marker + 16 <= data.Length && IsSerializedFloatPropertyPrefix(data, marker))
                return true;
        }

        return false;
    }

    private static bool IsSerializedFloatPropertyPrefix(byte[] data, int marker)
    {
        return data[marker + 1] == 0
            && data[marker + 2] == 0
            && data[marker + 3] == 0
            && data[marker + 4] == 0
            && data[marker + 5] == 0
            && data[marker + 6] == 0
            && data[marker + 7] == 0
            && data[marker + 8] == 0x04
            && data[marker + 9] == 0
            && data[marker + 10] == 0
            && data[marker + 11] == 0
            && data[marker + 12] == 0
            && data[marker + 13] == 0
            && data[marker + 14] == 0
            && data[marker + 15] == 0;
    }

    private static bool IsLikelySemanticRecoilValue(float value)
    {
        if (!float.IsFinite(value))
            return false;
        var abs = Math.Abs(value);
        return abs >= 0.001f && abs <= 20f;
    }

    private static List<RecoilPatchOp> LearnRecoilPatchOps(string templateOriginalPak, string templateModifiedPak)
    {
        var original = ReadOriginalPak(templateOriginalPak);
        var modified = ReadOriginalPak(templateModifiedPak);
        var modifiedByPath = modified.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var ops = new List<RecoilPatchOp>();

        using var originalStream = new FileStream(templateOriginalPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var modifiedStream = new FileStream(templateModifiedPak, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var entry in original.Entries)
        {
            if (!entry.Path.Contains("/Weapon/MainWeapon/", StringComparison.Ordinal) || !entry.Path.EndsWith(".uexp", StringComparison.Ordinal))
                continue;
            if (!modifiedByPath.TryGetValue(entry.Path, out var modifiedEntry)) continue;

            var before = ReadEntryContent(originalStream, entry);
            var after = ReadEntryContent(modifiedStream, modifiedEntry);
            if (before.SequenceEqual(after)) continue;

            foreach (var (start, end) in FindDiffClusters(before, after))
            {
                if (TryFindScaledFloat(before, after, start, out var floatOffset))
                {
                    ops.Add(new RecoilPatchOp(entry.Path, floatOffset, RecoilPatchKind.FloatScale, null));
                    continue;
                }

                if (end - start + 1 == 4 && BitConverter.ToInt32(after, start) == 0)
                {
                    ops.Add(new RecoilPatchOp(entry.Path, start, RecoilPatchKind.Int32Zero, null));
                    continue;
                }

                var raw = after.AsSpan(start, end - start + 1).ToArray();
                ops.Add(new RecoilPatchOp(entry.Path, start, RecoilPatchKind.Raw, raw));
            }
        }

        if (ops.Count == 0)
            throw new InvalidOperationException("No recoil value positions could be learned from the template paks.");
        return ops;
    }

    private static List<(int Start, int End)> FindDiffClusters(byte[] before, byte[] after)
    {
        var clusters = new List<(int Start, int End)>();
        var start = -1;
        var last = -1;
        var length = Math.Min(before.Length, after.Length);
        for (var i = 0; i < length; i++)
        {
            if (before[i] == after[i]) continue;
            if (start < 0)
            {
                start = i;
                last = i;
                continue;
            }

            if (i <= last + 1)
                last = i;
            else
            {
                clusters.Add((start, last));
                start = last = i;
            }
        }

        if (start >= 0)
            clusters.Add((start, last));
        return clusters;
    }

    private static bool TryFindScaledFloat(byte[] before, byte[] after, int diffStart, out int offset)
    {
        offset = -1;
        for (var candidate = diffStart - 3; candidate <= diffStart; candidate++)
        {
            if (candidate < 0 || candidate + 4 > before.Length || candidate + 4 > after.Length)
                continue;

            var original = BitConverter.ToSingle(before, candidate);
            var modified = BitConverter.ToSingle(after, candidate);
            if (!float.IsFinite(original) || !float.IsFinite(modified)) continue;
            if (Math.Abs(original) < 0.000001f || Math.Abs(original) > 10000f) continue;
            if (Math.Abs(modified / original - 0.125f) > 0.0001f) continue;
            offset = candidate;
            return true;
        }

        return false;
    }

    private static bool TryBuildInPlacePatches(
        PakData pak,
        IReadOnlyList<ChangedFile> changedFiles,
        out List<InPlacePatch> patches,
        out string reason)
    {
        patches = new List<InPlacePatch>();
        reason = "";

        foreach (var changed in changedFiles)
        {
            var entry = changed.Entry;
            var data = changed.Data;
            if (data.Length != entry.Size)
            {
                reason = $"{entry.Path} size changed from {entry.Size} to {data.Length}.";
                return false;
            }

            if (entry.Method == 0)
            {
                entry.Hash = SHA1.HashData(data);
                entry.ContentHash = new byte[20];
                patches.Add(new InPlacePatch(entry.Offset + PubgmCnNonCompressedHeaderSize, data));
                patches.Add(new InPlacePatch(entry.Offset, BuildEntryBytes(entry, entry.Offset, Array.Empty<(long Start, long End)>(), data.Length, entry.Hash, data.Length)));
                continue;
            }

            if (entry.Method != 1)
            {
                reason = $"{entry.Path} uses unsupported compression method {entry.Method}.";
                return false;
            }

            if (!TryBuildCompressedInPlacePatches(entry, data, patches, out reason))
                return false;
        }

        if (!TryAddIndexAndFooterPatches(pak, patches, out reason))
            return false;

        return true;
    }

    private static bool TryBuildInPlacePatchesPreserveIndex(
        PakData pak,
        IReadOnlyList<ChangedFile> changedFiles,
        out List<InPlacePatch> patches,
        out string reason)
    {
        patches = new List<InPlacePatch>();
        reason = "";

        foreach (var changed in changedFiles)
        {
            var localPatches = new List<InPlacePatch>();
            if (!TryBuildSingleInPlacePatch(changed.Entry, changed.Data, localPatches, out reason))
                return false;
            patches.AddRange(localPatches);
        }

        return true;
    }

    private static bool TryBuildBestEffortInPlacePatches(
        PakData pak,
        IReadOnlyList<ChangedFile> changedFiles,
        out List<InPlacePatch> patches,
        out int appliedCount,
        out List<string> skipped)
    {
        patches = new List<InPlacePatch>();
        skipped = new List<string>();
        appliedCount = 0;

        foreach (var changed in changedFiles)
        {
            var entry = changed.Entry;
            var originalHash = entry.Hash.ToArray();
            var originalContentHash = entry.ContentHash.ToArray();
            var originalBlocks = entry.Blocks.ToList();
            var originalPh2 = entry.Ph2Flag;
            var localPatches = new List<InPlacePatch>();

            try
            {
                if (!TryBuildSingleInPlacePatch(entry, changed.Data, localPatches, out var reason))
                {
                    skipped.Add($"{entry.Path}: {reason}");
                    RestoreEntryPatchState(entry, originalHash, originalContentHash, originalBlocks, originalPh2);
                    continue;
                }

                patches.AddRange(localPatches);
                appliedCount++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{entry.Path}: {ex.Message}");
                RestoreEntryPatchState(entry, originalHash, originalContentHash, originalBlocks, originalPh2);
            }
        }

        if (appliedCount == 0)
            return false;

        if (!TryAddIndexAndFooterPatches(pak, patches, out var indexReason))
        {
            skipped.Add($"pak index/footer: {indexReason}");
            return false;
        }

        return true;
    }

    private static bool TryAddIndexAndFooterPatches(PakData pak, List<InPlacePatch> patches, out string reason)
    {
        reason = "";
        var indexRaw = BuildIndex(pak);
        if (indexRaw.Length != pak.IndexSize)
        {
            reason = $"rebuilt index size changed from {pak.IndexSize} to {indexRaw.Length}.";
            return false;
        }

        patches.Add(new InPlacePatch(pak.IndexOffset, indexRaw));
        patches.Add(new InPlacePatch(pak.FooterOffset, BuildFooterBytes(
            pak.Version,
            pak.IndexOffset,
            pak.IndexSize,
            SHA1.HashData(indexRaw),
            pak.EncryptedIndex)));
        return true;
    }

    private static bool TryBuildSingleInPlacePatch(Entry entry, byte[] data, List<InPlacePatch> patches, out string reason)
    {
        reason = "";
        if (data.Length != entry.Size)
        {
            reason = $"size changed from {entry.Size} to {data.Length}.";
            return false;
        }

        if (entry.Method == 0)
        {
            entry.Hash = SHA1.HashData(data);
            entry.ContentHash = new byte[20];
            patches.Add(new InPlacePatch(entry.Offset + PubgmCnNonCompressedHeaderSize, data));
            patches.Add(new InPlacePatch(entry.Offset, BuildEntryBytes(entry, entry.Offset, Array.Empty<(long Start, long End)>(), data.Length, entry.Hash, data.Length)));
            return true;
        }

        if (entry.Method != 1)
        {
            reason = $"uses unsupported compression method {entry.Method}.";
            return false;
        }

        return TryBuildCompressedInPlacePatches(entry, data, patches, out reason);
    }

    private static void RestoreEntryPatchState(
        Entry entry,
        byte[] originalHash,
        byte[] originalContentHash,
        List<(long Start, long End)> originalBlocks,
        int originalPh2)
    {
        entry.Hash = originalHash;
        entry.ContentHash = originalContentHash;
        entry.Blocks = originalBlocks;
        entry.Ph2Flag = originalPh2;
    }

    private static bool TryBuildCompressedInPlacePatches(
        Entry entry,
        byte[] data,
        List<InPlacePatch> patches,
        out string reason)
    {
        reason = "";
        if (entry.Blocks.Count == 0)
        {
            reason = $"{entry.Path} has no compression blocks.";
            return false;
        }

        var blockIndex = 0;
        var hashInput = new List<byte>();
        for (var offset = 0; offset < data.Length; offset += DefaultBlockSize)
        {
            if (blockIndex >= entry.Blocks.Count)
            {
                reason = $"{entry.Path} has fewer original blocks than the modified file needs.";
                return false;
            }

            var size = Math.Min(DefaultBlockSize, data.Length - offset);
            var block = data.AsSpan(offset, size).ToArray();
            ApplyPh2(block, offset, entry.Ph2Flag);
            var (start, end) = entry.Blocks[blockIndex++];
            var originalLength = checked((int)(end - start));
            if (!TryCompressZlibForInPlace(block, originalLength, out var compressed))
            {
                reason = $"{entry.Path} block {blockIndex} cannot be compressed into the original {originalLength} byte block.";
                return false;
            }

            patches.Add(new InPlacePatch(start, compressed));
            hashInput.AddRange(compressed.AsSpan(0, originalLength).ToArray());
        }

        if (blockIndex != entry.Blocks.Count)
        {
            reason = $"{entry.Path} uses fewer blocks than the original entry.";
            return false;
        }

        entry.Hash = SHA1.HashData(hashInput.ToArray());
        entry.ContentHash = new byte[20];
        patches.Add(new InPlacePatch(entry.Offset, BuildEntryBytes(entry, entry.Offset, entry.Blocks, entry.CompressedSize, entry.Hash, entry.Size)));
        return true;
    }

    private static bool TryCompressZlibForInPlace(byte[] block, int originalLength, out byte[] compressed)
    {
        for (var level = ZlibLevel; level <= 9; level++)
        {
            compressed = CompressZlib(block, level);
            if (compressed.Length <= originalLength)
            {
                if (compressed.Length != originalLength)
                    Array.Resize(ref compressed, originalLength);
                return true;
            }
        }

        for (var level = 1; level < ZlibLevel; level++)
        {
            compressed = CompressZlib(block, level);
            if (compressed.Length <= originalLength)
            {
                if (compressed.Length != originalLength)
                    Array.Resize(ref compressed, originalLength);
                return true;
            }
        }

        compressed = Array.Empty<byte>();
        return false;
    }

    private static void WriteChangedEntry(FileStream output, Entry entry, byte[] data)
    {
        var newOffset = output.Position;
        var packed = PackEntryData(entry, data, newOffset);
        output.Write(packed.LocalEntry);
        output.Write(packed.Data);

        entry.Offset = newOffset;
        entry.Size = data.Length;
        entry.CompressedSize = packed.StoredSize;
        entry.Blocks = packed.Blocks;
        entry.BlockSize = (uint)Math.Min(DefaultBlockSize, Math.Max(1, data.Length));
        entry.Hash = packed.Hash;
        entry.Encrypted = 0;
    }

    private static void CopyUnchangedEntry(FileStream input, FileStream output, Entry entry)
    {
        var oldOffset = entry.Offset;
        var oldBlocks = entry.Blocks.ToList();
        var oldLocalEntrySize = GetEntrySize(entry.Method, oldBlocks.Count);
        var newOffset = output.Position;
        var newLocalEntrySize = GetEntrySize(entry.Method, oldBlocks.Count);
        var oldDataStart = oldOffset + oldLocalEntrySize;
        var newDataStart = newOffset + newLocalEntrySize;

        entry.Offset = newOffset;
        if (entry.Method != 0)
        {
            var delta = newDataStart - oldDataStart;
            entry.Blocks = oldBlocks.Select(b => (b.Start + delta, b.End + delta)).ToList();
        }

        using var local = new MemoryStream();
        WriteEntry(local, entry);
        output.Write(local.ToArray());
        CopyRange(input, output, oldDataStart, entry.CompressedSize);
    }

    private static PakData ReadOriginalPak(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);
        var footer = ReadFooter(fs, reader);
        if (footer.Magic != PubgmCnMagic || footer.Version < 10)
            throw new InvalidDataException("Patch mode currently supports PUBGM_CN v10 pak files only.");

        fs.Seek(footer.IndexOffset, SeekOrigin.Begin);
        var index = reader.ReadBytes(checked((int)footer.IndexSize));
        using var indexMs = new MemoryStream(index);
        using var indexReader = new BinaryReader(indexMs);

        var mountPoint = ReadPakString(indexReader);
        var count = indexReader.ReadInt32();
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
            entries.Add(ReadEntry(indexReader, i));

        fs.Seek(footer.IndexOffset + indexMs.Position, SeekOrigin.Begin);
        var textSize = reader.ReadInt64();
        var text = reader.ReadBytes(checked((int)textSize));
        ApplyTextSection(mountPoint, entries, text);

        return new PakData
        {
            MountPoint = mountPoint,
            Entries = entries,
            IndexOffset = footer.IndexOffset,
            IndexSize = footer.IndexSize,
            FooterOffset = fs.Length - PubgmCnFooterSize,
            Version = footer.Version,
            EncryptedIndex = footer.EncryptedIndex
        };
    }

    private static Entry ReadEntry(BinaryReader reader, int index)
    {
        var entry = new Entry { Index = index };
        entry.Hash = reader.ReadBytes(20);
        entry.Offset = reader.ReadInt64();
        entry.Size = reader.ReadInt64();
        entry.Method = reader.ReadInt32() & 0x0F;
        entry.CompressedSize = (long)reader.ReadUInt64();
        entry.Unknown = reader.ReadByte();
        entry.ContentHash = reader.ReadBytes(20);
        if (entry.Method != 0)
        {
            var blockCount = reader.ReadInt32();
            for (var i = 0; i < blockCount; i++)
                entry.Blocks.Add((reader.ReadInt64(), reader.ReadInt64()));
        }
        entry.BlockSize = reader.ReadUInt32();
        entry.Encrypted = reader.ReadByte();
        return entry;
    }

    private static (byte[] LocalEntry, byte[] Data, long StoredSize, List<(long Start, long End)> Blocks, byte[] Hash) PackEntryData(Entry template, byte[] data, long entryOffset)
    {
        if (template.Method == 0)
        {
            var localEntry = BuildEntryBytes(template, entryOffset, Array.Empty<(long Start, long End)>(), data.Length, SHA1.HashData(data), data.Length);
            var stored = (byte[])data.Clone();
            return (localEntry, stored, data.Length, new List<(long Start, long End)>(), SHA1.HashData(data));
        }

        if (template.Method != 1)
            throw new NotSupportedException("Patch mode currently supports zlib-compressed changed entries.");

        var blocks = new List<(long Start, long End)>();
        var compressedBlocks = new List<byte[]>();
        var localEntrySize = GetEntrySize(template.Method, (data.Length + DefaultBlockSize - 1) / DefaultBlockSize);
        var cursor = entryOffset + localEntrySize;
        for (var offset = 0; offset < data.Length; offset += DefaultBlockSize)
        {
            var size = Math.Min(DefaultBlockSize, data.Length - offset);
            var block = data.AsSpan(offset, size).ToArray();
            ApplyPh2(block, offset, template.Ph2Flag);
            var compressed = CompressZlib(block);
            var start = cursor;
            var end = start + compressed.Length;
            blocks.Add((start, end));
            var padded = Align(compressed.Length, 16);
            if (padded != compressed.Length)
                Array.Resize(ref compressed, padded);
            compressedBlocks.Add(compressed);
            cursor += compressed.Length;
        }

        var storedData = compressedBlocks.SelectMany(b => b).ToArray();
        var hashInput = blocks.SelectMany(b => storedData.AsSpan((int)(b.Start - entryOffset - localEntrySize), (int)(b.End - b.Start)).ToArray()).ToArray();
        var hash = SHA1.HashData(hashInput);
        var local = BuildEntryBytes(template, entryOffset, blocks, storedData.Length, hash, data.Length);
        return (local, storedData, storedData.Length, blocks, hash);
    }

    private static byte[] ComputeOriginalContentHash(FileStream input, Entry entry)
    {
        return SHA1.HashData(ReadEntryContent(input, entry));
    }

    private static byte[] ReadEntryContent(FileStream input, Entry entry)
    {
        if (entry.Method == 0)
        {
            var data = new byte[entry.Size];
            input.Position = entry.Offset + 0x4A;
            ReadExactly(input, data);
            return data;
        }

        if (entry.Method != 1)
            throw new NotSupportedException($"{entry.Path} uses unsupported compression method {entry.Method}.");

        using var output = new MemoryStream((int)entry.Size);
        var remaining = entry.Size;
        foreach (var (start, end) in entry.Blocks)
        {
            var compressed = new byte[end - start];
            input.Position = start;
            ReadExactly(input, compressed);
            var expected = (int)Math.Min(DefaultBlockSize, remaining);
            var decompressed = UncompressZlib(compressed, expected);
            if (output.Position == 0)
                entry.Ph2Flag = DetectPh2Flag(decompressed);
            ApplyPh2(decompressed, output.Position, entry.Ph2Flag);
            output.Write(decompressed);
            remaining -= decompressed.Length;
        }

        return output.ToArray();
    }

    private static int DetectPh2Flag(byte[] data)
    {
        if (data.Length < 8) return Ph2None;

        var lm = true;
        for (var i = 0; i < Ph2MatchBytes.Length; i++)
        {
            if ((byte)(data[i] ^ SLongMapKey) != Ph2MatchBytes[i])
            {
                lm = false;
                break;
            }
        }
        if (lm) return Ph2LmSxr;

        var n = Ph2MatchBytes.Length;
        while (--n > 0 && (byte)(data[n] ^ (StXorKey + n)) == Ph2MatchBytes[n])
        {
        }
        return n == 0 ? Ph2LmStxr : Ph2None;
    }

    private static void ApplyPh2(byte[] data, long filePosition, int flag)
    {
        if (flag == Ph2LmSxr)
        {
            for (var i = 0; i < data.Length; i++)
                data[i] ^= SLongMapKey;
            return;
        }

        if (flag != Ph2LmStxr) return;

        const int maxN = 9;
        var nIndex = filePosition;
        if (nIndex >= DefaultBlockSize)
            nIndex += (nIndex / DefaultBlockSize) - 1;

        var skip = 0xFFL;
        for (var i = 0; i < data.Length;)
        {
            var nNum = nIndex++ % maxN;
            if (nNum == skip) continue;

            data[i] ^= (byte)(StXorKey + nNum);
            skip = ((i + filePosition) / maxN) % maxN;
            i++;
        }
    }

    private static byte[] BuildIndex(PakData pak)
    {
        using var ms = new MemoryStream();
        WriteString(ms, pak.MountPoint);
        WriteU32(ms, (uint)pak.Entries.Count);
        foreach (var entry in pak.Entries)
            WriteEntry(ms, entry);
        return ms.ToArray();
    }

    private static byte[] BuildEntryBytes(Entry template, long offset, IReadOnlyList<(long Start, long End)> blocks, long compressedSize, byte[] hash, long size)
    {
        var entry = new Entry
        {
            Hash = hash,
            Offset = offset,
            Size = size,
            Method = template.Method,
            CompressedSize = compressedSize,
            Unknown = template.Unknown,
            ContentHash = template.ContentHash,
            Blocks = blocks.ToList(),
            BlockSize = (uint)Math.Min(DefaultBlockSize, Math.Max(1, size)),
            Encrypted = 0
        };
        using var ms = new MemoryStream();
        WriteEntry(ms, entry);
        return ms.ToArray();
    }

    private static void WriteEntry(Stream stream, Entry entry)
    {
        stream.Write(entry.Hash);
        WriteI64(stream, entry.Offset);
        WriteI64(stream, entry.Size);
        WriteU32(stream, (uint)entry.Method);
        WriteI64(stream, entry.CompressedSize);
        stream.WriteByte(entry.Unknown);
        stream.Write(entry.ContentHash);
        if (entry.Method != 0)
        {
            WriteU32(stream, (uint)entry.Blocks.Count);
            foreach (var (start, end) in entry.Blocks)
            {
                WriteI64(stream, start);
                WriteI64(stream, end);
            }
        }
        WriteU32(stream, entry.BlockSize);
        stream.WriteByte(entry.Encrypted);
    }

    private static byte[] BuildTextSection(string mountPoint, IReadOnlyList<Entry> entries)
    {
        var root = "../../../";
        var mountRelative = mountPoint.StartsWith(root, StringComparison.Ordinal)
            ? mountPoint[root.Length..]
            : "";

        var dirs = entries
            .Select(e =>
            {
                var path = e.Path;
                if (!string.IsNullOrEmpty(mountRelative) && path.StartsWith(mountRelative, StringComparison.Ordinal))
                    path = path[mountRelative.Length..];
                var slash = path.LastIndexOf('/');
                return new
                {
                    Dir = slash >= 0 ? path[..(slash + 1)] : "",
                    Name = slash >= 0 ? path[(slash + 1)..] : path,
                    e.Index
                };
            })
            .GroupBy(x => x.Dir, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        using var ms = new MemoryStream();
        WriteI64(ms, dirs.Count);
        foreach (var dir in dirs)
        {
            WriteString(ms, dir.Key);
            WriteI64(ms, dir.Count());
            foreach (var file in dir.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                WriteString(ms, file.Name);
                WriteU32(ms, (uint)file.Index);
            }
        }
        return ms.ToArray();
    }

    private static void ApplyTextSection(string mountPoint, List<Entry> entries, byte[] text)
    {
        using var ms = new MemoryStream(text);
        using var reader = new BinaryReader(ms);
        var dirCount = reader.ReadInt64();
        var root = "../../../";
        var mountRelative = mountPoint.StartsWith(root, StringComparison.Ordinal) ? mountPoint[root.Length..] : "";
        for (long d = 0; d < dirCount; d++)
        {
            var dir = ReadPakString(reader);
            var fileCount = reader.ReadInt64();
            for (long f = 0; f < fileCount; f++)
            {
                var name = ReadPakString(reader);
                var index = reader.ReadInt32();
                if (index < 0) index = ~index;
                var path = dir + name;
                entries[index].Path = !string.IsNullOrEmpty(mountRelative) && !path.StartsWith(mountRelative, StringComparison.Ordinal)
                    ? mountRelative + path
                    : path;
            }
        }
    }

    private static string? FindSourcePath(Dictionary<string, string> sourceFiles, string mountPoint, string entryPath)
    {
        if (sourceFiles.TryGetValue(entryPath, out var path)) return path;
        const string root = "../../../";
        if (mountPoint.StartsWith(root, StringComparison.Ordinal))
        {
            var mountRelative = mountPoint[root.Length..];
            if (entryPath.StartsWith(mountRelative, StringComparison.Ordinal))
            {
                var stripped = entryPath[mountRelative.Length..];
                if (sourceFiles.TryGetValue(stripped, out path)) return path;
            }
        }
        return null;
    }

    private static HashSet<string> GetRecentEditBatch(Dictionary<string, string> sourceFiles)
    {
        var ordered = sourceFiles
            .Select(kvp => new { Relative = kvp.Key, Time = File.GetLastWriteTimeUtc(kvp.Value) })
            .OrderByDescending(x => x.Time)
            .ToList();
        if (ordered.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var newest = ordered[0].Time;
        var secondBatch = ordered.FirstOrDefault(x => (newest - x.Time).TotalSeconds > 30);
        if (secondBatch == null)
            return new HashSet<string>(StringComparer.Ordinal);

        return ordered
            .Where(x => (newest - x.Time).TotalSeconds <= 5)
            .Select(x => x.Relative)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (uint Magic, int Version, long IndexSize, long IndexOffset, bool EncryptedIndex) ReadFooter(FileStream fs, BinaryReader reader)
    {
        fs.Seek(-PubgmCnFooterSize, SeekOrigin.End);
        var enc = reader.ReadByte();
        var magic = reader.ReadUInt32();
        var version = (int)reader.ReadUInt32();
        reader.ReadBytes(20);
        var rawSize = reader.ReadUInt64();
        var rawOffset = reader.ReadUInt64();
        var keys = ZucCipher.GenerateKeyArray(PubgmCnZucKey, PubgmCnZucIv, 16);
        var sizeKey = ((ulong)keys[10] << 32) | keys[11];
        var offsetKey = ((ulong)keys[0] << 32) | keys[1];
        return (magic, version, (long)(rawSize ^ sizeKey), (long)(rawOffset ^ offsetKey), (enc ^ (byte)(keys[3] & 0xFF)) != 0);
    }

    private static void WriteFooter(Stream output, int version, long indexOffset, long indexSize, byte[] hash, bool encryptedIndex)
    {
        var keys = ZucCipher.GenerateKeyArray(PubgmCnZucKey, PubgmCnZucIv, 16);
        var sizeKey = ((ulong)keys[10] << 32) | keys[11];
        var offsetKey = ((ulong)keys[0] << 32) | keys[1];
        output.WriteByte((byte)((encryptedIndex ? 1 : 0) ^ (byte)(keys[3] & 0xFF)));
        WriteU32(output, PubgmCnMagic);
        WriteU32(output, (uint)version);
        var xorHash = (byte[])hash.Clone();
        for (var i = 0; i < 5; i++)
        {
            var key = keys[4 + i];
            var offset = i * 4;
            xorHash[offset] ^= (byte)(key & 0xFF);
            xorHash[offset + 1] ^= (byte)((key >> 8) & 0xFF);
            xorHash[offset + 2] ^= (byte)((key >> 16) & 0xFF);
            xorHash[offset + 3] ^= (byte)((key >> 24) & 0xFF);
        }
        output.Write(xorHash);
        WriteI64(output, (long)((ulong)indexSize ^ sizeKey));
        WriteI64(output, (long)((ulong)indexOffset ^ offsetKey));
    }

    private static byte[] BuildFooterBytes(int version, long indexOffset, long indexSize, byte[] hash, bool encryptedIndex)
    {
        using var ms = new MemoryStream(PubgmCnFooterSize);
        WriteFooter(ms, version, indexOffset, indexSize, hash, encryptedIndex);
        return ms.ToArray();
    }

    private static unsafe byte[] CompressZlib(byte[] data, int level = ZlibLevel)
    {
        fixed (byte* srcPtr = data)
        {
            var srcLen = (nuint)data.Length;
            var destLen = NativeMethods.compressBound(srcLen);
            var dest = new byte[destLen];
            fixed (byte* dstPtr = dest)
            {
                var result = NativeMethods.compress2((nint)dstPtr, ref destLen, (nint)srcPtr, srcLen, level);
                if (result != 0) throw new InvalidOperationException("zlib compress failed: " + result);
                Array.Resize(ref dest, (int)destLen);
                return dest;
            }
        }
    }

    private static unsafe byte[] UncompressZlib(byte[] data, int expectedSize)
    {
        var dest = new byte[expectedSize];
        fixed (byte* srcPtr = data)
        fixed (byte* dstPtr = dest)
        {
            var destLen = (nuint)dest.Length;
            var result = NativeMethods.uncompress((nint)dstPtr, ref destLen, (nint)srcPtr, (nuint)data.Length);
            if (result != 0) throw new InvalidDataException("zlib uncompress failed: " + result + " (data is not a valid zlib stream or uses another pak compression method)");
            if ((int)destLen != dest.Length)
                Array.Resize(ref dest, (int)destLen);
            return dest;
        }
    }

    private static bool IsSkippableTemplateReadError(Exception ex)
    {
        return ex is NotSupportedException
            || ex is InvalidDataException
            || ex is EndOfStreamException
            || ex.InnerException != null && IsSkippableTemplateReadError(ex.InnerException);
    }

    private static void CopyRange(Stream input, Stream output, long offset, long length)
    {
        input.Position = offset;
        var buffer = new byte[1024 * 1024];
        while (length > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            length -= read;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static int GetEntrySize(int method, int blockCount)
    {
        var size = 20 + 8 + 8 + 4 + 8 + 1 + 20;
        if (method != 0) size += 4 + blockCount * 16;
        return size + 4 + 1;
    }

    private static int Align(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string ReadPakString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            length = -length;
            return Encoding.Unicode.GetString(reader.ReadBytes(length * 2)).TrimEnd('\0');
        }
        return Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteU32(stream, (uint)bytes.Length + 1);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteI64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteF32(byte[] data, int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
    }
}
