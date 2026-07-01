using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace VRCTimeline.Helpers;

/// <summary>
/// ListBox.SelectedItems は依存プロパティではなくバインドできないため、
/// SelectionChanged を購読して選択項目を ViewModel 側の IList へミラーする添付ビヘイビア。
/// 一方向（ListBox → ViewModel）のみ。主選択は従来通り SelectedItem で別途バインドできる。
/// </summary>
public static class MultiSelectBehavior
{
    public static readonly DependencyProperty BindableSelectedItemsProperty =
        DependencyProperty.RegisterAttached(
            "BindableSelectedItems",
            typeof(IList),
            typeof(MultiSelectBehavior),
            new PropertyMetadata(null, OnBindableSelectedItemsChanged));

    public static IList? GetBindableSelectedItems(DependencyObject obj)
        => (IList?)obj.GetValue(BindableSelectedItemsProperty);

    public static void SetBindableSelectedItems(DependencyObject obj, IList? value)
        => obj.SetValue(BindableSelectedItemsProperty, value);

    private static void OnBindableSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox) return;
        listBox.SelectionChanged -= OnSelectionChanged;
        if (e.NewValue is IList)
            listBox.SelectionChanged += OnSelectionChanged;
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var target = GetBindableSelectedItems(listBox);
        if (target == null) return;
        target.Clear();
        foreach (var item in listBox.SelectedItems)
            target.Add(item);
    }
}
