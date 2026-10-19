namespace EazyDevirt.Logging;

internal static class LoggingTests
{
    internal static void Run()
    {
        string path=Path.GetTempFileName();var previous=Console.Out;var captured=new StringWriter();
        try
        {
            Console.SetOut(captured);var logger=new ConsoleLogger();
            using(logger.Capture(path,0))
            {
                logger.Info("low-level opcode detail");ConsoleLogger.Detail("wire layout detail");
                logger.Summary("[恢复统计] 方法 2/3；剩余 1");
                for(int i=0;i<30;i++)logger.Warning("warning "+i);
            }
            string visible=captured.ToString(),detail=File.ReadAllText(path);
            if(visible.Contains("opcode detail")||visible.Contains("wire layout")||!visible.Contains("方法 2/3")||visible.Contains("warning 29")||!detail.Contains("warning 29")||!detail.Contains("wire layout"))
                throw new Exception("Concise logging lost diagnostics or flooded stdout.");
            var number=new VerifiedKeyRecord("0x06000001",2,"Int32",-7,"合成测试").Format();
            var text=new VerifiedKeyRecord("0x06000002",0,"String","line\n\"quoted\"","合成测试").Format();
            if(!number.Contains("-7 (0xFFFFFFF9)")||!text.Contains("\\n")||text.Contains('\n')||!text.Contains("原始校验通过"))throw new Exception("Verified-key formatting is ambiguous or permits log-line injection.");
            var numberRecord=new VerifiedKeyRecord("0x06000001",2,"Int32",-7,"合成测试");
            var textRecord=new VerifiedKeyRecord("0x06000002",0,"String","A中\n","合成测试");
            if(numberRecord.CipherKeyBytesHex!="FFFFFFF9"||textRecord.CipherKeyBytesHex!="41002D4E0A00"||textRecord.CipherKeyEncoding!="UTF-16LE")throw new Exception("Logged key bytes differ from the cipher's serialization.");
            var named=new VerifiedKeyRecord("0x06000003",1,"Int32",123,"合成测试"){
                OriginalNamespace="Fixture.Space",OriginalTypeName="Outer+Inner",OriginalMethodName="M\n\u0002",
                OriginalMethodFullName="System.Void Fixture.Space.Outer+Inner::M\n\u0002(System.Int32)"};
            string namedLine=named.Format(),json=Newtonsoft.Json.JsonConvert.SerializeObject(named);
            if(!namedLine.Contains("Fixture.Space.Outer+Inner::M")||!namedLine.Contains("\\u0002")||namedLine.Contains('\n')||!json.Contains("OriginalNamespace")||!json.Contains("OriginalTypeName"))
                throw new Exception("Original namespace/type/method identity missing or not safely escaped.");
        }
        finally {Console.SetOut(previous);File.Delete(path);}
        Console.WriteLine("PASS: concise summaries, bounded visible warnings, full diagnostics and escaped verified-key values.");
    }
}
