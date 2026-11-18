using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace DynamicNotch.ViewModels;

public enum AskState { Idle, Thinking, Answer, Error }

/// <summary>Streams one reply: (history, onText (any thread), cancel). Normally <see cref="ClaudeService.StreamAsync"/>.</summary>
public delegate Task AskStream(IReadOnlyList<ChatMessage> history, Action<string> onText, CancellationToken ct);

/// <summary>
/// The Ask page: one box for plain questions or questions about an attached screenshot, streamed
/// answers, follow-ups in the same conversation, stop / retry / new chat. The conversation lives in
/// memory only; it is never written to disk or logged. (Same logic as the WPF view model; the UI
/// thread is a DispatcherQueue and images are SoftwareBitmaps.)
/// </summary>
public sealed class AskViewModel : ObservableObject
{
    private readonly SecretStore _secrets;
    private readonly DispatcherQueue _ui;
    private readonly List<ChatMessage> _history = new();
    private readonly StringBuilder _answer = new();
    private readonly DispatcherQueueTimer _flushTimer;
    private AskStream _stream;
    private CancellationTokenSource? _cts;

    public AskViewModel(ClaudeService claude, SecretStore secrets)
    {
        _secrets = secrets;
        _stream = claude.StreamAsync;
        _ui = DispatcherQueue.GetForCurrentThread();
        _flushTimer = _ui.CreateTimer();
        _flushTimer.IsRepeating = false;
        _flushTimer.Interval = TimeSpan.FromMilliseconds(Motion.StreamRenderMs);
        _flushTimer.Tick += (_, _) => Flush();

        SendCommand = new RelayCommand(Send);
        StopCommand = new RelayCommand(Stop);
        RetryCommand = new RelayCommand(Retry);
        NewChatCommand = new RelayCommand(NewChat);
        CopyCommand = new RelayCommand(CopyAnswer);
        RemoveAttachmentCommand = new RelayCommand(() => Attachment = null);
        SelectAreaCommand = new RelayCommand(() => SelectAreaRequested?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());

        // The page picks up a newly saved key immediately (and drops a stale "add a key" error).
        _secrets.Changed += () =>
        {
            OnPropertyChanged(nameof(HasKey));
            if (State == AskState.Error && ErrorKind is ClaudeErrorKind.NoKey or ClaudeErrorKind.InvalidKey && _secrets.HasKey)
                State = _history.Count > 0 && _history[^1].Role == "assistant" ? AskState.Answer : AskState.Idle;
        };
    }

    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand NewChatCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand SelectAreaCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    /// <summary>The window runs the region selector and calls <see cref="Attach"/> with the result.</summary>
    public event Action? SelectAreaRequested;
    public event Action? OpenSettingsRequested;
    /// <summary>Raised when the answer text grows (the view re-renders on a short throttle).</summary>
    public event Action? AnswerChanged;

    public bool HasKey => _secrets.HasKey;

    /// <summary>Dev only (`--ask-demo`): answers come from <paramref name="stream"/> instead of the Claude API.</summary>
    public void UseStream(AskStream stream) => _stream = stream;

