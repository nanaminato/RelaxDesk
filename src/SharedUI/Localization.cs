using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using RelaxDesk.Core.Localization;

namespace RelaxDesk.SharedUI;

// Explicitly binds presentation properties; never translates arbitrary user data or protocol values.
public static class LocalizedText
{
    public static T Bind<T>(T target, string property, Func<string> translate) where T : class
    {
        if (target is DependencyObject element &&
            DependencyPropertyDescriptor.FromName(property, target.GetType(), target.GetType()) is { } descriptor)
            BindingOperations.SetBinding(element, descriptor.DependencyProperty, new Binding(nameof(Message.Value))
            { Source = new Message(translate), Mode = BindingMode.OneWay });
        else target.GetType().GetProperty(property)?.SetValue(target, translate());
        return target;
    }
    public static T Bind<T>(T target, string property, string key, params object?[] arguments) where T : class
    {
        if (target is DependencyObject element &&
            DependencyPropertyDescriptor.FromName(property, target.GetType(), target.GetType()) is { } descriptor)
        {
            if (BindingOperations.GetBinding(element, descriptor.DependencyProperty)?.Source is Message current &&
                current.Key == key && current.Arguments.SequenceEqual(arguments)) return target;
            BindingOperations.SetBinding(element, descriptor.DependencyProperty, new Binding(nameof(Message.Value))
            { Source = new Message(key, arguments), Mode = BindingMode.OneWay });
        }
        else target.GetType().GetProperty(property)?.SetValue(target, Strings.Format(key, arguments));
        return target;
    }

    sealed class Message : INotifyPropertyChanged
    {
        public string Key { get; }
        public object?[] Arguments { get; }
        readonly Func<string>? translate;
        public string Value => translate?.Invoke() ?? Strings.Format(Key, Arguments);
        public event PropertyChangedEventHandler? PropertyChanged;
        public Message(string key, object?[] arguments)
        {
            Key = key; Arguments = arguments;
            WeakEventManager<LocalizationManager, EventArgs>.AddHandler(LocalizationManager.Current, nameof(LocalizationManager.LanguageChanged), Changed);
        }
        public Message(Func<string> factory) : this(string.Empty, []) => translate = factory;
        void Changed(object? sender, EventArgs args)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new(nameof(Value))));
            else PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }
}

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    Loc()
    {
        LocalizationManager.Current.LanguageChanged += (_, _) => RaiseChanged();
    }

    public string this[string key] => LocalizationManager.Current.Get(key);

    void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName)));
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return string.Empty;
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
