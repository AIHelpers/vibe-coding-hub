namespace AiCodeAgent.Core.Theming;

/// <summary>The kinds of text a code editor colours. Each has one colour per theme in <c>App.axaml</c> (E4-S2).</summary>
public enum SyntaxCategory
{
    /// <summary>Plain code: punctuation, operators, identifiers.</summary>
    Default,
    Keyword,
    Type,
    Method,
    String,
    Number,
    Comment,
    Tag,
    Attribute,
    Directive,
    Heading,
    Link,
    Added,
    Removed,
    /// <summary>A name the table does not know; its original colour is made readable instead.</summary>
    Unknown
}

/// <summary>
/// Maps the colour names used inside AvaloniaEdit's built-in highlighting definitions (they differ per language:
/// "Keywords", "TypeKeywords", "NumberLiteral", "XmlTag", ...) to a <see cref="SyntaxCategory"/> and to the theme resource that colours it.
/// The built-in definitions are authored for white backgrounds (Navy, DarkBlue, ...), so every name must be mapped or repaired.
/// </summary>
public static class SyntaxTokens
{
    private static readonly Dictionary<string, SyntaxCategory> Known = Build();

    private static Dictionary<string, SyntaxCategory> Build()
    {
        var map = new Dictionary<string, SyntaxCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(SyntaxCategory c, params string[] names) { foreach (var n in names) map[n] = c; }

        Add(SyntaxCategory.Default, "Text", "Default", "Punctuation", "CurlyBraces", "Colon", "Assignment", "XmlPunctuation",
            "CompoundKeywords", "Operators", "StringInterpolation", "UnchangedText", "Code", "Emphasis", "StrongEmphasis",
            "Literals", "ASPSection", "ASPSectionStartEndTags", "LineBreak", "Brace", "Delimiter");
        Add(SyntaxCategory.Comment, "Comment", "DocComment", "XmlDoc", "CommentTags", "JavaDocTags", "KnownDocTags", "BlockQuote",
            "XmlComment", "CommentLine", "CommentBlock");
        Add(SyntaxCategory.String, "String", "Char", "Character", "Regex", "XmlString", "AttributeValue", "CData", "VerbatimString");
        Add(SyntaxCategory.Number, "Number", "Digits", "NumberLiteral", "Bool", "BooleanConstants", "TrueFalse", "Null",
            "NullOrValueKeywords", "Constants", "DateLiteral", "Value", "JavaScriptLiterals", "Float", "Hex");
        Add(SyntaxCategory.Type, "TypeKeywords", "ValueTypeKeywords", "ReferenceTypeKeywords", "ValueTypes", "ReferenceTypes",
            "OtherTypes", "DataTypes", "Void", "Class", "TypeName", "ClassName");
        Add(SyntaxCategory.Method, "MethodName", "MethodCall", "FunctionCall", "JavaScriptGlobalFunctions", "Command", "FunctionName");
        Add(SyntaxCategory.Keyword, "Keywords", "Keyword", "OperatorKeywords", "Modifiers", "Visibility", "AccessModifiers", "AccessKeywords",
            "LoopKeywords", "JumpKeywords", "ControlFlow", "ControlStatements", "SelectionStatements", "IterationStatements",
            "JumpStatements", "ExceptionHandling", "ExceptionHandlingStatements", "ExceptionKeywords", "This", "ThisOrBaseReference",
            "Friend", "Namespace", "NamespaceKeywords", "Package", "GetSetAddRemove", "SemanticKeywords", "FunctionKeywords",
            "UnsafeKeywords", "CheckedKeyword", "ContextKeywords", "GotoKeywords", "ParameterModifiers", "JavaScriptKeyWords",
            "JavaScriptIntrinsics");
        Add(SyntaxCategory.Tag, "XmlTag", "HtmlTag", "Tags", "ScriptTag", "JavaScriptTag", "JScriptTag", "VBScriptTag",
            "UnknownScriptTag", "Selector", "Slash", "DocType", "XmlDeclaration");
        Add(SyntaxCategory.Attribute, "Attributes", "AttributeName", "UnknownAttribute", "Property", "FieldName", "Variable",
            "Entity", "Entities", "EntityReference", "BrokenEntity", "XmlAttribute", "PropertyName");
        Add(SyntaxCategory.Directive, "Preprocessor", "PreprocessorWord", "Position", "FileName", "Header");
        Add(SyntaxCategory.Heading, "Heading", "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6");
        Add(SyntaxCategory.Link, "Link", "Image");
        Add(SyntaxCategory.Added, "AddedText");
        Add(SyntaxCategory.Removed, "RemovedText");
        return map;
    }

    public static SyntaxCategory Classify(string? colorName)
        => !string.IsNullOrEmpty(colorName) && Known.TryGetValue(colorName, out var c) ? c : SyntaxCategory.Unknown;

    /// <summary>Every colour name the table knows (for tests).</summary>
    public static IReadOnlyCollection<string> KnownNames => Known.Keys;

    /// <summary>The <c>App.axaml</c> colour resource for a category, or null for <see cref="SyntaxCategory.Unknown"/>.</summary>
    public static string? ResourceKey(SyntaxCategory category) => category switch
    {
        SyntaxCategory.Default => "ThemeCodeFg",
        SyntaxCategory.Keyword => "ThemeSyntaxKeyword",
        SyntaxCategory.Type => "ThemeSyntaxType",
        SyntaxCategory.Method => "ThemeSyntaxMethod",
        SyntaxCategory.String => "ThemeSyntaxString",
        SyntaxCategory.Number => "ThemeSyntaxNumber",
        SyntaxCategory.Comment => "ThemeSyntaxComment",
        SyntaxCategory.Tag => "ThemeSyntaxTag",
        SyntaxCategory.Attribute => "ThemeSyntaxAttribute",
        SyntaxCategory.Directive => "ThemeSyntaxDirective",
        SyntaxCategory.Heading => "ThemeSyntaxHeading",
        SyntaxCategory.Link => "ThemeSyntaxLink",
        SyntaxCategory.Added => "ThemeSuccess",
        SyntaxCategory.Removed => "ThemeDanger",
        _ => null
    };
}
