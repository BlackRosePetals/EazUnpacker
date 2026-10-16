using dnlib.DotNet;

namespace EazFixer {
    internal class Flags
    {
        public static string  InFile;
        public static string  OutFile;

        public static bool    KeepTypes;
        public static bool    VirtFix;
        public static bool    PreserveAll;
        
        public static MDToken StrDecTok;
        public static MDToken ResResolverTok;
        public static MDToken ResInitTok;
        public static MDToken AsmResTypeTok;
        public static MDToken AsmResMoveNextTok;
        public static MDToken AsmResDecompressTok;
        public static MDToken AsmResDecryptTok;
    }
}
