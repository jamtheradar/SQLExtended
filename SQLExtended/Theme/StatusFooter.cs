using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SQLExtended.Theme;

/// <summary>
/// The busy look for a window's status line: while work runs, the footer turns <c>SqlxHighlight</c> (yellow in dark,
/// amber in light), semibold and <see cref="BusyFontIncrease"/> points larger, so a status line changing at the bottom
/// of a big window is noticed. It goes back to exactly what the XAML set once the work ends.
///
/// <para><c>theme:StatusFooter.Track="True"</c> on the TextBlock turns it on by text: busy while the text ends with an
/// ellipsis (<see cref="StatusFooterText"/>). Long jobs whose progress lines don't all end that way (export, ScriptCast,
/// validation, compare) also hold <c>StatusFooter.SetBusy(StatusText, true)</c> from start to finish.</para>
///
/// <para>Pure WPF, no SSMS types: Schema Compare's standalone shell links it with the compare's XAML.</para>
/// </summary>
public static class StatusFooter
{
    public const double BusyFontIncrease = 3;
    private const string BusyBrushKey = "SqlxHighlight";
    private const string FallbackBrushKey = "SqlxTextMuted";

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(StatusFooter), new PropertyMetadata(false, OnTrackChanged));

    public static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached(
        "Busy", typeof(bool), typeof(StatusFooter), new PropertyMetadata(false, (d, _) => Update(d as TextBlock)));

    // Bound to the TextBlock's own Text, so its change callback sees every write to Text. A self-binding, unlike a
    // DependencyPropertyDescriptor handler, roots nothing: a closed window's footer is collected with it.
    private static readonly DependencyProperty WatchedTextProperty = DependencyProperty.RegisterAttached(
        "WatchedText", typeof(string), typeof(StatusFooter), new PropertyMetadata(null, (d, _) => Update(d as TextBlock)));

    private static readonly DependencyProperty IdleProperty = DependencyProperty.RegisterAttached(
        "Idle", typeof(IdleLook), typeof(StatusFooter), new PropertyMetadata(null));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);
    public static bool GetBusy(DependencyObject d) => (bool)d.GetValue(BusyProperty);
    public static void SetBusy(DependencyObject d, bool value) => d.SetValue(BusyProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        if ((bool)e.NewValue)
            BindingOperations.SetBinding(text, WatchedTextProperty, new Binding { Source = text, Path = new PropertyPath(TextBlock.TextProperty), Mode = BindingMode.OneWay });
        else
            BindingOperations.ClearBinding(text, WatchedTextProperty);
        Update(text);
    }

    private static void Update(TextBlock text)
    {
        if (text == null) return;
        bool busy = GetBusy(text) || (GetTrack(text) && StatusFooterText.LooksBusy(text.Text));
        var idle = (IdleLook)text.GetValue(IdleProperty);

        if (busy && idle == null)
        {
            idle = IdleLook.Capture(text);
            text.SetValue(IdleProperty, idle);
            text.FontSize = idle.FontSizeValue + BusyFontIncrease;
            text.FontWeight = FontWeights.SemiBold;
            text.SetResourceReference(TextBlock.ForegroundProperty, BusyBrushKey);
        }
        else if (!busy && idle != null)
        {
            text.ClearValue(IdleProperty);
            idle.Restore(text);
        }
    }

    /// <summary>What the footer looked like before it went busy: each property's local value, or none if it had none.</summary>
    private sealed class IdleLook
    {
        public double FontSizeValue;
        private object _fontSize, _fontWeight, _foreground;
        private object _foregroundKey;

        public static IdleLook Capture(TextBlock text)
        {
            var look = new IdleLook
            {
                FontSizeValue = text.FontSize,
                _fontSize = text.ReadLocalValue(TextBlock.FontSizeProperty),
                _fontWeight = text.ReadLocalValue(TextBlock.FontWeightProperty),
                _foreground = text.ReadLocalValue(TextBlock.ForegroundProperty),
            };

            // A {DynamicResource} foreground reads back as an expression, which can't be set again once replaced; keep
            // its key and re-reference that, so the restored colour still follows a theme switch.
            if (look._foreground != DependencyProperty.UnsetValue && look._foreground is not Brush)
                look._foregroundKey = ResourceKeyOf(look._foreground) ?? FallbackBrushKey;
            return look;
        }

        public void Restore(TextBlock text)
        {
            Put(text, TextBlock.FontSizeProperty, _fontSize);
            Put(text, TextBlock.FontWeightProperty, _fontWeight);
            if (_foregroundKey != null) text.SetResourceReference(TextBlock.ForegroundProperty, _foregroundKey);
            else Put(text, TextBlock.ForegroundProperty, _foreground);
        }

        private static void Put(TextBlock text, DependencyProperty property, object local)
        {
            if (local == DependencyProperty.UnsetValue) text.ClearValue(property);
            else text.SetValue(property, local);
        }

        // ResourceReferenceExpression is internal; its key is a non-public property. Any failure falls back to the muted text role.
        private static object ResourceKeyOf(object expression)
        {
            try { return expression.GetType().GetProperty("ResourceKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(expression); }
            catch (Exception) { return null; }
        }
    }
}
