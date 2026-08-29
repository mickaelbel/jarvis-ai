using System.Text.RegularExpressions;
using Markdig;

var pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()
    .DisableHtml()
    .Build();

// ---- Test 1: The original sample ----
var md1 = """
Comparaison ChatGPT vs Claude (2026)


Raisonnement & Complexité | ✅ Égalité




ChatGPT: Très bon en logique mathématique


Claude: Meilleur sur les tâches narratives
""";
Console.WriteLine("=== TEST 1: Original sample ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md1, pipeline));

// ---- Test 2: Lines with only spaces (creates empty paragraphs?) ----
var md2 = """
Line 1

  

Line 3
""";
Console.WriteLine("\n=== TEST 2: Blank line with spaces ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md2, pipeline));

// ---- Test 3: Lots of blank lines ----
var md3 = "A\n\n\n\n\n\nB";
Console.WriteLine("\n=== TEST 3: 5 blank lines between A and B ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md3, pipeline));

// ---- Test 4: Hard line breaks (trailing spaces) ----
var md4 = "Line 1    \nLine 2    \nLine 3";
Console.WriteLine("\n=== TEST 4: Trailing spaces (hard breaks) ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md4, pipeline));

// ---- Test 5: Lines with explicit line break syntax ----
var md5 = "Line 1\n\n\n\nLine 2";
Console.WriteLine("\n=== TEST 5: 3 blank lines (should ExcessiveBlankLines fire?) ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md5, pipeline));

// ---- Test 6: What the user might actually be seeing - LLM output ----
var md6 = """


Hello world

This is a test

More content

""";
Console.WriteLine("\n=== TEST 6: LLM-style with leading/trailing blank lines ===");
Console.WriteLine("Raw HTML:");
Console.WriteLine(Markdown.ToHtml(md6, pipeline));

// ---- Summary of what Markdig produces for various inputs ----
Console.WriteLine("\n=== KEY FINDING ===");
Console.WriteLine("Markdig converts blank lines between paragraphs into separate <p> tags.");
Console.WriteLine("It does NOT produce <br> tags for blank lines.");
Console.WriteLine("It does NOT produce <p></p> empty paragraphs for blank lines.");
Console.WriteLine("The CleanExcessiveLineBreaks regex targets <br> and <p></p> which Markdig never creates.");
Console.WriteLine("The real issue: multiple consecutive <p>...</p> blocks.");
Console.WriteLine();
Console.WriteLine("The ExcessiveParagraphs regex matches: (</p>\\s*<p[^>]*>){2,}");
Console.WriteLine("But it replaces with </p><p> — the SAME structure, so it does nothing!");
