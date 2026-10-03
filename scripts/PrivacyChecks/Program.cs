using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Services;

var root = Path.Combine(Path.GetTempPath(), "liuzhuan-privacy-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); App.DataDir = root;
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true,name); return; } throw new Exception(name); }
var now = new DateTimeOffset(2026,10,3,2,0,0,TimeSpan.Zero);
var rules = ClipboardPrivacyPolicy.NormalizeApps(new[]{" WeChat ","wechat.EXE","KeePassXC.exe","","中文应用.exe"});
Check(rules.SequenceEqual(new[]{"WeChat.exe","KeePassXC.exe","中文应用.exe"}),"app names trim, normalize suffix and deduplicate case-insensitively");
Check(!ClipboardPrivacyPolicy.ShouldCapture(now,null,rules,"WECHAT.EXE"),"excluded owner is blocked");
Check(ClipboardPrivacyPolicy.ShouldCapture(now,null,rules,"notepad.exe"),"other known owners are allowed");
Check(!ClipboardPrivacyPolicy.ShouldCapture(now,null,rules,null),"unknown owner is blocked with exclusions");
Check(ClipboardPrivacyPolicy.ShouldCapture(now,null,Array.Empty<string>(),null),"unknown owner remains compatible when no exclusions exist");
Check(!ClipboardPrivacyPolicy.ShouldCapture(now,now.AddMinutes(15),Array.Empty<string>(),"notepad.exe"),"pause blocks any automatic owner");
Check(ClipboardPrivacyPolicy.ShouldCapture(now,now,Array.Empty<string>(),"notepad.exe"),"deadline restores capture without a polling timer");
Reject(()=>ClipboardPrivacyPolicy.NormalizeApps(new[]{"F:/password.exe"}),"paths cannot masquerade as app names");
Reject(()=>ClipboardPrivacyPolicy.NormalizeApps(new[]{"bad\nname.exe"}),"control characters are rejected");
Reject(()=>ClipboardPrivacyPolicy.NormalizeApps(Enumerable.Range(0,51).Select(i=>"p"+i)),"rule count is bounded");
AppSettings.ClipboardMonitorEnabled=true; AppSettings.HeicConversion=HeicConversionMode.Jpeg;
AppSettings.ConfigurePrivacy(rules,now.AddHours(1)); AppSettings.Load();
Check(AppSettings.ExcludedClipboardApps.SequenceEqual(rules) && AppSettings.ClipboardPausedUntil==now.AddHours(1),"privacy settings survive disk reload");
Check(AppSettings.ClipboardMonitorEnabled && AppSettings.HeicConversion==HeicConversionMode.Jpeg,"privacy save retains existing settings");
var returned=AppSettings.ExcludedClipboardApps; returned[0]="changed.exe";
Check(AppSettings.ExcludedClipboardApps[0]=="WeChat.exe","callers cannot mutate rules behind the store");
var path=Path.Combine(root,"appsettings.json"); var original=File.ReadAllText(path);
using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
{
    var failed=false; try { AppSettings.ConfigurePrivacy(new[]{"other.exe"},null); } catch(IOException) { failed=true; } catch(UnauthorizedAccessException) { failed=true; }
    Check(failed && AppSettings.ExcludedClipboardApps.SequenceEqual(rules),"failed atomic save leaves active privacy rules intact");
}
Check(File.ReadAllText(path)==original && Directory.GetFiles(root,"*.tmp").Length==0,"failed save retains original bytes and removes only its temporary file");
File.WriteAllText(path,"{\"ClipboardMonitorEnabled\":true,\"ExcludedClipboardApps\":[\"bad/path.exe\"]}"); AppSettings.Load();
Check(AppSettings.ClipboardPrivacyLoadError!=null,"invalid saved rules fail closed with an explicit load error");
var invalidOriginal=File.ReadAllText(path); AppSettings.Save();
Check(File.ReadAllText(path)==invalidOriginal,"unrelated settings save preserves unreadable privacy configuration");
AppSettings.ConfigurePrivacy(new[]{"KeePassXC.exe"},null);
Check(AppSettings.ClipboardPrivacyLoadError==null,"valid replacement clears the configuration block");
File.WriteAllText(path,"{\"ClipboardMonitorEnabled\":true,\"ExcludedClipboardApps\":\"KeePassXC.exe\"}"); AppSettings.Load();
Check(AppSettings.ClipboardPrivacyLoadError!=null,"malformed non-array exclusions cannot silently disable protection");
File.WriteAllText(path,"{\"ClipboardMonitorEnabled\":true,\"ClipboardPausedUntil\":\"invalid\"}"); AppSettings.Load();
Check(AppSettings.ClipboardPrivacyLoadError!=null,"malformed deadlines cannot silently resume collection");
File.WriteAllText(path,"{\"ClipboardMonitorEnabled\":false,\"HeicConversion\":\"Png\"}"); AppSettings.Load();
Check(!AppSettings.ClipboardMonitorEnabled && AppSettings.ExcludedClipboardApps.Length==0 && AppSettings.ClipboardPausedUntil==null,"older settings remain compatible and reload clears obsolete flags");
Console.WriteLine($"PrivacyChecks: {count} passed");
