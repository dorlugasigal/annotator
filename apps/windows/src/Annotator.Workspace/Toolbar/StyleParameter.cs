using System.Globalization;
using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>Port of StyleParameter in StyleValueControl.swift: the style values a slider and a precise field edit.</summary>
public enum StyleParameter
{
    Width,
    TextSize,
    Opacity,
    Effect,
    GridRows,
    GridColumns,
    AxisTickCount,
}

public static class StyleParameterExtensions
{
    extension(StyleParameter parameter)
    {
        /// <summary>The caption, the slider's name and the field's placeholder (Mac title(for:)).</summary>
        public string Title(Tool tool) => parameter switch
        {
            StyleParameter.Width => tool == Tool.Eraser ? "Size" : "Width",
            StyleParameter.TextSize => "Text size",
            StyleParameter.Opacity => "Opacity",
            StyleParameter.GridRows => "Rows",
            StyleParameter.GridColumns => "Columns",
            StyleParameter.AxisTickCount => "Ticks per direction",
            StyleParameter.Effect => tool switch
            {
                Tool.Pixelate => "Pixel size",
                Tool.Distort => "Strength",
                _ => "Blur",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };

        /// <summary>The values the precise field accepts.</summary>
        public (double Low, double High) Range => parameter switch
        {
            StyleParameter.Width => (1, 40),
            StyleParameter.TextSize => (8, 120),
            StyleParameter.Opacity or StyleParameter.Effect => (0, 100),
            StyleParameter.GridRows or StyleParameter.GridColumns => (1, 24),
            StyleParameter.AxisTickCount => (0, 24),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };

        public string Unit => parameter switch
        {
            StyleParameter.Width or StyleParameter.TextSize => "pt",
            StyleParameter.Opacity => "%",
            _ => string.Empty,
        };

        /// <summary>The slider's range: effects use a shorter one, and the field accepts up to 100 for a stronger effect.</summary>
        public (double Low, double High) SliderRange(Tool tool) => parameter != StyleParameter.Effect
            ? parameter.Range
            : tool switch
            {
                Tool.Blur => (0, 20),
                Tool.Pixelate => (0, 40),
                _ => (2, 100),
            };

        public double Value(in Style style) => parameter switch
        {
            StyleParameter.Width => style.Width,
            StyleParameter.TextSize => style.FontSize,
            StyleParameter.Opacity => style.Opacity * 100,
            StyleParameter.Effect => style.EffectAmount ?? 18,
            StyleParameter.GridRows => style.GridRows ?? 4,
            StyleParameter.GridColumns => style.GridColumns ?? 4,
            StyleParameter.AxisTickCount => style.AxisTickCount ?? 9,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };

        public void Apply(double value, ref Style style)
        {
            switch (parameter)
            {
                case StyleParameter.Width:
                    style.Width = value;
                    break;
                case StyleParameter.TextSize:
                    style.FontSize = value;
                    break;
                case StyleParameter.Opacity:
                    style.Opacity = value / 100;
                    break;
                case StyleParameter.Effect:
                    style.EffectAmount = value;
                    break;
                case StyleParameter.GridRows:
                    style.GridRows = (long)value;
                    break;
                case StyleParameter.GridColumns:
                    style.GridColumns = (long)value;
                    break;
                case StyleParameter.AxisTickCount:
                    style.AxisTickCount = (long)value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(parameter));
            }
        }

        public bool RequiresInteger => parameter is StyleParameter.GridRows or StyleParameter.GridColumns or StyleParameter.AxisTickCount;

        /// <summary>
        /// Mac parse(_:locale:): a finite number inside <see cref="Range"/> (whole for counts), with the culture's
        /// decimal separator or a point; null otherwise.
        /// </summary>
        public double? Parse(string draft, CultureInfo? culture = null)
        {
            ArgumentNullException.ThrowIfNull(draft);
            string separator = (culture ?? CultureInfo.CurrentCulture).NumberFormat.NumberDecimalSeparator;
            string normalized = draft.Trim().Replace(separator, ".", StringComparison.Ordinal);
            (double low, double high) = parameter.Range;
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value) || value < low || value > high)
            {
                return null;
            }
            return parameter.RequiresInteger && Math.Round(value) != value ? null : value;
        }

        /// <summary>What the field says when <see cref="Parse"/> refuses a draft.</summary>
        public string ValidationMessage
        {
            get
            {
                (double low, double high) = parameter.Range;
                return $"Enter {(parameter.RequiresInteger ? "a whole number from " : string.Empty)}{(int)low} to {(int)high}.";
            }
        }

        /// <summary>The precise field's help, such as "Width: 1–40 pt" (the Mac keeps the space before an empty unit).</summary>
        public string FieldHelp(Tool tool)
        {
            (double low, double high) = parameter.Range;
            return $"{parameter.Title(tool)}: {(int)low}–{(int)high} {parameter.Unit}";
        }

        /// <summary>The slider's help.</summary>
        public string SliderHelp(Tool tool)
        {
            (double low, double high) = parameter.SliderRange(tool);
            return parameter == StyleParameter.Effect
                ? $"Slider: {(int)low}–{(int)high}. Enter up to 100 in the number field for a stronger effect."
                : $"{parameter.Title(tool)}: {(int)low}–{(int)high}.";
        }
    }

    extension(StyleParameter)
    {
        /// <summary>Mac formatted: up to one fraction digit.</summary>
        public static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
