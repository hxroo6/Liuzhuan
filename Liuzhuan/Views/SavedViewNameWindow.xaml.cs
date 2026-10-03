using System.Windows;

namespace Liuzhuan.Views;

public partial class SavedViewNameWindow : Window
{
    private readonly Action<string> _save;
    public bool Saved { get; private set; }
    public SavedViewNameWindow(string summary, string initialName, Action<string> save, bool rename = false)
    {
        InitializeComponent();
        _save = save;
        Criteria.Text = summary;
        NameInput.Text = initialName;
        if (rename) { Title = "重命名常用筛选"; Heading.Text = "给筛选一个更好认的名字"; }
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _save(NameInput.Text); Saved = true; DialogResult = true; }
        catch (Exception ex) { Status.Text = "未保存：" + ex.Message; Status.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush"); }
    }
}
