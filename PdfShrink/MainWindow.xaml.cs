using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfShrink.Core;
using PdfShrink.Models;

namespace PdfShrink;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ShrinkItem> _items = new();
    private readonly Queue<ShrinkItem> _queue = new();
    private bool _running;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _items;
        UpdateQualityText();

        // コマンドラインやエクスプローラーの「送る」から渡された PDF も受け付ける
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) Loaded += (_, _) => Enqueue(args);
    }

    // ---------- 入力 ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "Line");
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Enqueue(paths);
    }

    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "圧縮する PDF を選ぶ",
            Filter = "PDF (*.pdf)|*.pdf",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == true) Enqueue(dlg.FileNames);
    }

    /// <summary>PDF を一覧に足して順番に処理する。フォルダーを渡されたら中の PDF を拾う。</summary>
    private void Enqueue(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.pdf", SearchOption.TopDirectoryOnly));
            else if (File.Exists(p))
                files.Add(p);
        }

        int ignored = 0;
        foreach (var f in files)
        {
            if (!f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { ignored++; continue; }
            var item = new ShrinkItem { InputPath = f, Before = ShrinkItem.FormatBytes(new FileInfo(f).Length) };
            _items.Add(item);
            _queue.Enqueue(item);
        }
        if (ignored > 0) FooterText.Text = $"PDF 以外の {ignored} 件は無視しました。";

        _ = RunQueueAsync();
    }

    // ---------- 処理 ----------

    private async Task RunQueueAsync()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        CancelButton.IsEnabled = true;

        long totalBefore = 0, totalAfter = 0;
        int ok = 0, failed = 0;

        try
        {
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                if (_cts.IsCancellationRequested) { item.Status = "中止"; continue; }

                var options = new ShrinkOptions
                {
                    Quality = (int)Math.Round(QualitySlider.Value),
                    MaxLongSide = SizeCombo.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag?.ToString(), out int px) ? px : 0
                };

                item.Status = "処理中…";
                string output = PdfCompressor.OutputPathFor(item.InputPath);
                var progress = new Progress<string>(s => item.Status = "処理中… " + s);
                var token = _cts.Token;

                try
                {
                    var r = await Task.Run(() => PdfCompressor.Compress(item.InputPath, output, options, progress, token), token);
                    item.OutputPath = output;
                    item.After = ShrinkItem.FormatBytes(r.OutputBytes);
                    double ratio = r.OriginalBytes == 0 ? 0 : 1 - (double)r.OutputBytes / r.OriginalBytes;
                    item.Ratio = ratio > 0 ? $"-{ratio * 100:0} %" : $"+{-ratio * 100:0} %";
                    item.Status = r.OutputBytes < r.OriginalBytes
                        ? $"完了（画像 {r.ImagesRecompressed}/{r.ImagesFound} 枚を圧縮）"
                        : $"小さくなりませんでした（画像 {r.ImagesRecompressed}/{r.ImagesFound} 枚）";
                    totalBefore += r.OriginalBytes;
                    totalAfter += r.OutputBytes;
                    ok++;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(output);   // 書きかけは残さない
                    item.Status = "中止";
                }
                catch (Exception ex)
                {
                    TryDelete(output);
                    item.Status = "失敗: " + ex.Message;
                    failed++;
                }
            }
        }
        finally
        {
            _running = false;
            CancelButton.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }

        if (ok + failed > 0)
            FooterText.Text = $"{ok} 件完了" + (failed > 0 ? $"、{failed} 件失敗" : "")
                + (totalBefore > 0 ? $"　合計 {ShrinkItem.FormatBytes(totalBefore)} → {ShrinkItem.FormatBytes(totalAfter)}" : "")
                + "　（行をダブルクリックでフォルダーを開く）";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------- その他 ----------

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _items.Clear();
        FooterText.Text = "行をダブルクリックすると、保存先のフォルダーを開きます。";
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultGrid.SelectedItem is not ShrinkItem item) return;
        string target = File.Exists(item.OutputPath) ? item.OutputPath : item.InputPath;
        try { Process.Start("explorer.exe", $"/select,\"{target}\""); } catch { }
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateQualityText();

    private void UpdateQualityText()
    {
        if (QualityText is null) return;
        int v = (int)Math.Round(QualitySlider.Value);
        string desc = v >= 80 ? "高画質" : v >= 40 ? "標準" : "低画質（とても小さく）";
        QualityText.Text = $"{v}　{desc}";
    }
}
