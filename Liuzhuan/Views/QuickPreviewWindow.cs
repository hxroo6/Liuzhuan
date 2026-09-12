using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Models;

namespace Liuzhuan.Views;

public class QuickPreviewWindow : Window
{
    private readonly List<MaterialItem> _items;
    private int _index;
    private readonly TextBlock _title = new() { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ContentControl _content = new();
    private readonly Slider _zoom = new() { Minimum = 0.25, Maximum = 3, Value = 1, Width = 130, Margin = new Thickness(10,0,0,0) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    public QuickPreviewWindow(List<MaterialItem> items, int index, Action<MaterialItem> copy)
    {
        _items = items; _index = index;
        Title = "素材预览"; Width = 760; Height = 640;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("PanelBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        MinWidth = 620; MinHeight = 360;
        var root = new DockPanel { Margin = new Thickness(20), Background = Background };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,14) };
        void Button(string label, Action action) { var b = new Button { Content=label, Padding=new Thickness(12,6,12,6), Margin=new Thickness(0,0,8,0), Style=(Style)FindResource("ActionButtonStyle") }; b.Click+=(_,_)=>action(); controls.Children.Add(b); }
        Button("← 上一项",()=>Move(-1)); Button("下一项 →",()=>Move(1)); Button("复制",()=>copy(_items[_index])); Button("适应窗口",()=>{_zoom.Value=1;});
        controls.Children.Add(_zoom); _zoom.ValueChanged+=(_,_)=>ResizeImage();
        DockPanel.SetDock(controls,Dock.Top); root.Children.Add(controls);
        _title.Margin=new Thickness(0,0,0,14); DockPanel.SetDock(_title,Dock.Top); root.Children.Add(_title);
        root.Children.Add(_content); Content=root;
        KeyDown+=(_,e)=> { if(e.Key==Key.Escape){Close();e.Handled=true;} else if(e.Key==Key.Left){Move(-1);e.Handled=true;} else if(e.Key==Key.Right){Move(1);e.Handled=true;} };
        _content.SizeChanged+=(_,_)=>ResizeImage();
        Loaded+=(_,_)=>Utils.UiMotion.Reveal(root);
        ShowItem();
    }
    private void Move(int delta) { _index=(_index+delta+_items.Count)%_items.Count; ShowItem(); Utils.UiMotion.Reveal(_content); }
    private void ResizeImage() { _image.Width=Math.Max(100,_content.ActualWidth-20)*_zoom.Value; _image.Height=Math.Max(100,_content.ActualHeight-20)*_zoom.Value; }
    private void ShowItem()
    {
        if (_content.Content is ScrollViewer previous) previous.Content = null;
        var item=_items[_index]; _zoom.Value=1;
        _title.Text=$"{_index+1} / {_items.Count} · {item.DisplayName}";
        _zoom.IsEnabled=item.Type==MaterialType.Image;
        try
        {
            if(item.Type==MaterialType.Text) _content.Content=new TextBox { Text=item.TextContent, IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Background=Background, Foreground=Foreground, BorderThickness=new Thickness(0), FontSize=16 };
            else
            {
                using var stream=File.OpenRead(item.FilePath);
                var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth=1600; bitmap.StreamSource=stream; bitmap.EndInit(); bitmap.Freeze();
                _image.Source=bitmap; ResizeImage();
                _content.Content=new ScrollViewer { Content=_image, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            }
        }
        catch { _content.Content=new TextBlock { Text="无法预览此素材。原文件可能已移动，或系统缺少对应解码器。", TextWrapping=TextWrapping.Wrap }; }
    }
}
