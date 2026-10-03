using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>素材库内的图片处理参数集；校验及磁盘写入成功后才更新内存。</summary>
public sealed class ImageProcessingPresetStore
{
    public const string FileName = "imagepresets.json";
    public const int MaxPresets = 20;
    public const int MaxNameLength = 40;
    private const int MaxFileBytes = 128 * 1024;
    private readonly string _path;
    private List<ImageProcessingPreset> _presets = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public IReadOnlyList<ImageProcessingPreset> Presets => _presets.AsReadOnly();
    public string? LoadError { get; private set; }

    public ImageProcessingPresetStore(string? directory = null)
    {
        _path = Path.Combine(Path.GetFullPath(directory ?? ConfigService.GetEffectiveDataDir()), FileName);
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > MaxFileBytes) throw new InvalidDataException("图片预设文件过大。");
            var document = JsonSerializer.Deserialize<ImageProcessingPresetDocument>(File.ReadAllText(_path), Json);
            _presets = NormalizeDocument(document).Presets;
        }
        catch (Exception exception) { LoadError = exception.Message; }
    }

    public static ImageProcessingPreset Normalize(ImageProcessingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var name = preset.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength || (preset.Name?.Any(char.IsControl) ?? false))
            throw new ArgumentException($"预设名称需为 1–{MaxNameLength} 个字符，且不能换行。");
        if (!Enum.IsDefined(typeof(ImageOutputFormat), preset.Format)) throw new ArgumentException("预设图片格式无效。");
        if (preset.MaxEdge is <= 0 or > 32768) throw new ArgumentException("预设最长边需为 1–32768，或关闭尺寸限制。");
        if (preset.JpegQuality is < 1 or > 100) throw new ArgumentException("预设 JPEG 质量需为 1–100。");
        return preset with { Name = name };
    }

    public static List<ImageProcessingPreset> NormalizeAll(IEnumerable<ImageProcessingPreset>? presets)
    {
        var result = new List<ImageProcessingPreset>();
        foreach (var preset in presets ?? Array.Empty<ImageProcessingPreset>())
        {
            if (result.Count == MaxPresets) throw new ArgumentException($"最多保存 {MaxPresets} 个图片处理预设。");
            result.Add(Normalize(preset));
        }
        if (result.Select(preset => preset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new ArgumentException("图片处理预设名称不能重复。");
        return result;
    }

    public static ImageProcessingPresetDocument NormalizeDocument(ImageProcessingPresetDocument? document)
    {
        if (document == null || document.Version != 1 || document.Presets == null)
            throw new InvalidDataException("图片处理预设格式无效或版本不支持。");
        return new() { Presets = NormalizeAll(document.Presets) };
    }

    public static ImageProcessingPreset Create(string name, ImageProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Normalize(new() { Name = name, Format = options.Format, MaxEdge = options.MaxEdge, JpegQuality = options.JpegQuality });
    }
    public ImageProcessingPresetDocument ExportDocument()
    {
        EnsureReadable();
        return new() { Presets = _presets.Select(preset => preset with { }).ToList() };
    }
    public void ImportDocument(ImageProcessingPresetDocument document) => Commit(NormalizeDocument(document).Presets);
    public void Add(ImageProcessingPreset preset) => Commit(_presets.Append(preset));
    public void Update(string name, ImageProcessingOptions options)
    {
        var index = Find(name);
        var next = _presets.ToList();
        next[index] = Create(next[index].Name, options);
        Commit(next);
    }
    public void Rename(string name, string newName)
    {
        var index = Find(name);
        var next = _presets.ToList(); next[index] = next[index] with { Name = newName };
        Commit(next);
    }
    public void Remove(string name)
    {
        var index = Find(name);
        var next = _presets.ToList(); next.RemoveAt(index);
        Commit(next);
    }
    /// <summary>只供用户显式确认后调用。先保留并验证损坏文件，再建立空预设。</summary>
    public string RebuildAfterLoadError()
    {
        if (LoadError == null) throw new InvalidOperationException("图片处理预设可正常读取，无需重建。");
        var backup = _path + ".preserved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        using (var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var originalHash = SHA256.HashData(source);
            File.Copy(_path, backup, false);
            using var saved = File.OpenRead(backup);
            if (!originalHash.SequenceEqual(SHA256.HashData(saved))) throw new IOException("原预设副本校验失败，未重建。");
        }
        var error = LoadError;
        LoadError = null;
        try { Commit(Array.Empty<ImageProcessingPreset>()); }
        catch { LoadError = error; throw; }
        return backup;
    }
    private int Find(string name)
    {
        EnsureReadable();
        var index = _presets.FindIndex(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("该图片处理预设已被移除，请重新选择。");
        return index;
    }
    private void EnsureReadable()
    {
        if (LoadError != null) throw new IOException("图片处理预设读取失败，原文件已保留，请检查 " + FileName + "：" + LoadError);
    }
    private void Commit(IEnumerable<ImageProcessingPreset> presets)
    {
        EnsureReadable();
        var next = NormalizeAll(presets);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ImageProcessingPresetDocument { Presets = next }, Json));
            File.Move(temporary, _path, true);
            _presets = next;
        }
        finally
        {
            // 失败保留原异常；只清理本次创建的临时文件。
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }
}
