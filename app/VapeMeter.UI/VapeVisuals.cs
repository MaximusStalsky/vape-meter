using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VapeMeter.UI;

internal static class VapeVisuals
{
    private static readonly Dictionary<string, Geometry> Glyphs = new Dictionary<string, string>
    {
        ["home"] = "M3 10 L12 3 21 10 V19 Q21 21 19 21 H15 V15 H9 V21 H5 Q3 21 3 19 Z",
        ["calendar"] = "M5 5 H19 Q21 5 21 7 V19 Q21 21 19 21 H5 Q3 21 3 19 V7 Q3 5 5 5 Z M16 3 V7 M8 3 V7 M3 10 H21 M8 15 H10 M14 15 H16",
        ["pod"] = "M8 9 V6.5 A4 4 0 0 1 16 6.5 V9 M7 9 H17 L18 18.5 A1.5 1.5 0 0 1 16.5 20 H7.5 A1.5 1.5 0 0 1 6 18.5 Z M7 13 H17",
        ["history"] = "M3 12 A9 9 0 1 1 5.6 18.4 M3 4 V12 H11 M12 8 V12 L15 14",
        ["chart"] = "M4 20 V4 M4 20 H20 M9 16 V12 M14 16 V7 M19 16 V4",
        ["settings"] = "M9.5 3 H14.5 L15.1 5.6 17.1 6.5 19.4 5.4 21.9 9.7 20 11.5 V13.5 L21.9 15.3 19.4 19.6 17.1 18.5 15.1 19.4 14.5 22 H9.5 L8.9 19.4 6.9 18.5 4.6 19.6 2.1 15.3 4 13.5 V11.5 L2.1 9.7 4.6 5.4 6.9 6.5 8.9 5.6 Z M9 12.5 A3 3 0 1 0 15 12.5 A3 3 0 1 0 9 12.5",
        ["plus"] = "M12 5 V19 M5 12 H19",
        ["left"] = "M15 6 L9 12 15 18",
        ["right"] = "M9 6 L15 12 9 18",
        ["clock"] = "M3 12 A9 9 0 1 0 21 12 A9 9 0 1 0 3 12 M12 7 V12 L15 14",
        ["layers"] = "M12 3 L21 8 12 13 3 8 Z M3 12 L12 17 21 12 M3 16 L12 21 21 16",
        ["down"] = "M12 4 V19 M6 13 L12 19 18 13",
        ["up"] = "M12 20 V5 M6 11 L12 5 18 11",
        ["spark"] = "M10 2 C10 7 7 10 2 10 C7 10 10 13 10 18 C10 13 13 10 18 10 C13 10 10 7 10 2 Z M19 14 V21 M15.5 17.5 H22.5",
        ["sun"] = "M8 12 A4 4 0 1 0 16 12 A4 4 0 1 0 8 12 M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22 M5 5 L6.5 6.5 M17.5 17.5 L19 19 M5 19 L6.5 17.5 M17.5 6.5 L19 5",
        ["moon"] = "M20.9 13.4 A9 9 0 1 1 10.6 3.1 A7 7 0 0 0 20.9 13.4 Z",
        ["edit"] = "M14 6 L18 10 M4 20 L8 19 20 7 17 4 5 16 Z",
        ["trash"] = "M3 6 H21 M9 6 V3 H15 V6 M5 6 L6 21 H18 L19 6 M10 10 V17 M14 10 V17",
        ["close"] = "M6 6 L18 18 M18 6 L6 18",
        ["check"] = "M5 12 L9 16 19 6",
        ["sync"] = "M4 12 A8 8 0 1 1 7 18.2 M4 6 V12 H10",
        ["file"] = "M6 3 H14 L19 8 V21 H6 Z M14 3 V8 H19 M9 13 H16 M9 17 H16",
    }.ToDictionary(item => item.Key, item => Geometry.Parse(item.Value));

