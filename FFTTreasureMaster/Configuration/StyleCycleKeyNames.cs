using System;
using System.ComponentModel;
using System.Globalization;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Shows each tile hotkey choice by a plain label while the settings file keeps its name.
/// </summary>
public sealed class StyleCycleKeyNames : EnumConverter
{
    public StyleCycleKeyNames() : base(typeof(StyleCycleKey)) { }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value,
        Type destinationType)
    {
        if (destinationType == typeof(string) && value is StyleCycleKey key && key != StyleCycleKey.Off
            && Enum.IsDefined(key))
            return StyleHotkey.Digit(key) + " - Keyboard";
        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
            foreach (var key in Enum.GetValues<StyleCycleKey>())
            {
                var label = ConvertToString(context, culture, key);
                if (string.Equals(label, text.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
            }
        return base.ConvertFrom(context, culture, value);
    }
}
