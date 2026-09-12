namespace Liuzhuan.Views;
public partial class TransferWindow : System.Windows.Window
{
    public TransferWindow() { InitializeComponent(); DataContext=Services.TransferJournal.Entries; }
}
