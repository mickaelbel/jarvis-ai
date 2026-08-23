using JarvisAI.Application.Voice;

namespace JarvisAI.Tests;

public class WakeWordMatcherTests
{
    private static readonly string[] WakePhrases = ["jarvis", "hey jarvis"];

    [Fact]
    public void Exact_wake_word_yields_empty_command()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("jarvis", WakePhrases, out var command));
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void Hey_jarvis_exact_yields_empty_command()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("hey jarvis", WakePhrases, out var command));
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void Wake_word_followed_by_command_extracts_command()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("jarvis quelle heure est-il", WakePhrases, out var command));
        Assert.Equal("quelle heure est-il", command);
    }

    [Fact]
    public void Command_without_wake_word_returns_false()
    {
        Assert.False(WakeWordMatcher.TryExtractCommand("quelle heure est-il", WakePhrases, out _));
    }

    [Fact]
    public void Wake_word_with_punctuation_is_stripped()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("jarvis, rappelle-moi d'appeler", WakePhrases, out var command));
        Assert.Equal("rappelle-moi d'appeler", command);
    }

    [Fact]
    public void Case_insensitive_matching()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("Jarvis ouvre le navigateur", WakePhrases, out var command));
        Assert.Equal("ouvre le navigateur", command);
    }

    [Fact]
    public void Wake_word_mid_sentence_returns_false()
    {
        Assert.False(WakeWordMatcher.TryExtractCommand("bonjour jarvis comment ça va", WakePhrases, out _));
    }

    [Fact]
    public void Empty_transcript_returns_false()
    {
        Assert.False(WakeWordMatcher.TryExtractCommand("   ", WakePhrases, out _));
    }

    [Fact]
    public void Similary_word_does_not_trigger()
    {
        Assert.False(WakeWordMatcher.TryExtractCommand("jargon je parle vite", WakePhrases, out _));
    }

    [Fact]
    public void One_edit_distance_wake_word_triggers()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("parvis", WakePhrases, out var command));
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void One_edit_distance_wake_word_with_command_triggers()
    {
        Assert.True(WakeWordMatcher.TryExtractCommand("parvis quelle heure est-il", WakePhrases, out var command));
        Assert.Equal("quelle heure est-il", command);
    }

    [Fact]
    public void Long_first_word_does_not_fuzzy_match()
    {
        Assert.False(WakeWordMatcher.TryExtractCommand("jardinage quand il pleut", WakePhrases, out _));
    }
}
