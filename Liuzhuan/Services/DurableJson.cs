using System.IO;
using System.Text;
using System.Text.Json;

namespace Liuzhuan.Services;

/// <summary>Same-volume atomic replacement; acknowledge only after the new file has been flushed.</summary>
public static class DurableJson
{
    public static void WriteText(string path,string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                var bytes=Encoding.UTF8.GetBytes(value);output.Write(bytes);output.Flush(true);
            }
            File.Move(temp,path,true);
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public static void Write<T>(string path,T value)=>WriteText(path,JsonSerializer.Serialize(value));
    public static T? Read<T>(string path)=>File.Exists(path)?JsonSerializer.Deserialize<T>(File.ReadAllText(path)):default;
}
