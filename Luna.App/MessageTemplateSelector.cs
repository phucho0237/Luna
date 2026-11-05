using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luna.App;

/// <summary>Picks the bubble template: one for the user's own messages (right) and one for the others (left).</summary>
public sealed partial class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SelfTemplate { get; set; }

    public DataTemplate? OtherTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => Choose(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Choose(item);

    private DataTemplate? Choose(object item) => item is MessageRow { IsSelf: true } ? SelfTemplate : OtherTemplate;
}
