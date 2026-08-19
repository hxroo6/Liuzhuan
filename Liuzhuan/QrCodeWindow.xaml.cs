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
    public QrCodeWindow(string ip, int port, string password)
    {
        InitializeComponent();

        // 二维码内容（JSON）
        var payload = $"{{\"ip\":\"{ip}\",\"port\":{port},\"pwd\":\"{password}\"}}";
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 280, Height = 280, Margin = 2 }
        };
        var data = writer.Write(payload);
        var bmp = new WriteableBitmap(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, data.Width, data.Height), data.Pixels, data.Width * 4, 0);
        QrImage.Source = bmp;

        InfoText.Text = $"电脑 {ip}:{port}\n口令 {password}";
    }
}
