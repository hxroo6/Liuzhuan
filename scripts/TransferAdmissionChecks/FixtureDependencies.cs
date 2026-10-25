// Only app/desktop wiring is substituted. Kestrel, hashing, JSON fsync, capacity and expiry use production code.
namespace Liuzhuan.Models {
    public sealed class MaterialItem {public string FilePath {get;set;}="";public string DisplayName {get;set;}="";public bool HasFileSource=>FilePath.Length>0;public long SourceRevision {get;set;}=1;}
}
namespace Liuzhuan.Services {
    public static class ConfigService {public static string Root="";public static string GetEffectiveDataDir()=>Root;}
    public sealed record TransferEntry(string Id,string Name,string Direction,long Done,long Total,string State,string Error,double Speed,string SavedPath="",bool CanControl=false,bool IsPaused=false);
    public static class TransferJournal {
        public static readonly System.Collections.Concurrent.ConcurrentBag<TransferEntry> Reports=new();
        public static void Report(TransferEntry value)=>Reports.Add(value);
        public static void RegisterPersistentControl(string id,Action pause,Action cancel) { }
        public static void EndPersistentControl(string id) { }
    }
}
namespace Liuzhuan.Services.Lan {
    public static class LanConfig {public static string PasswordHash=>"isolated-admission-fixture";}
    // These checks never complete an upload or register a material/receipt.
    public sealed class DurableInbox {
        public DurableInbox(string root) { }
        public Task RegisterAsync(string key,string fingerprint,Func<string,Task> register)=>throw new InvalidOperationException("Material registration is outside this fixture");
    }
}
namespace Liuzhuan.Utils {public static class Logger {public static void Error(string format,params object[] args) { }}}