    // ---- State -------------------------------------------------------------------------------
    private AskState _state = AskState.Idle;
    public AskState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsThinking));
            OnPropertyChanged(nameof(IsAnswer));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(HasConversation));
        }
    }
    public bool IsIdle => State == AskState.Idle;
    public bool IsThinking => State == AskState.Thinking;
    public bool IsAnswer => State == AskState.Answer;
    public bool IsError => State == AskState.Error;
    public bool HasConversation => State != AskState.Idle || _history.Count > 0;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsSendActive)); }
    }

    // ---- Input -------------------------------------------------------------------------------
    private string _input = "";
    public string InputText
    {
        get => _input;
        set { if (Set(ref _input, value ?? "")) OnPropertyChanged(nameof(IsSendActive)); }
    }

    private Screenshot? _attachment;
    public Screenshot? Attachment
    {
        get => _attachment;
        private set
        {
            if (!Set(ref _attachment, value)) return;
            OnPropertyChanged(nameof(HasAttachment));
            OnPropertyChanged(nameof(AttachmentThumb));
            OnPropertyChanged(nameof(IsSendActive));
        }
    }
    public bool HasAttachment => Attachment != null;
    public SoftwareBitmap? AttachmentThumb => Attachment?.Thumbnail;

    /// <summary>Send looks active with text or a screenshot; otherwise it's dimmed and ignores clicks.</summary>
    public bool IsSendActive => !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || HasAttachment);

    public void Attach(Screenshot shot) => Attachment = shot;

    // ---- What's on screen for the current turn ---------------------------------------------------
    private string _question = "";
    public string Question { get => _question; private set => Set(ref _question, value); }

    public string AnswerText => _answer.ToString();

    private string _error = "";
    public string ErrorText { get => _error; private set => Set(ref _error, value); }

    private ClaudeErrorKind _errorKind;
    public ClaudeErrorKind ErrorKind
    {
        get => _errorKind;
        private set { if (Set(ref _errorKind, value)) OnPropertyChanged(nameof(ShowSettingsButton)); }
    }
    public bool ShowSettingsButton => ErrorKind is ClaudeErrorKind.NoKey or ClaudeErrorKind.InvalidKey;

    // ---- Conversation turns (what the answer view shows) -----------------------------------------
    /// <summary>Every question/answer pair of this conversation, oldest first (memory only).</summary>
    public ObservableCollection<TurnViewModel> Turns { get; } = new();
    private TurnViewModel? Current => Turns.Count > 0 ? Turns[^1] : null;

    // Streamed text is pushed to the turn on a short throttle, so the Markdown re-renders smoothly.
    private void ScheduleFlush()
    {
        if (!_flushTimer.IsRunning) _flushTimer.Start();
    }

    private void Flush()
    {
        _flushTimer.Stop();
        if (Current is { } t) t.Answer = _answer.ToString();
        OnPropertyChanged(nameof(AnswerText));
        AnswerChanged?.Invoke();
    }

    // ---- Actions -------------------------------------------------------------------------------
    private void Send()
    {
        if (!IsSendActive) return; // dimmed: ignore the click
        string text = InputText.Trim();
        var shot = Attachment;
        if (text.Length == 0 && shot != null) text = AppConfig.DefaultScreenQuestion;

        _history.Add(new ChatMessage("user", text, shot?.Base64Jpeg));
        Question = text;
        foreach (var old in Turns) old.IsLatest = false;
        Turns.Add(new TurnViewModel(text, shot?.Thumbnail));
        InputText = "";
        Attachment = null;
        _ = RunAsync();
    }

    private void Retry()
    {
        if (IsBusy || _history.Count == 0 || _history[^1].Role != "user") return;
        _ = RunAsync();
    }

    /// <summary>Runs <paramref name="a"/> on the UI thread and completes when it has run.</summary>
    private Task OnUi(Action a, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(priority, () => { try { a(); } finally { tcs.TrySetResult(); } })) tcs.TrySetResult();
        return tcs.Task;
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var turn = Current;
        _answer.Clear();
        turn?.Start();
        Flush();
        IsBusy = true;
        SoundCue?.Invoke("asksent");
        State = AskState.Thinking;

        var snapshot = _history.ToList();
        var stream = _stream;
        try
        {
            await Task.Run(() => stream(snapshot, delta =>
                _ui.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _answer.Append(delta);
                    if (State == AskState.Thinking) State = AskState.Answer;
                    turn?.MarkStreaming();
                    ScheduleFlush();
                }), cts.Token), cts.Token);

            // Let any queued deltas land before closing the turn.
            await OnUi(() => { }, DispatcherQueuePriority.Low);
            if (cts.IsCancellationRequested) return;
            Flush();
            _history.Add(new ChatMessage("assistant", _answer.ToString()));
            turn?.Finish();
            SoundCue?.Invoke("askdone");
            State = AskState.Answer;
        }
        catch (OperationCanceledException)
        {
            // Stop / new chat / collapse: handled by whoever cancelled.
        }
        catch (ClaudeException ex)
        {
            if (cts.IsCancellationRequested) return;
            ShowError(turn, ex.Kind, ex.Message);
            SoundCue?.Invoke("error");
        }
        catch (Exception)
        {
            if (cts.IsCancellationRequested) return;
            ShowError(turn, ClaudeErrorKind.Other, "Something went wrong talking to Claude. Try again.");
            SoundCue?.Invoke("error");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    private void ShowError(TurnViewModel? turn, ClaudeErrorKind kind, string message)
    {
        _answer.Clear();
        Flush();
        ErrorKind = kind;
        ErrorText = message;
        turn?.Fail(message, kind is ClaudeErrorKind.NoKey or ClaudeErrorKind.InvalidKey);
        State = AskState.Error;
    }

    /// <summary>Stop button: keep whatever arrived, or take the question back into the box.</summary>
    private void Stop()
    {
        if (!IsBusy) return;
        _cts?.Cancel();
        IsBusy = false;
        if (_answer.Length > 0)
        {
            Flush();
            _history.Add(new ChatMessage("assistant", _answer.ToString()));
            Current?.Finish();
            State = AskState.Answer;
            return;
        }
        if (_history.Count > 0 && _history[^1].Role == "user") _history.RemoveAt(_history.Count - 1);
        if (Current is { } last) Turns.Remove(last);
        if (Current is { } previous) previous.IsLatest = true;
        InputText = Question == AppConfig.DefaultScreenQuestion ? "" : Question;
        State = Turns.Count > 0 ? AskState.Answer : AskState.Idle;
    }

    /// <summary>Cancels an in-flight request (used when the notch collapses).</summary>
    public void CancelIfBusy()
    {
        if (IsBusy) Stop();
    }

    private void NewChat()
    {
        _cts?.Cancel();
        IsBusy = false;
        _history.Clear();
        _answer.Clear();
        Turns.Clear();
        Question = "";
        InputText = "";
        Attachment = null;
        ErrorText = "";
        OnPropertyChanged(nameof(AnswerText));
        AnswerChanged?.Invoke();
        State = AskState.Idle;
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(IsSendActive));
    }

    private void CopyAnswer()
    {
        var text = AnswerText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CopiedFlash?.Invoke();
        }
        catch { /* clipboard busy */ }
    }

    /// <summary>Raised after Copy so the view can show a quick confirmation.</summary>
    public event Action? CopiedFlash;
    /// <summary>"asksent", "askdone" or "error" (the notch sound for it plays if enabled).</summary>
    public event Action<string>? SoundCue;
}

