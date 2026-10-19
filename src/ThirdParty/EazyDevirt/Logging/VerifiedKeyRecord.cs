using Newtonsoft.Json;

namespace EazyDevirt.Logging;

internal sealed record VerifiedKeyRecord(string MethodToken,int RegionId,string KeyType,object Value,string Source)
{
    public string OriginalNamespace {get;init;}="";
    public string OriginalTypeName {get;init;}="";
    public string OriginalMethodName {get;init;}="";
    public string OriginalMethodFullName {get;init;}="";
    public bool OriginalVerifierPassed => true;
    public string CipherKeyEncoding => Value is int?"Int32 big-endian":"UTF-16LE";
    public string CipherKeyBytesHex => BitConverter.ToString(Serialize(Value)).Replace("-","");
    internal static byte[] Serialize(object key)=>key switch {
        int integer=>new byte[]{(byte)(integer>>24),(byte)(integer>>16),(byte)(integer>>8),(byte)integer},
        string text=>System.Text.Encoding.Unicode.GetBytes(text),
        _=>throw new NotSupportedException("Unsupported verified key serialization.")};
    internal string Format()
    {
        string value=Value is int number?number+" (0x"+unchecked((uint)number).ToString("X8")+")":JsonConvert.SerializeObject(Value);
        string identity=string.IsNullOrEmpty(OriginalMethodFullName)?"": " "+JsonConvert.SerializeObject(OriginalMethodFullName);
        return $"[密钥] 方法 {MethodToken}{identity} / 区域 {RegionId}：{KeyType} = {value}；来源：{Source}；原始校验通过。";
    }
}
