using JarvisAI.Infrastructure.ComputerUse;

namespace JarvisAI.Tests;

/// <summary>
/// Vérifie que la table des touches couvre TOUTES les touches (et pas seulement
/// les lettres/chiffres) : modificateurs pressables seuls (win), fonction F1-F24,
/// pavé numérique, verrouillage, ponctuation… Un mapping à 0 = touche jamais
/// enfoncée (bug historique : "win" restait un modificateur sans touche principale,
/// donc la touche Windows n'était jamais pressée).
/// </summary>
public sealed class KeyboardKeyMappingTests
{
    [Theory]
    // Modificateurs seuls
    [InlineData("win", 0x5B)]
    [InlineData("lwin", 0x5B)]
    [InlineData("rwin", 0x5C)]
    [InlineData("ctrl", 0x11)]
    [InlineData("alt", 0x12)]
    [InlineData("shift", 0x10)]
    // Fonction (au-delà de F12 aussi)
    [InlineData("f1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("f13", 0x7C)]
    [InlineData("f24", 0x87)]
    // Pavé numérique
    [InlineData("numpad0", 0x60)]
    [InlineData("numpad9", 0x69)]
    [InlineData("numpadadd", 0x6B)]
    [InlineData("numpadsubtract", 0x6D)]
    [InlineData("numpadmultiply", 0x6A)]
    [InlineData("numpaddivide", 0x6F)]
    [InlineData("numpaddecimal", 0x6E)]
    // Verrouillage / système
    [InlineData("capslock", 0x14)]
    [InlineData("numlock", 0x90)]
    [InlineData("scrolllock", 0x91)]
    [InlineData("printscreen", 0x2C)]
    [InlineData("pause", 0x13)]
    [InlineData("apps", 0x5D)]
    // Édition / navigation
    [InlineData("enter", 0x0D)]
    [InlineData("tab", 0x09)]
    [InlineData("space", 0x20)]
    [InlineData("backspace", 0x08)]
    [InlineData("delete", 0x2E)]
    [InlineData("escape", 0x1B)]
    [InlineData("up", 0x26)]
    [InlineData("down", 0x28)]
    [InlineData("left", 0x25)]
    [InlineData("right", 0x27)]
    // Ponctuation (codes OEM, pas ASCII)
    [InlineData("plus", 0xBB)]
    [InlineData("+", 0xBB)]
    [InlineData("minus", 0xBD)]
    [InlineData("-", 0xBD)]
    [InlineData("/", 0xBF)]
    [InlineData(".", 0xBE)]
    [InlineData("equal", 0xBB)]
    // Lettres / chiffres (ASCII == VK)
    [InlineData("a", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    public void MapKeyCode_supports_expected_key(string name, int expectedVk)
    {
        Assert.Equal((ushort)expectedVk, WindowsComputerController.MapKeyCode(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("touche_inexistante")]
    public void MapKeyCode_returns_zero_for_unknown_key(string name)
    {
        Assert.Equal(0, WindowsComputerController.MapKeyCode(name));
    }
}
