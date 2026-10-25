using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Grpc.Core;
using Passenger.App.Aspire.Protocol;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>A native AppHost prompt. Server updates retain the interaction and editable field values.</summary>
public sealed class AspireInteractionDialog : Window
{
    private readonly StackPanel fields = new() { Spacing = 8 };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock intent = new();
    private readonly Dictionary<string, InteractionInput> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> controls = new(StringComparer.Ordinal);
    private readonly Func<WatchInteractionsRequestUpdate, Task> respond;
    private readonly Func<InteractionInput, string, Task<string>> upload;
    private readonly CancellationToken cancel;
    private WatchInteractionsResponseUpdate interaction;
    private bool applying;
    private bool sendingChanges;
    private bool changesPending;
    private bool completed;
    private bool busy;
    private int inputGeneration;

    public AspireInteractionDialog(WatchInteractionsResponseUpdate interaction,
        Func<WatchInteractionsRequestUpdate, Task> respond,
        Func<InteractionInput, string, Task<string>> upload, CancellationToken cancel)
    {
        UiThread.Check();
        this.interaction = interaction;
        this.respond = respond;
        this.upload = upload;
        this.cancel = cancel;
        Width = 500;
        MaxWidth = 640;
        MaxHeight = 700;
        MinWidth = 300;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Primary = new Button { IsDefault = true, Classes = { "primary" } };
        Secondary = new Button { IsCancel = true };
        Problem = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Primary.Click += (_, _) => IdeErrors.Observe(RunActionAsync(SubmitAsync));
        Secondary.Click += (_, _) => IdeErrors.Observe(RunActionAsync(() => AnswerAsync(false)));
        Opened += (_, _) => controls.Values.FirstOrDefault(control => control.IsEnabled)?.Focus();
        Closing += (_, e) =>
        {
            if (completed || cancel.IsCancellationRequested)
            {
                return;
            }
            // Closed with the window or the IDE: kept open, it would keep the window, and so the AppHost, up.
            e.Cancel = e.CloseReason is not (WindowCloseReason.OwnerWindowClosing or WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
            if (this.interaction.PromptTerminal is not null)
            {
                IdeErrors.Observe(RunActionAsync(() => AnswerAsync(false)));
            }
            else if (this.interaction.ShowDismiss)
            {
                IdeErrors.Observe(RunActionAsync(() => respond(new WatchInteractionsRequestUpdate
                {
                    InteractionId = InteractionId,
                    Complete = new InteractionComplete(),
                })));
            }
        };
        Content = DialogFooter.Page(new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = { intent, message, fields, Problem },
            },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        }, DialogFooter.Bar(DialogFooter.Of(Primary, Secondary)));
        Apply(interaction);
    }

    public int InteractionId => interaction.InteractionId;
    public Button Primary { get; }
    public Button Secondary { get; }
    public TextBlock Problem { get; }
    public IReadOnlyDictionary<string, Control> InputControls => controls;

    public void Complete()
    {
        UiThread.Check();
        if (completed)
        {
            return;
        }
        completed = true;
        Close();
    }

    public void Apply(WatchInteractionsResponseUpdate update)
    {
        UiThread.Check();
        if (update.InteractionId != InteractionId)
        {
            throw new InvalidDataException("The interaction ID changed during an update.");
        }
        inputGeneration++;
        applying = true;
        try
        {
            string? focused = controls.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin).Key;
            TextBox? editing = focused is not null && controls[focused] is TextBox text ? text : null;
            int selectionStart = editing?.SelectionStart ?? 0;
            int selectionEnd = editing?.SelectionEnd ?? 0;
            Dictionary<string, InteractionInput> previous = new(inputs, StringComparer.Ordinal);
            interaction = update.Clone();
            Title = update.Title;
            message.Text = update.Message;
            if (update.PromptTerminal is not null)
            {
                message.Text = "Terminal interactions are not supported in Passenger. Use the Aspire dashboard to open this terminal.";
            }
            AutomationProperties.SetName(message, message.Text);
            MessageIntent severity = update.MessageBox?.Intent ?? update.Notification?.Intent ?? MessageIntent.None;
            intent.Text = severity.ToString();
            intent.IsVisible = severity != MessageIntent.None;
            bool notificationConfirmation = update.Notification?.Intent == MessageIntent.Confirmation;
            Primary.IsCancel = update.PromptTerminal is not null;
            Primary.Content = string.IsNullOrEmpty(update.PrimaryButtonText) ? "OK" : update.PrimaryButtonText;
            Primary.IsVisible = (update.PromptProgress is null || !string.IsNullOrEmpty(update.PrimaryButtonText))
                && (update.Notification is null || !string.IsNullOrEmpty(update.PrimaryButtonText) || notificationConfirmation);
            Secondary.Content = string.IsNullOrEmpty(update.SecondaryButtonText) ? "Cancel" : update.SecondaryButtonText;
            Secondary.IsVisible = update.ShowSecondaryButton && update.PromptProgress is null
                && (update.Notification is null || !string.IsNullOrEmpty(update.SecondaryButtonText) || notificationConfirmation);
            if (update.PromptTerminal is not null)
            {
                Primary.Content = "Close";
                Primary.IsVisible = true;
                Secondary.IsVisible = false;
            }
            fields.Children.Clear();
            inputs.Clear();
            controls.Clear();
            if (interaction.InputsDialog is { } form)
            {
                if (form.InputItems.Count > 100)
                {
                    throw new InvalidDataException("The AppHost prompt exceeds 100 inputs.");
                }
                foreach (InteractionInput input in form.InputItems)
                {
                    if (string.IsNullOrWhiteSpace(input.Name) || !inputs.TryAdd(input.Name, input))
                    {
                        throw new InvalidDataException("The AppHost prompt has missing or duplicate input names.");
                    }
                    if (previous.TryGetValue(input.Name, out InteractionInput? old)
                        && old.InputType == input.InputType && !old.Disabled && !input.Disabled && !(old.Loading && !input.Loading))
                    {
                        input.Value = old.Value;
                    }
                    AddInput(input);
                }
            }
            if (update.PromptProgress is not null)
            {
                fields.Children.Add(new ProgressBar { IsIndeterminate = true });
            }
            if (update.Notification is { LinkUrl.Length: > 0 } notification)
            {
                Button link = new() { Content = string.IsNullOrEmpty(notification.LinkText) ? "Open link" : notification.LinkText };
                link.Click += (_, _) => IdeErrors.Observe(RunActionAsync(() =>
                {
                    if (!Uri.TryCreate(notification.LinkUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http"))
                    {
                        throw new InvalidDataException("The AppHost notification link must use HTTP or HTTPS.");
                    }
                    if (Owner is not MainWindow window)
                    {
                        throw new InvalidOperationException("The notification needs its owning Passenger window to open a link.");
                    }
                    window.Browse(uri);
                    return Task.CompletedTask;
                }));
                fields.Children.Add(link);
            }
            Problem.Text = string.Join(Environment.NewLine, inputs.Values.SelectMany(input => input.ValidationErrors));
            SetEnabled();
            if (focused is not null && controls.TryGetValue(focused, out Control? replacement) && replacement.IsEnabled)
            {
                replacement.Focus();
                if (replacement is TextBox restored)
                {
                    restored.SelectionStart = Math.Min(selectionStart, restored.Text?.Length ?? 0);
                    restored.SelectionEnd = Math.Min(selectionEnd, restored.Text?.Length ?? 0);
                }
            }
        }
        finally
        {
            applying = false;
        }
    }

    public async Task SubmitAsync()
    {
        UiThread.Check();
        cancel.ThrowIfCancellationRequested();
        if (interaction.InputsDialog is null)
        {
            await AnswerAsync(interaction.PromptProgress is null);
            return;
        }
        List<string> errors = [];
        foreach (InteractionInput input in inputs.Values)
        {
            if (input.Required && input.InputType != InputType.Boolean && string.IsNullOrWhiteSpace(input.Value))
            {
                errors.Add($"{input.Label} is required.");
            }
            if (input.InputType == InputType.Number && !string.IsNullOrWhiteSpace(input.Value)
                && (!double.TryParse(input.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)))
            {
                errors.Add($"{input.Label} must be a finite number.");
            }
            if (input.InputType == InputType.Choice && !input.AllowCustomChoice && input.Value.Length > 0 && !input.Options.ContainsKey(input.Value))
            {
                errors.Add($"Choose an advertised option for {input.Label}.");
            }
            if (input.MaxLength > 0 && input.Value.Length > input.MaxLength)
            {
                errors.Add($"{input.Label} exceeds its maximum length.");
            }
        }
        Problem.Text = string.Join(Environment.NewLine, errors);
        if (errors.Count == 0 && !inputs.Values.Any(input => input.Loading))
        {
            await respond(InputResponse(false));
        }
    }

    private void AddInput(InteractionInput input)
    {
        string label = input.Label + (input.Required ? " *" : string.Empty);
        Control control;
        switch (input.InputType)
        {
            case InputType.Boolean:
                CheckBox check = new() { Content = label, IsChecked = bool.TryParse(input.Value, out bool value) && value };
                input.Value = check.IsChecked == true ? "true" : "false";
                check.IsCheckedChanged += (_, _) => Changed(input, check.IsChecked == true ? "true" : "false");
                control = check;
                break;
            case InputType.Choice when !input.AllowCustomChoice:
                KeyValuePair<string, string>[] options = input.Options.ToArray();
                if (input.Value.Length == 0 && input.Placeholder.Length == 0 && options.Length > 0)
                {
                    input.Value = options[0].Key;
                }
                ComboBox choice = new()
                {
                    ItemsSource = options.Select(option => option.Value).ToArray(),
                    SelectedIndex = Array.FindIndex(options, option => option.Key == input.Value),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = input.Placeholder,
                };
                choice.SelectionChanged += (_, _) => Changed(input, choice.SelectedIndex >= 0 ? options[choice.SelectedIndex].Key : string.Empty);
                control = choice;
                break;
            case InputType.Choice:
            case InputType.Text:
            case InputType.SecretText:
            case InputType.Number:
                TextBox text = new()
                {
                    Text = input.Value,
                    PlaceholderText = input.Placeholder,
                    PasswordChar = input.InputType == InputType.SecretText ? '●' : '\0',
                    MaxLength = input.MaxLength > 0 ? input.MaxLength : int.MaxValue,
                };
                text.TextChanged += (_, _) => Changed(input, text.Text ?? string.Empty);
                control = text;
                if (input.InputType == InputType.Choice && input.Options.Count > 0)
                {
                    fields.Children.Add(new TextBlock { Text = string.Join(", ", input.Options.Select(option => $"{option.Key}: {option.Value}")), TextWrapping = TextWrapping.Wrap });
                }
                break;
            case InputType.File:
                Button file = new() { Content = input.Value.Length == 0 ? "Choose file…" : "Replace uploaded file…" };
                file.Click += (_, _) => IdeErrors.Observe(RunActionAsync(async () =>
                {
                    int version = inputGeneration;
                    if (!ReferenceEquals(CurrentUploadInput(input.Name, version), input))
                    {
                        throw new InvalidOperationException("The file input changed. Choose the file again.");
                    }
                    // The window's own picker, which tests answer for.
                    MainWindow window = Owner as MainWindow ?? throw new InvalidOperationException("The AppHost's prompt has no window to choose a file in.");
                    IReadOnlyList<string> selected = await window.PickFiles(input.Label, FileFilters(input.FileFilter), input.AllowMultipleFiles);
                    if (selected.Count > 0)
                    {
                        await UploadFilesCoreAsync(input.Name, selected, version);
                    }
                }));
                control = file;
                break;
            default:
                throw new NotSupportedException($"The AppHost requested unsupported input type {(int)input.InputType}.");
        }
        control.IsEnabled = !input.Disabled && !input.Loading;
        AutomationProperties.SetName(control, label);
        controls.Add(input.Name, control);
        if (input.InputType != InputType.Boolean)
        {
            fields.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        }
        fields.Children.Add(control);
        if (input.Description.Length > 0)
        {
            fields.Children.Add(new TextBlock { Text = input.Description, TextWrapping = TextWrapping.Wrap });
        }
        if (input.Loading)
        {
            fields.Children.Add(new TextBlock { Text = "Loading options…" });
        }
    }

    private async Task UploadFilesCoreAsync(string inputName, IReadOnlyList<string> files, int version)
    {
        InteractionInput input = CurrentUploadInput(inputName, version);
        if (input.Disabled || input.Loading || files.Count > 100 || (!input.AllowMultipleFiles && files.Count > 1))
        {
            throw new InvalidDataException("Select only the files this input currently allows.");
        }
        if (files.Count == 0)
        {
            return;
        }
        List<object> references = [];
        foreach (string file in files)
        {
            string id = await upload(CurrentUploadInput(inputName, version), file);
            CurrentUploadInput(inputName, version);
            references.Add(new { Id = id, Name = Path.GetFileName(file) });
        }
        InteractionInput latest = CurrentUploadInput(inputName, version);
        Changed(latest, JsonSerializer.Serialize(references));
        if (controls[latest.Name] is Button currentFile)
        {
            currentFile.Content = $"{files.Count} file(s) uploaded";
        }
    }

    private InteractionInput CurrentUploadInput(string name, int version)
    {
        cancel.ThrowIfCancellationRequested();
        if (completed || inputGeneration != version || !inputs.TryGetValue(name, out InteractionInput? current)
            || current.InputType != InputType.File || current.Disabled || current.Loading)
        {
            throw new InvalidOperationException("The file input changed during selection or upload. Choose the file again.");
        }
        return current;
    }

    private static IReadOnlyList<FilePickerFileType>? FileFilters(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }
        string[] parts = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [new FilePickerFileType("AppHost files")
        {
            Patterns = parts.Where(part => part.StartsWith('.')).Select(part => "*" + part).ToArray(),
            MimeTypes = parts.Where(part => part.Contains('/')).ToArray(),
        }];
    }

    private void Changed(InteractionInput input, string value)
    {
        input.Value = value;
        if (!applying && input.UpdateStateOnChange && !completed)
        {
            changesPending = true;
            if (!sendingChanges)
            {
                IdeErrors.Observe(SendChangesAsync());
            }
        }
    }

    private async Task SendChangesAsync()
    {
        sendingChanges = true;
        try
        {
            while (changesPending && !completed && !cancel.IsCancellationRequested)
            {
                changesPending = false;
                await RunActionAsync(() => respond(InputResponse(true)), disableControls: false);
            }
        }
        finally
        {
            sendingChanges = false;
        }
    }

    private WatchInteractionsRequestUpdate InputResponse(bool update) => new()
    {
        InteractionId = InteractionId,
        InputsDialog = interaction.InputsDialog.Clone(),
        ResponseUpdate = update,
    };

    private Task AnswerAsync(bool answer)
    {
        WatchInteractionsRequestUpdate response = new() { InteractionId = InteractionId };
        if (interaction.MessageBox is not null)
        {
            response.MessageBox = new InteractionMessageBox { Result = answer };
        }
        else if (interaction.Notification is not null)
        {
            response.Notification = new InteractionNotification { Result = answer };
        }
        else if (interaction.PromptProgress is not null)
        {
            response.PromptProgress = new InteractionPromptProgress { Result = false };
        }
        else if (interaction.PromptTerminal is not null)
        {
            response.PromptTerminal = new InteractionPromptTerminal
            {
                TerminalId = interaction.PromptTerminal.TerminalId,
                Result = false,
            };
        }
        else
        {
            response.Complete = new InteractionComplete();
        }
        return respond(response);
    }

    private async Task RunActionAsync(Func<Task> action, bool disableControls = true)
    {
        if (completed || cancel.IsCancellationRequested)
        {
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        if (disableControls)
        {
            busy = true;
            SetEnabled();
        }
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Complete();
        }
        catch (Exception error)
        {
            Problem.Text = error is RpcException ? "The AppHost interaction request failed." : error.Message;
        }
        finally
        {
            if (disableControls)
            {
                busy = false;
                SetEnabled();
            }
        }
    }

    private void SetEnabled()
    {
        Primary.IsEnabled = !busy && !inputs.Values.Any(input => input.Loading);
        Secondary.IsEnabled = !busy;
        foreach ((string name, Control control) in controls)
        {
            control.IsEnabled = !busy && !inputs[name].Disabled && !inputs[name].Loading;
        }
    }
}
