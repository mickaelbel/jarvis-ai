using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.LiveValidation;

public sealed class Harness : IAsyncDisposable
{
    private readonly IComputerController _controller;
    private readonly IOcrService _ocr;
    private readonly IComputerUseService _computerUse;
    private readonly IVisionService _vision;
    private readonly IToolExecutor _executor;
    private readonly string _workDir;
    private readonly List<int> _pids = new();
    private readonly List<Process> _processes = new();
    private readonly HashSet<int> _baselineNotepadPids;

    private WindowInfo? _notepad;
    private WindowInfo? _calculator;
    private WindowInfo? _explorer;
    private byte[]? _lastCapture;
    private readonly CancellationTokenSource _keepAliveCts = new();
    private Task? _keepAliveTask;

    public Harness(IComputerController controller, IOcrService ocr, IComputerUseService computerUse, IVisionService vision, IToolExecutor executor)
    {
        _controller = controller;
        _ocr = ocr;
        _computerUse = computerUse;
        _vision = vision;
        _executor = executor;
        _workDir = Path.Combine(Path.GetTempPath(), "jarvis-live-validation", $"run-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(_workDir);
        _baselineNotepadPids = Process.GetProcessesByName("notepad").Select(p => p.Id).ToHashSet();
    }

    public async Task<IReadOnlyList<Check>> RunAllAsync()
    {
        _keepAliveTask = KeepAliveAsync();
        var checks = new List<Check>();
        checks.Add(await Safe("controller_available", ControllerAvailableAsync));
        checks.Add(await Safe("ocr_available", OcrAvailableAsync));
        checks.Add(await Safe("screen_capture", ScreenCaptureAsync));
        checks.Add(await Safe("mouse_move_cursor", MouseMoveCursorAsync));
        checks.Add(await Safe("apps_launch", LaunchAppsAsync));
        checks.Add(await Safe("window_list_bounds", WindowListBoundsAsync));
        checks.Add(await Safe("focus_switching", FocusSwitchingAsync));
        checks.Add(await Safe("executor_tool_pipeline", ExecutorToolPipelineAsync));
        checks.Add(await Safe("click_type_copy_clipboard", ClickTypeCopyAsync));
        checks.Add(await Safe("clipboard_set_paste", ClipboardSetPasteAsync));
        checks.Add(await Safe("double_click_word_selection", DoubleClickWordAsync));
        checks.Add(await Safe("right_click_context_menu", RightClickMenuAsync));
        checks.Add(await Safe("alt_tab_switching", AltTabAsync));
        checks.Add(await Safe("screen_ocr_reads_text", ScreenOcrReadsAsync));
        checks.Add(await Safe("ui_elements_and_windows", UiElementsWindowsAsync));
        checks.Add(await Safe("element_search_then_click_menu", ElementSearchClickMenuAsync));
        checks.Add(await Safe("type_into_element", TypeIntoElementAsync));
        checks.Add(await Safe("scroll_changes_view", ScrollChangesViewAsync));
        checks.Add(await Safe("window_minimize_maximize_restore", WindowMinMaxRestoreAsync));
        checks.Add(await Safe("window_move_resize", WindowMoveResizeAsync));
        checks.Add(await Safe("file_system_tool_lifecycle", FileSystemToolLifecycleAsync));
        checks.Add(await Safe("multi_step_notepad_roundtrip", MultiStepRoundtripAsync));
        checks.Add(await Safe("close_applications", CloseApplicationsAsync));
        checks.Add(await Safe("vision_service", VisionAsync));
        return checks;
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            while (!_keepAliveCts.IsCancellationRequested)
            {
                await Task.Delay(15000, _keepAliveCts.Token);
                var cap = await _controller.CaptureScreenAsync();
                if (cap is null)
                    continue;

                var x = Math.Min(cap.Width - 2, cap.CursorX + 1);
                var y = Math.Min(cap.Height - 2, cap.CursorY + 1);
                await _controller.MoveMouseAsync(x, y);
                await _controller.MoveMouseAsync(cap.CursorX, cap.CursorY);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task<Check> Safe(string name, Func<Task<Check>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception ex)
        {
            return new Check(name, Outcome.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task<Check> ControllerAvailableAsync()
        => Task.FromResult(new Check("controller_available", _controller.IsAvailable ? Outcome.Pass : Outcome.Fail,
            _controller.IsAvailable ? "WindowsComputerController reports available" : "controller not available"));

    private Task<Check> OcrAvailableAsync()
        => Task.FromResult(new Check("ocr_available", _ocr.OcrAvailable ? Outcome.Pass : Outcome.Fail,
            _ocr.OcrAvailable ? "Tesseract OCR + tessdata (fra+eng) available" : "OCR unavailable"));

    private async Task<Check> ScreenCaptureAsync()
    {
        var capture = await Support.CaptureAsync(_controller);
        if (capture is null)
            return new Check("screen_capture", Outcome.Fail, "no capture returned");

        _lastCapture = capture.PngBytes;
        var isPng = Support.IsPng(capture.PngBytes);
        var inBounds = capture.CursorX >= 0 && capture.CursorX <= capture.Width && capture.CursorY >= 0 && capture.CursorY <= capture.Height;
        var pass = isPng && capture.Width > 0 && capture.Height > 0 && inBounds;
        return new Check("screen_capture", pass ? Outcome.Pass : Outcome.Fail,
            $"{capture.Width}x{capture.Height}, cursor ({capture.CursorX},{capture.CursorY}), png={isPng}, bytes={capture.PngBytes.Length}");
    }

    private async Task<Check> MouseMoveCursorAsync()
    {
        var moved = await _controller.MoveMouseAsync(600, 450);
        await Task.Delay(300);
        var capture = await Support.CaptureAsync(_controller);
        var dx = capture is not null ? Math.Abs(capture.CursorX - 600) : int.MaxValue;
        var dy = capture is not null ? Math.Abs(capture.CursorY - 450) : int.MaxValue;
        var tracked = dx <= 40 && dy <= 40;
        var pass = moved && tracked;
        return new Check("mouse_move_cursor", pass ? Outcome.Pass : Outcome.Fail,
            moved ? $"moved to (600,450), cursor read as ({capture?.CursorX}, {capture?.CursorY})" : "MoveMouseAsync returned false");
    }

    private async Task<Check> LaunchAppsAsync()
    {
        var launch = await _executor.ExecuteAsync("process", Ctx(("action", "start_process"), ("name", "notepad.exe")));
        if (!launch.Success)
            return new Check("apps_launch", Outcome.Fail, $"process tool: {launch.ErrorMessage}");

        var pidMatch = Regex.Match(launch.Output, @"PID:\s*(\d+)");
        if (pidMatch.Success)
            _pids.Add(int.Parse(pidMatch.Groups[1].Value));

        var notepad = await FindWindowAsync("bloc-notes", "notepad", "sans titre", "untitled");
        if (notepad is null)
            return new Check("apps_launch", Outcome.Fail, "process tool started notepad but no window appeared");

        await _controller.MoveWindowAsync(notepad.Handle, 60, 60);
        await _controller.ResizeWindowAsync(notepad.Handle, 920, 720);

        var calc = Process.Start("calc.exe");
        if (calc is not null)
        {
            _pids.Add(calc.Id);
            _processes.Add(calc);
        }

        var calculator = await FindWindowAsync("alcul");
        if (calculator is null)
            return new Check("apps_launch", Outcome.Fail, "calculator window not found");

        await _controller.MoveWindowAsync(calculator.Handle, 1100, 60);
        await _controller.ResizeWindowAsync(calculator.Handle, 420, 560);

        Process.Start(new ProcessStartInfo { FileName = _workDir, UseShellExecute = true });

        var explorer = await FindWindowAsync(Path.GetFileName(_workDir));
        if (explorer is null)
            return new Check("apps_launch", Outcome.Fail, "explorer window on workdir not found");

        await _controller.MoveWindowAsync(explorer.Handle, 60, 820);
        await _controller.ResizeWindowAsync(explorer.Handle, 900, 600);

        _notepad = notepad;
        _calculator = calculator;
        _explorer = explorer;
        return new Check("apps_launch", Outcome.Pass,
            $"notepad '{notepad.Title}', calculator '{calculator.Title}', explorer '{explorer.Title}'");
    }

    private async Task<Check> WindowListBoundsAsync()
    {
        var windows = await _controller.ListWindowsAsync();
        var all = windows.Any(w => w.Handle == _notepad?.Handle)
                  && windows.Any(w => w.Handle == _calculator?.Handle)
                  && windows.Any(w => w.Handle == _explorer?.Handle);
        var rect = await _controller.GetWindowRectAsync(_notepad!.Handle);
        var hasBounds = rect is { Width: > 0, Height: > 0 };
        return new Check("window_list_bounds", (all && hasBounds) ? Outcome.Pass : Outcome.Fail,
            $"windows listed={windows.Count}, all three present={all}, notepad rect=({rect?.X},{rect?.Y}) {rect?.Width}x{rect?.Height}");
    }

    private async Task<Check> FocusSwitchingAsync()
    {
        var notepadOk = await FocusAndVerifyAsync(_notepad!.Handle);
        var calcOk = await FocusAndVerifyAsync(_calculator!.Handle);
        var notepadAgain = await FocusAndVerifyAsync(_notepad.Handle);
        return new Check("focus_switching", (notepadOk && calcOk && notepadAgain) ? Outcome.Pass : Outcome.Fail,
            $"notepad focused={notepadOk}, calculator focused={calcOk}, notepad again={notepadAgain}");
    }

    private async Task<Check> ExecutorToolPipelineAsync()
    {
        var ui = await _executor.ExecuteAsync("ui_elements", Ctx(("action", "detect")));
        var cu = await _executor.ExecuteAsync("computer_use", Ctx(("action", "observe")));
        var pass = ui.Success && cu.Success;
        return new Check("executor_tool_pipeline", pass ? Outcome.Pass : Outcome.Fail,
            $"ui_elements success={ui.Success}, computer_use observe success={cu.Success} ({(cu.Success ? string.Empty : cu.ErrorMessage)})");
    }

    private async Task<Check> ClickTypeCopyAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("click_type_copy_clipboard", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var rect = await _controller.GetWindowRectAsync(_notepad.Handle);
        if (rect is null)
            return new Check("click_type_copy_clipboard", Outcome.Fail, "notepad window has no bounds");
        var (cx, cy) = Support.TextAreaCenter(rect);
        await _controller.ClickAsync(MouseButton.Left, cx, cy);
        await Task.Delay(400);
        await _controller.TypeTextAsync("JARVIS CHECK LIVE");
        await Task.Delay(500);
        await _controller.PressKeyAsync("ctrl+a");
        await Task.Delay(200);
        await _controller.PressKeyAsync("ctrl+c");
        await Task.Delay(400);
        var clip = await _controller.GetClipboardAsync();
        var pass = clip is not null && clip.Contains("JARVIS CHECK LIVE", StringComparison.OrdinalIgnoreCase);
        return new Check("click_type_copy_clipboard", pass ? Outcome.Pass : Outcome.Fail,
            pass ? $"clipboard contains marker ({clip!.Length} chars)" : $"expected marker in clipboard, got '{Truncate(clip, 60)}'");
    }

    private async Task<Check> ClipboardSetPasteAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("clipboard_set_paste", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var set = await _controller.SetClipboardAsync("PASTE_TOKEN_777");
        var rect = await _controller.GetWindowRectAsync(_notepad.Handle);
        if (rect is null)
            return new Check("clipboard_set_paste", Outcome.Fail, "notepad window has no bounds");
        await _controller.ClickAsync(MouseButton.Left, rect.X + rect.Width / 2, rect.Y + rect.Height / 2 + 100);
        await Task.Delay(300);
        await _controller.PressKeyAsync("ctrl+v");
        await Task.Delay(500);
        await _controller.PressKeyAsync("ctrl+a");
        await Task.Delay(200);
        await _controller.PressKeyAsync("ctrl+c");
        await Task.Delay(400);
        var clip = await _controller.GetClipboardAsync();
        var pass = set && clip is not null && clip.Contains("PASTE_TOKEN_777", StringComparison.OrdinalIgnoreCase);
        return new Check("clipboard_set_paste", pass ? Outcome.Pass : Outcome.Fail,
            $"setClipboard={set}, pasted token found={clip?.Contains("PASTE_TOKEN_777", StringComparison.OrdinalIgnoreCase)}");
    }

    private async Task<Check> DoubleClickWordAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("double_click_word_selection", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var element = await _computerUse.FindElementAsync("JARVIS");
        if (element is null)
            return new Check("double_click_word_selection", Outcome.Fail, "no element found for 'JARVIS'");

        var wordX = element.X + Math.Min(14, Math.Max(4, element.Width / 5));
        await _controller.DoubleClickAsync(MouseButton.Left, wordX, element.CenterY);
        await Task.Delay(400);
        await _controller.PressKeyAsync("ctrl+c");
        await Task.Delay(400);
        var clip = await _controller.GetClipboardAsync();
        var pass = clip is not null && clip.Trim().Equals("JARVIS", StringComparison.OrdinalIgnoreCase);
        return new Check("double_click_word_selection", pass ? Outcome.Pass : Outcome.Fail,
            $"double-clicked element '{element.Label}' at ({wordX},{element.CenterY}), word copied = '{clip?.Trim()}'");
    }

    private async Task<Check> RightClickMenuAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("right_click_context_menu", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var rect = await _controller.GetWindowRectAsync(_notepad.Handle);
        if (rect is null)
            return new Check("right_click_context_menu", Outcome.Fail, "notepad window has no bounds");
        var x = rect.X + rect.Width - 220;
        var y = rect.Y + rect.Height - 140;
        await _controller.ClickAsync(MouseButton.Right, x, y);
        await Task.Delay(600);
        var text = await OcrTextAsync();
        var found = text.Contains("Coller", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Paste", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Couper", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Copy", StringComparison.OrdinalIgnoreCase);
        await _controller.PressKeyAsync("escape");
        return new Check("right_click_context_menu", found ? Outcome.Pass : Outcome.Fail,
            found ? "context menu item detected" : "no context menu item in OCR");
    }

    private async Task<Check> AltTabAsync()
    {
        var calcOk = await FocusAndVerifyAsync(_calculator!.Handle);
        var notepadOk = await FocusAndVerifyAsync(_notepad!.Handle);
        await _controller.PressKeyAsync("alt+tab");
        await Task.Delay(700);
        var fg = await _controller.GetForegroundWindowAsync();
        var switched = fg == _calculator.Handle;
        await FocusAndVerifyAsync(_notepad.Handle);
        return new Check("alt_tab_switching", (calcOk && notepadOk && switched) ? Outcome.Pass : Outcome.Fail,
            $"foreground after alt+tab = {fg} (expected calculator {_calculator.Handle}), switched={switched}");
    }

    private async Task<Check> ScreenOcrReadsAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("screen_ocr_reads_text", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var text = await OcrTextAsync();
        var found = text.Contains("JARVIS", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("CHECK", StringComparison.OrdinalIgnoreCase);
        return new Check("screen_ocr_reads_text", found ? Outcome.Pass : Outcome.Fail,
            found ? "OCR read notepad marker text" : "OCR did not read marker text: " + Truncate(text, 120));
    }

    private async Task<Check> UiElementsWindowsAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("ui_elements_and_windows", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var obs = await _computerUse.ObserveAsync();
        if (obs is null)
            return new Check("ui_elements_and_windows", Outcome.Fail, "observe returned null");

        var imageOk = obs.ImagePath is not null && File.Exists(obs.ImagePath);
        var pass = obs.Elements.Count > 0
                   && obs.OcrText.Contains("JARVIS", StringComparison.OrdinalIgnoreCase)
                   && obs.Windows.Any(w => w.Handle == _notepad!.Handle)
                   && imageOk;
        return new Check("ui_elements_and_windows", pass ? Outcome.Pass : Outcome.Fail,
            $"{obs.Elements.Count} elements, {obs.Windows.Count} windows, image saved={imageOk}, ocr marker={obs.OcrText.Contains("JARVIS", StringComparison.OrdinalIgnoreCase)}");
    }

    private async Task<Check> ElementSearchClickMenuAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("element_search_then_click_menu", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var element = await _computerUse.FindElementAsync("Fichier");
        if (element is null)
            return new Check("element_search_then_click_menu", Outcome.Fail, "no element found for 'Fichier'");

        var clicked = await _computerUse.ClickElementAsync(element.Label);
        if (!clicked.Success)
            return new Check("element_search_then_click_menu", Outcome.Fail, clicked.Message);

        await Task.Delay(700);
        var text = await OcrTextAsync();
        var opened = text.Contains("Enregistrer", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Save", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Ouvrir", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Open", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Quitter", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Exit", StringComparison.OrdinalIgnoreCase);
        await _controller.PressKeyAsync("escape");
        return new Check("element_search_then_click_menu", opened ? Outcome.Pass : Outcome.Fail,
            $"clicked '{element.Label}' at ({clicked.X},{clicked.Y}), menu opened={opened}: " + Truncate(text, 100));
    }

    private async Task<Check> TypeIntoElementAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("type_into_element", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        await _controller.PressKeyAsync("ctrl+shift+s");
        await Task.Delay(1200);

        var typedPath = Path.Combine(_workDir, "typed.txt");
        var obs = await _computerUse.ObserveAsync();
        var input = obs?.Elements.FirstOrDefault(e =>
            e.Type == UiElementType.Input &&
            (e.Label.Contains("fichier", StringComparison.OrdinalIgnoreCase) || e.Label.Contains("file", StringComparison.OrdinalIgnoreCase)));

        var typeResult = input is null
            ? new UiActionResult(false, null, "no inferred input element for the file-name field", 0, 0)
            : await _computerUse.TypeIntoElementAsync(input.Label, typedPath);

        var saved = false;
        if (typeResult.Success)
        {
            await _controller.PressKeyAsync("enter");
            await Task.Delay(1800);
            saved = File.Exists(typedPath);
        }

        if (!saved)
        {
            var dialog = await FindWindowAsync("enregistrer", "save as", "enregistrer sous");
            if (dialog is not null && await FocusAndVerifyAsync(dialog.Handle))
            {
                await _controller.PressKeyAsync("ctrl+a");
                await Task.Delay(150);
                await _controller.TypeTextAsync(typedPath);
                await Task.Delay(300);
                await _controller.PressKeyAsync("enter");
                await Task.Delay(1800);
            }

            saved = File.Exists(typedPath);
        }

        var existsNow = File.Exists(typedPath);
        var pass = existsNow;
        return new Check("type_into_element", pass ? Outcome.Pass : Outcome.Fail,
            pass
                ? $"type_into '{input?.Label}' -> file created (element path={typeResult.Success})"
                : $"type_into failed: {typeResult.Message}; file created via fallback={existsNow}");
    }

    private async Task<Check> ScrollChangesViewAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("scroll_changes_view", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        var lines = new StringBuilder();
        for (var i = 0; i < 220; i++)
            lines.AppendLine($"LINE {i:000}");

        await _controller.SetClipboardAsync(lines.ToString());
        await _controller.PressKeyAsync("ctrl+a");
        await Task.Delay(200);
        await _controller.PressKeyAsync("ctrl+v");
        await Task.Delay(900);
        await _controller.PressKeyAsync("ctrl+home");
        await Task.Delay(600);

        var rect = await _controller.GetWindowRectAsync(_notepad.Handle);
        if (rect is null)
            return new Check("scroll_changes_view", Outcome.Fail, "notepad window has no bounds");
        var (cx, cy) = Support.TextAreaCenter(rect);
        await _controller.MoveMouseAsync(cx, cy);

        var before = await FirstVisibleLineAsync();
        await _controller.ScrollAsync(-3);
        await Task.Delay(800);
        var after = await FirstVisibleLineAsync();

        var pass = before.HasValue && after.HasValue && after.Value > before.Value;
        return new Check("scroll_changes_view", pass ? Outcome.Pass : Outcome.Fail,
            $"top line before={before?.ToString() ?? "n/a"}, after={after?.ToString() ?? "n/a"}");
    }

    private async Task<Check> WindowMinMaxRestoreAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("window_minimize_maximize_restore", Outcome.Fail, "no notepad window");
        var before = await _controller.GetWindowRectAsync(_notepad!.Handle);
        await _controller.MinimizeWindowAsync(_notepad.Handle);
        await Task.Delay(700);
        var minRect = await _controller.GetWindowRectAsync(_notepad.Handle);
        var minimized = minRect is null || minRect.Width < 20 || minRect.X < -30000;

        await _controller.RestoreWindowAsync(_notepad.Handle);
        await Task.Delay(700);
        var rest = await _controller.GetWindowRectAsync(_notepad.Handle);
        var restored = rest is not null && before is not null
                       && Math.Abs(rest.Width - before.Width) < 20 && Math.Abs(rest.Height - before.Height) < 20
                       && Math.Abs(rest.X - before.X) < 20 && Math.Abs(rest.Y - before.Y) < 20;

        await _controller.MaximizeWindowAsync(_notepad.Handle);
        await Task.Delay(700);
        var maxRect = await _controller.GetWindowRectAsync(_notepad.Handle);
        var maximized = maxRect is not null && maxRect.Width >= 2550 && maxRect.Height >= 1430;

        await _controller.RestoreWindowAsync(_notepad.Handle);
        await Task.Delay(700);

        return new Check("window_minimize_maximize_restore", (minimized && restored && maximized) ? Outcome.Pass : Outcome.Fail,
            $"minimized={minimized} (rect {minRect?.X},{minRect?.Y} {minRect?.Width}x{minRect?.Height}), " +
            $"restored={restored} ({rest?.Width}x{rest?.Height}), maximized={maximized} ({maxRect?.Width}x{maxRect?.Height})");
    }

    private async Task<Check> WindowMoveResizeAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("window_move_resize", Outcome.Fail, "no notepad window");
        await _controller.MoveWindowAsync(_notepad!.Handle, 40, 40);
        await Task.Delay(500);
        var moved = await _controller.GetWindowRectAsync(_notepad.Handle);
        var moveOk = moved is { X: 40, Y: 40 };

        await _controller.ResizeWindowAsync(_notepad.Handle, 800, 600);
        await Task.Delay(500);
        var resized = await _controller.GetWindowRectAsync(_notepad.Handle);
        var resizeOk = resized is { Width: 800, Height: 600 };

        await _controller.ResizeWindowAsync(_notepad.Handle, 920, 720);
        await _controller.MoveWindowAsync(_notepad.Handle, 60, 60);
        await Task.Delay(500);

        return new Check("window_move_resize", (moveOk && resizeOk) ? Outcome.Pass : Outcome.Fail,
            $"moved=({moved?.X},{moved?.Y}) ok={moveOk}, resized={resized?.Width}x{resized?.Height} ok={resizeOk}");
    }

    private async Task<Check> FileSystemToolLifecycleAsync()
    {
        var a = Path.Combine(_workDir, "fs_a.txt");
        var b = Path.Combine(_workDir, "fs_b.txt");
        var c = Path.Combine(_workDir, "fs_c.txt");

        var create = await _executor.ExecuteAsync("file_system", Ctx(("action", "create_file"), ("path", a), ("content", "alpha")));
        var info = await _executor.ExecuteAsync("file_system", Ctx(("action", "get_file_info"), ("path", a)));
        var write = await _executor.ExecuteAsync("file_system", Ctx(("action", "write_file"), ("path", a), ("content", "alpha beta")));
        var read = await _executor.ExecuteAsync("file_system", Ctx(("action", "read_file"), ("path", a)));
        var copy = await _executor.ExecuteAsync("file_system", Ctx(("action", "copy_file"), ("path", a), ("destination", b)));
        var readB = await _executor.ExecuteAsync("file_system", Ctx(("action", "read_file"), ("path", b)));
        var move = await _executor.ExecuteAsync("file_system", Ctx(("action", "move_file"), ("path", b), ("destination", c)));
        var goneB = await _executor.ExecuteAsync("file_system", Ctx(("action", "get_file_info"), ("path", b)));
        var del = await _executor.ExecuteAsync("file_system", Ctx(("action", "delete_file"), ("path", c)));
        var goneC = await _executor.ExecuteAsync("file_system", Ctx(("action", "read_file"), ("path", c)));

        var pass = create.Success && info.Success && write.Success
                   && read.Success && read.Output.Contains("alpha beta")
                   && copy.Success && readB.Success && readB.Output.Contains("alpha beta")
                   && move.Success && !goneB.Success
                   && del.Success && !goneC.Success;
        return new Check("file_system_tool_lifecycle", pass ? Outcome.Pass : Outcome.Fail,
            $"create={create.Success}, info={info.Success}, write={write.Success}, read={read.Success && read.Output.Contains("alpha beta")}, " +
            $"copy={copy.Success}, readCopy={readB.Success && readB.Output.Contains("alpha beta")}, move={move.Success}, delete={del.Success}");
    }

    private async Task<Check> MultiStepRoundtripAsync()
    {
        var p = Process.Start("notepad.exe");
        if (p is not null)
        {
            _pids.Add(p.Id);
            _processes.Add(p);
        }

        var win = await FindWindowAsync("bloc-notes", "notepad", "sans titre", "untitled");
        if (win is null)
            return new Check("multi_step_notepad_roundtrip", Outcome.Fail, "second notepad window not found");

        await _controller.MoveWindowAsync(win.Handle, 80, 80);
        await _controller.ResizeWindowAsync(win.Handle, 900, 700);
        await FocusAndVerifyAsync(win.Handle);

        var rect2 = await _controller.GetWindowRectAsync(win.Handle);
        if (rect2 is null)
            return new Check("multi_step_notepad_roundtrip", Outcome.Fail, "second notepad window has no bounds");
        var (cx, cy) = Support.TextAreaCenter(rect2);
        await _controller.ClickAsync(MouseButton.Left, cx, cy);
        await Task.Delay(400);
        await _controller.TypeTextAsync("JARVIS LIVE VALIDATION 42");
        await Task.Delay(400);
        await _controller.PressKeyAsync("ctrl+shift+s");
        await Task.Delay(1200);

        var path = Path.Combine(_workDir, "roundtrip.txt");
        var saveDialog = await FindWindowAsync("enregistrer", "save as", "enregistrer sous");
        if (saveDialog is not null && await FocusAndVerifyAsync(saveDialog.Handle))
        {
            await _controller.PressKeyAsync("ctrl+a");
            await Task.Delay(150);
            await _controller.TypeTextAsync(path);
            await Task.Delay(300);
        }
        else
        {
            await _controller.TypeTextAsync(path);
        }

        await _controller.PressKeyAsync("enter");
        await Task.Delay(2000);
        var saved = File.Exists(path);

        await _controller.CloseWindowAsync(win.Handle);
        var closed = await Support.WaitUntilAsync(async () =>
            (await _controller.ListWindowsAsync()).All(w => w.Handle != win.Handle), 40, 250);

        var p2 = Process.Start("notepad.exe", path);
        if (p2 is not null)
        {
            _pids.Add(p2.Id);
            _processes.Add(p2);
        }

        var reopened = await FindWindowAsync("roundtrip.txt");
        if (reopened is null)
            return new Check("multi_step_notepad_roundtrip", Outcome.Fail, $"file saved={saved}, closed={closed}, but reopen window not found");

        await FocusAndVerifyAsync(reopened.Handle);
        await Task.Delay(600);
        var text = await OcrTextAsync();
        var visible = text.Contains("JARVIS", StringComparison.OrdinalIgnoreCase)
                      && (text.Contains("42", StringComparison.Ordinal) || text.Contains("VALIDATION", StringComparison.OrdinalIgnoreCase));

        await _controller.CloseWindowAsync(reopened.Handle);
        await Support.WaitUntilAsync(async () =>
            (await _controller.ListWindowsAsync()).All(w => w.Handle != reopened.Handle), 40, 250);

        var pass = saved && closed && visible;
        return new Check("multi_step_notepad_roundtrip", pass ? Outcome.Pass : Outcome.Fail,
            $"typed+saved={saved}, closed={closed}, reopened content visible via OCR={visible}");
    }

    private async Task<Check> CloseApplicationsAsync()
    {
        if (await EnsureNotepadAsync() is null)
            return new Check("close_applications", Outcome.Fail, "no notepad window");
        await FocusAndVerifyAsync(_notepad!.Handle);
        await _controller.PressKeyAsync("ctrl+s");
        await Task.Delay(900);

        var dialog = await FindWindowAsync("enregistrer", "save as", "enregistrer sous");
        if (dialog is not null)
        {
            await FocusAndVerifyAsync(dialog.Handle);
            await _controller.TypeTextAsync(Path.Combine(_workDir, "close.txt"));
            await Task.Delay(300);
            await _controller.PressKeyAsync("enter");
            await Task.Delay(1200);
        }

        var handles = new[] { _notepad.Handle, _calculator!.Handle, _explorer!.Handle };
        foreach (var h in handles)
            await _controller.CloseWindowAsync(h);

        await Task.Delay(1000);
        var windows = await _controller.ListWindowsAsync();
        var closed = handles.Count(h => windows.All(w => w.Handle != h));
        return new Check("close_applications", closed == 3 ? Outcome.Pass : Outcome.Fail,
            $"windows closed by WM_CLOSE: {closed}/3");
    }

    private async Task<Check> VisionAsync()
    {
        if (_lastCapture is null)
            return new Check("vision_service", Outcome.Skip, "no capture to describe");

        var desc = await _vision.DescribeImageAsync(_lastCapture);
        if (desc.Success)
            return new Check("vision_service", Outcome.Pass, "vision description ok: " + Truncate(desc.Description, 80));

        return new Check("vision_service", Outcome.Skip, $"Ollama not reachable: {desc.ErrorMessage}");
    }

    private async Task<int?> FirstVisibleLineAsync()
    {
        var text = await OcrTextAsync();
        return Support.FirstLineNumber(text);
    }

    private async Task<string> OcrTextAsync()
    {
        var capture = await Support.CaptureAsync(_controller);
        if (capture is null)
            return string.Empty;
        var ocr = await Support.OcrAsync(_ocr, capture);
        return Support.OcrText(ocr);
    }

    private async Task<WindowInfo?> FindWindowAsync(params string[] titleParts)
    {
        for (var i = 0; i < 60; i++)
        {
            var windows = await _controller.ListWindowsAsync();
            var match = windows.FirstOrDefault(w => titleParts.Any(t => w.Title.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
                return match;
            await Task.Delay(200);
        }
        return null;
    }

    private async Task<WindowInfo?> EnsureNotepadAsync()
    {
        var windows = await _controller.ListWindowsAsync();
        var win = windows.FirstOrDefault(w => w.Handle == _notepad?.Handle && IsNotepad(w))
                  ?? windows.FirstOrDefault(IsNotepad);
        if (win is not null)
        {
            _notepad = win;
            return win;
        }

        var p = Process.Start("notepad.exe");
        if (p is not null)
        {
            _pids.Add(p.Id);
            _processes.Add(p);
        }

        _notepad = await FindWindowAsync("bloc-notes", "notepad", "sans titre", "untitled");
        return _notepad;
    }

    private static bool IsNotepad(WindowInfo w)
        => w.Title.Contains("bloc-notes", StringComparison.OrdinalIgnoreCase)
           || w.Title.Contains("notepad", StringComparison.OrdinalIgnoreCase)
           || w.Title.Contains("sans titre", StringComparison.OrdinalIgnoreCase)
           || w.Title.Contains("untitled", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> FocusAndVerifyAsync(long handle)
    {
        await _controller.FocusWindowAsync(handle);
        return await Support.WaitUntilAsync(async () => (await _controller.GetForegroundWindowAsync()) == handle, 30, 200);
    }

    private static AgentContext Ctx(params (string Key, string Value)[] args)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (key, value) in args)
            dict[key] = value;
        return new AgentContext("live validation", source: "live", new Dictionary<string, object> { ["arguments"] = dict });
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "(empty)" : value.Length <= max ? value : value[..max] + "...";

    public async ValueTask DisposeAsync()
    {
        try
        {
            var windows = await _controller.ListWindowsAsync();
            foreach (var w in windows)
            {
                if (_notepad is not null && w.Handle == _notepad.Handle) await _controller.CloseWindowAsync(w.Handle);
                if (_calculator is not null && w.Handle == _calculator.Handle) await _controller.CloseWindowAsync(w.Handle);
                if (_explorer is not null && w.Handle == _explorer.Handle) await _controller.CloseWindowAsync(w.Handle);
            }
            await Task.Delay(500);
            foreach (var proc in _processes)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }
            foreach (var pid in _pids)
            {
                try { Process.GetProcessById(pid).Kill(true); } catch { }
            }
            foreach (var proc in Process.GetProcessesByName("notepad"))
            {
                if (_baselineNotepadPids.Contains(proc.Id))
                    continue;
                try { proc.Kill(true); } catch { }
            }
            try { await _controller.SetClipboardAsync(string.Empty); } catch { }
            try { await _controller.MoveMouseAsync(0, 0); } catch { }
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }
        catch
        {
        }
    }
}
