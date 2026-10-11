namespace Liuzhuan.Views;
public partial class TransferWindow : System.Windows.Window
{
    public TransferWindow()
    {
        InitializeComponent();
        DataContext=Services.TransferJournal.Entries;
        Loaded+=(_,_)=>Utils.UiMotion.Reveal((System.Windows.FrameworkElement)Content);
    }
    private void OpenDirectory(string directory) {
        try { Services.FolderLauncher.Open(directory); }
        catch(Exception ex) {System.Windows.MessageBox.Show(this,ex.Message,"无法打开目录",System.Windows.MessageBoxButton.OK,System.Windows.MessageBoxImage.Warning);}
    }
    private void OpenReceived_Click(object sender,System.Windows.RoutedEventArgs e) {
        var directory=System.IO.Path.Combine(Services.ConfigService.GetEffectiveDataDir(),"uploads");
        System.IO.Directory.CreateDirectory(directory);OpenDirectory(directory);
    }
    private void OpenSaved_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string file}) OpenDirectory(System.IO.Path.GetDirectoryName(file)!);
    }
    private void Manager_Click(object sender,System.Windows.RoutedEventArgs e)=>new FileManagerWindow {Owner=this}.ShowDialog();
    private void Pause_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string id}) Services.TransferJournal.TogglePause(id);
    }
    private void Cancel_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string id}) Services.TransferJournal.Cancel(id);
    }
}
