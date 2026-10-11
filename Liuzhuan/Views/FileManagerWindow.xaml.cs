using System.Windows;
using Liuzhuan.Services;

namespace Liuzhuan.Views;
public partial class FileManagerWindow : Window
{
    public FileManagerWindow() { InitializeComponent();var saved=FolderLauncher.Load();ExecutableBox.Text=saved.Executable;ArgumentsBox.Text=saved.Arguments; }
    private void Browse_Click(object sender,RoutedEventArgs e) {
        var picker=new Microsoft.Win32.OpenFileDialog {Title="选择文件管理器",Filter="应用程序 (*.exe)|*.exe",CheckFileExists=true};
        if(picker.ShowDialog(this)==true) ExecutableBox.Text=picker.FileName;
    }
    private void Reset_Click(object sender,RoutedEventArgs e) {ExecutableBox.Text="";ArgumentsBox.Text="{folder}";}
    private void Save_Click(object sender,RoutedEventArgs e) {
        try {FolderLauncher.Save(new(ExecutableBox.Text.Trim(),ArgumentsBox.Text));DialogResult=true;}
        catch(Exception ex) {MessageBox.Show(this,ex.Message,"无法保存",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
