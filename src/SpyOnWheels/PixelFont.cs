using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SpyOnWheels;

/// <summary>
/// A 3x5 block font drawn with one white pixel (WI-020: the HUD needs words, the game has no content pipeline). Capitals,
/// digits and a little punctuation; anything else draws as a space.
/// </summary>
public static class PixelFont
{
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "010101111101101", ['B'] = "110101110101110", ['C'] = "011100100100011", ['D'] = "110101101101110",
        ['E'] = "111100110100111", ['F'] = "111100110100100", ['G'] = "011100101101011", ['H'] = "101101111101101",
        ['I'] = "111010010010111", ['J'] = "001001001101010", ['K'] = "101101110101101", ['L'] = "100100100100111",
        ['M'] = "101111111101101", ['N'] = "110101101101101", ['O'] = "010101101101010", ['P'] = "110101110100100",
        ['Q'] = "010101101110011", ['R'] = "110101110101101", ['S'] = "011100010001110", ['T'] = "111010010010010",
        ['U'] = "101101101101111", ['V'] = "101101101101010", ['W'] = "101101111111101", ['X'] = "101101010101101",
        ['Y'] = "101101010010010", ['Z'] = "111001010100111",
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "110001010100111", ['3'] = "110001010001110",
        ['4'] = "101101111001001", ['5'] = "111100110001110", ['6'] = "011100111101111", ['7'] = "111001010010010",
        ['8'] = "111101111101111", ['9'] = "111101111001110",
        ['-'] = "000000111000000", ['!'] = "010010010000010", ['.'] = "000000000000010", [':'] = "000010000010000",
        ['/'] = "001001010100100", ['%'] = "101001010100101", ['+'] = "000010111010000",
    };

    /// <summary>Width of <paramref name="text"/> in screen px at <paramref name="scale"/> px a dot.</summary>
    public static float Width(string text, float scale) => text.Length * 4 * scale - scale;

    public static void Draw(SpriteBatch batch, Texture2D pixel, string text, Vector2 at, float scale, Color color)
    {
        var x = at.X;
        foreach (char raw in text)
        {
            if (Glyphs.TryGetValue(char.ToUpperInvariant(raw), out var g))
                for (int i = 0; i < 15; i++)
                    if (g[i] == '1')
                        batch.Draw(pixel, new Rectangle((int)(x + i % 3 * scale), (int)(at.Y + i / 3 * scale), (int)scale, (int)scale), color);
            x += 4 * scale;
        }
    }
}
