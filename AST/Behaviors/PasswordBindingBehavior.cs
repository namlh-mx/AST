using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace AST.Behaviors;

// Binds PasswordBox.Password to a ViewModel string via a Behavior DP (the supported WPF pattern).
// Attached-property bridges on PasswordBox are unreliable with TwoWay binding; this keeps VMs BCL-only.
public sealed class PasswordBindingBehavior : Behavior<PasswordBox>
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            nameof(Password),
            typeof(string),
            typeof(PasswordBindingBehavior),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordPropertyChanged));

    private bool _isUpdating;

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PasswordChanged += OnPasswordBoxPasswordChanged;
        ApplyPasswordToBox(Password);
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PasswordChanged -= OnPasswordBoxPasswordChanged;
        base.OnDetaching();
    }

    private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBindingBehavior behavior)
        {
            behavior.ApplyPasswordToBox((string)e.NewValue);
        }
    }

    private void OnPasswordBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        Password = AssociatedObject.Password;
        _isUpdating = false;
    }

    private void ApplyPasswordToBox(string? password)
    {
        if (AssociatedObject is null || _isUpdating) return;
        var normalized = password ?? string.Empty;
        if (AssociatedObject.Password == normalized) return;
        _isUpdating = true;
        AssociatedObject.Password = normalized;
        _isUpdating = false;
    }
}
