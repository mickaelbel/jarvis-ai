using JarvisAI.Infrastructure.Tools;

namespace JarvisAI.Tests;

public sealed class UserPathsTests
{
    [Theory]
    [InlineData("Bureau")]
    [InlineData("BUREAU")]
    [InlineData("bureau")]
    [InlineData("Desktop")]
    public void ResolveUserPath_bureau_resolves_to_desktop(string value)
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), UserPaths.ResolveUserPath(value));
    }

    [Fact]
    public void ResolveUserPath_documents_resolves_to_documents()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            UserPaths.ResolveUserPath("Documents"));
    }

    [Theory]
    [InlineData("Téléchargements")]
    [InlineData("Téléchargement")]
    [InlineData("Telechargements")]
    [InlineData("Downloads")]
    [InlineData("Download")]
    public void ResolveUserPath_downloads_resolves_to_downloads(string value)
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            UserPaths.ResolveUserPath(value));
    }

    [Theory]
    [InlineData("Bureau\\fichier.txt")]
    [InlineData("Bureau/fichier.txt")]
    [InlineData("desktop\\fichier.txt")]
    public void ResolveUserPath_bureau_with_file_appends_relative_path(string value)
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "fichier.txt"),
            UserPaths.ResolveUserPath(value));
    }

    [Fact]
    public void ResolveUserPath_documents_subfolder_resolves()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "notes", "brouillon.txt"),
            UserPaths.ResolveUserPath("Documents\\notes\\brouillon.txt"));
    }

    [Fact]
    public void ResolveUserPath_unknown_path_unchanged()
    {
        Assert.Equal(@"C:\quelque part\x.txt", UserPaths.ResolveUserPath(@"C:\quelque part\x.txt"));
    }

    [Fact]
    public void ResolveUserPath_null_returns_null()
    {
        Assert.Null(UserPaths.ResolveUserPath(null));
    }

    [Fact]
    public void GetSpecialFolderPath_accepts_french_names()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), UserPaths.GetSpecialFolderPath("Bureau"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), UserPaths.GetSpecialFolderPath("Desktop"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), UserPaths.GetSpecialFolderPath("Documents"));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            UserPaths.GetSpecialFolderPath("Téléchargements"));
        Assert.Null(UserPaths.GetSpecialFolderPath("Zorglub"));
    }
}
