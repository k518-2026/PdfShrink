using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PdfShrink.Models;

/// <summary>一覧の 1 行（1 ファイル）。</summary>
public sealed class ShrinkItem : INotifyPropertyChanged
{
    public required string InputPath { get; init; }
    public string FileName => System.IO.Path.GetFileName(InputPath);

    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

    private string _before = "";
    public string Before { get => _before; set => Set(ref _before, value); }

    private string _after = "";
    public string After { get => _after; set => Set(ref _after, value); }

    private string _ratio = "";
    public string Ratio { get => _ratio; set => Set(ref _ratio, value); }

    private string _status = "待機中";
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}
