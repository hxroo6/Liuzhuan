using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Liuzhuan;

/// <summary>
/// 配对二维码弹窗 — 手机扫码自动填入 IP/端口/口令
/// 二维码内容：{"ip":"x","port":8899,"pwd":"口令"}
/// </summary>
public partial class QrCodeWindow : Window
{
    private readonly int _port;
    private readonly string _password;
    public QrCodeWindow(string ip, int port, string password)
    {
        _port = port; _password = password;
        InitializeComponent();
        var addresses = Utils.LanNetUtil.GetLanAddresses();
        NetworkChoice.ItemsSource = addresses;
        if (addresses.Count == 0)
        {
            InfoText.Text = "未找到可用的 Wi-Fi / 以太网地址。\n请连接局域网后重新打开二维码。";
            NetworkChoice.IsEnabled = false;
            return;
        }
        NetworkChoice.SelectedItem = addresses.FirstOrDefault(a => a.Address == ip) ?? addresses[0];
    }

    private void NetworkChoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NetworkChoice.SelectedItem is not Utils.LanNetUtil.LanAddress address) return;
        var ip = address.Address;
        // 二维码内容（JSON）
        var payload = System.Text.Json.JsonSerializer.Serialize(new { ip, port = _port, pwd = _password });
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 280, Height = 280, Margin = 2 }
        };
        var data = writer.Write(payload);
        var bmp = new WriteableBitmap(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, data.Width, data.Height), data.Pixels, data.Width * 4, 0);
        QrImage.Source = bmp;

        InfoText.Text = $"电脑 {ip}:{_port}\n口令 {_password}";
    }
}
