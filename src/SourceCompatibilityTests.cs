using ICSharpCode.Decompiler.CSharp.Syntax;

namespace Unpacker;

internal static class SourceCompatibilityTests
{
    internal static void Run(string root)
    {
        var type=new TypeDeclaration {Name="ConstructorFixture"};
        var field=new FieldDeclaration {ReturnType=new PrimitiveType("string")};
        field.Variables.Add(new VariableInitializer("text"));type.Members.Add(field);
        var constructor=new ConstructorDeclaration {Name=type.Name,Body=new BlockStatement()};type.Members.Add(constructor);
        constructor.Body.Statements.Add(new BlockStatement(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("text"),new PrimitiveExpression("initial")))));
        constructor.Body.Statements.Add(BaseCall());
        type.AcceptVisitor(new MetadataCompatibility());
        if(field.Variables.Single().Initializer is not PrimitiveExpression {Value:"initial"}
            || constructor.Body.Statements.Count!=0 || constructor.Initializer?.ConstructorInitializerType!=ConstructorInitializerType.Base)
            throw new Exception("Pre-base constant field block was not preserved.");

        var temporaryType=new TypeDeclaration {Name="TemporaryFixture"};
        var temporaryField=new FieldDeclaration {ReturnType=new SimpleType("Payload")};
        temporaryField.Variables.Add(new VariableInitializer("payload"));temporaryType.Members.Add(temporaryField);
        var temporaryConstructor=new ConstructorDeclaration {Name=temporaryType.Name,Body=new BlockStatement()};temporaryType.Members.Add(temporaryConstructor);
        temporaryConstructor.Body.Statements.Add(new VariableDeclarationStatement(new SimpleType("Payload"),"local",new ObjectCreateExpression(new SimpleType("Payload"))));
        temporaryConstructor.Body.Statements.Add(new BlockStatement(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("payload"),new IdentifierExpression("local")))));
        temporaryConstructor.Body.Statements.Add(BaseCall());
        temporaryType.AcceptVisitor(new MetadataCompatibility());
        if(temporaryField.Variables.Single().Initializer is not ObjectCreateExpression || temporaryConstructor.Body.Statements.Count!=0
            || temporaryType.Members.OfType<MethodDeclaration>().Any())throw new Exception("Pre-base temporary store became invalid static instance access.");

        string fixture=Path.Combine(root,"probing");Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(Path.Combine(fixture,"Components","Vendor"));
        string original=Path.Combine(fixture,"app.exe");
        File.WriteAllText(original+".config","<configuration><runtime><assemblyBinding xmlns='urn:schemas-microsoft-com:asm.v1'><probing privatePath='Components;Components\\Vendor;..;C:\\Windows'/></assemblyBinding></runtime></configuration>");
        string[] paths=SourceCompiler.DependencyDirectories(original,original);
        if(paths.Length!=3 || !paths.Contains(Path.Combine(fixture,"Components","Vendor")) || paths.Contains(root))
            throw new Exception("Config probing paths were not resolved within the application directory.");
        Console.WriteLine("PASS: pre-base constant blocks/temporary field stores and contained config probing paths.");
    }

    private static ExpressionStatement BaseCall()=>new(new InvocationExpression(new MemberReferenceExpression(new BaseReferenceExpression(),".ctor")));
}
