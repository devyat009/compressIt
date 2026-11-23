using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CompressIt.Controls;

public static partial class MarkdownTextBehavior
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.RegisterAttached(
            "Markdown",
            typeof(string),
            typeof(MarkdownTextBehavior),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public static string GetMarkdown(DependencyObject obj) => (string)obj.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject obj, string value) => obj.SetValue(MarkdownProperty, value);

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|https?://[^\s\)]+)")]
    private static partial Regex InlineTokenRegex();

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;

        string markdown = e.NewValue as string ?? string.Empty;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = rtb.FontFamily,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225))
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        List? currentList = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                currentList = null;
                continue;
            }

            if (line.StartsWith("### "))
            {
                currentList = null;
                doc.Blocks.Add(CreateHeading(line[4..], 13, Color.FromRgb(226, 232, 240)));
            }
            else if (line.StartsWith("## "))
            {
                currentList = null;
                doc.Blocks.Add(CreateHeading(line[3..], 14, Color.FromRgb(74, 222, 128)));
            }
            else if (line.StartsWith("# "))
            {
                currentList = null;
                doc.Blocks.Add(CreateHeading(line[2..], 15, Colors.White));
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (currentList is null)
                {
                    currentList = new List
                    {
                        MarkerStyle = TextMarkerStyle.Disc,
                        Margin = new Thickness(16, 2, 0, 6),
                        Padding = new Thickness(4, 0, 0, 0)
                    };
                    doc.Blocks.Add(currentList);
                }

                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 3), LineHeight = 18 };
                AppendFormattedInlines(para.Inlines, line[2..].Trim());
                currentList.ListItems.Add(new ListItem(para));
            }
            else
            {
                currentList = null;
                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6), LineHeight = 18 };
                AppendFormattedInlines(para.Inlines, line);
                doc.Blocks.Add(para);
            }
        }

        rtb.Document = doc;
    }

    private static Paragraph CreateHeading(string text, double fontSize, Color color)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 6),
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color)
        };
        AppendFormattedInlines(para.Inlines, text.Trim());
        return para;
    }

    private static void AppendFormattedInlines(InlineCollection inlines, string text)
    {
        int lastIndex = 0;
        foreach (Match match in InlineTokenRegex().Matches(text))
        {
            if (match.Index > lastIndex)
            {
                inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            string token = match.Value;
            if (token.StartsWith("**") && token.EndsWith("**") && token.Length > 4)
            {
                inlines.Add(new Run(token[2..^2])
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                });
            }
            else if (token.StartsWith('`') && token.EndsWith('`') && token.Length > 2)
            {
                inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128))
                });
            }
            else if (token.StartsWith('[') && token.Contains("](") && token.EndsWith(')'))
            {
                int mid = token.IndexOf("](", StringComparison.Ordinal);
                string label = token[1..mid];
                string url = token[(mid + 2)..^1];
                inlines.Add(CreateHyperlink(label, url));
            }
            else if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                inlines.Add(CreateHyperlink(token, token));
            }
            else
            {
                inlines.Add(new Run(token));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private static Hyperlink CreateHyperlink(string label, string url)
    {
        var link = new Hyperlink(new Run(label))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
            TextDecorations = null
        };

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
        }

        return link;
    }
}
