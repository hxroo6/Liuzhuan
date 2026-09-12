namespace Liuzhuan.Views;
public partial class TransferWindow : System.Windows.Window
{
    public TransferWindow()
    {
        InitializeComponent();
        DataContext=Services.TransferJournal.Entries;
        Loaded+=(_,_)=>Utils.UiMotion.Reveal((System.Windows.FrameworkElement)Content);
    }
}
