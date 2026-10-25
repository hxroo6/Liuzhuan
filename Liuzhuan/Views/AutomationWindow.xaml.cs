using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Models;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public partial class AutomationWindow : Window
{
    private readonly DataStore _store;
    private readonly FolderCollectionService _folders;
    private readonly Action _collect;
    private readonly Func<bool, bool> _hotkey;
    private AutomationDocument _document = new();
    private readonly ObservableCollection<CollectionRule> _rules = new();
    private CollectionRule? _selected;
    private bool _loading;
    private sealed record Choice(string Value, string Label);
    public AutomationWindow(DataStore store, FolderCollectionService folders, Action collect, bool hotkeyEnabled, Func<bool,bool> hotkey)
    {
        InitializeComponent(); _store=store; _folders=folders; _collect=collect; _hotkey=hotkey;
        TypeInput.ItemsSource = new[] { new Choice("All","全部类型") }.Concat(Enum.GetValues<MaterialType>().Select(x => new Choice(x.ToString(), x switch { MaterialType.Image=>"图片",MaterialType.Text=>"文字",MaterialType.Video=>"视频",MaterialType.Audio=>"音频",_=>"其他文件" }))).ToList();
        SourceInput.ItemsSource = new[] { new Choice("All","全部来源"),new Choice("Desktop","电脑手动收集"),new Choice("Android","手机发送"),new Choice("Directory","目录订阅（两端）") };
        RuleList.ItemsSource=_rules; QuickHotkey.IsChecked=hotkeyEnabled; Reload(); RefreshFolders();
    }
    private void Reload()
    {
        _loading=true; _selected=null; _document=_store.Automation.Export(); _rules.Clear(); foreach(var r in _document.Rules) _rules.Add(r); _loading=false;
        RuleList.SelectedIndex=_rules.Count>0?0:-1; Status.Text=_store.Automation.LoadError ?? "手机与电脑共用此规则表；另一端更改后，请先重新加载。";
    }
    private void Capture()
    {
        if(_selected==null) return;
        var next=_selected with { Name=NameInput.Text,Enabled=EnabledInput.IsChecked==true,Type=TypeInput.SelectedValue as string??"All",Source=SourceInput.SelectedValue as string??"All",Contains=ContainsInput.Text,Device=DeviceInput.Text,Group=GroupInput.Text,Tags=TagsInput.Text.Split(new[]{',','，',';','；'},StringSplitOptions.RemoveEmptyEntries) };
        AutomationStore.Normalize(new AutomationDocument { Rules=new(){next} });
        var i=_rules.IndexOf(_selected); if(i>=0) { _loading=true; _rules[i]=next; _selected=next; RuleList.SelectedItem=next; _loading=false; }
    }
    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(_loading) return;
        var next=RuleList.SelectedItem as CollectionRule;
        // Draft fields are kept until explicitly saved/previewed; selection commits them to the draft.
        try { Capture(); } catch(Exception ex) { Status.Text=ex.Message; _loading=true;RuleList.SelectedItem=_selected;_loading=false;return; }
        _selected=next;
        if(next==null) return;
        NameInput.Text=next.Name; EnabledInput.IsChecked=next.Enabled; TypeInput.SelectedValue=next.Type; SourceInput.SelectedValue=next.Source; ContainsInput.Text=next.Contains; DeviceInput.Text=next.Device; GroupInput.Text=next.Group; TagsInput.Text=string.Join(", ",next.Tags);
        _loading=true;RuleList.SelectedItem=next;_loading=false;
    }
    private AutomationDocument Draft() { Capture(); return AutomationStore.Normalize(_document with { Rules=_rules.ToList() }); }
    private void NewRule_Click(object sender,RoutedEventArgs e) { try { Capture();if(_rules.Count>=30) throw new ArgumentException("最多 30 条规则。"); var rule=new CollectionRule { Group="待整理" };_rules.Add(rule);RuleList.SelectedItem=rule; } catch(Exception ex){Status.Text=ex.Message;} }
    private void RemoveRule_Click(object sender,RoutedEventArgs e) { if(_selected==null)return; var old=_selected;_selected=null;_rules.Remove(old);RuleList.SelectedIndex=_rules.Count>0?0:-1; }
    private void Save_Click(object sender,RoutedEventArgs e) { try { _document=_store.Automation.Save(Draft());Status.Text="已保存，之后新收集的素材将按规则整理。"; } catch(Exception ex){Status.Text=ex.Message;} }
    private void Reload_Click(object sender,RoutedEventArgs e) => Reload();
    private void Preview_Click(object sender,RoutedEventArgs e) { try { var draft=Draft();var result=_store.Items.Take(50).Select(x=>(Item:x,Result:AutomationStore.Evaluate(draft,x))).Where(x=>x.Result.Matches.Length>0).ToList();PreviewText.Text=$"预览：最近 50 条中 {result.Count} 条匹配（未修改素材）\n"+string.Join("\n",result.Take(6).Select(x=>$"{x.Item.DisplayName} → {x.Result.Group} · {string.Join(" / ",x.Result.Tags)}")); }catch(Exception ex){Status.Text=ex.Message;} }
    private void RefreshFolders() { FoldersList.ItemsSource=_folders.Snapshot(); }
    private async void AddFolder_Click(object sender,RoutedEventArgs e)
    {
        using var picker=new System.Windows.Forms.FolderBrowserDialog { Description="选择要订阅的来源文件夹（不会移动或删除原文件）",UseDescriptionForTitle=true };
        if(picker.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
        try { await Task.Run(()=>_folders.AddAsync(picker.SelectedPath));RefreshFolders();Status.Text=_folders.Status; }catch(Exception ex){Status.Text=ex.Message;}
    }
    private async void RemoveFolder_Click(object sender,RoutedEventArgs e) { if(FoldersList.SelectedItem is not FolderSubscription f)return;try{await _folders.RemoveAsync(f.Id);RefreshFolders();Status.Text="订阅已停止，原文件和已收集素材均保留。";}catch(Exception ex){Status.Text=ex.Message;} }
    private async void ScanFolder_Click(object sender,RoutedEventArgs e) { try {Status.Text="正在检查…";await _folders.ScanAsync();Status.Text=_folders.Status;}catch(Exception ex){Status.Text=ex.Message;} }
    private void Collect_Click(object sender,RoutedEventArgs e) => _collect();
    private void QuickHotkey_Click(object sender,RoutedEventArgs e) { var enabled=QuickHotkey.IsChecked==true;if(!_hotkey(enabled)){QuickHotkey.IsChecked=false;Status.Text="快捷键被占用或偏好保存失败，请检查后重试。";}else Status.Text=enabled?"已启用 Ctrl + Alt + V 快捷收集。":"快捷收集热键已关闭。"; }
}
