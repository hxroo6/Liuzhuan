namespace Liuzhuan.Services;

public sealed record StorageRetention(int Days=30,int KeepNewest=500)
{
    public static StorageRetention Load(string root)=>Validate(DurableJson.Read<StorageRetention>(System.IO.Path.Combine(root,"storage-retention.json"))??new());
    public static StorageRetention Validate(StorageRetention value)
    {if(value.Days is <1 or >3650||value.KeepNewest is <0 or >100000)throw new ArgumentException("保留策略范围无效。");return value;}
}
