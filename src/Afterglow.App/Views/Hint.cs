using System.Windows;

namespace Afterglow.App.Views;

/// <summary>
/// Placeholder text for a TextBox, drawn by the TextBox template while the box is empty
/// (WPF has no PlaceholderText of its own).
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) =>
        (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) =>
        element.SetValue(PlaceholderProperty, value);
}
