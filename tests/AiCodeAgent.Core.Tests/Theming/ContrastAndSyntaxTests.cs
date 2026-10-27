using AiCodeAgent.Core.Theming;
using Xunit;

namespace AiCodeAgent.Core.Tests.Theming;

public class ContrastTests
{
    [Fact]
    public void Ratio_MatchesTheWcagReferenceValues()
    {
        Assert.Equal(21.0, Contrast.Ratio("#000000", "#FFFFFF"), 1);
        Assert.Equal(1.0, Contrast.Ratio("#336699", "#336699"), 3);
        // #777777 on white is the classic "just fails AA" example: 4.48:1.
        Assert.Equal(4.48, Contrast.Ratio("#777777", "#FFFFFF"), 2);
        // Symmetric.
        Assert.Equal(Contrast.Ratio("#123456", "#FEDCBA"), Contrast.Ratio("#FEDCBA", "#123456"), 6);
    }

    [Theory]
    [InlineData("#FFF", 255, 255, 255)]
    [InlineData("#1E1E2E", 0x1E, 0x1E, 0x2E)]
    [InlineData("#FF102030", 0x10, 0x20, 0x30)]
    public void ParseHex_AcceptsShortLongAndAlphaForms(string hex, int r, int g, int b)
        => Assert.Equal((r, g, b), Contrast.ParseHex(hex));

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    public void ParseHex_RejectsGarbage(string hex) => Assert.Throws<FormatException>(() => Contrast.ParseHex(hex));

    [Theory]
    [InlineData("#00008B", "#181825")] // DarkBlue keyword on a dark editor: the reported bug
    [InlineData("#000080", "#181825")] // Navy
    [InlineData("#FFFF99", "#FFFFFF")] // pale yellow on white
    [InlineData("#808080", "#808080")] // same colour as the background
    public void EnsureContrast_MakesAnyColourReadable(string fg, string bg)
    {
        var fixedColor = Contrast.EnsureContrast(fg, bg);
        Assert.True(Contrast.Ratio(fixedColor, bg) >= Contrast.AaText, $"{fg} on {bg} -> {fixedColor}");
    }

    [Fact]
    public void EnsureContrast_LeavesGoodColoursAlone()
        => Assert.Equal("#1A1A1A", Contrast.EnsureContrast("#1A1A1A", "#FFFFFF"));

    [Fact]
    public void EnsureContrast_KeepsTheHueFamily()
    {
        // Dark blue on a dark background is lightened (still blue), not turned into white.
        var lighter = Contrast.EnsureContrast("#00008B", "#181825");
        var (r, g, b) = Contrast.ParseHex(lighter);
        Assert.True(b > r && b > g, lighter);
    }
}

public class SyntaxTokensTests
{
    // Every colour name used by the built-in AvaloniaEdit highlighting definitions (C#, C++, CSS, HTML, Java, JavaScript,
    // Json, MarkDown, Patch, PowerShell, Python, TSQL, VB, XML, ASP/XHTML, Boo, Coco).
    private static readonly string[] BuiltInNames =
    {
        "ASPSectionStartEndTags", "ASPSection", "Comment", "String", "Regex", "Punctuation", "Keywords", "Character", "Preprocessor",
        "MethodName", "Digits", "CompoundKeywords", "This", "Operators", "Namespace", "Friend", "Modifiers", "TypeKeywords",
        "BooleanConstants", "LoopKeywords", "JumpKeywords", "ExceptionHandling", "ControlFlow", "StringInterpolation", "Char",
        "ValueTypeKeywords", "ReferenceTypeKeywords", "MethodCall", "NumberLiteral", "ThisOrBaseReference", "NullOrValueKeywords",
        "GotoKeywords", "ContextKeywords", "ExceptionKeywords", "CheckedKeyword", "UnsafeKeywords", "OperatorKeywords",
        "ParameterModifiers", "Visibility", "NamespaceKeywords", "GetSetAddRemove", "TrueFalse", "SemanticKeywords", "Selector",
        "Class", "Property", "Value", "CurlyBraces", "Colon", "ScriptTag", "JavaScriptTag", "JScriptTag", "VBScriptTag",
        "UnknownScriptTag", "HtmlTag", "Tags", "Attributes", "Slash", "Assignment", "EntityReference", "Entities", "UnknownAttribute",
        "AccessKeywords", "SelectionStatements", "IterationStatements", "ExceptionHandlingStatements", "ValueTypes", "ReferenceTypes",
        "Void", "JumpStatements", "AccessModifiers", "Package", "Literals", "CommentTags", "JavaDocTags", "JavaScriptKeyWords",
        "JavaScriptIntrinsics", "JavaScriptLiterals", "JavaScriptGlobalFunctions", "Bool", "Number", "Null", "FieldName", "Heading",
        "Emphasis", "StrongEmphasis", "Code", "BlockQuote", "Link", "Image", "LineBreak", "Heading1", "Heading2", "Heading3",
        "Heading4", "Heading5", "Heading6", "AddedText", "RemovedText", "UnchangedText", "Position", "Header", "FileName",
        "FunctionCall", "ControlStatements", "OtherTypes", "Variable", "Command", "DateLiteral", "DataTypes", "Constants",
        "FunctionKeywords", "CData", "DocType", "XmlDeclaration", "XmlTag", "AttributeName", "AttributeValue", "Entity",
        "BrokenEntity", "XmlString", "DocComment", "XmlPunctuation", "KnownDocTags", "XmlDoc"
    };

    [Fact]
    public void EveryBuiltInColourName_IsMapped()
    {
        var unmapped = BuiltInNames.Where(n => SyntaxTokens.Classify(n) == SyntaxCategory.Unknown).ToList();
        Assert.True(unmapped.Count == 0, "Unmapped: " + string.Join(", ", unmapped));
    }

    [Theory]
    [InlineData("Keywords", SyntaxCategory.Keyword)]
    [InlineData("TypeKeywords", SyntaxCategory.Type)]
    [InlineData("NullOrValueKeywords", SyntaxCategory.Number)]
    [InlineData("NumberLiteral", SyntaxCategory.Number)]
    [InlineData("MethodCall", SyntaxCategory.Method)]
    [InlineData("XmlTag", SyntaxCategory.Tag)]
    [InlineData("xmltag", SyntaxCategory.Tag)]
    [InlineData("Comment", SyntaxCategory.Comment)]
    [InlineData("StringInterpolation", SyntaxCategory.Default)]
    public void Classify_PutsNamesInTheRightCategory(string name, SyntaxCategory expected)
        => Assert.Equal(expected, SyntaxTokens.Classify(name));

    [Fact]
    public void Classify_UnknownOrEmpty_IsUnknown()
    {
        Assert.Equal(SyntaxCategory.Unknown, SyntaxTokens.Classify("SomethingNew"));
        Assert.Equal(SyntaxCategory.Unknown, SyntaxTokens.Classify(null));
        Assert.Equal(SyntaxCategory.Unknown, SyntaxTokens.Classify(""));
    }

    [Fact]
    public void ResourceKey_ExistsForEveryCategoryExceptUnknown()
    {
        foreach (var category in Enum.GetValues<SyntaxCategory>())
        {
            var key = SyntaxTokens.ResourceKey(category);
            if (category == SyntaxCategory.Unknown) Assert.Null(key);
            else Assert.StartsWith("Theme", key);
        }
    }
}