    public static readonly Lazy<Bitmap> Backdrop = new(() =>
    {
        using var source = AssetLoader.Open(new Uri("avares://VapeMeter.UI/Assets/neon-vapor.png"));
        return new Bitmap(source);
    });

    public static TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal,
        string tone = "", bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        block.Classes.Add("vm-text");
        if (tone.Length > 0)
        {
            block.Classes.Add(tone);
        }
        return block;
    }

    public static Viewbox Glyph(string name, double size = 18, string tone = "")
    {
        var path = new Avalonia.Controls.Shapes.Path { Data = Glyphs[name] };
        path.Classes.Add("vm-icon");
        if (tone.Length > 0)
        {
            path.Classes.Add(tone);
        }
        var shape = new Canvas { Width = 24, Height = 24, Children = { path } };
        return new Viewbox
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Stretch = Stretch.Uniform,
            Child = shape,
        };
    }

    public static Border IconTile(string icon, bool mint = false, double size = 36)
    {
        var tile = new Border
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Glyph(icon, size * .54, mint ? "mint" : ""),
        };
        tile.Classes.Add("vm-icon-tile");
        if (mint)
        {
            tile.Classes.Add("mint");
        }
        return tile;
    }

    public static StackPanel Row(double spacing, params Control[] children) =>
        Stack(Orientation.Horizontal, spacing, children);

    public static StackPanel Column(double spacing, params Control[] children) =>
        Stack(Orientation.Vertical, spacing, children);

    private static StackPanel Stack(Orientation orientation, double spacing, IEnumerable<Control> children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }
        return panel;
    }

    public static WrapPanel Wrap(params Control[] children)
    {
        var panel = new WrapPanel { ItemSpacing = 7, LineSpacing = 7 };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }
        return panel;
    }

    public static Border Card(Control child, string id = "") => Container("vm-card", child, id);

    public static Border Inset(Control child) => Container("vm-inset", child);

    private static Border Container(string style, Control child, string id = "")
    {
        var border = new Border { Child = child };
        border.Classes.Add(style);
        if (id.Length > 0)
        {
            AutomationProperties.SetAutomationId(border, id);
        }
        return border;
    }

    public static Control Heading(string icon, string title, string subtitle, bool mint = false)
    {
        var labels = Column(3,
            Text(title, 16, FontWeight.SemiBold, wrap: true),
            Text(subtitle, 12, tone: "muted", wrap: true));
        labels.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 9 };
        grid.Children.Add(IconTile(icon, mint));
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        return grid;
    }

    public static Button ActionButton(string label, Action action, string variant = "", string icon = "",
        string id = "")
    {
        var button = MakeButton(label, variant, icon, id);
        button.Click += (_, _) => action();
        return button;
    }

    public static Button AsyncButton(string label, Func<Task> action, string variant = "", string icon = "",
        string id = "")
    {
        var button = MakeButton(label, variant, icon, id);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static Button MakeButton(string label, string variant, string icon, string id)
    {
        var button = new Button { Content = label };
        button.Classes.Add("vm-button");
        if (variant.Length > 0)
        {
            button.Classes.Add(variant);
        }
        if (icon.Length > 0)
        {
            button.Content = Row(6, Glyph(icon, 16, variant == "primary" ? "white" : ""), new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        AutomationProperties.SetName(button, label);
        if (id.Length > 0)
        {
            AutomationProperties.SetAutomationId(button, id);
        }
        return button;
    }

    public static Button IconButton(string name, string glyph, Action action, string id = "")
    {
        var button = ActionButton(name, action, id: id);
        button.Content = Glyph(glyph, 17, "muted");
        button.Width = 35;
        button.Padding = new Thickness(0);
        ToolTip.SetTip(button, name);
        return button;
    }

    public static Border Dot(bool violet = false)
    {
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4) };
        dot.Classes.Add(violet ? "vm-violet-dot" : "vm-dot");
        return dot;
    }

    public static Border Rule()
    {
        var border = new Border();
        border.Classes.Add("vm-rule");
        return border;
    }
}
