using System.Xml;
using System.Xml.Linq;

namespace Unpacker;

internal static class RuntimeCompatibility
{
    // Keep the public key/token: plug-in catalogs, binding and reflection may use
    // the original assembly identity. Rewriting cannot reproduce its private key.
    // Restore the CLR's per-application full-trust verification bypass rather
    // than stripping identity or modifying machine-wide verification settings.
    internal static void WriteConfiguration(string originalConfig,string existingConfig,string destination)
    {
        string? source=File.Exists(existingConfig)?existingConfig:File.Exists(originalConfig)?originalConfig:null;
        XDocument document;
        if(source==null)document=new XDocument(new XElement("configuration"));
        else
        {
            var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
            using var reader=XmlReader.Create(source,settings);
            document=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
        }
        if(document.Root?.Name!="configuration")throw new InvalidDataException("Invalid runtime configuration: "+source);
        var runtime=document.Root.Element("runtime");
        if(runtime==null){runtime=new XElement("runtime");document.Root.Add(runtime);}
        var policies=runtime.Elements("bypassTrustedAppStrongNames").ToArray();
        var policy=policies.FirstOrDefault();
        if(policy==null){policy=new XElement("bypassTrustedAppStrongNames");runtime.Add(policy);}
        policy.SetAttributeValue("enabled","true");
        foreach(var duplicate in policies.Skip(1))duplicate.Remove();
        document.Save(destination);
        using var verify=XmlReader.Create(destination,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
        var check=XDocument.Load(verify);
        if((string?)check.Root?.Element("runtime")?.Element("bypassTrustedAppStrongNames")?.Attribute("enabled")!="true")
            throw new InvalidDataException("Failed to write runtime signature compatibility policy.");
    }
}
