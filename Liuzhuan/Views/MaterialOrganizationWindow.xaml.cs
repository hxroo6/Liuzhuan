using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Liuzhuan.Models;

namespace Liuzhuan.Views;

public partial class MaterialOrganizationWindow : Window
{
    private readonly Func<MaterialOrganizationPatch, int> _save;
    private readonly string[] _groups;
    private readonly string[] _tags;
    public bool Saved { get; private set; }
    public int ChangedCount { get; private set; }

    public MaterialOrganizationWindow(IReadOnlyList<MaterialItem> items, IEnumerable<string> groups, IEnumerable<string> tags,
        Func<MaterialOrganizationPatch, int> save)
    {
        InitializeComponent();
        _save = save;
        _groups = groups.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _tags = tags.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var single = items.Count == 1;
        Summary.Text = single ? items[0].DisplayName : $"已选择 {items.Count} 项素材";
        BatchHint.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        GroupApply.IsChecked = TagsApply.IsChecked = NoteApply.IsChecked = single;
        if (items.Count > 0)
        {
            GroupInput.Text = items.All(x => x.GroupName == items[0].GroupName) ? items[0].GroupName : "";
            NoteInput.Text = items.All(x => x.Note == items[0].Note) ? items[0].Note : "";
            TagsInput.Text = single ? string.Join("，", items[0].Tags) : "";
        }
        TagsReplace.IsChecked = single;
        TagsAdd.IsChecked = !single;
    }

    private static string[] ParseTags(string text) => text.Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private MaterialOrganizationPatch BuildPatch() => MaterialOrganization.Normalize(new MaterialOrganizationPatch
    {
        GroupName = GroupApply.IsChecked == true ? GroupInput.Text : null,
        Tags = TagsApply.IsChecked == true ? ParseTags(TagsInput.Text) : null,
        TagsMode = TagsAdd.IsChecked == true ? TagsMode.Add : TagsRemove.IsChecked == true ? TagsMode.Remove : TagsMode.Replace,
        Note = NoteApply.IsChecked == true ? NoteInput.Text : null
    });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ChangedCount = _save(BuildPatch());
            Saved = true;
            DialogResult = true;
        }
        catch (Exception ex) { Status.Text = "未保存：" + ex.Message; Status.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush"); }
    }

    private void ChooseGroup_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        var ungrouped = new MenuItem { Header = "未分组" };
        ungrouped.Click += (_, _) => { GroupApply.IsChecked = true; GroupInput.Clear(); };
        menu.Items.Add(ungrouped);
        foreach (var group in _groups)
        {
            var option = new MenuItem { Header = group };
            option.Click += (_, _) => { GroupApply.IsChecked = true; GroupInput.Text = group; };
            menu.Items.Add(option);
        }
        menu.IsOpen = true;
    }

    private void ChooseTag_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        foreach (var tag in _tags)
        {
            var option = new MenuItem { Header = tag };
            option.Click += (_, _) => { TagsApply.IsChecked = true; TagsInput.Text = string.Join("，", ParseTags(TagsInput.Text).Append(tag).Distinct(StringComparer.OrdinalIgnoreCase)); };
            menu.Items.Add(option);
        }
        if (_tags.Length == 0) menu.Items.Add(new MenuItem { Header = "还没有标签，勾选「修改标签」后输入", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Save_Click(sender, e); e.Handled = true; }
    }
}
