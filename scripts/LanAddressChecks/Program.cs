using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Liuzhuan;
using Liuzhuan.Utils;
using ZXing;

internal class Program
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    [STAThread] static void Main()
    {
        var wifi = new LanNetUtil.LanAddress("192.168.5.60",24,"WLAN","Intel Wi-Fi",NetworkInterfaceType.Wireless80211,true);
        var wired = new LanNetUtil.LanAddress("10.1.0.2",24,"Ethernet","Realtek",NetworkInterfaceType.Ethernet,true);
        var candidates = new[] {
            new LanNetUtil.LanAddress("172.30.201.230",32,"vgate0","Rust Wintun Tunnel Tunnel",NetworkInterfaceType.Ethernet,false),
            new LanNetUtil.LanAddress("192.168.137.1",24,"Local 2","Microsoft Wi-Fi Direct Virtual Adapter",NetworkInterfaceType.Wireless80211,false),
            new LanNetUtil.LanAddress("169.254.39.90",16,"Ethernet 2","Realtek",NetworkInterfaceType.Ethernet,false),
            new LanNetUtil.LanAddress("100.64.0.2",32,"Other","Unknown adapter",NetworkInterfaceType.Ethernet,true), wifi, wired };
        Check(LanNetUtil.SelectAddresses(candidates).First()==wifi,"QR ignores tunnel, virtual hotspot, link-local and point-to-point addresses");
        Check(LanNetUtil.SelectAddresses(candidates.Reverse()).First()==wifi,"enumeration order does not change preferred address");
        Check(LanNetUtil.SelectAddresses(candidates,"10.1.0.40").First()==wired,"discovery chooses peer subnet over default Wi-Fi");
        Check(LanNetUtil.SelectAddresses(new[]{ wifi with {Address="172.20.1.2"} }).Count==1,"legitimate 172 private LAN remains usable");
        Check(LanNetUtil.SelectAddresses(candidates.Take(4)).Count==0,"no valid LAN yields no misleading address");

        var actual = LanNetUtil.GetLanAddresses();
        Console.WriteLine("Current LAN choices: " + string.Join(", ", actual.Select(a=>a.Label)));
        if (actual.Count > 0)
        {
            var app = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            var qr = new QrCodeWindow("172.30.201.230",8899,"test-only");
            var choice=(ComboBox)qr.FindName("NetworkChoice");
            var chosen=(LanNetUtil.LanAddress)choice.SelectedItem;
            var bitmap=(BitmapSource)((Image)qr.FindName("QrImage")).Source;
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            var result=new BarcodeReaderGeneric().Decode(new RGBLuminanceSource(pixels,bitmap.PixelWidth,bitmap.PixelHeight,RGBLuminanceSource.BitmapFormat.BGRA32));
            using var payload=JsonDocument.Parse(result.Text);
            Check(chosen.Address==actual[0].Address && payload.RootElement.GetProperty("ip").GetString()==chosen.Address,"QR payload matches actual selected LAN, rejecting stale tunnel IP");
        }
    }
}
