using System.Windows;
using Liuzhuan.Services.Lan;
namespace Liuzhuan.Views;
public partial class DeviceNameWindow:Window
{
    public DeviceNameWindow() {InitializeComponent();var d=DeviceIdentity.Load(App.DataDir);NameInput.Text=d.Name;IdentityText.Text=$"设备 ID：{d.Id}";}
    private void Save_Click(object sender,RoutedEventArgs e) {try{DeviceIdentity.Rename(App.DataDir,NameInput.Text);DialogResult=true;}catch(Exception ex){ErrorText.Text=ex is ArgumentException?ex.Message:"名称未能保存，请检查存储。";}}
}
