using CommandLine;

namespace EazFixer
{
    class Options
    {
        [Option("file",
            Default = false,
            Required = true)]
        public string InFile { get; set; }

        [Option("out")]
        public string OutFile { get; set; }

        [Option("keep-types", HelpText = "Don't cleanup/remove obfuscator types")]
        public bool KeepTypes { get; set; }

        [Option("virt-fix", HelpText = "Don't process obfuscated parts necessary for the code virtualization to work")]
        public bool VirtFix { get; set; }
        
        [Option("preserve-all", HelpText = "Preserve all metadata")]
        public bool PreserveAll { get; set; }
        
        [Option("str-decrypt-tok", Default = "0", HelpText = "Manually specify string decryptor method token for if detection fails (Format: 0x<token>) ")]
        public string StrDecTok { get; set; }
        
        [Option("res-resolver-tok", Default = "0", HelpText =  "Manually specify resource resolver type token for if detection fails (Format: 0x<token>) ")]
        public string ResResolverTok { get; set; }
        
        [Option("res-init-tok", Default = "0", HelpText =  "Manually specify resource init method token for if detection fails (Format: 0x<token>) ")]
        public string ResInitTok { get; set; }
        
        [Option("asmres-type-tok", Default = "0", HelpText = "Manually specify assembly decryptor type token for if detection fails (Format: 0x<token>) ")]
        public string AsmResTypeTok { get; set; }
        
        [Option("asmres-movenext-tok", Default = "0", HelpText = "Manually specify assembly decryptor MoveNext token for if detection fails (Format: 0x<token>) ")]
        public string AsmResMoveNextTok { get; set; }
        
        [Option("asmres-decompress-tok", Default = "0", HelpText = "Manually specify assembly decompressor method token for if detection fails (Format: 0x<token>) ")]
        public string AsmResDecompressTok { get; set; }
        
        [Option("asmres-decrypt-tok", Default = "0", HelpText = "Manually specify assembly decryptor method token for if detection fails (Format: 0x<token>) ")]
        public string AsmResDecryptTok { get; set; }
    }
}