public enum TurnState { Thinking, Streaming, Done, Error }

/// <summary>One exchange: the question block (with its screenshot) and Claude's answer block.</summary>
public sealed class TurnViewModel : ObservableObject
{
    public TurnViewModel(string question, SoftwareBitmap? thumb)
    {
        Question = question;
        Thumb = thumb;
    }

    public string Question { get; }
    public SoftwareBitmap? Thumb { get; }
    public bool HasThumb => Thumb != null;

    private string _answer = "";
    public string Answer { get => _answer; set => Set(ref _answer, value); }

    private TurnState _state = TurnState.Thinking;
    public TurnState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsThinking));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(HasAnswer));
            OnPropertyChanged(nameof(IsError));
        }
    }
    public bool IsThinking => State == TurnState.Thinking;
    public bool IsStreaming => State == TurnState.Streaming;
    public bool HasAnswer => State is TurnState.Streaming or TurnState.Done;
    public bool IsError => State == TurnState.Error;

    private string _error = "";
    public string ErrorText { get => _error; private set => Set(ref _error, value); }

    private bool _showSettings;
    public bool ShowSettingsButton { get => _showSettings; private set => Set(ref _showSettings, value); }

    /// <summary>Older turns are dimmed a little so the newest exchange stands out.</summary>
    private bool _isLatest = true;
    public bool IsLatest { get => _isLatest; set => Set(ref _isLatest, value); }

    internal void Start() { Answer = ""; ErrorText = ""; State = TurnState.Thinking; }
    internal void MarkStreaming() { if (State == TurnState.Thinking) State = TurnState.Streaming; }
    internal void Finish() => State = TurnState.Done;
    internal void Fail(string message, bool keyProblem)
    {
        ErrorText = message;
        ShowSettingsButton = keyProblem;
        State = TurnState.Error;
    }
}
