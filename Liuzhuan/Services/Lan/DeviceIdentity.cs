using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Liuzhuan.Services.Lan;

/// <summary>Installation identity, separate from addresses and per-library transfer identity. Never exported.</summary>
public static class DeviceIdentity
{
    public sealed record Document(string Id, string Name);
    private static readonly object Gate = new();
    public static Document Load(string root)
    {
        lock (Gate)
        {
            var path = Path.Combine(root, "device-identity.json");
            var value = DurableJson.Read<Document>(path);
            if (value == null) { value = new(Guid.NewGuid().ToString("D"), Environment.MachineName); DurableJson.Write(path, value); }
            if (!Guid.TryParseExact(value.Id, "D", out _) || value.Name.Length is < 1 or > 64 || value.Name.Any(char.IsControl))
                throw new InvalidDataException("设备身份记录不可读取，原文件已保留。");
            return value;
        }
    }
    public static void Rename(string root, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("电脑名称需为1–64个字符，不能包含控制字符。");
        lock (Gate) DurableJson.Write(Path.Combine(root,"device-identity.json"), Load(root) with { Name=name });
    }
    public static string Proof(string authHash,string nonce,string id,int port,string encodedName)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(authHash));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"LZ-ID-1|{nonce}|{id}|{port}|{encodedName}"))).ToLowerInvariant();
    }
}
