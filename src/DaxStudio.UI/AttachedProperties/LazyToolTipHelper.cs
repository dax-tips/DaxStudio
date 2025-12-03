using System;
using System.Windows;
using System.Windows.Controls;

namespace DaxStudio.UI.AttachedProperties
{
    /// <summary>
    /// Attached property to enable lazy loading of tooltips.
    /// When enabled, the tooltip content is only created when the tooltip is about to be displayed,
    /// avoiding performance issues when loading large models with many items.
    /// Usage: Set LazyToolTipTemplate to a DataTemplate, and the tooltip will be created lazily on demand.
    /// </summary>
    public static class LazyToolTipHelper
    {
        #region LazyToolTipTemplate Attached Property

        public static readonly DependencyProperty LazyToolTipTemplateProperty =
            DependencyProperty.RegisterAttached(
                "LazyToolTipTemplate",
                typeof(DataTemplate),
                typeof(LazyToolTipHelper),
                new PropertyMetadata(null, OnLazyToolTipTemplateChanged));

        public static DataTemplate GetLazyToolTipTemplate(DependencyObject obj)
        {
            return (DataTemplate)obj.GetValue(LazyToolTipTemplateProperty);
        }

        public static void SetLazyToolTipTemplate(DependencyObject obj, DataTemplate value)
        {
            obj.SetValue(LazyToolTipTemplateProperty, value);
        }

        #endregion

        #region Private Methods

        private static void OnLazyToolTipTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement element)
            {
                if (e.NewValue is DataTemplate)
                {
                    // Initially set an empty tooltip placeholder to enable the ToolTipOpening event
                    // The actual content will be created lazily when the tooltip opens
                    element.ToolTip = string.Empty;

                    // Subscribe to the opening event
                    element.ToolTipOpening += OnToolTipOpening;
                    element.ToolTipClosing += OnToolTipClosing;
                }
                else
                {
                    // Unsubscribe from events
                    element.ToolTipOpening -= OnToolTipOpening;
                    element.ToolTipClosing -= OnToolTipClosing;
                }
            }
        }

        private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                var template = GetLazyToolTipTemplate(element);
                if (template != null)
                {
                    // Check if we already have a proper tooltip (not the placeholder)
                    if (element.ToolTip is string)
                    {
                        // Create the tooltip content from the template on demand
                        var content = template.LoadContent() as FrameworkElement;
                        if (content != null)
                        {
                            // Set the DataContext to the element's DataContext
                            content.DataContext = element.DataContext;

                            // Create and set the tooltip
                            var tooltip = new ToolTip
                            {
                                Content = content
                            };
                            element.ToolTip = tooltip;
                        }
                    }
                }
            }
        }

        private static void OnToolTipClosing(object sender, ToolTipEventArgs e)
        {
            // When the tooltip closes, we reset it to a placeholder
            // This allows the tooltip content to be garbage collected
            // and recreated fresh the next time it opens
            if (sender is FrameworkElement element)
            {
                var template = GetLazyToolTipTemplate(element);
                if (template != null)
                {
                    // Reset to placeholder for next time
                    element.ToolTip = string.Empty;
                }
            }
        }

        #endregion
    }
}
