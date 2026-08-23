using JarvisAI.Application.Voice;

namespace JarvisAI.Tests;

public class TextCleanerTests
{
    [Fact]
    public void Plain_text_is_unchanged()
    {
        Assert.Equal("Bonjour comment allez-vous", TextCleaner.StripMarkdown("Bonjour comment allez-vous"));
    }

    [Fact]
    public void Inline_code_is_unwrapped()
    {
        Assert.Equal("utilise la commande dir pour lister", TextCleaner.StripMarkdown("utilise la commande `dir` pour lister"));
    }

    [Fact]
    public void Code_fences_are_removed()
    {
        var text = "Voici le code:\n```csharp\nvar x = 1;\n```\nC'est tout.";
        Assert.Equal("Voici le code: C'est tout.", TextCleaner.StripMarkdown(text));
    }

    [Fact]
    public void Bold_and_italic_are_removed()
    {
        Assert.Equal("très important aujourd'hui", TextCleaner.StripMarkdown("**très** *important* aujourd'hui"));
    }

    [Fact]
    public void Urls_are_removed()
    {
        Assert.Equal("voir pour plus d'infos", TextCleaner.StripMarkdown("voir https://example.com/docs pour plus d'infos"));
    }

    [Fact]
    public void List_markers_are_removed()
    {
        Assert.Equal("premier point second point", TextCleaner.StripMarkdown("- premier point\n- second point"));
    }

    [Fact]
    public void Headers_are_removed()
    {
        Assert.Equal("Titre du document", TextCleaner.StripMarkdown("# Titre du document"));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, TextCleaner.StripMarkdown(null));
        Assert.Equal(string.Empty, TextCleaner.StripMarkdown("  "));
    }

    [Fact]
    public void Multiple_whitespace_is_collapsed()
    {
        Assert.Equal("un  deux", "un  deux");
        Assert.Equal("bonjour le monde", TextCleaner.StripMarkdown("bonjour   le  monde"));
    }
}
